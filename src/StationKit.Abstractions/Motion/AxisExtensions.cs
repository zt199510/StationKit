namespace StationKit.Abstractions.Motion;

public static class AxisExtensions
{
    /// <summary>
    /// 相對運動（寸動）：以當前實際位置為起點換算成絕對目標，再調用 <see cref="IAxis.MoveAbsoluteAsync"/>。
    /// 做成擴展方法而不是接口成員：各廠商實現不用重複寫，經過 GuardedAxis 時換算後的目標同樣受軟限位檢查。
    /// 軸必須靜止，運動中位置一直在變，換算沒有意義。
    /// </summary>
    public static Task MoveRelativeAsync(this IAxis axis, double distance, MotionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (!double.IsFinite(distance))
        {
            throw new ArgumentException($"相對距離非法：{distance}", nameof(distance));
        }

        var status = axis.GetStatus();
        if (status.State != AxisState.Idle)
        {
            throw new MotionRejectedException(axis.Name, $"相對運動要求軸靜止，當前狀態 {status.State}");
        }

        return axis.MoveAbsoluteAsync(status.Position + distance, profile, cancellationToken);
    }
}
