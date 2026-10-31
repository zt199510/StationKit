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

/// <summary>軸狀態快照。值類型，讀取後不會被其他線程改變。</summary>
public readonly record struct AxisStatus(
    AxisState State,
    double Position,
    bool IsHomed,
    bool PositiveLimit,
    bool NegativeLimit,
    string? AlarmReason)
{
    public bool IsEnabled => State is not AxisState.Disabled and not AxisState.Alarm;
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
