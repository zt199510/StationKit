using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Safety;
using StationKit.Stations;

namespace StationKit.Machines;

/// <summary>運行條件在哪些階段生效。門在暫停時能否打開、回零時是否檢查，每台設備不同，必須明確配置。</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1008",
    Justification = "None 表示未配置，配置校驗會拒絕，不是合法的默認值")]
public enum MachineGuardScope
{
    None = 0,

    /// <summary>初始化期間。</summary>
    Initialize = 1,

    /// <summary>自動運行期間（運行、暫停中、循環停止中）。</summary>
    Run = 2,

    /// <summary>非自動運行時有軸在動（例如手動點動）。</summary>
    ManualMotion = 4,
}

/// <summary>運行條件，例如「安全門關閉」。數據不可信等同不滿足；違反後鎖存，信號恢復也不會自動繼續。</summary>
/// <param name="StopMode">違反時的停軸方式。null 為未指定，拒絕創建 <see cref="Machine"/>。</param>
public sealed record MachineGuard(string Name, IDigitalInput Input, bool Expected, StopMode? StopMode)
{
    public MachineGuardScope Scope { get; init; }
}

/// <summary>初始化時按順序執行的一步，例如「氣缸縮回」「Z 軸回零」。</summary>
public sealed record MachineStartupStep(string Name, Func<CancellationToken, Task> Action);

public enum StationFaultPolicy
{
    /// <summary>其他工站立即取消：不再繼續運動，代價是持有中的資源進入不確定。</summary>
    AbortAll,

    /// <summary>其他工站運行到安全點後停下，再進入 Faulted。超時升級為 <see cref="AbortAll"/>。</summary>
    OthersToSafePoint,
}

public sealed record MachineConfig
{
    public required string Name { get; init; }

    public required IReadOnlyList<Station> Stations { get; init; }

    public required IReadOnlyList<AxisSafetyBinding> Axes { get; init; }

    public required IReadOnlyList<MachineStartupStep> StartupSteps { get; init; }

    public required IReadOnlyList<MachineGuard> Guards { get; init; }

    public required MachineMotionGate MotionGate { get; init; }

    /// <summary>工站故障時其他工站怎麼辦。必填，沒有默認值。運行條件和軸的違反永遠立即異常停止，不受此影響。</summary>
    public required StationFaultPolicy? OnStationFault { get; init; }

    public required TimeSpan MonitorPeriod { get; init; }

    /// <summary>異常停止時，等工站和軸停下的上限。</summary>
    public required TimeSpan StopTimeout { get; init; }

    /// <summary>等工站到達安全點的上限，超時升級為立即停止。</summary>
    public required TimeSpan CycleStopTimeout { get; init; }

    public ResourceArbiter? Resources { get; init; }

    /// <summary>
    /// 復位時最先調用的硬件復位，例如雷賽的 <c>Poller.ResetFault()</c>（不在 IAxis 上）。必須可以重複調用：
    /// 復位條件不滿足時操作員會重試。
    /// </summary>
    public Func<CancellationToken, Task>? ResetHardware { get; init; }

    /// <summary>返回所有問題（而不是遇到第一個就停），讓現場一次看到全部要改的地方。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("整機名稱未配置");
        }

        if (Stations.Count == 0)
        {
            errors.Add("沒有配置工站");
        }

        errors.AddRange(Duplicates("工站", Stations.Select(s => s.Name)));
        errors.AddRange(Duplicates("軸", Axes.Select(a => a.Axis.Name)));
        errors.AddRange(Duplicates("運行條件", Guards.Select(g => g.Name)));

        foreach (var guard in Guards)
        {
            if (guard.StopMode is null)
            {
                errors.Add($"運行條件 {guard.Name}：未指定停軸方式");
            }

            if (guard.Scope == MachineGuardScope.None)
            {
                errors.Add($"運行條件 {guard.Name}：未指定生效範圍");
            }
        }

        if (OnStationFault is null)
        {
            errors.Add("未指定工站故障策略");
        }

        foreach (var (name, value) in new[]
                 {
                     (nameof(MonitorPeriod), MonitorPeriod),
                     (nameof(StopTimeout), StopTimeout),
                     (nameof(CycleStopTimeout), CycleStopTimeout),
                 })
        {
            if (value <= TimeSpan.Zero)
            {
                errors.Add($"{name} 必須大於 0（{value}）");
            }
        }

        return errors;
    }

    private static IEnumerable<string> Duplicates(string kind, IEnumerable<string> names) =>
        names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{kind} {g.Key} 重名");
}

public sealed class MachineConfigException(IReadOnlyList<string> errors)
    : Exception("整機配置不正確：" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// 操作被拒絕，或進行中被異常停止打斷。<see cref="Reasons"/> 列出全部原因，現場一次看到要處理的地方。
/// 被拒絕時狀態沒有改變；被打斷時整機已進入 Faulted。
/// </summary>
public sealed class MachineRejectedException(string operation, IReadOnlyList<string> reasons)
    : InvalidOperationException($"不能{operation}：" + string.Join("；", reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>初始化在 <see cref="Step"/> 失敗。整機已停下並進入 Faulted。</summary>
public sealed class MachineInitializationException(string step, Exception inner)
    : Exception($"初始化步驟「{step}」失敗：{inner.Message}", inner)
{
    public string Step { get; } = step;
}
