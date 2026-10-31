namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>
/// 雷賽 DMC 總線軸（EtherCAT，nmc_* 系列）。運動、停止、安全參數等公共邏輯在 <see cref="LeadshineAxisBase"/>。
/// </summary>
public sealed class DmcAxis : LeadshineAxisBase
{
    private const short HomeProfileCompatCode = 16088;

    private readonly IDmcNativeApi _api;
    private readonly DmcAxisOptions _options;

    public DmcAxis(IDmcNativeApi api, DmcAxisOptions options, TimeProvider? timeProvider = null,
        Action<string>? log = null)
        : base(api, options, "dmc", timeProvider, log)
    {
        _api = api;
        _options = options;
        if (options.HomeMethod == LeadshineHomeMethod.Sensor && options.HomeProfile is null)
        {
            throw new ArgumentException($"{options.Name}：感應器回零未配置回零參數（HomeProfile）");
        }
    }

    protected override Task EnableCoreAsync()
    {
        Guard.EnsureSuccess("nmc_set_axis_enable", _api.EnableAxis(Card, Axis), "enabled=true", "Enable");
        return Task.CompletedTask;
    }

    protected override Task DisableCoreAsync()
    {
        Guard.EnsureSuccess("nmc_set_axis_disable", _api.DisableAxis(Card, Axis), "enabled=false", "Disable");
        return Task.CompletedTask;
    }

    protected override Task ResetAlarmCoreAsync(CancellationToken cancellationToken)
    {
        // 驅動器報警清除後處於故障復位狀態，需要重新使能並回零
        Guard.EnsureSuccess("nmc_clear_axis_errcode", _api.ClearAxisErrorCode(Card, Axis), "errcode=0", "Reset");
        return Task.CompletedTask;
    }

    protected override void ConfigureAndVerifyHome()
    {
        const string stage = "Home";
        // 只有感應器回零會走到這裡，構造時已確認不為 null
        var expected = _options.HomeProfile!.Value;
        var result = _api.SetHomeProfile(Card, Axis, expected);
        if (result == HomeProfileCompatCode)
        {
            // 部分總線固件對該調用返回 16088 但參數實際已生效，沿用現場兼容處理，以下面的回讀為準
            Guard.Warn("nmc_set_home_profile", result, Describe(expected), stage, "按兼容碼處理，以回讀結果為準");
        }
        else
        {
            Guard.EnsureSuccess("nmc_set_home_profile", result, Describe(expected), stage);
        }

        result = _api.GetHomeProfile(Card, Axis, out var actual);
        Guard.EnsureSuccess("nmc_get_home_profile", result, Describe(expected), stage, Describe(actual));
        Guard.EnsureReadback("nmc_get_home_profile", Same(expected, actual), Describe(expected), Describe(actual),
            stage);
    }

    protected override short StartHome() => _api.HomeMove(Card, Axis);

    protected override short ReadHomeResult(out ushort state) => _api.GetHomeResult(Card, Axis, out state);

    protected override string DescribeServoAlarm()
    {
        var result = _api.GetAxisErrorCode(Card, Axis, out var errorCode);
        return result == 0 ? $"伺服報警 ALM，驅動器錯誤碼 0x{errorCode:X4}" : "伺服報警 ALM（錯誤碼讀取失敗）";
    }

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
}
