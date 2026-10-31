using System.Collections.Concurrent;
using System.Globalization;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 一張卡的集中輪詢：一個循環逐軸讀取 指令位置 / 編碼器 / IO / check_done，生成不可變快照。
/// 軸的 <c>GetStatus()</c> 只讀快照，不調 DLL；同一張卡上的所有 DLL 調用經過 <see cref="Gate"/> 串行。
/// <para>
/// 讀一根軸時持有卡鎖，讀完立即釋放，不是整輪都持有：stop 最多等一根軸讀完就能插進來。
/// </para>
/// <para>
/// 後台輪詢用專用線程，不佔線程池；每隔 <see cref="StatsInterval"/> 記錄一次週期統計，上機時據此確定週期。
/// </para>
/// <para>
/// 任何一根軸讀取失敗（DLL 拋異常或返回錯誤碼），整張卡進入故障：停止讀取，本卡所有軸禁止操作（停止、急停除外），
/// 直到排查後調用 <see cref="ResetFault"/>。
/// </para>
/// 設計說明見 docs/design/central-polling.md。
/// </summary>
public sealed class LeadshineStatusPoller : IDisposable
{
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(1);

    /// <summary>快照超過這個時間沒有更新就視為過期（docs/design/central-polling.md 4.2）。</summary>
    public static readonly TimeSpan DefaultStaleTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>過期時間至少是輪詢週期的幾倍：偶爾慢一兩輪不能誤報。</summary>
    private const int MinStalePeriods = 3;

    private readonly ILeadshineMotionApi _api;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly string _family;
    private readonly object _refreshSync = new();
    private readonly object _registrationSync = new();
    private readonly ConcurrentDictionary<ushort, LeadshineAxisSnapshot> _snapshots = new();
    private Registration[] _axes = [];
    private long _cycle;
    private long _completedCycle;
    private TaskCompletionSource _cycleCompleted = NewSignal();
    private CancellationTokenSource? _loopCts;
    private Thread? _thread;
    private TimeSpan _statsInterval = TimeSpan.FromSeconds(10);
    private volatile string? _fault;
    private (ushort Inputs, ushort Outputs) _ioPorts;
    private LeadshineIoSnapshot? _ioSnapshot;

    /// <param name="api">原始 SDK，輪詢器內部用 <see cref="Gate"/> 加鎖調用。</param>
    /// <param name="family">日誌裡的系列名，DMC 或 SMC。</param>
    /// <param name="staleTimeout">快照過期時間，默認 <see cref="DefaultStaleTimeout"/>。</param>
    public LeadshineStatusPoller(ILeadshineMotionApi api, ushort cardNo, string family,
        TimeProvider? timeProvider = null, Action<string>? log = null, TimeSpan? staleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        var stale = staleTimeout ?? DefaultStaleTimeout;
        if (stale <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleTimeout), stale, "過期時間必須大於 0");
        }

        _api = api;
        CardNo = cardNo;
        _family = family;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        StaleTimeout = stale;
    }

    public ushort CardNo { get; }

    /// <summary>
    /// 快照超過這個時間沒有更新，狀態報 Alarm，等待中的運動報故障。
    /// 用來發現輪詢線程停了或卡住（讀取失敗另有卡故障處理，見 <see cref="Fault"/>）。
    /// </summary>
    public TimeSpan StaleTimeout { get; }

    /// <summary>快照時間戳所用的時鐘，判斷過期時必須用同一個。</summary>
    internal TimeProvider Time => _time;

    /// <summary>這張卡的 DLL 調用鎖，輪詢器、控制器和軸共用。</summary>
    internal LeadshineCardGate Gate { get; } = new();

    /// <summary>最近一輪開始時的週期號。下發指令後讀它作為圍欄：只有週期號更大的快照才是指令之後讀的。</summary>
    public long CurrentCycle => Interlocked.Read(ref _cycle);

    /// <summary>最近讀完的一輪的週期號。</summary>
    public long CompletedCycle => Interlocked.Read(ref _completedCycle);

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>卡故障原因；正常時為 null。故障期間不再讀取，本卡所有軸禁止操作（停止、急停除外）。</summary>
    public string? Fault => _fault;

    /// <summary>週期統計日誌的間隔，在 <see cref="Start"/> 之前設置。調試時可以縮短。</summary>
    public TimeSpan StatsInterval
    {
        get => _statsInterval;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                // 0 會導致每輪都寫一條統計，刷屏
                throw new ArgumentOutOfRangeException(nameof(value), value, "統計間隔必須大於 0");
            }

            _statsInterval = value;
        }
    }

    /// <summary>
    /// 讀一輪：所有已註冊的軸各讀一次。可以和後台循環並存，兩者互斥執行。
    /// 卡故障時不讀取；本輪中途出現讀取失敗，後面的軸也不再讀。
    /// </summary>
    public void RefreshOnce()
    {
        lock (_refreshSync)
        {
            if (_fault != null)
            {
                return;
            }

            var cycle = Interlocked.Increment(ref _cycle);
            foreach (var axis in Volatile.Read(ref _axes))
            {
                if (!RefreshAxis(axis, cycle))
                {
                    // 故障時不更新完成號，只喚醒等待方，讓它們看到故障後立即退出
                    WakeWaiters();
                    return;
                }
            }

            if (!RefreshIo(cycle))
            {
                WakeWaiters();
                return;
            }

            // 先更新完成號，再喚醒等待方：等待方被喚醒後一定能看到新的完成號
            Interlocked.Exchange(ref _completedCycle, cycle);
            WakeWaiters();
        }
    }

    /// <summary>
    /// 排查完讀取故障後手動復位，恢復輪詢。本卡所有軸需要重新使能並回零。
    /// 故障原因仍在時，下一輪會再次進入故障。
    /// </summary>
    public void ResetFault()
    {
        lock (_refreshSync)
        {
            if (_fault == null)
            {
                return;
            }

            _fault = null;
        }

        Log("Stage=Poll, 卡故障已手動復位，恢復輪詢；本卡所有軸需重新使能並回零");
    }

    /// <summary>
    /// 等到週期號大於 <paramref name="afterCycle"/> 的一輪讀完，返回讀完的週期號。
    /// <paramref name="timeout"/> 內沒有等到則拋 <see cref="TimeoutException"/>。
    /// 卡故障時立即返回，由調用方檢查 <see cref="Fault"/>。
    /// </summary>
    public async Task<long> WaitForCycleAsync(long afterCycle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            // 先取信號再判斷：判斷之後才完成的那一輪一定會設置這個信號，不會漏掉
            var signal = Volatile.Read(ref _cycleCompleted);
            var completed = CompletedCycle;
            if (completed > afterCycle || _fault != null)
            {
                return completed;
            }

            var remaining = timeout - _time.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"{timeout.TotalMilliseconds:0} ms 內沒有新的輪詢週期");
            }

            await signal.Task.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>快照距今多久。</summary>
    internal TimeSpan AgeOf(LeadshineAxisSnapshot snapshot) => _time.GetElapsedTime(snapshot.Timestamp);

    /// <summary>最新快照；這根軸還沒被讀過時返回 null。</summary>
    public LeadshineAxisSnapshot? GetSnapshot(ushort axisNo) =>
        _snapshots.TryGetValue(axisNo, out var snapshot) ? snapshot : null;

    /// <summary>
    /// 每輪輪詢讀取的通用 IO 端口數（每個端口 32 位）。默認都為 0，不讀 IO。
    /// 按端口讀而不是按位讀：一輪只多幾次調用，以太網卡上很重要。
    /// </summary>
    public void ConfigureIo(ushort inputPorts, ushort outputPorts)
    {
        lock (_refreshSync)
        {
            _ioPorts = (inputPorts, outputPorts);
            _ioSnapshot = null;
        }
    }

    /// <summary>最新的 IO 快照；未配置 IO 或還沒讀過時返回 null。</summary>
    public LeadshineIoSnapshot? GetIoSnapshot() => Volatile.Read(ref _ioSnapshot);

    /// <summary>啟動後台輪詢。重複調用無效。</summary>
    public void Start(TimeSpan period)
    {
        ValidatePeriod(period);
        if (IsRunning)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _thread = new Thread(() => RunLoop(period, token))
        {
            IsBackground = true,
            Name = $"{_family} 輪詢 卡{CardNo}",
            // 狀態讀取優先於界面刷新；每輪只讀幾個值，不會餓死其他線程
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>檢查輪詢週期：大於 0，並且過期時間至少是週期的 <see cref="MinStalePeriods"/> 倍。</summary>
    internal void ValidatePeriod(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "輪詢週期必須大於 0");
        }

        if (period * MinStalePeriods > StaleTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period,
                $"輪詢週期 {period.TotalMilliseconds:0.#} ms 太長：過期時間 {StaleTimeout.TotalMilliseconds:0} ms " +
                $"至少要是週期的 {MinStalePeriods} 倍，否則正常輪詢也會被判為過期");
        }
    }

    public void Stop()
    {
        var cts = _loopCts;
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        // DLL 調用卡死時不能讓 Close 跟著卡死；後台線程不會阻止進程退出
        if (_thread?.Join(StopJoinTimeout) == false)
        {
            Log($"Stage=Poll, 輪詢線程 {StopJoinTimeout.TotalSeconds:0} 秒內未退出，可能卡在 DLL 調用裡", "[警告] ");
        }

        cts.Dispose();
        _loopCts = null;
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <param name="describeAlarm">ALM 有效時在輪詢中調用（持卡鎖），讀出報警詳情存進快照。</param>
    /// <param name="onCardFault">卡進入故障時調用，軸據此作廢使能和原點狀態。</param>
    internal void Register(ushort axisNo, bool readFeedback, Func<string>? describeAlarm = null,
        Action? onCardFault = null)
    {
        lock (_registrationSync)
        {
            if (_axes.Any(a => a.AxisNo == axisNo))
            {
                throw new InvalidOperationException($"卡{CardNo} 的軸 {axisNo} 已經註冊過，同一根軸不能創建兩個對象");
            }

            // 寫時複製：輪詢循環拿到的數組不會在遍歷中被修改
            Volatile.Write(ref _axes, [.. _axes, new Registration(axisNo, readFeedback, describeAlarm, onCardFault)]);
        }
    }

    internal void Unregister(ushort axisNo)
    {
        lock (_registrationSync)
        {
            Volatile.Write(ref _axes, [.. _axes.Where(a => a.AxisNo != axisNo)]);
        }

        _snapshots.TryRemove(axisNo, out _);
    }

    private void RunLoop(TimeSpan period, CancellationToken token)
    {
        var timerRaised = TimerResolution.Begin();
        if (!timerRaised)
        {
            Log("Stage=Poll, 系統計時精度提高失敗，實際週期可能比配置長", "[警告] ");
        }

        try
        {
            var stats = new CycleStats();
            var statsStart = _time.GetTimestamp();
            long? lastStart = null;
            var failing = false;
            while (!token.IsCancellationRequested)
            {
                var start = _time.GetTimestamp();
                try
                {
                    RefreshOnce();
                    if (failing)
                    {
                        failing = false;
                        Log("Stage=Poll, 輪詢循環已恢復");
                    }
                }
                catch (Exception ex)
                {
                    // 單軸異常在 RefreshAxis 裡已經兜住，到這裡是意外；線程不能退出，持續失敗由過期檢測報出來
                    if (!failing)
                    {
                        failing = true;
                        Log($"Stage=Poll, 輪詢循環異常：{ex.GetType().Name}: {ex.Message}", "[報警] ");
                    }
                }

                var duration = _time.GetElapsedTime(start);
                stats.Add(duration, lastStart is { } last ? _time.GetElapsedTime(last, start) : TimeSpan.Zero,
                    period);
                lastStart = start;
                if (_time.GetElapsedTime(statsStart) >= StatsInterval)
                {
                    LogStats(stats, period);
                    stats = new CycleStats();
                    statsStart = _time.GetTimestamp();
                }

                // 按週期從本輪開始算；讀取超時就不睡，直接下一輪，不補發積壓的週期
                var wait = period - duration;
                if (wait > TimeSpan.Zero)
                {
                    token.WaitHandle.WaitOne(wait);
                }
            }
        }
        finally
        {
            if (timerRaised)
            {
                TimerResolution.End();
            }
        }
    }

    private void LogStats(CycleStats stats, TimeSpan period)
    {
        var message = string.Create(CultureInfo.InvariantCulture,
            $"Stage=Poll, 週期統計：Cycles={stats.Cycles}, PeriodMs={period.TotalMilliseconds:0.###}, " +
            $"AvgMs={stats.AverageMs:0.###}, MaxMs={stats.MaxMs:0.###}, MaxGapMs={stats.MaxGapMs:0.###}, " +
            $"Overruns={stats.Overruns}");
        if (stats.Overruns > 0)
        {
            Log($"{message}，有 {stats.Overruns} 輪讀取耗時超過週期，需加大輪詢週期或減少本卡輪詢的軸數", "[警告] ");
        }
        else
        {
            Log(message);
        }
    }

    /// <summary>讀一根軸。讀取失敗時卡進入故障，返回 false。</summary>
    private bool RefreshAxis(Registration axis, long cycle)
    {
        LeadshineAxisSnapshot snapshot;
        try
        {
            // 同一根軸的幾個值在一次持鎖內讀完，中間不會插進運動指令
            lock (Gate.Sync)
            {
                snapshot = LeadshineAxisSnapshot.Read(_api, CardNo, axis.AxisNo, axis.ReadFeedback, cycle,
                    _time.GetTimestamp());
                if (axis.DescribeAlarm != null && LeadshineAxisIo.Parse(snapshot.Io).Alarm)
                {
                    snapshot = snapshot with { AlarmDetail = DescribeAlarm(axis) };
                }
            }
        }
        catch (Exception ex)
        {
            EnterFault($"軸 {axis.AxisNo}", $"AxisIndex={axis.AxisNo}, ", $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (snapshot.ReadFailed)
        {
            EnterFault($"軸 {axis.AxisNo}", $"AxisIndex={axis.AxisNo}, ", snapshot.DescribeReadFailure());
            return false;
        }

        _snapshots[axis.AxisNo] = snapshot;
        return true;
    }

    /// <summary>
    /// 讀通用 IO 端口。read_inport / read_outport 沒有錯誤碼，讀完在同一次持鎖內讀軸 0 的指令位置作探針：
    /// 探針失敗說明這次讀到的端口值不可信（docs/design/digital-io.md 4.2）。讀取失敗時卡進入故障，返回 false。
    /// </summary>
    private bool RefreshIo(long cycle)
    {
        var (inputPorts, outputPorts) = _ioPorts;
        if (inputPorts == 0 && outputPorts == 0)
        {
            return true;
        }

        var inputs = new uint[inputPorts];
        var outputs = new uint[outputPorts];
        short probe;
        long timestamp;
        try
        {
            lock (Gate.Sync)
            {
                timestamp = _time.GetTimestamp();
                for (ushort port = 0; port < inputPorts; port++)
                {
                    _api.ReadInputPort(CardNo, port, out inputs[port]);
                }

                for (ushort port = 0; port < outputPorts; port++)
                {
                    _api.ReadOutputPort(CardNo, port, out outputs[port]);
                }

                probe = _api.ReadPosition(CardNo, 0, out _);
            }
        }
        catch (Exception ex)
        {
            EnterFault("IO", "Target=IO, ", $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (probe != 0)
        {
            EnterFault("IO", "Target=IO, ", $"通訊探針 get_position_unit={probe}");
            return false;
        }

        Volatile.Write(ref _ioSnapshot, new LeadshineIoSnapshot(cycle, timestamp, inputs, outputs));
        return true;
    }

    /// <summary>
    /// 讀取失敗說明通訊或卡本身出了問題，同一張卡上其他軸的數據也不可信：
    /// 停止讀取，本卡所有軸禁止操作，等人工排查後復位。
    /// </summary>
    /// <param name="target">讀取失敗的對象，例如「軸 1」「IO」。</param>
    /// <param name="logFields">日誌裡附加的結構化字段，例如「AxisIndex=1, 」「Target=IO, 」。</param>
    private void EnterFault(string target, string logFields, string detail)
    {
        _fault = $"卡{CardNo} {target} 狀態讀取失敗（{detail}）";
        Log($"{logFields}Stage=Poll, 狀態讀取失敗，停止輪詢，本卡所有軸和 IO 禁止操作：{detail}", "[報警] ");
        foreach (var axis in Volatile.Read(ref _axes))
        {
            axis.OnCardFault?.Invoke();
        }
    }

    private void WakeWaiters() => Interlocked.Exchange(ref _cycleCompleted, NewSignal()).TrySetResult();

    private static string DescribeAlarm(Registration axis)
    {
        try
        {
            return axis.DescribeAlarm!();
        }
        catch (Exception ex)
        {
            // 報警詳情只是附加信息，讀不出來也不能影響快照本身（ALM 位照樣生效）
            return $"伺服報警 ALM（報警詳情讀取異常：{ex.GetType().Name}）";
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Log(string message, string prefix = "") =>
        _log?.Invoke($"{prefix}{_family} 輪詢：CardId={CardNo}, {message}");

    private sealed record Registration(ushort AxisNo, bool ReadFeedback, Func<string>? DescribeAlarm,
        Action? OnCardFault);

    /// <summary>
    /// 一段時間內的週期統計。耗時（Avg/Max）是讀一輪所用的時間；
    /// 間隔（MaxGap）是相鄰兩輪開始的時間差，比週期大很多說明計時精度不夠或線程被搶佔。
    /// </summary>
    private sealed class CycleStats
    {
        private double _totalMs;

        public int Cycles { get; private set; }

        public double MaxMs { get; private set; }

        public double MaxGapMs { get; private set; }

        public int Overruns { get; private set; }

        public double AverageMs => Cycles == 0 ? 0 : _totalMs / Cycles;

        public void Add(TimeSpan duration, TimeSpan gap, TimeSpan period)
        {
            var ms = duration.TotalMilliseconds;
            Cycles++;
            _totalMs += ms;
            MaxMs = Math.Max(MaxMs, ms);
            MaxGapMs = Math.Max(MaxGapMs, gap.TotalMilliseconds);
            if (duration > period)
            {
                Overruns++;
            }
        }
    }
}
