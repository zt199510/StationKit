namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>回零參數（對應 nmc_set_home_profile）。</summary>
public readonly record struct DmcHomeProfile(
    ushort Mode,
    double LowVelocity,
    double HighVelocity,
    double AccelerationTime,
    double DecelerationTime,
    double Offset);

/// <summary>
/// 雷賽 DMC 原生接口：公共單軸函數之外，加上板卡連接和 EtherCAT 總線軸（nmc_*）特有的函數。
/// </summary>
public interface IDmcNativeApi : ILeadshineMotionApi
{
    short BoardClose();

    short BoardInit();

    short BoardInitEthernet(ushort cardNo, string ipAddress);

    short GetTotalAxes(ushort cardNo, out uint totalAxes);

    short EnableAxis(ushort cardNo, ushort axisNo);

    short DisableAxis(ushort cardNo, ushort axisNo);

    short ClearAxisErrorCode(ushort cardNo, ushort axisNo);

    short GetAxisErrorCode(ushort cardNo, ushort axisNo, out ushort errorCode);

    short SetHomeProfile(ushort cardNo, ushort axisNo, DmcHomeProfile profile);

    short GetHomeProfile(ushort cardNo, ushort axisNo, out DmcHomeProfile profile);

    short HomeMove(ushort cardNo, ushort axisNo);

    /// <summary>回零結果：1 表示本次回零完成。</summary>
    short GetHomeResult(ushort cardNo, ushort axisNo, out ushort state);
}
