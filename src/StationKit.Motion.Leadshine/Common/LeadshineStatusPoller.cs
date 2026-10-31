using System.Collections.Concurrent;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 一張卡的集中輪詢：一個循環逐軸讀取 指令位置 / 編碼器 / IO / check_done，生成不可變快照。
/// 軸的 <c>GetStatus()</c> 只讀快照，不調 DLL；同一張卡上的所有 DLL 調用經過 <see cref="Gate"/> 串行。
/// <para>
/// 讀一根軸時持有卡鎖，讀完立即釋放，不是整輪都持有：stop 最多等一根軸讀完就能插進來。
/// </para>
/// 設計說明見 docs/design/central-polling.md。
/// </summary>
public sealed class LeadshineStatusPoller : IDisposable
{
    private readonly ILeadshineMotionApi _api;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly string _family;
    private readonly object _refreshSync = new();
    private readonly object _registrationSync = new();
    private readonly ConcurrentDictionary<ushort, LeadshineAxisSnapshot> _snapshots = new();
    private readonly HashSet<ushort> _failingAxes = [];
    private Registration[] _axes = [];
    private long _cycle;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    /// <param name="api">原始 SDK，輪詢器內部用 <see cref="Gate"/> 加鎖調用。</param>
    /// <param name="family">日誌裡的系列名，DMC 或 SMC。</param>
    public LeadshineStatusPoller(ILeadshineMotionApi api, ushort cardNo, string family,
        TimeProvider? timeProvider = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        CardNo = cardNo;
        _family = family;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public ushort CardNo { get; }

    /// <summary>這張卡的 DLL 調用鎖，輪詢器、控制器和軸共用。</summary>
    internal LeadshineCardGate Gate { get; } = new();

    /// <summary>最近一輪開始時的週期號。</summary>
    public long CurrentCycle => Interlocked.Read(ref _cycle);

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>讀一輪：所有已註冊的軸各讀一次。可以和後台循環並存，兩者互斥執行。</summary>
    public void RefreshOnce()
    {
        lock (_refreshSync)
        {
            var cycle = Interlocked.Increment(ref _cycle);
            foreach (var axis in Volatile.Read(ref _axes))
            {
                RefreshAxis(axis, cycle);
            }
        }
    }

    /// <summary>最新快照；這根軸還沒被讀過時返回 null。</summary>
    public LeadshineAxisSnapshot? GetSnapshot(ushort axisNo) =>
        _snapshots.TryGetValue(axisNo, out var snapshot) ? snapshot : null;

    /// <summary>啟動後台輪詢。重複調用無效。</summary>
    public void Start(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "輪詢週期必須大於 0");
        }

        if (IsRunning)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(() => RunLoopAsync(period, token), CancellationToken.None);
    }

    public void Stop()
    {
        var cts = _loopCts;
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            _loop?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }

        cts.Dispose();
        _loopCts = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    internal void Register(ushort axisNo, bool readFeedback)
    {
        lock (_registrationSync)
        {
            if (_axes.Any(a => a.AxisNo == axisNo))
            {
                throw new InvalidOperationException($"卡{CardNo} 的軸 {axisNo} 已經註冊過，同一根軸不能創建兩個對象");
            }

            // 寫時複製：輪詢循環拿到的數組不會在遍歷中被修改
            Volatile.Write(ref _axes, [.. _axes, new Registration(axisNo, readFeedback)]);
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

    private async Task RunLoopAsync(TimeSpan period, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            RefreshOnce();
            await Task.Delay(period, _time, token).ConfigureAwait(false);
        }
    }

    private void RefreshAxis(Registration axis, long cycle)
    {
        try
        {
            LeadshineAxisSnapshot snapshot;
            // 同一根軸的幾個值在一次持鎖內讀完，中間不會插進運動指令
            lock (Gate.Sync)
            {
                snapshot = LeadshineAxisSnapshot.Read(_api, CardNo, axis.AxisNo, axis.ReadFeedback, cycle,
                    _time.GetTimestamp());
            }

            _snapshots[axis.AxisNo] = snapshot;
            if (_failingAxes.Remove(axis.AxisNo))
            {
                Log($"AxisIndex={axis.AxisNo}, Stage=Poll, 狀態讀取已恢復");
            }
        }
        catch (Exception ex)
        {
            // 一根軸讀取異常不能結束整個輪詢，其他軸照常讀；只在第一次出錯時記錄，避免每週期刷屏
            if (_failingAxes.Add(axis.AxisNo))
            {
                Log($"AxisIndex={axis.AxisNo}, Stage=Poll, 狀態讀取異常：{ex.GetType().Name}: {ex.Message}", "[報警] ");
            }
        }
    }

    private void Log(string message, string prefix = "") =>
        _log?.Invoke($"{prefix}{_family} 輪詢：CardId={CardNo}, {message}");

    private sealed record Registration(ushort AxisNo, bool ReadFeedback);
}
