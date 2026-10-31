using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Safety;

/// <summary>
/// 安全包裝器：在指令下發前檢查回零、軟限位、速度上限和互鎖規則（點位運動和點動都檢查），不通過則拋出
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

        CheckProfile(profile);
        CheckInterlocks(new MotionRequest(Name, MotionKind.Move, position));
        return inner.MoveAbsoluteAsync(position, profile, cancellationToken);
    }

    /// <summary>
    /// 點動。已回零和未回零走不同的路：
    /// <list type="bullet">
    /// <item>已回零：不用速度模式，改成點位運動到軟限位邊界，鬆開時停止。
    /// 即使控制卡上的軟限位參數配錯，軸也不可能越過邊界。</item>
    /// <item>未回零：坐標不可信，只允許不超過 <see cref="AxisSafetyConfig.UnhomedJogMaxVelocity"/> 的低速速度模式，
    /// 靠硬限位兜底；未配置該速度則拒絕。</item>
    /// </list>
    /// </summary>
    public async Task JogAsync(JogDirection direction, MotionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        CheckProfile(profile);

        var status = inner.GetStatus();
        if (!status.IsHomed)
        {
            if (double.IsNaN(config.UnhomedJogMaxVelocity))
            {
                throw new MotionRejectedException(Name, "未回零，且未配置未回零點動速度，禁止點動");
            }

            if (profile.Velocity > config.UnhomedJogMaxVelocity)
            {
                throw new MotionRejectedException(Name,
                    $"未回零，點動速度 {profile.Velocity} 超過未回零上限 {config.UnhomedJogMaxVelocity}");
            }

            CheckInterlocks(new MotionRequest(Name, MotionKind.Jog) { Direction = direction });
            await inner.JogAsync(direction, profile, cancellationToken).ConfigureAwait(false);
            return;
        }

        var boundary = direction == JogDirection.Positive ? config.SoftLimitMax : config.SoftLimitMin;
        var remaining = (boundary - status.Position) * (int)direction;
        if (!(remaining > 0))
        {
            throw new MotionRejectedException(Name, $"已在軟限位邊界 {boundary}，不能繼續{Describe(direction)}點動");
        }

        CheckInterlocks(new MotionRequest(Name, MotionKind.Jog, boundary) { Direction = direction });
        try
        {
            await inner.MoveAbsoluteAsync(boundary, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 鬆開按鈕：內層已先停軸（或根本沒有下發），點動正常結束
        }
    }

    private void CheckProfile(MotionProfile profile)
    {
        if (profile.Velocity > config.MaxVelocity)
        {
            throw new MotionRejectedException(Name, $"速度 {profile.Velocity} 超過上限 {config.MaxVelocity}");
        }

        if (Math.Max(profile.Acceleration, profile.Deceleration) > config.MaxAcceleration)
        {
            throw new MotionRejectedException(Name, $"加減速度超過上限 {config.MaxAcceleration}");
        }
    }

    private static string Describe(JogDirection direction) => direction == JogDirection.Positive ? "正向" : "負向";

    private void CheckInterlocks(MotionRequest request)
    {
        foreach (var interlock in _interlocks)
        {
            string? reason;
            try
            {
                reason = interlock.CheckBlocked(request);
            }
            catch (Exception ex)
            {
                // 規則本身出錯（例如讀 IO 拋異常）：軸沒有動，是「拒絕」不是運動故障，工站按拒絕處理
                reason = $"互鎖檢查異常：{ex.Message}";
            }

            if (reason != null)
            {
                throw new MotionRejectedException(Name, $"[{interlock.Name}] {reason}");
            }
        }
    }
}
