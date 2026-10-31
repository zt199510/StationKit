namespace StationKit.Abstractions.Motion;

/// <summary>
/// 單軸運動接口。所有廠商實現（雷賽、固高、正運動、EtherCAT、模擬）都必須遵守下列語義：
/// <list type="bullet">
/// <item>運動方法的 Task 在「到位」時完成，而不是在指令下發時完成。</item>
/// <item>取消必須在指令下發前檢查；一旦已下發，取消會先停軸，再拋出 <see cref="OperationCanceledException"/>。</item>
/// <item>報警、限位、被外部停止時拋出 <see cref="MotionFaultException"/>，絕不返回 false 讓調用方忽略。</item>
/// <item>例外：點動（<see cref="JogAsync"/>）沒有目標位置，取消表示「鬆開按鈕」，停穩後正常完成。</item>
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

    /// <summary>
    /// 速度模式點動：按住期間一直運動，<paramref name="cancellationToken"/> 取消（鬆開按鈕）時減速停止，
    /// 停穩後 Task 正常完成，不拋 <see cref="OperationCanceledException"/>；下發前已取消則不運動直接完成。
    /// 觸發點動方向上的限位、報警、被外部停止時拋出 <see cref="MotionFaultException"/>。
    /// <para>
    /// 這是裸的速度模式點動，不檢查軟限位。界面應通過 GuardedAxis 調用（已回零時會改成走到軟限位邊界），
    /// 並用 JogSession 加看門狗，防止鬆開事件丟失導致軸一直走。
    /// </para>
    /// </summary>
    Task JogAsync(JogDirection direction, MotionProfile profile, CancellationToken cancellationToken);

    /// <summary>停止運動。停止本身不可取消：一個不能保證停下來的停止沒有意義。</summary>
    Task StopAsync(StopMode mode = StopMode.Decelerate);

    Task ResetAlarmAsync(CancellationToken cancellationToken = default);
}
