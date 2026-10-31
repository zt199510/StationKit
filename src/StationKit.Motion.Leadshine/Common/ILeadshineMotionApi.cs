namespace StationKit.Motion.Leadshine;

/// <summary>dmc_stop / smc_stop 的停止模式。</summary>
public enum LeadshineStopMode : ushort
{
    /// <summary>減速停止。</summary>
    Decelerated = 0,

    /// <summary>緊急停止。</summary>
    Immediate = 1,
}

/// <summary>軟限位參數（對應 dmc_set_softlimit_unit / smc_set_softlimit_unit）。</summary>
public readonly record struct LeadshineSoftLimit(bool Enabled, ushort Source, ushort Action, double Negative, double Positive);

/// <summary>
/// DMC 和 SMC 兩個系列簽名一致的單軸函數。所有方法原樣返回雷賽錯誤碼（0 表示成功），不做任何判斷，
/// 判斷和日誌統一放在調用方。cardNo 在 SMC 上對應 ConnectNo。
/// </summary>
public interface ILeadshineMotionApi
{
    short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value);

    short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value);

    /// <summary>注意：雷賽用加減速時間（秒），不是加速度。</summary>
    short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity);

    short SetSProfile(ushort cardNo, ushort axisNo, double seconds);

    short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic);

    short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic);

    short SetSoftLimit(ushort cardNo, ushort axisNo, LeadshineSoftLimit softLimit);

    short GetSoftLimit(ushort cardNo, ushort axisNo, out LeadshineSoftLimit softLimit);

    short MoveAbsolute(ushort cardNo, ushort axisNo, double position);

    /// <summary>0 運動中，1 已停止；其他值視為非法狀態。</summary>
    short CheckDone(ushort cardNo, ushort axisNo);

    short StopAxis(ushort cardNo, ushort axisNo, LeadshineStopMode mode);

    short EmergencyStop(ushort cardNo);

    short ReadPosition(ushort cardNo, ushort axisNo, out double position);

    short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status);
}
