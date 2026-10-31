namespace StationKit.Motion.Dmc;

/// <summary>dmc_axis_io_status 状态字解析。</summary>
public readonly record struct DmcAxisIo(
    bool Alarm,
    bool PositiveHardLimit,
    bool NegativeHardLimit,
    bool EmergencyStop,
    bool Origin,
    bool PositiveSoftLimit,
    bool NegativeSoftLimit)
{
    public bool PositiveLimit => PositiveHardLimit || PositiveSoftLimit;

    public bool NegativeLimit => NegativeHardLimit || NegativeSoftLimit;

    public static DmcAxisIo Parse(uint status) => new(
        Alarm: (status & 1u) != 0,                      // bit0 报警信号 ALM
        PositiveHardLimit: (status & (1u << 1)) != 0,   // bit1 正硬限位 EL+
        NegativeHardLimit: (status & (1u << 2)) != 0,   // bit2 负硬限位 EL-
        EmergencyStop: (status & (1u << 3)) != 0,       // bit3 急停信号 EMG
        Origin: (status & (1u << 4)) != 0,              // bit4 原点信号 ORG
        PositiveSoftLimit: (status & (1u << 6)) != 0,   // bit6 软正限位 SL+
        NegativeSoftLimit: (status & (1u << 7)) != 0);  // bit7 软负限位 SL-
}
