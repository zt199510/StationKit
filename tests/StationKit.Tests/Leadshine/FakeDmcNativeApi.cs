using StationKit.Motion.Leadshine.Dmc;

namespace StationKit.Tests.Leadshine;

/// <summary>假 DMC：公共部分之外，模擬板卡連接、nmc_* 使能和回零參數。</summary>
internal sealed class FakeDmcNativeApi() : FakeLeadshineMotionApi("dmc"), IDmcNativeApi
{
    private DmcHomeProfile _homeProfile;

    public ushort AxisErrorCode { get; set; }

    public Func<DmcHomeProfile, DmcHomeProfile>? HomeProfileReadback { get; set; }

    public short BoardClose() => Record("dmc_board_close");

    public short BoardInit() => Record("dmc_board_init");

    public short BoardInitEthernet(ushort cardNo, string ipAddress) => Record("dmc_board_init_eth");

    public short GetTotalAxes(ushort cardNo, out uint totalAxes)
    {
        totalAxes = 4;
        return Record("nmc_get_total_axes");
    }

    public short EnableAxis(ushort cardNo, ushort axisNo) => Record("nmc_set_axis_enable");

    public short DisableAxis(ushort cardNo, ushort axisNo) => Record("nmc_set_axis_disable");

    public short ClearAxisErrorCode(ushort cardNo, ushort axisNo)
    {
        IoStatus &= ~1u;
        return Record("nmc_clear_axis_errcode");
    }

    public short GetAxisErrorCode(ushort cardNo, ushort axisNo, out ushort errorCode)
    {
        errorCode = AxisErrorCode;
        return 0;
    }

    public short SetHomeProfile(ushort cardNo, ushort axisNo, DmcHomeProfile profile)
    {
        _homeProfile = profile;
        return Record("nmc_set_home_profile");
    }

    public short GetHomeProfile(ushort cardNo, ushort axisNo, out DmcHomeProfile profile)
    {
        profile = HomeProfileReadback?.Invoke(_homeProfile) ?? _homeProfile;
        return Record("nmc_get_home_profile");
    }

    public short HomeMove(ushort cardNo, ushort axisNo)
    {
        var result = Record("dmc_home_move");
        if (result == 0)
        {
            StartHoming();
        }

        return result;
    }

    public short GetHomeResult(ushort cardNo, ushort axisNo, out ushort state)
    {
        state = TakeHomeResult();
        return 0;
    }
}
