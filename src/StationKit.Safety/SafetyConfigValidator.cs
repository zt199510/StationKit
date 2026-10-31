using StationKit.Abstractions.Safety;

namespace StationKit.Safety;

public static class SafetyConfigValidator
{
    /// <summary>返回所有問題（而不是遇到第一個就停），讓現場一次看到全部要改的地方。</summary>
    public static IReadOnlyList<string> Validate(string axis, AxisSafetyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        if (!double.IsFinite(config.SoftLimitMin) || !double.IsFinite(config.SoftLimitMax))
        {
            errors.Add($"{axis}：軟限位未配置");
        }
        else if (config.SoftLimitMin >= config.SoftLimitMax)
        {
            errors.Add($"{axis}：軟限位下限 {config.SoftLimitMin} 不小於上限 {config.SoftLimitMax}");
        }

        if (!double.IsFinite(config.MaxVelocity) || config.MaxVelocity <= 0)
        {
            errors.Add($"{axis}：最大速度未配置或非法（{config.MaxVelocity}）");
        }

        if (!double.IsFinite(config.MaxAcceleration) || config.MaxAcceleration <= 0)
        {
            errors.Add($"{axis}：最大加速度未配置或非法（{config.MaxAcceleration}）");
        }

        if (config.EmergencyStopEnabled && config.EmergencyStopActiveHigh is null)
        {
            errors.Add($"{axis}：已啟用急停，但未指定急停有效電平");
        }

        if (!config.ConfirmedOnMachine)
        {
            errors.Add($"{axis}：安全參數未經實機確認");
        }

        return errors;
    }
}
