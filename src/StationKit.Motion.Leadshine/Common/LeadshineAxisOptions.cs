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

    /// <summary>判定到位的位置容差（mm）。</summary>
    public double InPositionTolerance { get; init; } = 0.01;

    public TimeSpan MotionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HomeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);
}
