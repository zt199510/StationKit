using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Safety;

/// <summary>
/// 安全包裝器：在指令下發前檢查回零、軟限位、速度上限和互鎖規則，不通過則拋出
/// <see cref="MotionRejectedException"/>，軸不會動。業務代碼只拿到包裝後的軸。
/// <para>
/// 注意：互鎖是「檢查後執行」，檢查和運動之間其他軸的狀態可能改變。涉及多軸碰撞的場景，
/// 應同時用工站資源租約（ResourceArbiter）保證互斥，並保留硬件互鎖。
/// </para>
/// </summary>
public sealed class GuardedAxis(IAxis inner, AxisSafetyConfig config, IEnumerable<IMotionInterlock>? interlocks = null)
    : IAxis
{
    private readonly IMotionInterlock[] _interlocks = interlocks?.ToArray() ?? [];

    public string Name => inner.Name;

    public AxisStatus GetStatus() => inner.GetStatus();

    public Task EnableAsync(CancellationToken cancellationToken = default) => inner.EnableAsync(cancellationToken);

    public Task DisableAsync(CancellationToken cancellationToken = default) => inner.DisableAsync(cancellationToken);

    public Task StopAsync(StopMode mode = StopMode.Decelerate) => inner.StopAsync(mode);

    public Task ResetAlarmAsync(CancellationToken cancellationToken = default) =>
        inner.ResetAlarmAsync(cancellationToken);

    public Task HomeAsync(CancellationToken cancellationToken = default)
    {
        CheckInterlocks(new MotionRequest(Name, MotionKind.Home));
        return inner.HomeAsync(cancellationToken);
    }

    public Task MoveAbsoluteAsync(double position, MotionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        if (!inner.GetStatus().IsHomed)
        {
            throw new MotionRejectedException(Name, "未回零");
        }

        if (!(position >= config.SoftLimitMin && position <= config.SoftLimitMax))
        {
            throw new MotionRejectedException(Name,
                $"目標 {position} 超出軟限位 [{config.SoftLimitMin}, {config.SoftLimitMax}]");
        }

        if (profile.Velocity > config.MaxVelocity)
        {
            throw new MotionRejectedException(Name, $"速度 {profile.Velocity} 超過上限 {config.MaxVelocity}");
        }

        if (Math.Max(profile.Acceleration, profile.Deceleration) > config.MaxAcceleration)
        {
            throw new MotionRejectedException(Name, $"加減速度超過上限 {config.MaxAcceleration}");
        }

        CheckInterlocks(new MotionRequest(Name, MotionKind.Move, position));
        return inner.MoveAbsoluteAsync(position, profile, cancellationToken);
    }

    private void CheckInterlocks(MotionRequest request)
    {
        foreach (var interlock in _interlocks)
        {
            if (interlock.CheckBlocked(request) is { } reason)
            {
                throw new MotionRejectedException(Name, $"[{interlock.Name}] {reason}");
            }
        }
    }
}
