using System.Globalization;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Motion.Dmc;

/// <summary>
/// 雷赛 DMC 总线轴（EtherCAT，nmc_* 系列）的 <see cref="IAxis"/> 实现。
/// <list type="bullet">
/// <item>运动、回零、停止共用一把发令锁：取消后不会有晚到的指令覆盖停止。</item>
/// <item>发令锁只在下发指令时持有，等待到位时释放，停止随时能插进来。</item>
/// <item>到位判定：dmc_check_done 返回 1 且位置在容差内；停了但没到位视为被外部停止，报故障。</item>
/// <item>回零完成以 dmc_get_home_result == 1 为准，原点信号不能代替回零结果。</item>
/// </list>
/// </summary>
public sealed class DmcAxis : IAxis, IAxisSafetyPort, IDisposable
{
    private const short HomeProfileCompatCode = 16088;
    private const double ReadbackTolerance = 1e-6;
    private const ushort EmergencyLogicLow = 0;  // dmc_set_emg_mode emg_logic：0 低电平有效
    private const ushort EmergencyLogicHigh = 1; // 1 高电平有效

    private readonly IDmcNativeApi _api;
    private readonly DmcAxisOptions _options;
    private readonly TimeProvider _time;
    private readonly DmcCallGuard _guard;
    private readonly SemaphoreSlim _motionCommandGate = new(1, 1);
    private readonly object _sync = new();
    private bool _enabled;
    private bool _homed;
    private bool _homing;
    private AxisSafetyConfig _softwareSafety = new();

    public DmcAxis(IDmcNativeApi api, DmcAxisOptions options, TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        _api = api;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _guard = new DmcCallGuard(options.CardNo, options.AxisNo, options.Name, log);
    }

    public string Name => _options.Name;

    private ushort Card => _options.CardNo;

    private ushort Axis => _options.AxisNo;

    /// <summary>
    /// 下发基础参数（脉冲当量、S 段时间）。必须在安全初始化和使能之前调用。
    /// 脉冲当量错了，所有距离都会按比例走错，所以写入后必须回读。
    /// </summary>
    public void ApplyBaseParameters()
    {
        const string stage = "BaseParameters";
        var equiv = _options.PulseEquivalent;
        if (!double.IsFinite(equiv) || equiv <= 0)
        {
            throw new ArgumentException($"{Name}：脉冲当量非法 {equiv}");
        }

        _guard.EnsureSuccess("dmc_set_equiv", _api.SetPulseEquivalent(Card, Axis, equiv), $"equiv={equiv:R}", stage);
        var result = _api.GetPulseEquivalent(Card, Axis, out var actual);
        _guard.EnsureSuccess("dmc_get_equiv", result, $"equiv={equiv:R}", stage, $"equiv={actual:R}");
        _guard.EnsureReadback("dmc_get_equiv", Math.Abs(actual - equiv) <= ReadbackTolerance,
            $"equiv={equiv:R}", $"equiv={actual:R}", stage);

        _guard.EnsureSuccess("dmc_set_s_profile", _api.SetSProfile(Card, Axis, _options.STime),
            $"sTime={_options.STime:R}", stage);
    }

    public AxisStatus GetStatus()
    {
        var positionResult = _api.ReadPosition(Card, Axis, out var position);
        var ioResult = _api.ReadAxisIoStatus(Card, Axis, out var rawIo);
        var done = _api.CheckDone(Card, Axis);
        bool enabled, homed, homing;
        lock (_sync)
        {
            (enabled, homed, homing) = (_enabled, _homed, _homing);
        }

        if (positionResult != 0 || ioResult != 0 || done is not (0 or 1))
        {
            return new AxisStatus(AxisState.Alarm, position, homed, false, false,
                $"状态读取失败：dmc_get_position_unit={positionResult}, dmc_axis_io_status={ioResult}, dmc_check_done={done}");
        }

        var io = DmcAxisIo.Parse(rawIo);
        var alarm = io.Alarm ? DescribeServoAlarm() : io.EmergencyStop ? "急停 EMG 有效" : null;
        var state = alarm != null ? AxisState.Alarm
            : !enabled ? AxisState.Disabled
            : homing ? AxisState.Homing
            : done == 0 ? AxisState.Moving
            : AxisState.Idle;
        return new AxisStatus(state, position, homed, io.PositiveLimit, io.NegativeLimit, alarm);
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = GetStatus();
            if (status.State == AxisState.Alarm)
            {
                throw new MotionFaultException(Name, $"报警中，需先复位：{status.AlarmReason}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _guard.EnsureSuccess("nmc_set_axis_enable", _api.EnableAxis(Card, Axis), "enabled=true", "Enable");
            lock (_sync)
            {
                _enabled = true;
            }
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        // 先停再失能：直接断使能，垂直轴可能下坠
        await StopAsync().ConfigureAwait(false);
        await _motionCommandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _guard.EnsureSuccess("nmc_set_axis_disable", _api.DisableAxis(Card, Axis), "enabled=false", "Disable");
            lock (_sync)
            {
                _enabled = false;
            }
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public async Task MoveAbsoluteAsync(double position, MotionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (!double.IsFinite(position))
        {
            throw new ArgumentException($"目标位置非法：{position}", nameof(position));
        }

        // 雷赛用加减速时间，不是加速度
        var tacc = profile.Velocity / profile.Acceleration;
        var tdec = profile.Velocity / profile.Deceleration;
        var minVelocity = Math.Min(_options.MinVelocity, profile.Velocity);
        var stopVelocity = Math.Min(_options.StopVelocity, profile.Velocity);

        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadyForMotion();
            _guard.EnsureSuccess("dmc_set_profile_unit",
                _api.SetProfile(Card, Axis, minVelocity, profile.Velocity, tacc, tdec, stopVelocity),
                $"min={minVelocity:R}, max={profile.Velocity:R}, tacc={tacc:R}, tdec={tdec:R}, stop={stopVelocity:R}",
                "Move");

            // 取消检查放在下发前最后一步：取消完成后不能再有运动指令下发
            cancellationToken.ThrowIfCancellationRequested();
            _guard.EnsureSuccess("dmc_pmove_unit", _api.MoveAbsolute(Card, Axis, position),
                $"target={position:R}, posi_mode=1", "Move");
        }
        finally
        {
            _motionCommandGate.Release();
        }

        await WaitUntilAsync("Move", _options.MotionTimeout, status => status.State == AxisState.Idle,
            cancellationToken).ConfigureAwait(false);

        var actual = GetStatus().Position;
        if (Math.Abs(actual - position) > _options.InPositionTolerance)
        {
            throw new MotionFaultException(Name,
                $"运动已停止但未到位：目标 {position:R}，实际 {actual:R}（可能被外部停止）");
        }
    }

    public async Task StopAsync(StopMode mode = StopMode.Decelerate)
    {
        var dmcMode = mode == StopMode.Immediate ? DmcStopMode.Immediate : DmcStopMode.Decelerated;
        // 停止不可取消；与发令共用锁，保证停止排在已下发的指令之后
        await _motionCommandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _guard.EnsureSuccess("dmc_stop", _api.StopAxis(Card, Axis, dmcMode),
                $"stop_mode={(ushort)dmcMode}", "Stop");
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public async Task HomeAsync(CancellationToken cancellationToken = default)
    {
        const string stage = "Home";
        var expected = _options.HomeProfile;

        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadyForMotion();
            lock (_sync)
            {
                _homed = false;
            }

            var result = _api.SetHomeProfile(Card, Axis, expected);
            if (result == HomeProfileCompatCode)
            {
                // 部分总线固件对该调用返回 16088 但参数实际已生效，沿用现场兼容处理，以下面的回读为准
                _guard.Warn("nmc_set_home_profile", result, Describe(expected), stage, "按兼容码处理，以回读结果为准");
            }
            else
            {
                _guard.EnsureSuccess("nmc_set_home_profile", result, Describe(expected), stage);
            }

            result = _api.GetHomeProfile(Card, Axis, out var actual);
            _guard.EnsureSuccess("nmc_get_home_profile", result, Describe(expected), stage, Describe(actual));
            _guard.EnsureReadback("nmc_get_home_profile", Same(expected, actual), Describe(expected),
                Describe(actual), stage);

            cancellationToken.ThrowIfCancellationRequested();
            _guard.EnsureSuccess("dmc_home_move", _api.HomeMove(Card, Axis), "home=start", stage);
            lock (_sync)
            {
                _homing = true;
            }
        }
        finally
        {
            _motionCommandGate.Release();
        }

        try
        {
            await WaitUntilAsync(stage, _options.HomeTimeout, _ => IsHomeFinished(), cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _homed = true;
            }
        }
        finally
        {
            lock (_sync)
            {
                _homing = false;
            }
        }
    }

    public async Task ResetAlarmAsync(CancellationToken cancellationToken = default)
    {
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _guard.EnsureSuccess("nmc_clear_axis_errcode", _api.ClearAxisErrorCode(Card, Axis), "errcode=0", "Reset");
            // 驱动器报警后原点不再可信；清除后需要重新使能并回零
            lock (_sync)
            {
                _enabled = false;
                _homed = false;
            }
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public Task WriteSafetyConfigAsync(AxisSafetyConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        if (config.EmergencyStopEnabled && config.EmergencyStopActiveHigh is null)
        {
            // 急停电平不猜默认值
            throw new ArgumentException($"{Name}：已启用急停，但未指定急停有效电平");
        }

        const string stage = "Safety";
        var logic = config.EmergencyStopActiveHigh == true ? EmergencyLogicHigh : EmergencyLogicLow;
        _guard.EnsureSuccess("dmc_set_emg_mode",
            _api.SetEmergencyStopMode(Card, Axis, config.EmergencyStopEnabled, logic),
            $"enable={config.EmergencyStopEnabled}, logic={logic}", stage);

        var softLimit = ExpectedSoftLimit(config);
        _guard.EnsureSuccess("dmc_set_softlimit_unit", _api.SetSoftLimit(Card, Axis, softLimit), Describe(softLimit),
            stage);

        lock (_sync)
        {
            _softwareSafety = config;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 急停和软限位从控制卡回读。最大速度、最大加速度在雷赛卡上没有对应参数，
    /// 由 GuardedAxis 在软件层限制，这里返回写入值。
    /// </summary>
    public Task<AxisSafetyConfig> ReadSafetyConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string stage = "SafetyReadback";
        AxisSafetyConfig written;
        lock (_sync)
        {
            written = _softwareSafety;
        }

        var result = _api.GetEmergencyStopMode(Card, Axis, out var emgEnabled, out var logic);
        _guard.EnsureSuccess("dmc_get_emg_mode", result, "read", stage, $"enable={emgEnabled}, logic={logic}");

        result = _api.GetSoftLimit(Card, Axis, out var softLimit);
        _guard.EnsureSuccess("dmc_get_softlimit_unit", result, "read", stage, Describe(softLimit));
        // 计数源和触发动作不在通用安全配置里，在这里单独核对
        var expectedSoftLimit = ExpectedSoftLimit(written);
        _guard.EnsureReadback("dmc_get_softlimit_unit",
            softLimit.Source == expectedSoftLimit.Source && softLimit.Action == expectedSoftLimit.Action,
            Describe(expectedSoftLimit), Describe(softLimit), stage);

        return Task.FromResult(written with
        {
            SoftLimitMin = softLimit.Enabled ? softLimit.Negative : double.NaN,
            SoftLimitMax = softLimit.Enabled ? softLimit.Positive : double.NaN,
            EmergencyStopEnabled = emgEnabled,
            EmergencyStopActiveHigh = logic switch
            {
                EmergencyLogicLow => false,
                EmergencyLogicHigh => true,
                _ => null,
            },
        });
    }

    public void Dispose() => _motionCommandGate.Dispose();

    private void EnsureReadyForMotion()
    {
        var status = GetStatus();
        switch (status.State)
        {
            case AxisState.Alarm:
                throw new MotionFaultException(Name, $"报警中：{status.AlarmReason}");
            case AxisState.Disabled:
                throw new MotionFaultException(Name, "未使能");
            case AxisState.Moving or AxisState.Homing:
                throw new MotionFaultException(Name, "轴正在运动");
        }
    }

    /// <summary>
    /// 轮询直到完成。期间出现报警立即急停并报故障；超时减速停止并报故障；
    /// 调用方取消时先停轴，再抛出取消。
    /// </summary>
    private async Task WaitUntilAsync(string stage, TimeSpan timeout, Func<AxisStatus, bool> finished,
        CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                var status = GetStatus();
                if (status.State == AxisState.Alarm)
                {
                    await StopAsync(StopMode.Immediate).ConfigureAwait(false);
                    throw new MotionFaultException(Name, $"{stage} 过程中报警：{status.AlarmReason}");
                }

                if (status.PositiveLimit || status.NegativeLimit)
                {
                    // 限位由控制卡自行停轴，这里只负责报出来
                    throw new MotionFaultException(Name, $"{stage} 过程中触发{(status.PositiveLimit ? "正" : "负")}限位");
                }

                if (finished(status))
                {
                    return;
                }

                if (_time.GetElapsedTime(start) > timeout)
                {
                    await StopAsync().ConfigureAwait(false);
                    throw new MotionFaultException(Name,
                        $"{stage} 超时（{timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒），已减速停止");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 已下发的运动被取消：先停轴，再报告取消
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool IsHomeFinished()
    {
        var result = _api.GetHomeResult(Card, Axis, out var state);
        if (result != 0 || state is not (0 or 1))
        {
            throw new DmcCallException(Name, "dmc_get_home_result", result,
                $"回零结果读取失败：错误码 {result}，状态 {state}");
        }

        // 1 才表示本次回零完成，并且要等轴真正停下来
        return state == 1 && _api.CheckDone(Card, Axis) == 1;
    }

    private string DescribeServoAlarm()
    {
        var result = _api.GetAxisErrorCode(Card, Axis, out var errorCode);
        return result == 0 ? $"伺服报警 ALM，驱动器错误码 0x{errorCode:X4}" : "伺服报警 ALM（错误码读取失败）";
    }

    private DmcSoftLimit ExpectedSoftLimit(AxisSafetyConfig config) =>
        new(true, _options.SoftLimitSource, _options.SoftLimitAction, config.SoftLimitMin, config.SoftLimitMax);

    private static bool Same(DmcHomeProfile a, DmcHomeProfile b) =>
        a.Mode == b.Mode &&
        Math.Abs(a.LowVelocity - b.LowVelocity) <= ReadbackTolerance &&
        Math.Abs(a.HighVelocity - b.HighVelocity) <= ReadbackTolerance &&
        Math.Abs(a.AccelerationTime - b.AccelerationTime) <= ReadbackTolerance &&
        Math.Abs(a.DecelerationTime - b.DecelerationTime) <= ReadbackTolerance &&
        Math.Abs(a.Offset - b.Offset) <= ReadbackTolerance;

    private static string Describe(DmcHomeProfile p) =>
        $"mode={p.Mode}, low={p.LowVelocity:R}, high={p.HighVelocity:R}, tacc={p.AccelerationTime:R}, " +
        $"tdec={p.DecelerationTime:R}, offset={p.Offset:R}";

    private static string Describe(DmcSoftLimit s) =>
        $"enable={s.Enabled}, source={s.Source}, action={s.Action}, n={s.Negative:R}, p={s.Positive:R}";
}
