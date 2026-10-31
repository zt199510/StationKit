using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Motion.Simulation;

/// <summary>
/// 模擬軸：按梯形規劃隨時間推進位置，支持硬限位、報警注入和安全參數回讀故障注入。
/// 時間來自 <see cref="TimeProvider"/>，測試裡可以用假時鐘精確推進。
/// 簡化：減速停止按立即停止處理（位置凍結在停止時刻）。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "運動用的 CancellationTokenSource 不帶定時器，無需釋放；Abort 可能在鎖外晚於運動結束調用 Cancel，釋放反而會拋 ObjectDisposedException")]
public sealed class SimAxis : IAxis, IAxisSafetyPort
{
    private const double JogSimulatedDistance = 1e6;

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private AxisState _state = AxisState.Disabled;
    private bool _homed;
    private double _position;
    private TrapezoidProfile? _segment;
    private long _segmentStart;
    private CancellationTokenSource? _motionCts;
    private string? _abortReason;
    private string? _alarm;
    private bool _positiveLimit;
    private bool _negativeLimit;
    private AxisSafetyConfig _safety = new();

    public SimAxis(string name, TimeProvider? timeProvider = null)
    {
        Name = name;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    /// <summary>正向硬限位開關位置。</summary>
    public double PositiveLimitSwitch { get; init; } = double.PositiveInfinity;

    /// <summary>負向硬限位開關位置。</summary>
    public double NegativeLimitSwitch { get; init; } = double.NegativeInfinity;

    public TimeSpan HomeDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>故障注入：模擬控制卡回讀值與寫入值不一致。</summary>
    public Func<AxisSafetyConfig, AxisSafetyConfig>? ReadBackTransform { get; set; }

    public AxisStatus GetStatus()
    {
        lock (_sync)
        {
            return new(_state, CurrentPositionLocked(), _homed, _positiveLimit, _negativeLimit, _alarm);
        }
    }

    public Task EnableAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state == AxisState.Alarm)
            {
                throw new MotionFaultException(Name, $"報警中，需先復位：{_alarm}");
            }

            if (_state == AxisState.Disabled)
            {
                _state = AxisState.Idle;
            }
        }

        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        Abort("軸被失能", next => next == AxisState.Alarm ? AxisState.Alarm : AxisState.Disabled);
        return Task.CompletedTask;
    }

    public Task StopAsync(StopMode mode = StopMode.Decelerate)
    {
        Abort(mode == StopMode.Immediate ? "急停" : "被停止",
            current => current is AxisState.Moving or AxisState.Homing ? AxisState.Idle : current);
        return Task.CompletedTask;
    }

    public Task HomeAsync(CancellationToken cancellationToken = default) =>
        RunMotionAsync(AxisState.Homing,
            _ => new MotionPlan(HomeDuration, null, 0, Homes: true, Fault: null), cancellationToken);

    public Task MoveAbsoluteAsync(double position, MotionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (!double.IsFinite(position))
        {
            throw new ArgumentException($"目標位置非法：{position}", nameof(position));
        }

        return RunMotionAsync(AxisState.Moving, start =>
        {
            // 目標超出硬限位：運動到限位開關處觸發報警
            var reachable = Math.Clamp(position, NegativeLimitSwitch, PositiveLimitSwitch);
            string? fault = null;
            if (reachable < position)
            {
                fault = "正限位觸發";
            }
            else if (reachable > position)
            {
                fault = "負限位觸發";
            }

            var segment = TrapezoidProfile.Plan(start, reachable, profile);
            return new MotionPlan(segment.Duration, segment, reachable, Homes: false, fault);
        }, cancellationToken);
    }

    public async Task JogAsync(JogDirection direction, MotionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        try
        {
            await RunMotionAsync(AxisState.Moving, start =>
            {
                // 速度模式：一直走到限位開關；沒有限位開關時按很遠的距離模擬，實際由鬆開按鈕停止
                var far = start + ((int)direction * JogSimulatedDistance);
                var limit = direction == JogDirection.Positive ? PositiveLimitSwitch : NegativeLimitSwitch;
                var hitsLimit = double.IsFinite(limit);
                var reachable = hitsLimit ? limit : far;
                var segment = TrapezoidProfile.Plan(start, reachable, profile);
                var fault = hitsLimit ? (direction == JogDirection.Positive ? "正限位觸發" : "負限位觸發") : null;
                return new MotionPlan(segment.Duration, segment, reachable, Homes: false, fault);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 鬆開按鈕：RunMotionAsync 已先停軸（或根本沒有下發），點動正常結束
            return;
        }

        // 沒有限位時不會走到這裡：時長足夠長，只會被鬆開或停止打斷
        throw new MotionFaultException(Name, "點動未鬆開就結束了");
    }

    public Task ResetAlarmAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state != AxisState.Alarm)
            {
                return Task.CompletedTask;
            }

            // 報警後原點不再可信，必須重新回零
            _alarm = null;
            _positiveLimit = _negativeLimit = false;
            _homed = false;
            _state = AxisState.Disabled;
        }

        return Task.CompletedTask;
    }

    /// <summary>故障注入：模擬伺服報警。正在進行的運動會以 <see cref="MotionFaultException"/> 結束。</summary>
    public void InjectAlarm(string reason)
    {
        Abort($"報警：{reason}", _ => AxisState.Alarm, () => _alarm = reason);
    }

    public Task WriteSafetyConfigAsync(AxisSafetyConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _safety = config;
        }

        return Task.CompletedTask;
    }

    public Task<AxisSafetyConfig> ReadSafetyConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(ReadBackTransform?.Invoke(_safety) ?? _safety);
        }
    }

    private async Task RunMotionAsync(AxisState motionState, Func<double, MotionPlan> plan,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource motionCts;
        MotionPlan motion;
        lock (_sync)
        {
            // 下發前檢查取消：被取消的運動絕不能晚到下發
            cancellationToken.ThrowIfCancellationRequested();
            EnsureReadyLocked();
            motion = plan(_position);
            _segment = motion.Segment;
            _segmentStart = _time.GetTimestamp();
            _state = motionState;
            _abortReason = null;
            motionCts = _motionCts = new CancellationTokenSource();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, motionCts.Token);
        try
        {
            await Task.Delay(motion.Duration, _time, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentMotion(motionCts))
            {
                // 調用方取消了已下發的運動：先停軸，再報告取消
                await StopAsync().ConfigureAwait(false);
                throw;
            }

            throw new MotionFaultException(Name, AbortReason());
        }

        lock (_sync)
        {
            // 用引用判斷而不是 IsCancellationRequested：Abort 在鎖外才觸發取消，
            // 中間這段窗口裡運動已被中止，但令牌還沒有被取消
            if (!ReferenceEquals(_motionCts, motionCts))
            {
                throw new MotionFaultException(Name, _abortReason ?? "運動被中止");
            }

            _segment = null;
            _motionCts = null;
            _position = motion.FinalPosition;
            _homed |= motion.Homes;
            if (motion.Fault is { } fault)
            {
                _alarm = fault;
                _positiveLimit = fault.StartsWith('正');
                _negativeLimit = fault.StartsWith('負');
                _state = AxisState.Alarm;
            }
            else
            {
                _state = AxisState.Idle;
            }
        }

        if (motion.Fault is { } reason)
        {
            throw new MotionFaultException(Name, reason);
        }
    }

    /// <summary>中止當前運動並切換狀態。取消回調在鎖外觸發，避免等待方在同一線程上重入半更新的狀態。</summary>
    private void Abort(string reason, Func<AxisState, AxisState> nextState, Action? updateLocked = null)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _position = CurrentPositionLocked();
            _segment = null;
            cts = _motionCts;
            _motionCts = null;
            if (cts != null)
            {
                _abortReason = reason;
            }

            updateLocked?.Invoke();
            _state = nextState(_state);
        }

        cts?.Cancel();
    }

    private bool IsCurrentMotion(CancellationTokenSource motionCts)
    {
        lock (_sync)
        {
            return ReferenceEquals(_motionCts, motionCts);
        }
    }

    private string AbortReason()
    {
        lock (_sync)
        {
            return _abortReason ?? "運動被中止";
        }
    }

    private void EnsureReadyLocked()
    {
        switch (_state)
        {
            case AxisState.Alarm:
                throw new MotionFaultException(Name, $"報警中：{_alarm}");
            case AxisState.Disabled:
                throw new MotionFaultException(Name, "未使能");
            case AxisState.Moving or AxisState.Homing:
                throw new MotionFaultException(Name, "軸正在運動");
        }
    }

    private double CurrentPositionLocked() =>
        _segment is { } segment ? segment.PositionAt(_time.GetElapsedTime(_segmentStart)) : _position;

    private sealed record MotionPlan(
        TimeSpan Duration,
        TrapezoidProfile? Segment,
        double FinalPosition,
        bool Homes,
        string? Fault);
}
