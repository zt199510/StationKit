namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>
/// 單根 DMC 總線軸的配置。感應器回零的參數沒有默認值：回零方式選錯會直接撞機，必須由現場明確填寫。
/// </summary>
public sealed record DmcAxisOptions : LeadshineAxisOptions
{
    /// <summary>感應器回零的參數。<see cref="LeadshineHomeMethod.Sensor"/> 時必填，絕對值回零不用。</summary>
    public DmcHomeProfile? HomeProfile { get; init; }
}
