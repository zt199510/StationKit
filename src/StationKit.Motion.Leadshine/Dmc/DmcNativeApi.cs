using StationKit.Motion.Leadshine.Dmc.Native;

namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>調用真實 LTDMC.dll 的實現。</summary>
public sealed class DmcNativeApi : IDmcNativeApi
{
    private const ushort AbsoluteMode = 1; // dmc_pmove_unit posi_mode：1 絕對坐標
    private const ushort SCurveMode = 0;   // dmc_set_s_profile s_mode：保留參數，固定為 0

    public short BoardClose() => NativeMethods.dmc_board_close();

    public short BoardInit() => NativeMethods.dmc_board_init();

    public short BoardInitEthernet(ushort cardNo, string ipAddress) => NativeMethods.dmc_board_init_eth(cardNo, ipAddress);

    public short GetTotalAxes(ushort cardNo, out uint totalAxes)
    {
        totalAxes = 0;
        return NativeMethods.nmc_get_total_axes(cardNo, ref totalAxes);
    }

    public short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value) =>
        NativeMethods.dmc_set_equiv(cardNo, axisNo, value);

    public short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value)
    {
        value = 0;
        return NativeMethods.dmc_get_equiv(cardNo, axisNo, ref value);
    }

    public short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity) =>
        NativeMethods.dmc_set_profile_unit(cardNo, axisNo, minVelocity, maxVelocity, accelerationTime,
            decelerationTime, stopVelocity);

    public short SetSProfile(ushort cardNo, ushort axisNo, double seconds) =>
        NativeMethods.dmc_set_s_profile(cardNo, axisNo, SCurveMode, seconds);

    public short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic) =>
        NativeMethods.dmc_set_emg_mode(cardNo, axisNo, ToEnable(enabled), logic);

    public short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic)
    {
        ushort rawEnabled = 0;
        logic = 0;
        var result = NativeMethods.dmc_get_emg_mode(cardNo, axisNo, ref rawEnabled, ref logic);
        enabled = rawEnabled != 0;
        return result;
    }

    public short SetSoftLimit(ushort cardNo, ushort axisNo, LeadshineSoftLimit softLimit) =>
        NativeMethods.dmc_set_softlimit_unit(cardNo, axisNo, ToEnable(softLimit.Enabled), softLimit.Source,
            softLimit.Action, softLimit.Negative, softLimit.Positive);

    public short GetSoftLimit(ushort cardNo, ushort axisNo, out LeadshineSoftLimit softLimit)
    {
        ushort enabled = 0, source = 0, action = 0;
        double negative = 0, positive = 0;
        var result = NativeMethods.dmc_get_softlimit_unit(cardNo, axisNo, ref enabled, ref source, ref action,
            ref negative, ref positive);
        softLimit = new LeadshineSoftLimit(enabled != 0, source, action, negative, positive);
        return result;
    }

    public short EnableAxis(ushort cardNo, ushort axisNo) => NativeMethods.nmc_set_axis_enable(cardNo, axisNo);

    public short DisableAxis(ushort cardNo, ushort axisNo) => NativeMethods.nmc_set_axis_disable(cardNo, axisNo);

    public short ClearAxisErrorCode(ushort cardNo, ushort axisNo) => NativeMethods.nmc_clear_axis_errcode(cardNo, axisNo);

    public short GetAxisErrorCode(ushort cardNo, ushort axisNo, out ushort errorCode)
    {
        errorCode = 0;
        return NativeMethods.nmc_get_axis_errcode(cardNo, axisNo, ref errorCode);
    }

    public short MoveAbsolute(ushort cardNo, ushort axisNo, double position) =>
        NativeMethods.dmc_pmove_unit(cardNo, axisNo, position, AbsoluteMode);

    public short CheckDone(ushort cardNo, ushort axisNo) => NativeMethods.dmc_check_done(cardNo, axisNo);

    public short StopAxis(ushort cardNo, ushort axisNo, LeadshineStopMode mode) =>
        NativeMethods.dmc_stop(cardNo, axisNo, (ushort)mode);

    public short EmergencyStop(ushort cardNo) => NativeMethods.dmc_emg_stop(cardNo);

    public short ReadPosition(ushort cardNo, ushort axisNo, out double position)
    {
        position = 0;
        return NativeMethods.dmc_get_position_unit(cardNo, axisNo, ref position);
    }

    public short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status)
    {
        // dmc_axis_io_status 直接返回狀態字，沒有錯誤碼
        status = NativeMethods.dmc_axis_io_status(cardNo, axisNo);
        return 0;
    }

    public short SetHomeProfile(ushort cardNo, ushort axisNo, DmcHomeProfile profile) =>
        NativeMethods.nmc_set_home_profile(cardNo, axisNo, profile.Mode, profile.LowVelocity, profile.HighVelocity,
            profile.AccelerationTime, profile.DecelerationTime, profile.Offset);

    public short GetHomeProfile(ushort cardNo, ushort axisNo, out DmcHomeProfile profile)
    {
        ushort mode = 0;
        double low = 0, high = 0, tacc = 0, tdec = 0, offset = 0;
        var result = NativeMethods.nmc_get_home_profile(cardNo, axisNo, ref mode, ref low, ref high, ref tacc,
            ref tdec, ref offset);
        profile = new DmcHomeProfile(mode, low, high, tacc, tdec, offset);
        return result;
    }

    public short HomeMove(ushort cardNo, ushort axisNo) => NativeMethods.dmc_home_move(cardNo, axisNo);

    public short GetHomeResult(ushort cardNo, ushort axisNo, out ushort state)
    {
        state = 0;
        return NativeMethods.dmc_get_home_result(cardNo, axisNo, ref state);
    }

    private static ushort ToEnable(bool enabled) => enabled ? (ushort)1 : (ushort)0;
}
