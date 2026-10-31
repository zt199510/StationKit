namespace StationKit.Motion.Dmc;

/// <summary>
/// 单根 DMC 总线轴的配置。回零参数没有默认值：回零方式选错会直接撞机，必须由现场明确填写。
/// </summary>
public sealed record DmcAxisOptions
{
    public required ushort CardNo { get; init; }

    public required ushort AxisNo { get; init; }

    public required string Name { get; init; }

    /// <summary>脉冲当量（脉冲/mm）。</summary>
    public required double PulseEquivalent { get; init; }

    public required DmcHomeProfile HomeProfile { get; init; }

    /// <summary>起始速度。</summary>
    public double MinVelocity { get; init; }

    /// <summary>停止速度。</summary>
    public double StopVelocity { get; init; }

    /// <summary>S 段时间（秒）。</summary>
    public double STime { get; init; } = 0.01;

    /// <summary>软限位计数源（dmc_set_softlimit_unit source_sel），取值含义以所用卡型手册为准，需实机确认。</summary>
    public ushort SoftLimitSource { get; init; }

    /// <summary>软限位触发后的动作（dmc_set_softlimit_unit SL_action），取值含义以所用卡型手册为准，需实机确认。</summary>
    public ushort SoftLimitAction { get; init; }

    /// <summary>判定到位的位置容差（mm）。</summary>
    public double InPositionTolerance { get; init; } = 0.01;

    public TimeSpan MotionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HomeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);
}
