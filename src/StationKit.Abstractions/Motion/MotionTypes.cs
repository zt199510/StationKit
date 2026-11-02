namespace StationKit.Abstractions.Motion;

public enum AxisState
{
    Disabled,
    Idle,
    Moving,
    Homing,
    Alarm,
}

public enum StopMode
{
    /// <summary>按減速度停止。</summary>
    Decelerate,

    /// <summary>立即停止（急停）。</summary>
    Immediate,
}

public enum JogDirection
{
    Negative = -1,
    Positive = 1,
}

/// <summary>
/// 軸狀態快照。值類型，讀取後不會被其他線程改變。
/// <para>
/// <see cref="Position"/> 是<b>指令位置</b>（規劃器發出去的位置），運動判定和相對運動的起點都用它；
/// <see cref="FeedbackPosition"/> 是<b>反饋位置</b>（編碼器），軸沒有反饋時為 NaN。
/// </para>
/// </summary>
public readonly record struct AxisStatus(
    AxisState State,
    double Position,
    bool IsHomed,
    bool PositiveLimit,
    bool NegativeLimit,
    string? AlarmReason,
    double FeedbackPosition = double.NaN)
{
    public bool IsEnabled => State is not AxisState.Disabled and not AxisState.Alarm;

    public bool HasFeedback => !double.IsNaN(FeedbackPosition);

    /// <summary>跟隨誤差 = 指令位置 − 反饋位置；沒有反饋時為 NaN。</summary>
    public double FollowingError => Position - FeedbackPosition;
}

/// <summary>運動參數（單位由軸配置決定，通常為 mm、mm/s、mm/s²）。</summary>
public sealed record MotionProfile(double Velocity, double Acceleration, double Deceleration)
{
    public static MotionProfile Symmetric(double velocity, double acceleration) =>
        new(velocity, acceleration, acceleration);

    public void Validate()
    {
        if (!IsPositiveFinite(Velocity) || !IsPositiveFinite(Acceleration) || !IsPositiveFinite(Deceleration))
        {
            throw new ArgumentException($"運動參數必須是正的有限數：{this}");
        }
    }

    /// <summary>
    /// 走完指定距離的預計規劃時間（秒）：梯形或三角形速度規劃，不含 S 段和通信延遲。
    /// 距離不足以加到目標速度時退化為三角形，這時只用距離除以速度會少算很多。
    /// </summary>
    public double EstimateMoveSeconds(double distance)
    {
        Validate();
        if (!double.IsFinite(distance) || distance < 0)
        {
            throw new ArgumentException($"距離必須是非負的有限數：{distance}", nameof(distance));
        }

        double v = Velocity, a = Acceleration, d = Deceleration;
        var rampDistance = v * v / (2 * a) + v * v / (2 * d);
        if (rampDistance <= distance)
        {
            return distance / v + v / (2 * a) + v / (2 * d);
        }

        // 三角形：峰值速度由 vp²/2a + vp²/2d = distance 解出
        var peak = Math.Sqrt(2 * distance * a * d / (a + d));
        return peak / a + peak / d;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}

/// <summary>運動故障：報警、限位、被停止。調用方必須處理，不能當成「移動失敗」靜默忽略。</summary>
public class MotionFaultException(string axis, string reason)
    : Exception($"軸 {axis}：{reason}")
{
    public string Axis { get; } = axis;

    public string Reason { get; } = reason;
}

/// <summary>運動在下發前被安全規則拒絕（軸沒有動過）。</summary>
public sealed class MotionRejectedException(string axis, string reason)
    : MotionFaultException(axis, $"運動被拒絕：{reason}");
