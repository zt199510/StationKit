using System.Globalization;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Motion.Dmc;

/// <summary>
/// 雷賽 DMC 總線軸（EtherCAT，nmc_* 系列）的 <see cref="IAxis"/> 實現。
/// <list type="bullet">
/// <item>運動、回零、停止共用一把發令鎖：取消後不會有晚到的指令覆蓋停止。</item>
/// <item>發令鎖只在下發指令時持有，等待到位時釋放，停止隨時能插進來。</item>
/// <item>到位判定：dmc_check_done 返回 1 且位置在容差內；停了但沒到位視為被外部停止，報故障。</item>
/// <item>回零完成以 dmc_get_home_result == 1 為準，原點信號不能代替回零結果。</item>
/// </list>
/// </summary>
public sealed class DmcAxis : IAxis, IAxisSafetyPort, IDisposable
{
    private const short HomeProfileCompatCode = 16088;
    private const double ReadbackTolerance = 1e-6;
    private const ushort EmergencyLogicLow = 0;  // dmc_set_emg_mode emg_logic：0 低電平有效
    private const ushort EmergencyLogicHigh = 1; // 1 高電平有效

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
    /// 下發基礎參數（脈衝當量、S 段時間）。必須在安全初始化和使能之前調用。
    /// 脈衝當量錯了，所有距離都會按比例走錯，所以寫入後必須回讀。
    /// </summary>
    public void ApplyBaseParameters()
    {
        const string stage = "BaseParameters";
        var equiv = _options.PulseEquivalent;
        if (!double.IsFinite(equiv) || equiv <= 0)
        {
            throw new ArgumentException($"{Name}：脈衝當量非法 {equiv}");
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
                $"狀態讀取失敗：dmc_get_position_unit={positionResult}, dmc_axis_io_status={ioResult}, dmc_check_done={done}");
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
                throw new MotionFaultException(Name, $"報警中，需先復位：{status.AlarmReason}");
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
        // 先停再失能：直接斷使能，垂直軸可能下墜
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
            throw new ArgumentException($"目標位置非法：{position}", nameof(position));
        }

        // 雷賽用加減速時間，不是加速度
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

            // 取消檢查放在下發前最後一步：取消完成後不能再有運動指令下發
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
                $"運動已停止但未到位：目標 {position:R}，實際 {actual:R}（可能被外部停止）");
        }
    }

    public async Task StopAsync(StopMode mode = StopMode.Decelerate)
    {
        var dmcMode = mode == StopMode.Immediate ? DmcStopMode.Immediate : DmcStopMode.Decelerated;
        // 停止不可取消；與發令共用鎖，保證停止排在已下發的指令之後
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
                // 部分總線固件對該調用返回 16088 但參數實際已生效，沿用現場兼容處理，以下面的回讀為準
                _guard.Warn("nmc_set_home_profile", result, Describe(expected), stage, "按兼容碼處理，以回讀結果為準");
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
            // 驅動器報警後原點不再可信；清除後需要重新使能並回零
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
            // 急停電平不猜默認值
            throw new ArgumentException($"{Name}：已啟用急停，但未指定急停有效電平");
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
    /// 急停和軟限位從控制卡回讀。最大速度、最大加速度在雷賽卡上沒有對應參數，
    /// 由 GuardedAxis 在軟件層限制，這裡返回寫入值。
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
        // 計數源和觸發動作不在通用安全配置裡，在這裡單獨核對
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
                throw new MotionFaultException(Name, $"報警中：{status.AlarmReason}");
            case AxisState.Disabled:
                throw new MotionFaultException(Name, "未使能");
            case AxisState.Moving or AxisState.Homing:
                throw new MotionFaultException(Name, "軸正在運動");
        }
    }

    /// <summary>
    /// 輪詢直到完成。期間出現報警立即急停並報故障；超時減速停止並報故障；
    /// 調用方取消時先停軸，再拋出取消。
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
                    throw new MotionFaultException(Name, $"{stage} 過程中報警：{status.AlarmReason}");
                }

                if (status.PositiveLimit || status.NegativeLimit)
                {
                    // 限位由控制卡自行停軸，這裡只負責報出來
                    throw new MotionFaultException(Name, $"{stage} 過程中觸發{(status.PositiveLimit ? "正" : "負")}限位");
                }

                if (finished(status))
                {
                    return;
                }

                if (_time.GetElapsedTime(start) > timeout)
                {
                    await StopAsync().ConfigureAwait(false);
                    throw new MotionFaultException(Name,
                        $"{stage} 超時（{timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒），已減速停止");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 已下發的運動被取消：先停軸，再報告取消
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
                $"回零結果讀取失敗：錯誤碼 {result}，狀態 {state}");
        }

        // 1 才表示本次回零完成，並且要等軸真正停下來
        return state == 1 && _api.CheckDone(Card, Axis) == 1;
    }

    private string DescribeServoAlarm()
    {
        var result = _api.GetAxisErrorCode(Card, Axis, out var errorCode);
        return result == 0 ? $"伺服報警 ALM，驅動器錯誤碼 0x{errorCode:X4}" : "伺服報警 ALM（錯誤碼讀取失敗）";
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
