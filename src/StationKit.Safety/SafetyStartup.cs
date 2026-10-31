using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Safety;

public sealed record AxisSafetyBinding(IAxis Axis, IAxisSafetyPort Port, AxisSafetyConfig Config);

public sealed class SafetyStartupException(IReadOnlyList<string> errors)
    : Exception("安全初始化失敗，所有軸保持失能：" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// 設備上電的安全初始化。順序固定為：校驗全部 → 寫入全部 → 回讀核對全部 → 使能全部。
/// 任何一步失敗，都不會有任何一根軸被使能；使能階段中途失敗，會把已使能的軸全部失能。
/// </summary>
public static class SafetyStartup
{
    private const double Tolerance = 1e-9;

    public static async Task<IReadOnlyList<string>> RunAsync(
        IReadOnlyList<AxisSafetyBinding> axes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(axes);
        var log = new List<string>();

        var errors = axes.SelectMany(b => SafetyConfigValidator.Validate(b.Axis.Name, b.Config)).ToList();
        if (errors.Count > 0)
        {
            throw new SafetyStartupException(errors);
        }

        log.Add($"校驗通過：{axes.Count} 根軸");

        foreach (var binding in axes)
        {
            await binding.Port.WriteSafetyConfigAsync(binding.Config, cancellationToken).ConfigureAwait(false);
        }

        log.Add("安全參數已寫入");

        foreach (var binding in axes)
        {
            var actual = await binding.Port.ReadSafetyConfigAsync(cancellationToken).ConfigureAwait(false);
            errors.AddRange(Compare(binding.Axis.Name, binding.Config, actual));
        }

        if (errors.Count > 0)
        {
            throw new SafetyStartupException(errors);
        }

        log.Add("回讀核對一致");

        var enabled = new List<IAxis>();
        try
        {
            foreach (var binding in axes)
            {
                await binding.Axis.EnableAsync(cancellationToken).ConfigureAwait(false);
                enabled.Add(binding.Axis);
            }
        }
        catch (Exception ex)
        {
            foreach (var axis in enabled)
            {
                await axis.DisableAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw new SafetyStartupException([$"使能失敗，已回退全部失能：{ex.Message}"]);
        }

        log.Add("全部軸已使能");
        return log;
    }

    private static IEnumerable<string> Compare(string axis, AxisSafetyConfig expected, AxisSafetyConfig actual)
    {
        static bool Same(double a, double b) => Math.Abs(a - b) <= Tolerance;

        if (!Same(expected.SoftLimitMin, actual.SoftLimitMin) || !Same(expected.SoftLimitMax, actual.SoftLimitMax))
        {
            yield return $"{axis}：軟限位回讀不一致，期望 [{expected.SoftLimitMin}, {expected.SoftLimitMax}]，" +
                         $"實際 [{actual.SoftLimitMin}, {actual.SoftLimitMax}]";
        }

        if (!Same(expected.MaxVelocity, actual.MaxVelocity))
        {
            yield return $"{axis}：最大速度回讀不一致，期望 {expected.MaxVelocity}，實際 {actual.MaxVelocity}";
        }

        if (!Same(expected.MaxAcceleration, actual.MaxAcceleration))
        {
            yield return $"{axis}：最大加速度回讀不一致，期望 {expected.MaxAcceleration}，實際 {actual.MaxAcceleration}";
        }

        if (expected.EmergencyStopEnabled != actual.EmergencyStopEnabled ||
            expected.EmergencyStopActiveHigh != actual.EmergencyStopActiveHigh)
        {
            yield return $"{axis}：急停配置回讀不一致，期望 {Describe(expected)}，實際 {Describe(actual)}";
        }
    }

    private static string Describe(AxisSafetyConfig c) =>
        !c.EmergencyStopEnabled ? "未啟用"
        : c.EmergencyStopActiveHigh switch { true => "高電平有效", false => "低電平有效", null => "電平未指定" };
}
