namespace StationKit.Motion.Dmc;

/// <summary>dmc_stop 的停止模式。</summary>
public enum DmcStopMode : ushort
{
    /// <summary>减速停止。</summary>
    Decelerated = 0,

    /// <summary>紧急停止。</summary>
    Immediate = 1,
}

/// <summary>回零参数（对应 nmc_set_home_profile）。</summary>
public readonly record struct DmcHomeProfile(
    ushort Mode,
    double LowVelocity,
    double HighVelocity,
    double AccelerationTime,
    double DecelerationTime,
    double Offset);

/// <summary>软限位参数（对应 dmc_set_softlimit_unit）。</summary>
public readonly record struct DmcSoftLimit(bool Enabled, ushort Source, ushort Action, double Negative, double Positive);

/// <summary>
/// 雷赛 DMC 原生接口。所有方法原样返回雷赛错误码（0 表示成功），不做任何判断，
/// 判断和日志统一放在调用方。测试时用假实现记录调用顺序、注入返回码。
/// </summary>
public interface IDmcNativeApi
{
    short BoardClose();

    short BoardInit();

    short BoardInitEthernet(ushort cardNo, string ipAddress);

    short GetTotalAxes(ushort cardNo, out uint totalAxes);

    short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value);

    short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value);

    /// <summary>注意：雷赛用加减速时间（秒），不是加速度。</summary>
    short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity);

    short SetSProfile(ushort cardNo, ushort axisNo, double seconds);

    short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic);

    short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic);

    short SetSoftLimit(ushort cardNo, ushort axisNo, DmcSoftLimit softLimit);

    short GetSoftLimit(ushort cardNo, ushort axisNo, out DmcSoftLimit softLimit);

    short EnableAxis(ushort cardNo, ushort axisNo);

    short DisableAxis(ushort cardNo, ushort axisNo);

    short ClearAxisErrorCode(ushort cardNo, ushort axisNo);

    short GetAxisErrorCode(ushort cardNo, ushort axisNo, out ushort errorCode);

    short MoveAbsolute(ushort cardNo, ushort axisNo, double position);

    /// <summary>0 运动中，1 已停止；其他值视为非法状态。</summary>
    short CheckDone(ushort cardNo, ushort axisNo);

    short StopAxis(ushort cardNo, ushort axisNo, DmcStopMode mode);

    short EmergencyStop(ushort cardNo);

    short ReadPosition(ushort cardNo, ushort axisNo, out double position);

    short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status);

    short SetHomeProfile(ushort cardNo, ushort axisNo, DmcHomeProfile profile);

    short GetHomeProfile(ushort cardNo, ushort axisNo, out DmcHomeProfile profile);

    short HomeMove(ushort cardNo, ushort axisNo);

    /// <summary>回零结果：1 表示本次回零完成。</summary>
    short GetHomeResult(ushort cardNo, ushort axisNo, out ushort state);
}
