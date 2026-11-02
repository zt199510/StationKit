using StationKit.Abstractions.Safety;

namespace StationKit.Machines;

/// <summary>
/// 整機運動閘：異常停止期間拒絕一切新運動，由 GuardedAxis 當作互鎖檢查。
/// 工站取消是協作式的，軸停下之後，沒有及時響應的循環還可能下發下一步，這裡把它擋住。
/// <para>
/// 單獨成類而不放在 <see cref="Machine"/> 上：工站要拿 GuardedAxis，GuardedAxis 要拿互鎖，Machine 又要拿工站，
/// 閘先創建才能解開這個循環依賴。只對經過 GuardedAxis 的運動有效，工站只能拿包裝後的軸。
/// </para>
/// </summary>
public sealed class MachineMotionGate : IMotionInterlock
{
    private volatile string? _closedReason;

    public string Name => "整機運動閘";

    public bool IsClosed => _closedReason != null;

    public string? CheckBlocked(MotionRequest request) => _closedReason;

    internal void Close(string reason) => _closedReason = reason;

    internal void Open() => _closedReason = null;
}
