namespace StationKit.Motion.Leadshine.Smc;

/// <summary>
/// 專用信號（ALM、EL）的電氣配置：是否啟用、有效電平、觸發動作。
/// 對應 smc_set_alm_mode(enable, alm_logic, alm_action) 和 smc_set_el_mode(enable, el_logic, el_mode)。
/// </summary>
public readonly record struct SmcSignalMode(bool Enabled, ushort Logic, ushort Action);

/// <summary>回零方式：smc_set_homemode(home_dir, vel_mode, mode, pos_source)。</summary>
public readonly record struct SmcHomeMode(ushort Direction, double VelocityMode, ushort Mode, ushort PositionSource);

/// <summary>回零速度：smc_set_home_profile_unit(Low_Vel, High_Vel, Tacc, Tdec)。</summary>
public readonly record struct SmcHomeSpeed(double LowVelocity, double HighVelocity, double AccelerationTime,
    double DecelerationTime);

/// <summary>原點信號：smc_set_home_pin_logic(org_logic, filter)。</summary>
public readonly record struct SmcHomePinLogic(ushort Logic, double Filter);

/// <summary>回零完成後的位置設定：smc_set_home_position_unit(enable, position)。</summary>
public readonly record struct SmcHomePosition(bool Enabled, double Position);

/// <summary>
/// SMC 脈衝型控制器的 SDK 封裝（LTSMC.dll）。運動、IO、急停、軟限位等與 DMC 相同的部分在
/// <see cref="ILeadshineMotionApi"/>；這裡是脈衝軸特有的：連接方式、SEVON 使能、ALM/EL 電平、回零參數、
/// 報警復位輸出。
/// </summary>
public interface ISmcNativeApi : ILeadshineMotionApi
{
    short BoardInit(ushort connectNo, ushort connectType, string connectString, uint baud);

    short BoardClose(ushort connectNo);

    /// <summary>寫 SEVON 輸出電平（0/1）。哪個電平表示使能由接線決定，見 <see cref="SmcAxisOptions.ServoOnLevel"/>。</summary>
    short WriteServoOn(ushort connectNo, ushort axisNo, ushort level);

    /// <summary>讀 SEVON 輸出電平。smc_read_sevon_pin 直接返回電平，沒有錯誤碼。</summary>
    short ReadServoOn(ushort connectNo, ushort axisNo);

    short SetAlarmMode(ushort connectNo, ushort axisNo, SmcSignalMode mode);

    short GetAlarmMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode);

    short SetLimitMode(ushort connectNo, ushort axisNo, SmcSignalMode mode);

    short GetLimitMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode);

    short SetHomeMode(ushort connectNo, ushort axisNo, SmcHomeMode mode);

    short GetHomeMode(ushort connectNo, ushort axisNo, out SmcHomeMode mode);

    short SetHomeSpeed(ushort connectNo, ushort axisNo, SmcHomeSpeed speed);

    short GetHomeSpeed(ushort connectNo, ushort axisNo, out SmcHomeSpeed speed);

    short SetHomePinLogic(ushort connectNo, ushort axisNo, SmcHomePinLogic logic);

    short GetHomePinLogic(ushort connectNo, ushort axisNo, out SmcHomePinLogic logic);

    short SetHomePosition(ushort connectNo, ushort axisNo, SmcHomePosition position);

    short GetHomePosition(ushort connectNo, ushort axisNo, out SmcHomePosition position);

    short HomeMove(ushort connectNo, ushort axisNo);

    short GetHomeResult(ushort connectNo, ushort axisNo, out ushort state);

    short WriteErcPin(ushort connectNo, ushort axisNo, ushort level);

    short WriteOutputBit(ushort connectNo, ushort bitNo, ushort level);
}
