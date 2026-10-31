namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>
/// 單根 DMC 總線軸的配置。回零參數沒有默認值：回零方式選錯會直接撞機，必須由現場明確填寫。
/// </summary>
public sealed record DmcAxisOptions : LeadshineAxisOptions
{
    public required DmcHomeProfile HomeProfile { get; init; }
}
