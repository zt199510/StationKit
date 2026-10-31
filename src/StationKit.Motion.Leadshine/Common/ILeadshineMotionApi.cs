using StationKit.Abstractions.Motion;

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

/// <summary>反饋位置來源。</summary>
public enum LeadshineFeedback
{
    /// <summary>沒有反饋（開環步進，或編碼器沒有接到卡上）：反饋位置為 NaN，不能拿指令位置冒充。</summary>
    None,

    /// <summary>編碼器反饋接到卡上：用 encoder_unit 讀取。</summary>
    Encoder,
}

/// <summary>
/// INP 到位信號配置（對應 dmc_set_inp_mode / smc_set_inp_mode）。
/// Logic：0 低電平有效、1 高電平有效，按驅動器 INP 輸出的接線填寫。
/// </summary>
public readonly record struct LeadshineInpMode(bool Enabled, ushort Logic)
{
    /// <summary>不使用 INP：運動以 check_done 為準。</summary>
    public static LeadshineInpMode Disabled => new(false, 0);
}

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

    short SetInpMode(ushort cardNo, ushort axisNo, LeadshineInpMode mode);

    short GetInpMode(ushort cardNo, ushort axisNo, out LeadshineInpMode mode);

    short MoveAbsolute(ushort cardNo, ushort axisNo, double position);

    /// <summary>速度模式連續運動（vmove），按 set_profile_unit 的最大速度一直走，直到 stop。</summary>
    short MoveContinuous(ushort cardNo, ushort axisNo, JogDirection direction);

    /// <summary>0 運動中，1 已停止；其他值視為非法狀態。</summary>
    short CheckDone(ushort cardNo, ushort axisNo);

    short StopAxis(ushort cardNo, ushort axisNo, LeadshineStopMode mode);

    short EmergencyStop(ushort cardNo);

    short ReadPosition(ushort cardNo, ushort axisNo, out double position);

    /// <summary>設置指令位置計數值，用於絕對值回零時把指令位置同步到編碼器絕對位置。</summary>
    short SetPosition(ushort cardNo, ushort axisNo, double position);

    /// <summary>讀反饋位置（編碼器，單位與脈衝當量一致）。</summary>
    short ReadFeedbackPosition(ushort cardNo, ushort axisNo, out double position);

    /// <summary>設置反饋位置計數值，用於回零後把編碼器和指令位置對齊。</summary>
    short SetFeedbackPosition(ushort cardNo, ushort axisNo, double position);

    short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status);

    /// <summary>
    /// 讀通用輸入端口（32 位，第 n 位對應端口內第 n 個輸入，值為電平）。
    /// read_inport 直接返回端口值，沒有錯誤碼：通訊失敗與「全部低電平」無法區分，
    /// 由輪詢器在同一次持鎖內加讀一個有錯誤碼的函數作探針（docs/design/digital-io.md 4.2）。
    /// </summary>
    short ReadInputPort(ushort cardNo, ushort portNo, out uint value);

    /// <summary>讀通用輸出端口的實際電平，用於寫入後回讀核對。同樣沒有錯誤碼。</summary>
    short ReadOutputPort(ushort cardNo, ushort portNo, out uint value);

    /// <summary>寫一個通用輸出位的電平（0/1）。</summary>
    short WriteOutputBit(ushort cardNo, ushort bitNo, ushort level);
}
