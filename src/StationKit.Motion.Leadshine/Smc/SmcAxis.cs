using StationKit.Abstractions.Motion;

namespace StationKit.Motion.Leadshine.Smc;

/// <summary>
/// 雷賽 SMC 脈衝軸（脈衝 + 方向，伺服通過 SEVON、ALM、ERC 等專用 IO 連到控制器）。
/// 運動、停止、安全參數等公共邏輯在 <see cref="LeadshineAxisBase"/>；這裡只處理脈衝軸特有的部分：
/// <list type="bullet">
/// <item>使能是寫 SEVON 電平，寫完回讀，回讀不一致視為使能失敗。</item>
/// <item>ALM、EL 的電氣配置和回零參數每次啟動都重新下發並回讀，不依賴控制卡裡固化的參數。</item>
/// <item>報警復位需要接線支持：未配置復位輸出時直接報故障，不假裝復位成功。</item>
/// </list>
/// </summary>
public sealed class SmcAxis : LeadshineAxisBase
{
    private readonly ISmcNativeApi _api;
    private readonly SmcAxisOptions _options;

    public SmcAxis(ISmcNativeApi api, SmcAxisOptions options, TimeProvider? timeProvider = null,
        Action<string>? log = null)
        : base(api, options, "smc", timeProvider, log)
    {
        _api = api;
        _options = options;
        if (options.ServoOnLevel > 1)
        {
            throw new ArgumentException($"{options.Name}：SEVON 使能電平只能是 0 或 1，實際 {options.ServoOnLevel}");
        }

        if (options.AlarmReset is { ActiveLevel: > 1 } reset)
        {
            throw new ArgumentException($"{options.Name}：報警復位有效電平只能是 0 或 1，實際 {reset.ActiveLevel}");
        }
    }

    private delegate short Getter<T>(out T value);

    private ushort ServoOffLevel => (ushort)(1 - _options.ServoOnLevel);

    protected override async Task EnableCoreAsync()
    {
        await WriteServoAsync(_options.ServoOnLevel, "Enable").ConfigureAwait(false);
    }

    protected override async Task DisableCoreAsync()
    {
        await WriteServoAsync(ServoOffLevel, "Disable").ConfigureAwait(false);
    }

    protected override void ApplySignalParameters()
    {
        const string stage = "BaseParameters";
        SetAndVerify("alm_mode", _options.AlarmSignal, m => _api.SetAlarmMode(Card, Axis, m),
            (out SmcSignalMode m) => _api.GetAlarmMode(Card, Axis, out m), (a, b) => a == b, Describe, stage);
        SetAndVerify("el_mode", _options.LimitSignal, m => _api.SetLimitMode(Card, Axis, m),
            (out SmcSignalMode m) => _api.GetLimitMode(Card, Axis, out m), (a, b) => a == b, Describe, stage);
    }

    protected override void ConfigureAndVerifyHome()
    {
        const string stage = "Home";
        SetAndVerify("homemode", _options.HomeMode, m => _api.SetHomeMode(Card, Axis, m),
            (out SmcHomeMode m) => _api.GetHomeMode(Card, Axis, out m), Same, Describe, stage);
        SetAndVerify("home_profile_unit", _options.HomeSpeed, s => _api.SetHomeSpeed(Card, Axis, s),
            (out SmcHomeSpeed s) => _api.GetHomeSpeed(Card, Axis, out s), Same, Describe, stage);
        SetAndVerify("home_pin_logic", _options.HomePinLogic, l => _api.SetHomePinLogic(Card, Axis, l),
            (out SmcHomePinLogic l) => _api.GetHomePinLogic(Card, Axis, out l), Same, Describe, stage);
        SetAndVerify("home_position_unit", _options.HomePosition, p => _api.SetHomePosition(Card, Axis, p),
            (out SmcHomePosition p) => _api.GetHomePosition(Card, Axis, out p), Same, Describe, stage);
    }

    protected override short StartHome() => _api.HomeMove(Card, Axis);

    /// <summary>
    /// 回零後編碼器計數不一定跟著清零（取決於回零方式和計數源），這裡統一把編碼器對齊到指令位置，
    /// 不依賴卡的行為。沿用舊代碼裡「反饋坐標清零」的做法。
    /// </summary>
    protected override void OnHomeCompleted()
    {
        if (_options.Feedback == LeadshineFeedback.Encoder)
        {
            AlignFeedbackToCommand("Home");
        }
    }

    protected override short ReadHomeResult(out ushort state) => _api.GetHomeResult(Card, Axis, out state);

    protected override async Task ResetAlarmCoreAsync(CancellationToken cancellationToken)
    {
        const string stage = "Reset";
        var reset = _options.AlarmReset
            ?? throw new MotionFaultException(Name, "脈衝軸未配置報警復位輸出，需到驅動器上手動清除報警");

        // 先斷使能再清報警：多數驅動器在使能狀態下不響應報警清除，且清除瞬間不能帶著使能
        await WriteServoAsync(ServoOffLevel, stage).ConfigureAwait(false);

        var inactiveLevel = (ushort)(1 - reset.ActiveLevel);
        WriteReset(reset, reset.ActiveLevel, stage);
        try
        {
            await Task.Delay(reset.PulseWidth, Time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 無論是否取消，復位信號都必須撤銷，否則驅動器一直處於清除狀態
            WriteReset(reset, inactiveLevel, stage);
        }

        var start = Time.GetTimestamp();
        while (ReadAlarm())
        {
            if (Time.GetElapsedTime(start) > reset.ClearTimeout)
            {
                throw new MotionFaultException(Name,
                    $"已輸出報警復位信號，{reset.ClearTimeout.TotalMilliseconds:0} ms 內 ALM 仍未消失，需檢查驅動器");
            }

            await Task.Delay(_options.PollInterval, Time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteServoAsync(ushort level, string stage)
    {
        var expected = $"sevon={level}";
        Guard.EnsureSuccess(Api("write_sevon_pin"), _api.WriteServoOn(Card, Axis, level), expected, stage);
        // 輸出口需要一點時間翻轉，馬上回讀可能讀到舊值
        await Task.Delay(_options.ServoSettleTime, Time).ConfigureAwait(false);
        var actual = _api.ReadServoOn(Card, Axis);
        Guard.EnsureReadback(Api("read_sevon_pin"), actual == level, expected, $"sevon={actual}", stage);
    }

    private void WriteReset(SmcAlarmReset reset, ushort level, string stage)
    {
        if (reset.Source == SmcAlarmResetSource.ErcPin)
        {
            Guard.EnsureSuccess(Api("write_erc_pin"), _api.WriteErcPin(Card, Axis, level), $"erc={level}", stage);
        }
        else
        {
            Guard.EnsureSuccess(Api("write_outbit"), _api.WriteOutputBit(Card, reset.OutputBit, level),
                $"bit={reset.OutputBit}, level={level}", stage);
        }
    }

    private bool ReadAlarm()
    {
        var result = _api.ReadAxisIoStatus(Card, Axis, out var raw);
        Guard.EnsureSuccess(Api("axis_io_status"), result, "read", "Reset", $"0x{raw:X}");
        return LeadshineAxisIo.Parse(raw).Alarm;
    }

    /// <summary>寫入、返回碼檢查、回讀、比對，任何一步不通過都拋出異常。</summary>
    private void SetAndVerify<T>(string function, T expected, Func<T, short> set, Getter<T> get,
        Func<T, T, bool> same, Func<T, string> describe, string stage)
    {
        Guard.EnsureSuccess(Api("set_" + function), set(expected), describe(expected), stage);
        var result = get(out var actual);
        Guard.EnsureSuccess(Api("get_" + function), result, describe(expected), stage, describe(actual));
        Guard.EnsureReadback(Api("get_" + function), same(expected, actual), describe(expected), describe(actual),
            stage);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= ReadbackTolerance;

    private static bool Same(SmcHomeMode a, SmcHomeMode b) =>
        a.Direction == b.Direction && Near(a.VelocityMode, b.VelocityMode) && a.Mode == b.Mode &&
        a.PositionSource == b.PositionSource;

    private static bool Same(SmcHomeSpeed a, SmcHomeSpeed b) =>
        Near(a.LowVelocity, b.LowVelocity) && Near(a.HighVelocity, b.HighVelocity) &&
        Near(a.AccelerationTime, b.AccelerationTime) && Near(a.DecelerationTime, b.DecelerationTime);

    private static bool Same(SmcHomePinLogic a, SmcHomePinLogic b) => a.Logic == b.Logic && Near(a.Filter, b.Filter);

    private static bool Same(SmcHomePosition a, SmcHomePosition b) =>
        a.Enabled == b.Enabled && Near(a.Position, b.Position);

    private static string Describe(SmcSignalMode m) => $"enable={m.Enabled}, logic={m.Logic}, action={m.Action}";

    private static string Describe(SmcHomeMode m) =>
        $"dir={m.Direction}, vel_mode={m.VelocityMode:R}, mode={m.Mode}, pos_source={m.PositionSource}";

    private static string Describe(SmcHomeSpeed s) =>
        $"low={s.LowVelocity:R}, high={s.HighVelocity:R}, tacc={s.AccelerationTime:R}, tdec={s.DecelerationTime:R}";

    private static string Describe(SmcHomePinLogic l) => $"org_logic={l.Logic}, filter={l.Filter:R}";

    private static string Describe(SmcHomePosition p) => $"enable={p.Enabled}, position={p.Position:R}";
}
