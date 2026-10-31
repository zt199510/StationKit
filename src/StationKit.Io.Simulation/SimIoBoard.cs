using StationKit.Abstractions.Io;

namespace StationKit.Io.Simulation;

/// <summary>
/// 模擬 IO 板：按週期把當前電平拍成快照，行為與實機的集中輪詢一致——寫輸出後要到下一輪才能回讀到。
/// <list type="bullet">
/// <item>測試用 <see cref="SetInputLevel"/> 設置輸入電平，立即算作新的一輪，不用等週期。</item>
/// <item>故障注入：<see cref="InjectFault"/> 模擬卡故障；<see cref="StickOutput"/> 模擬輸出口卡死（寫入無效）。</item>
/// </list>
/// 時間來自 <see cref="TimeProvider"/>，測試裡用假時鐘推進週期。
/// </summary>
public sealed class SimIoBoard : IoBoard, IDisposable
{
    /// <summary>默認刷新週期，與雷賽卡默認輪詢週期一致。</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMilliseconds(5);

    private readonly object _sync = new();
    private readonly bool[] _inputs;
    private readonly bool[] _outputs;
    private readonly Dictionary<ushort, bool> _stuckOutputs = [];
    private readonly ITimer _timer;
    private Snapshot _snapshot;
    private string? _fault;
    private TaskCompletionSource _cycleCompleted = NewSignal();

    /// <param name="period">刷新週期，默認 <see cref="DefaultPeriod"/>。</param>
    public SimIoBoard(string name = "模擬IO", int inputCount = 32, int outputCount = 32,
        TimeProvider? timeProvider = null, TimeSpan? period = null)
        : base(name, timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputCount);
        var interval = period ?? DefaultPeriod;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(period));
        _inputs = new bool[inputCount];
        _outputs = new bool[outputCount];
        _snapshot = new Snapshot(1, [.. _inputs], [.. _outputs]);
        _timer = Time.CreateTimer(_ => Refresh(), null, interval, interval);
    }

    /// <summary>卡故障原因；正常時為 null。</summary>
    public string? Fault => _fault;

    protected override long CurrentCycle => Volatile.Read(ref _snapshot).Cycle;

    /// <summary>設置輸入<b>電平</b>（不是邏輯值），並立即刷新一輪。</summary>
    public void SetInputLevel(ushort bit, bool level)
    {
        ValidateAddress(IoKind.Input, bit);
        lock (_sync)
        {
            _inputs[bit] = level;
        }

        Refresh();
    }

    /// <summary>輸出口當前的實際電平（不經過快照，給模擬設備和測試斷言用）。</summary>
    public bool GetOutputLevel(ushort bit)
    {
        ValidateAddress(IoKind.Output, bit);
        lock (_sync)
        {
            return _outputs[bit];
        }
    }

    /// <summary>模擬卡故障：之後所有讀取不可信，寫入被拒絕，等待中的操作立即報故障。</summary>
    public void InjectFault(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _fault = reason;
        WakeWaiters();
    }

    public void ClearFault()
    {
        _fault = null;
        Refresh();
    }

    /// <summary>模擬輸出口卡死在 <paramref name="level"/>：寫入返回成功，但電平不變。</summary>
    public void StickOutput(ushort bit, bool level)
    {
        ValidateAddress(IoKind.Output, bit);
        lock (_sync)
        {
            _stuckOutputs[bit] = level;
            _outputs[bit] = level;
        }
    }

    public void UnstickOutput(ushort bit)
    {
        lock (_sync)
        {
            _stuckOutputs.Remove(bit);
        }
    }

    /// <summary>立即刷新一輪：把當前電平拍成快照，喚醒等待方。卡故障時不刷新。</summary>
    public void Refresh()
    {
        lock (_sync)
        {
            if (_fault != null)
            {
                return;
            }

            Volatile.Write(ref _snapshot, new Snapshot(_snapshot.Cycle + 1, [.. _inputs], [.. _outputs]));
        }

        WakeWaiters();
    }

    public void Dispose() => _timer.Dispose();

    protected override IoSample Sample(IoKind kind, ushort bit)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var levels = kind == IoKind.Input ? snapshot.Inputs : snapshot.Outputs;
        return new IoSample(snapshot.Cycle, levels[bit], _fault is { } fault ? $"{Name} 故障：{fault}" : null);
    }

    protected override async Task WaitForCycleAsync(long afterCycle, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = Time.GetTimestamp();
        while (true)
        {
            // 先取信號再判斷：判斷之後才完成的那一輪一定會設置這個信號，不會漏掉
            var signal = Volatile.Read(ref _cycleCompleted);
            if (CurrentCycle > afterCycle || _fault != null)
            {
                return;
            }

            var remaining = timeout - Time.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"{timeout.TotalMilliseconds:0} ms 內沒有新的刷新週期");
            }

            await signal.Task.WaitAsync(remaining, Time, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void WriteOutput(ushort bit, bool level)
    {
        lock (_sync)
        {
            if (_fault is { } fault)
            {
                throw new IoFaultException($"{Name} 輸出位 {bit}", $"寫入失敗：{fault}");
            }

            // 卡死的輸出口：寫入「成功」但電平不變，只有回讀核對能發現
            _outputs[bit] = _stuckOutputs.TryGetValue(bit, out var stuck) ? stuck : level;
        }
    }

    protected override void ValidateAddress(IoKind kind, ushort bit)
    {
        var count = kind == IoKind.Input ? _inputs.Length : _outputs.Length;
        if (bit >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit,
                $"{Name} 只有 {count} 個{(kind == IoKind.Input ? "輸入" : "輸出")}");
        }
    }

    private void WakeWaiters() => Interlocked.Exchange(ref _cycleCompleted, NewSignal()).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Snapshot(long Cycle, bool[] Inputs, bool[] Outputs);
}
