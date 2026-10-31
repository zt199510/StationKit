namespace StationKit.Motion.Dmc;

/// <summary>
/// 單根 DMC 總線軸的配置。回零參數沒有默認值：回零方式選錯會直接撞機，必須由現場明確填寫。
/// </summary>
public sealed record DmcAxisOptions
{
    public required ushort CardNo { get; init; }

    public required ushort AxisNo { get; init; }

    public required string Name { get; init; }

    /// <summary>脈衝當量（脈衝/mm）。</summary>
    public required double PulseEquivalent { get; init; }

    public required DmcHomeProfile HomeProfile { get; init; }

    /// <summary>起始速度。</summary>
    public double MinVelocity { get; init; }

    /// <summary>停止速度。</summary>
    public double StopVelocity { get; init; }

    /// <summary>S 段時間（秒）。</summary>
    public double STime { get; init; } = 0.01;

    /// <summary>軟限位計數源（dmc_set_softlimit_unit source_sel），取值含義以所用卡型手冊為準，需實機確認。</summary>
    public ushort SoftLimitSource { get; init; }

    /// <summary>軟限位觸發後的動作（dmc_set_softlimit_unit SL_action），取值含義以所用卡型手冊為準，需實機確認。</summary>
    public ushort SoftLimitAction { get; init; }

    /// <summary>判定到位的位置容差（mm）。</summary>
    public double InPositionTolerance { get; init; } = 0.01;

    public TimeSpan MotionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HomeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);
}
