using System.Globalization;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 雷賽軸的公共實現（DMC 總線軸、SMC 脈衝軸共用）。安全相關的邏輯只在這裡寫一份：
/// <list type="bullet">
/// <item>運動、回零、停止共用一把發令鎖：取消後不會有晚到的指令覆蓋停止。</item>
/// <item>發令鎖只在下發指令時持有，等待到位時釋放，停止隨時能插進來。</item>
/// <item>到位判定：check_done 返回 1 且位置在容差內；停了但沒到位視為被外部停止，報故障。</item>
/// <item>回零完成以 get_home_result == 1 為準，原點信號不能代替回零結果。</item>
/// <item>運動中 ALM / EMG 有效立即急停並報故障；超時減速停止並報故障。</item>
/// </list>
/// 子類只實現各系列不同的部分：使能、回零參數、報警復位。
/// </summary>
public abstract class LeadshineAxisBase : IAxis, IAxisSafetyPort, IDisposable
{
    protected const double ReadbackTolerance = 1e-6;
    private const ushort EmergencyLogicLow = 0;  // emg_logic：0 低電平有效
    private const ushort EmergencyLogicHigh = 1; // 1 高電平有效

    private readonly ILeadshineMotionApi _api;
    private readonly LeadshineAxisOptions _options;
    private readonly SemaphoreSlim _motionCommandGate = new(1, 1);
    private readonly object _sync = new();
    private bool _enabled;
    private bool _homed;
    private bool _homing;
    private AxisSafetyConfig _softwareSafety = new();

    protected LeadshineAxisBase(ILeadshineMotionApi api, LeadshineAxisOptions options, string apiPrefix,
        TimeProvider? timeProvider, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        _api = api;
        _options = options;
        ApiPrefix = apiPrefix;
        Time = timeProvider ?? TimeProvider.System;
        Guard = new LeadshineCallGuard(apiPrefix.ToUpperInvariant(), options.CardNo, options.AxisNo, options.Name, log);
    }

    public string Name => _options.Name;

    /// <summary>函數名前綴：dmc 或 smc，用於日誌裡的 ApiName。</summary>
    protected string ApiPrefix { get; }

    protected TimeProvider Time { get; }

    internal LeadshineCallGuard Guard { get; }

    protected ushort Card => _options.CardNo;

    protected ushort Axis => _options.AxisNo;

    /// <summary>
    /// 下發基礎參數（脈衝當量、S 段時間，以及各系列自己的信號配置）。必須在安全初始化和使能之前調用。
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

        Guard.EnsureSuccess(Api("set_equiv"), _api.SetPulseEquivalent(Card, Axis, equiv), $"equiv={equiv:R}", stage);
        var result = _api.GetPulseEquivalent(Card, Axis, out var actual);
        Guard.EnsureSuccess(Api("get_equiv"), result, $"equiv={equiv:R}", stage, $"equiv={actual:R}");
        Guard.EnsureReadback(Api("get_equiv"), Math.Abs(actual - equiv) <= ReadbackTolerance,
            $"equiv={equiv:R}", $"equiv={actual:R}", stage);

        Guard.EnsureSuccess(Api("set_s_profile"), _api.SetSProfile(Card, Axis, _options.STime),
            $"sTime={_options.STime:R}", stage);

        ApplySignalParameters();
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
                $"狀態讀取失敗：{Api("get_position_unit")}={positionResult}, {Api("axis_io_status")}={ioResult}, " +
                $"{Api("check_done")}={done}");
        }

        var io = LeadshineAxisIo.Parse(rawIo);
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
            await EnableCoreAsync().ConfigureAwait(false);
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
            lock (_sync)
            {
                _enabled = false;
            }

            await DisableCoreAsync().ConfigureAwait(false);
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
            Guard.EnsureSuccess(Api("set_profile_unit"),
                _api.SetProfile(Card, Axis, minVelocity, profile.Velocity, tacc, tdec, stopVelocity),
                $"min={minVelocity:R}, max={profile.Velocity:R}, tacc={tacc:R}, tdec={tdec:R}, stop={stopVelocity:R}",
                "Move");

            // 取消檢查放在下發前最後一步：取消完成後不能再有運動指令下發
            cancellationToken.ThrowIfCancellationRequested();
            Guard.EnsureSuccess(Api("pmove_unit"), _api.MoveAbsolute(Card, Axis, position),
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
        var leadshineMode = mode == StopMode.Immediate ? LeadshineStopMode.Immediate : LeadshineStopMode.Decelerated;
        // 停止不可取消；與發令共用鎖，保證停止排在已下發的指令之後
        await _motionCommandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Guard.EnsureSuccess(Api("stop"), _api.StopAxis(Card, Axis, leadshineMode),
                $"stop_mode={(ushort)leadshineMode}", "Stop");
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public async Task HomeAsync(CancellationToken cancellationToken = default)
    {
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadyForMotion();
            lock (_sync)
            {
                _homed = false;
            }

            // 回零參數下發並回讀核對，不一致則不回零
            ConfigureAndVerifyHome();

            cancellationToken.ThrowIfCancellationRequested();
            Guard.EnsureSuccess(Api("home_move"), StartHome(), "home=start", "Home");
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
            await WaitUntilAsync("Home", _options.HomeTimeout, _ => IsHomeFinished(), cancellationToken)
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
            // 報警後原點不再可信；復位後需要重新使能並回零
            lock (_sync)
            {
                _enabled = false;
                _homed = false;
            }

            await ResetAlarmCoreAsync(cancellationToken).ConfigureAwait(false);
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
        Guard.EnsureSuccess(Api("set_emg_mode"),
            _api.SetEmergencyStopMode(Card, Axis, config.EmergencyStopEnabled, logic),
            $"enable={config.EmergencyStopEnabled}, logic={logic}", stage);

        var softLimit = ExpectedSoftLimit(config);
        Guard.EnsureSuccess(Api("set_softlimit_unit"), _api.SetSoftLimit(Card, Axis, softLimit), Describe(softLimit),
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
        Guard.EnsureSuccess(Api("get_emg_mode"), result, "read", stage, $"enable={emgEnabled}, logic={logic}");

        result = _api.GetSoftLimit(Card, Axis, out var softLimit);
        Guard.EnsureSuccess(Api("get_softlimit_unit"), result, "read", stage, Describe(softLimit));
        // 計數源和觸發動作不在通用安全配置裡，在這裡單獨核對
        var expectedSoftLimit = ExpectedSoftLimit(written);
        Guard.EnsureReadback(Api("get_softlimit_unit"),
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motionCommandGate.Dispose();
        }
    }

    /// <summary>使能伺服。失敗必須拋出異常。在發令鎖內調用。</summary>
    protected abstract Task EnableCoreAsync();

    /// <summary>斷開伺服使能。在發令鎖內調用，調用前軸已停止。</summary>
    protected abstract Task DisableCoreAsync();

    /// <summary>下發回零參數並回讀核對，不一致必須拋出異常。在發令鎖內調用。</summary>
    protected abstract void ConfigureAndVerifyHome();

    /// <summary>啟動回零，返回雷賽錯誤碼。在發令鎖內調用。</summary>
    protected abstract short StartHome();

    /// <summary>讀取回零結果（1 表示本次回零完成），返回雷賽錯誤碼。</summary>
    protected abstract short ReadHomeResult(out ushort state);

    /// <summary>清除伺服報警。在發令鎖內調用。</summary>
    protected abstract Task ResetAlarmCoreAsync(CancellationToken cancellationToken);

    /// <summary>各系列自己的信號配置（例如脈衝軸的 ALM、EL 電平），在 <see cref="ApplyBaseParameters"/> 末尾調用。</summary>
    protected virtual void ApplySignalParameters()
    {
    }

    /// <summary>ALM 有效時的報警描述。總線軸可以讀出驅動器錯誤碼。</summary>
    protected virtual string DescribeServoAlarm() => "伺服報警 ALM";

    protected string Api(string function) => $"{ApiPrefix}_{function}";

    protected static string Describe(LeadshineSoftLimit s) =>
        $"enable={s.Enabled}, source={s.Source}, action={s.Action}, n={s.Negative:R}, p={s.Positive:R}";

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
        var start = Time.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(_options.PollInterval, Time, cancellationToken).ConfigureAwait(false);
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

                if (Time.GetElapsedTime(start) > timeout)
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
        var result = ReadHomeResult(out var state);
        if (result != 0 || state is not (0 or 1))
        {
            throw new LeadshineCallException(Name, Api("get_home_result"), result,
                $"回零結果讀取失敗：錯誤碼 {result}，狀態 {state}");
        }

        // 1 才表示本次回零完成，並且要等軸真正停下來
        return state == 1 && _api.CheckDone(Card, Axis) == 1;
    }

    private LeadshineSoftLimit ExpectedSoftLimit(AxisSafetyConfig config) =>
        new(true, _options.SoftLimitSource, _options.SoftLimitAction, config.SoftLimitMin, config.SoftLimitMax);
}
