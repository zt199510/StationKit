using StationKit.Abstractions.Motion;
using StationKit.Motion.Leadshine.Smc.Native;

namespace StationKit.Motion.Leadshine.Smc;

/// <summary>調用真實 LTSMC.dll 的實現。</summary>
public sealed class SmcNativeApi : ISmcNativeApi
{
    private const ushort AbsoluteMode = 1; // smc_pmove_unit posi_mode：1 絕對坐標
    private const ushort SCurveMode = 0;   // smc_set_s_profile s_mode：保留參數，固定為 0

    public short BoardInit(ushort connectNo, ushort connectType, string connectString, uint baud) =>
        NativeMethods.smc_board_init(connectNo, connectType, connectString, baud);

    public short BoardClose(ushort connectNo) => NativeMethods.smc_board_close(connectNo);

    public short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value) =>
        NativeMethods.smc_set_equiv(cardNo, axisNo, value);

    public short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value)
    {
        value = 0;
        return NativeMethods.smc_get_equiv(cardNo, axisNo, ref value);
    }

    public short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity) =>
        NativeMethods.smc_set_profile_unit(cardNo, axisNo, minVelocity, maxVelocity, accelerationTime,
            decelerationTime, stopVelocity);

    public short SetSProfile(ushort cardNo, ushort axisNo, double seconds) =>
        NativeMethods.smc_set_s_profile(cardNo, axisNo, SCurveMode, seconds);

    public short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic) =>
        NativeMethods.smc_set_emg_mode(cardNo, axisNo, ToEnable(enabled), logic);

    public short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic)
    {
        ushort rawEnabled = 0;
        logic = 0;
        var result = NativeMethods.smc_get_emg_mode(cardNo, axisNo, ref rawEnabled, ref logic);
        enabled = rawEnabled != 0;
        return result;
    }

    public short SetSoftLimit(ushort cardNo, ushort axisNo, LeadshineSoftLimit softLimit) =>
        NativeMethods.smc_set_softlimit_unit(cardNo, axisNo, ToEnable(softLimit.Enabled), softLimit.Source,
            softLimit.Action, softLimit.Negative, softLimit.Positive);

    public short GetSoftLimit(ushort cardNo, ushort axisNo, out LeadshineSoftLimit softLimit)
    {
        ushort enabled = 0, source = 0, action = 0;
        double negative = 0, positive = 0;
        var result = NativeMethods.smc_get_softlimit_unit(cardNo, axisNo, ref enabled, ref source, ref action,
            ref negative, ref positive);
        softLimit = new LeadshineSoftLimit(enabled != 0, source, action, negative, positive);
        return result;
    }

    public short SetAlarmMode(ushort connectNo, ushort axisNo, SmcSignalMode mode) =>
        NativeMethods.smc_set_alm_mode(connectNo, axisNo, ToEnable(mode.Enabled), mode.Logic, mode.Action);

    public short GetAlarmMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode)
    {
        ushort enabled = 0, logic = 0, action = 0;
        var result = NativeMethods.smc_get_alm_mode(connectNo, axisNo, ref enabled, ref logic, ref action);
        mode = new SmcSignalMode(enabled != 0, logic, action);
        return result;
    }

    public short SetLimitMode(ushort connectNo, ushort axisNo, SmcSignalMode mode) =>
        NativeMethods.smc_set_el_mode(connectNo, axisNo, ToEnable(mode.Enabled), mode.Logic, mode.Action);

    public short GetLimitMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode)
    {
        ushort enabled = 0, logic = 0, action = 0;
        var result = NativeMethods.smc_get_el_mode(connectNo, axisNo, ref enabled, ref logic, ref action);
        mode = new SmcSignalMode(enabled != 0, logic, action);
        return result;
    }

    public short WriteServoOn(ushort connectNo, ushort axisNo, ushort level) =>
        NativeMethods.smc_write_sevon_pin(connectNo, axisNo, level);

    public short ReadServoOn(ushort connectNo, ushort axisNo) => NativeMethods.smc_read_sevon_pin(connectNo, axisNo);

    public short WriteErcPin(ushort connectNo, ushort axisNo, ushort level) =>
        NativeMethods.smc_write_erc_pin(connectNo, axisNo, level);

    public short WriteOutputBit(ushort connectNo, ushort bitNo, ushort level) =>
        NativeMethods.smc_write_outbit(connectNo, bitNo, level);

    public short MoveAbsolute(ushort cardNo, ushort axisNo, double position) =>
        NativeMethods.smc_pmove_unit(cardNo, axisNo, position, AbsoluteMode);

    public short MoveContinuous(ushort cardNo, ushort axisNo, JogDirection direction) =>
        NativeMethods.smc_vmove(cardNo, axisNo, ToVmoveDirection(direction));

    public short CheckDone(ushort cardNo, ushort axisNo) => NativeMethods.smc_check_done(cardNo, axisNo);

    public short StopAxis(ushort cardNo, ushort axisNo, LeadshineStopMode mode) =>
        NativeMethods.smc_stop(cardNo, axisNo, (ushort)mode);

    public short EmergencyStop(ushort cardNo) => NativeMethods.smc_emg_stop(cardNo);

    public short ReadPosition(ushort cardNo, ushort axisNo, out double position)
    {
        position = 0;
        return NativeMethods.smc_get_position_unit(cardNo, axisNo, ref position);
    }

    public short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status)
    {
        // smc_axis_io_status 直接返回狀態字，沒有錯誤碼
        status = NativeMethods.smc_axis_io_status(cardNo, axisNo);
        return 0;
    }

    public short SetHomeMode(ushort connectNo, ushort axisNo, SmcHomeMode mode) =>
        NativeMethods.smc_set_homemode(connectNo, axisNo, mode.Direction, mode.VelocityMode, mode.Mode,
            mode.PositionSource);

    public short GetHomeMode(ushort connectNo, ushort axisNo, out SmcHomeMode mode)
    {
        ushort direction = 0, homeMode = 0, source = 0;
        double velocityMode = 0;
        var result = NativeMethods.smc_get_homemode(connectNo, axisNo, ref direction, ref velocityMode,
            ref homeMode, ref source);
        mode = new SmcHomeMode(direction, velocityMode, homeMode, source);
        return result;
    }

    public short SetHomeSpeed(ushort connectNo, ushort axisNo, SmcHomeSpeed speed) =>
        NativeMethods.smc_set_home_profile_unit(connectNo, axisNo, speed.LowVelocity, speed.HighVelocity,
            speed.AccelerationTime, speed.DecelerationTime);

    public short GetHomeSpeed(ushort connectNo, ushort axisNo, out SmcHomeSpeed speed)
    {
        double low = 0, high = 0, tacc = 0, tdec = 0;
        var result = NativeMethods.smc_get_home_profile_unit(connectNo, axisNo, ref low, ref high, ref tacc,
            ref tdec);
        speed = new SmcHomeSpeed(low, high, tacc, tdec);
        return result;
    }

    public short SetHomePinLogic(ushort connectNo, ushort axisNo, SmcHomePinLogic logic) =>
        NativeMethods.smc_set_home_pin_logic(connectNo, axisNo, logic.Logic, logic.Filter);

    public short GetHomePinLogic(ushort connectNo, ushort axisNo, out SmcHomePinLogic logic)
    {
        ushort level = 0;
        double filter = 0;
        var result = NativeMethods.smc_get_home_pin_logic(connectNo, axisNo, ref level, ref filter);
        logic = new SmcHomePinLogic(level, filter);
        return result;
    }

    public short SetHomePosition(ushort connectNo, ushort axisNo, SmcHomePosition position) =>
        NativeMethods.smc_set_home_position_unit(connectNo, axisNo, ToEnable(position.Enabled), position.Position);

    public short GetHomePosition(ushort connectNo, ushort axisNo, out SmcHomePosition position)
    {
        ushort enabled = 0;
        double value = 0;
        var result = NativeMethods.smc_get_home_position_unit(connectNo, axisNo, ref enabled, ref value);
        position = new SmcHomePosition(enabled != 0, value);
        return result;
    }

    public short HomeMove(ushort connectNo, ushort axisNo) => NativeMethods.smc_home_move(connectNo, axisNo);

    public short GetHomeResult(ushort connectNo, ushort axisNo, out ushort state)
    {
        state = 0;
        return NativeMethods.smc_get_home_result(connectNo, axisNo, ref state);
    }

    private static ushort ToEnable(bool enabled) => enabled ? (ushort)1 : (ushort)0;

    // vmove dir：0 負方向，1 正方向
    private static ushort ToVmoveDirection(JogDirection direction) =>
        direction == JogDirection.Positive ? (ushort)1 : (ushort)0;
}
