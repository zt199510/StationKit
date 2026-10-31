namespace StationKit.Motion.Leadshine.Smc;

/// <summary>報警復位信號從哪裡輸出。</summary>
public enum SmcAlarmResetSource
{
    /// <summary>軸自己的 ERC 引腳（smc_write_erc_pin）。</summary>
    ErcPin,

    /// <summary>通用輸出口（smc_write_outbit），接線到驅動器 A-CLR。</summary>
    OutputBit,
}

/// <summary>
/// 報警復位輸出：輸出一個有效電平脈衝給驅動器的報警清除輸入。
/// 有效電平沒有默認值，由接線決定，必須現場確認。
/// </summary>
public sealed record SmcAlarmReset
{
    public required SmcAlarmResetSource Source { get; init; }

    /// <summary>通用輸出口號，<see cref="Source"/> 為 <see cref="SmcAlarmResetSource.OutputBit"/> 時使用。</summary>
    public ushort OutputBit { get; init; }

    /// <summary>有效電平（0/1）。</summary>
    public required ushort ActiveLevel { get; init; }

    /// <summary>有效電平保持時間，需滿足驅動器報警清除輸入的最小脈寬。</summary>
    public TimeSpan PulseWidth { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>撤銷復位信號後，等待 ALM 消失的最長時間。</summary>
    public TimeSpan ClearTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// 單根 SMC 脈衝軸的配置。以下參數全部沒有默認值，必須按現場接線和驅動器設置填寫：
/// <list type="bullet">
/// <item>SEVON 使能電平：同一套代碼在不同機台上見過 0 和 1 兩種接法，猜錯會在“失能”時反而使能。</item>
/// <item>ALM、EL 的有效電平和動作：電平錯了，報警和限位信號會一直有效或永遠不觸發。</item>
/// <item>回零方式、速度、原點電平：選錯會直接撞機。</item>
/// </list>
/// </summary>
public sealed record SmcAxisOptions : LeadshineAxisOptions
{
    /// <summary>SEVON 輸出為哪個電平時伺服使能（0 或 1）。</summary>
    public required ushort ServoOnLevel { get; init; }

    /// <summary>寫 SEVON 後等待多久再回讀。</summary>
    public TimeSpan ServoSettleTime { get; init; } = TimeSpan.FromMilliseconds(20);

    public required SmcSignalMode AlarmSignal { get; init; }

    public required SmcSignalMode LimitSignal { get; init; }

    public required SmcHomeMode HomeMode { get; init; }

    public required SmcHomeSpeed HomeSpeed { get; init; }

    public required SmcHomePinLogic HomePinLogic { get; init; }

    /// <summary>回零完成後是否把當前位置設為指定值；默認不設置（保持控制卡回零後的清零行為）。</summary>
    public SmcHomePosition HomePosition { get; init; } = new(false, 0);

    /// <summary>報警復位輸出。為 null 時不支持軟件復位，需要人工到驅動器上處理。</summary>
    public SmcAlarmReset? AlarmReset { get; init; }
}
