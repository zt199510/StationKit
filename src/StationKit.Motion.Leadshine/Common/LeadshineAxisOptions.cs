namespace StationKit.Motion.Leadshine;

/// <summary>雷賽軸的公共配置。DMC 和 SMC 各自的回零、使能參數在子類裡。</summary>
public abstract record LeadshineAxisOptions
{
    /// <summary>卡號（SMC 上對應 ConnectNo）。</summary>
    public required ushort CardNo { get; init; }

    public required ushort AxisNo { get; init; }

    public required string Name { get; init; }

    /// <summary>脈衝當量（脈衝/mm）。</summary>
    public required double PulseEquivalent { get; init; }

    /// <summary>
    /// 是否用 INP 判定到位。必填，沒有默認值：接沒接 INP 由現場接線決定，
    /// 猜「啟用」會永遠等不到到位，猜「不啟用」會以為檢查過了其實沒有。
    /// 不需要停穩的軸（上下料等）填 <see cref="LeadshineInpMode.Disabled"/>。
    /// </summary>
    public required LeadshineInpMode InPositionSignal { get; init; }

    /// <summary>check_done 之後等待 INP 有效的最長時間，超時報故障。</summary>
    public TimeSpan InPositionTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 停穩延時：未啟用 INP 時，到位後固定再等這麼久才返回，給伺服追完跟隨誤差。
    /// 集中在軸配置裡，代替業務代碼裡散落的 Sleep。啟用 INP 時不允許配置，以 INP 為準。
    /// 默認 0：上下料等不需要停穩的軸不浪費節拍。
    /// </summary>
    public TimeSpan SettleTime { get; init; }

    /// <summary>起始速度。</summary>
    public double MinVelocity { get; init; }

    /// <summary>停止速度。</summary>
    public double StopVelocity { get; init; }

    /// <summary>S 段時間（秒）。</summary>
    public double STime { get; init; } = 0.01;

    /// <summary>軟限位計數源（softlimit_unit source_sel），取值含義以所用卡型手冊為準，需實機確認。</summary>
    public ushort SoftLimitSource { get; init; }

    /// <summary>軟限位觸發後的動作（softlimit_unit SL_action），取值含義以所用卡型手冊為準，需實機確認。</summary>
    public ushort SoftLimitAction { get; init; }

    /// <summary>指令位置與目標的容差（mm），用於發現運動被提前停止；不是電機實際到位的判定，那個靠 INP。</summary>
    public double InPositionTolerance { get; init; } = 0.01;

    public TimeSpan MotionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HomeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 單次點動最長時間，到時減速停止並報故障。這是最後一道兜底，正常應由界面的 JogSession 看門狗停軸。
    /// </summary>
    public TimeSpan JogTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);
}
