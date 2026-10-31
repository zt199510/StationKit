namespace StationKit.Abstractions.Motion;

/// <summary>
/// 單軸運動接口。所有廠商實現（雷賽、固高、正運動、EtherCAT、模擬）都必須遵守下列語義：
/// <list type="bullet">
/// <item>運動方法的 Task 在「到位」時完成，而不是在指令下發時完成。</item>
/// <item>取消必須在指令下發前檢查；一旦已下發，取消會先停軸，再拋出 <see cref="OperationCanceledException"/>。</item>
/// <item>報警、限位、被外部停止時拋出 <see cref="MotionFaultException"/>，絕不返回 false 讓調用方忽略。</item>
/// </list>
/// </summary>
public interface IAxis
{
    string Name { get; }

    /// <summary>讀取當前狀態快照。必須是非阻塞、線程安全的。</summary>
    AxisStatus GetStatus();

    Task EnableAsync(CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);

    Task HomeAsync(CancellationToken cancellationToken = default);

    Task MoveAbsoluteAsync(double position, MotionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>停止運動。停止本身不可取消：一個不能保證停下來的停止沒有意義。</summary>
    Task StopAsync(StopMode mode = StopMode.Decelerate);

    Task ResetAlarmAsync(CancellationToken cancellationToken = default);
}
