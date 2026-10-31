using StationKit.Abstractions.Motion;

namespace StationKit.Motion.Simulation;

/// <summary>
/// 梯形速度規劃（加速度、減速度可不對稱）。距離不足以加到目標速度時退化為三角形。
/// </summary>
public readonly record struct TrapezoidProfile
{
    private TrapezoidProfile(double start, double target, double peakVelocity, double acceleration,
        double deceleration, double accelTime, double cruiseTime, double decelTime)
    {
        Start = start;
        Target = target;
        PeakVelocity = peakVelocity;
        Acceleration = acceleration;
        Deceleration = deceleration;
        AccelTime = accelTime;
        CruiseTime = cruiseTime;
        DecelTime = decelTime;
    }

    public double Start { get; }

    public double Target { get; }

    public double PeakVelocity { get; }

    public double Acceleration { get; }

    public double Deceleration { get; }

    public double AccelTime { get; }

    public double CruiseTime { get; }

    public double DecelTime { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(AccelTime + CruiseTime + DecelTime);

    public static TrapezoidProfile Plan(double start, double target, MotionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        var distance = Math.Abs(target - start);
        if (distance == 0)
        {
            return new(start, target, 0, profile.Acceleration, profile.Deceleration, 0, 0, 0);
        }

        double a = profile.Acceleration, d = profile.Deceleration, v = profile.Velocity;
        var rampDistance = v * v / (2 * a) + v * v / (2 * d);
        if (rampDistance <= distance)
        {
            return new(start, target, v, a, d, v / a, (distance - rampDistance) / v, v / d);
        }

        // 三角形：峰值速度由 vp²/2a + vp²/2d = distance 解出
        var peak = Math.Sqrt(2 * distance * a * d / (a + d));
        return new(start, target, peak, a, d, peak / a, 0, peak / d);
    }

    public double PositionAt(TimeSpan elapsed)
    {
        var t = elapsed.TotalSeconds;
        var direction = Math.Sign(Target - Start);
        var accelDistance = 0.5 * Acceleration * AccelTime * AccelTime;

        double travelled;
        if (t <= 0)
        {
            travelled = 0;
        }
        else if (t < AccelTime)
        {
            travelled = 0.5 * Acceleration * t * t;
        }
        else if (t < AccelTime + CruiseTime)
        {
            travelled = accelDistance + PeakVelocity * (t - AccelTime);
        }
        else if (t < AccelTime + CruiseTime + DecelTime)
        {
            var td = t - AccelTime - CruiseTime;
            travelled = accelDistance + PeakVelocity * CruiseTime + PeakVelocity * td - 0.5 * Deceleration * td * td;
        }
        else
        {
            return Target;
        }

        return Start + direction * travelled;
    }
}
