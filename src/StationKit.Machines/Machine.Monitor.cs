using StationKit.Abstractions.Motion;

namespace StationKit.Machines;

/// <summary>
/// 運行監視：定時檢查運行條件和軸狀態。只調非阻塞接口，不碰 DLL，也不佔卡鎖。
/// 響應時間沒有保證，不能替代硬件回路；作用是讓軟件跟上現場、報出原因。
/// </summary>
public sealed partial class Machine : IDisposable
{
    private readonly CancellationTokenSource _monitorCts = new();
    private Task _monitorTask = Task.CompletedTask;
    private int _disposed;

    /// <summary>停止監視並退訂事件。不會停軸：停機用 <see cref="AbortAsync"/>。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _monitorCts.Cancel();
        foreach (var station in _config.Stations)
        {
            station.StateChanged -= OnStationStateChanged;
        }

        _alarms.Raised -= OnAlarmRaised;
        _monitorTask.ContinueWith(_ => _monitorCts.Dispose(), TaskScheduler.Default);
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_config.MonitorPeriod, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    EvaluateGuards();
                    EvaluateAxisHealth();
                }
                catch (Exception ex)
                {
                    // 監視壞了卻沒人知道，比沒有監視更危險。報出後繼續：下一輪還可能發現真正的違反
                    var message = $"運行監視異常：{ex.Message}";
                    _ = TriggerAbort(Name, message, StopMode.Decelerate, toSafePoint: false);
                    _alarms.Raise(MachineAlarms.MonitorFault, Name, message);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Dispose
        }
    }

    private void EvaluateGuards()
    {
        var state = State;
        foreach (var guard in _config.Guards)
        {
            if (DescribeViolation(guard) is not { } violation)
            {
                continue;
            }

            var inAutomaticScope = (AutomaticScope(state) & guard.Scope) != MachineGuardScope.None;
            var mode = guard.StopMode!.Value;
            var source = $"{Name}/{guard.Name}";
            if (inAutomaticScope)
            {
                // 先觸發再報警：首出原因要帶信號名，不能被報警回調搶先寫成減速停止
                _ = TriggerAbort(source, violation, mode, toSafePoint: false);
                _alarms.Raise(MachineAlarms.GuardViolated, source, violation);
            }
            else if (AutomaticScope(state) == MachineGuardScope.None && guard.Scope.HasFlag(MachineGuardScope.ManualMotion) &&
                     AnyAxisMoving())
            {
                _ = StopAllAxesAsync(mode);
                _ = TriggerAbort(source, violation, mode, toSafePoint: false);
                _alarms.Raise(MachineAlarms.GuardViolated, source, violation);
            }

            // 其餘情況不報警：軸不動時門開著是合法狀態，只是啟動和恢復時會被拒絕
        }
    }

    private void EvaluateAxisHealth()
    {
        // 補上「空閒時卡故障沒有報警」：快照過期、整卡故障都會讓 GetStatus 返回 Alarm。
        // 其他狀態不查：未初始化時軸本來就可以未使能，故障後要等人工處理
        if (State is not (MachineState.Ready or MachineState.Running or MachineState.Pausing or MachineState.Paused
            or MachineState.CycleStopping))
        {
            return;
        }

        foreach (var axis in _config.Axes.Select(a => a.Axis))
        {
            if (DescribeUnhealthy(axis) is { } problem)
            {
                var source = $"{Name}/{axis.Name}";
                _ = TriggerAbort(source, problem, StopMode.Decelerate, toSafePoint: false);
                _alarms.Raise(MachineAlarms.AxisUnhealthy, source, problem);
            }
        }
    }

    /// <summary>當前狀態屬於哪個自動範圍。異常停止中兩個範圍都算：違反時還要能升級為立即停止。</summary>
    private static MachineGuardScope AutomaticScope(MachineState state) => state switch
    {
        MachineState.Initializing => MachineGuardScope.Initialize,
        MachineState.Running or MachineState.Pausing or MachineState.CycleStopping => MachineGuardScope.Run,
        MachineState.Aborting => MachineGuardScope.Initialize | MachineGuardScope.Run,
        _ => MachineGuardScope.None,
    };

    private bool AnyAxisMoving() =>
        _config.Axes.Any(a => a.Axis.GetStatus().State is AxisState.Moving or AxisState.Homing);

    /// <summary>滿足時返回 null。數據不可信等同不滿足：讀不到安全門，不能當作門是關的。</summary>
    private static string? DescribeViolation(MachineGuard guard)
    {
        var reading = guard.Input.GetReading();
        if (reading.Value is not { } value)
        {
            return $"{guard.Name}：{guard.Input.Name} 狀態不可信：{reading.FaultReason}";
        }

        return value == guard.Expected
            ? null
            : $"{guard.Name}：需要 {guard.Input.Name} {Describe(guard.Expected)}，當前{Describe(value)}";
    }

    private static string Describe(bool value) => value ? "有效" : "無效";

    private List<string> GuardBlockers(MachineGuardScope scope) =>
        [.. _config.Guards.Where(g => (g.Scope & scope) != MachineGuardScope.None)
            .Select(DescribeViolation).OfType<string>()];
}
