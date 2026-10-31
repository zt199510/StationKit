namespace StationKit.Abstractions.Safety;

/// <summary>
/// 單軸安全配置。可空字段表示「未指定」，校驗時一律拒絕，不使用默認值：
/// 急停電平猜錯等於沒有急停。
/// </summary>
public sealed record AxisSafetyConfig
{
    public double SoftLimitMin { get; init; } = double.NaN;

    public double SoftLimitMax { get; init; } = double.NaN;

    public double MaxVelocity { get; init; } = double.NaN;

    public double MaxAcceleration { get; init; } = double.NaN;

    /// <summary>
    /// 未回零時允許的最大點動速度。未回零時坐標不可信、軟限位無效，只能靠低速和硬限位兜底。
    /// 未配置（NaN）表示未回零時禁止點動。
    /// </summary>
    public double UnhomedJogMaxVelocity { get; init; } = double.NaN;

    public bool EmergencyStopEnabled { get; init; }

    /// <summary>急停輸入有效電平：true 高電平有效，false 低電平有效，null 未指定。</summary>
    public bool? EmergencyStopActiveHigh { get; init; }

    /// <summary>現場工程師已在實機上確認過這組參數。</summary>
    public bool ConfirmedOnMachine { get; init; }
}

/// <summary>控制卡上讀寫安全參數的端口。寫入後必須能回讀，用於校驗下發是否生效。</summary>
public interface IAxisSafetyPort
{
    Task WriteSafetyConfigAsync(AxisSafetyConfig config, CancellationToken cancellationToken = default);

    Task<AxisSafetyConfig> ReadSafetyConfigAsync(CancellationToken cancellationToken = default);
}
