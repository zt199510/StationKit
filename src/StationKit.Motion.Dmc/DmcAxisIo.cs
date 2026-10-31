namespace StationKit.Motion.Dmc;

/// <summary>dmc_axis_io_status 狀態字解析。</summary>
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
        Alarm: (status & 1u) != 0,                      // bit0 報警信號 ALM
        PositiveHardLimit: (status & (1u << 1)) != 0,   // bit1 正硬限位 EL+
        NegativeHardLimit: (status & (1u << 2)) != 0,   // bit2 負硬限位 EL-
        EmergencyStop: (status & (1u << 3)) != 0,       // bit3 急停信號 EMG
        Origin: (status & (1u << 4)) != 0,              // bit4 原點信號 ORG
        PositiveSoftLimit: (status & (1u << 6)) != 0,   // bit6 軟正限位 SL+
        NegativeSoftLimit: (status & (1u << 7)) != 0);  // bit7 軟負限位 SL-
}
