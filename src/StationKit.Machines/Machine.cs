using StationKit.Abstractions.Motion;
using StationKit.Alarms;
using StationKit.Safety;
using StationKit.Stations;

namespace StationKit.Machines;

public enum MachineState
{
    /// <summary>軸的位置和工站的進度不可信，不能啟動。</summary>
    Uninitialized,
    Initializing,

    /// <summary>已初始化且機構在起點，可以啟動。</summary>
    Ready,
    Running,

    /// <summary>已請求暫停，等待所有工站到達安全點。</summary>
    Pausing,
    Paused,

    /// <summary>工站運行到下一個安全點後結束，不產生不確定資源。</summary>
    CycleStopping,

    /// <summary>異常停止進行中。</summary>
    Aborting,

    /// <summary>已停下，等待人工處理。</summary>
    Faulted,
}

/// <summary>首出原因。連帶停止會引發次生報警，現場最需要知道的是第一個。</summary>
public sealed record MachineFault(DateTimeOffset At, string Source, string Description);

/// <summary>
/// 整機協調：把工站、軸、資源、報警統籌成一台設備的啟動、停止和恢復。
/// <para>
/// 這<b>不是安全功能</b>：它跑在托管線程裡，響應時間沒有保證。急停和安全門必須有硬件回路，
/// 這裡只負責讓軟件跟上現場、報出原因、不再下發新運動。設計說明見 docs/design/machine-coordinator.md。
/// </para>
/// </summary>
public sealed partial class Machine
{
    private readonly object _sync = new();
    private readonly MachineConfig _config;
    private readonly AlarmCenter _alarms;
    private readonly TimeProvider _time;
    private MachineState _state = MachineState.Uninitialized;
    private MachineFault? _firstFault;

    public Machine(MachineConfig config, AlarmCenter alarms, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(alarms);
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new MachineConfigException(errors);
        }

        _config = config;
        _alarms = alarms;
        _time = timeProvider ?? TimeProvider.System;
        foreach (var station in config.Stations)
        {
            station.StateChanged += OnStationStateChanged;
        }

        alarms.Raised += OnAlarmRaised;
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
    }

    public string Name => _config.Name;

    public event EventHandler<MachineState>? StateChanged;

    public MachineState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public MachineFault? FirstFault
    {
        get
        {
            lock (_sync)
            {
                return _firstFault;
            }
        }
    }

    /// <summary>
    /// 初始化：復位軸報警 → 安全初始化 → 按序執行啟動步驟 → 檢查軸狀態，全部通過才進入 Ready。
    /// 條件不滿足時拋 <see cref="MachineRejectedException"/>，狀態不變；
    /// 開始之後失敗則停所有軸、報 MCH-004、進入 Faulted，並拋 <see cref="MachineInitializationException"/>。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cts;
        Task work;
        lock (_sync)
        {
            if (_state != MachineState.Uninitialized)
            {
                throw new MachineRejectedException("初始化", [$"當前狀態為 {_state}，只有未初始化時才能初始化"]);
            }

            var reasons = InitializeBlockers();
            if (reasons.Count > 0)
            {
                throw new MachineRejectedException("初始化", reasons);
            }

            _state = MachineState.Initializing;
            cts = _initCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            work = RunInitializationAsync(cts.Token);
            _activeWork = [work];
        }

        NotifyState(MachineState.Initializing);
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (MachineInitializationException ex)
        {
            // 先觸發再報警：首出原因要帶步驟名，不能被報警回調搶先寫成報警代碼
            var abort = TriggerAbort(Name, ex.Message, StopMode.Decelerate, toSafePoint: false);
            _alarms.Raise(MachineAlarms.InitializationFailed, Name, ex.Message);
            await abort.ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TriggerAbort(Name, "初始化被取消", StopMode.Decelerate, toSafePoint: false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _initCts = null;
                _activeWork = [];
            }

            cts.Dispose();
        }

        Task? aborted = null;
        var ready = false;
        lock (_sync)
        {
            if (_state == MachineState.Initializing)
            {
                _state = MachineState.Ready;
                ready = true;
            }
            else
            {
                aborted = _abortTask;
            }
        }

        if (ready)
        {
            NotifyState(MachineState.Ready);
            return;
        }

        await aborted!.ConfigureAwait(false);
        throw new MachineRejectedException("完成初始化", [$"被異常停止打斷：{FirstFault?.Description}"]);
    }

    private List<string> InitializeBlockers()
    {
        var reasons = new List<string>();
        if (_alarms.HasBlockingAlarm())
        {
            reasons.Add("存在未清除的故障報警");
        }

        reasons.AddRange(GuardBlockers(MachineGuardScope.Initialize));
        return reasons;
    }

    private async Task RunInitializationAsync(CancellationToken token)
    {
        // 放到線程池：步驟可能在第一個 await 之前就阻塞很久（例如同步的 DLL 調用）
        await Task.Yield();

        await RunStepAsync("復位軸報警", async ct =>
        {
            foreach (var axis in _config.Axes.Select(a => a.Axis).Where(a => a.GetStatus().State == AxisState.Alarm))
            {
                await axis.ResetAlarmAsync(ct).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
        await RunStepAsync("安全初始化", ct => SafetyStartup.RunAsync(_config.Axes, ct), token).ConfigureAwait(false);

        foreach (var step in _config.StartupSteps)
        {
            await RunStepAsync(step.Name, step.Action, token).ConfigureAwait(false);
        }

        await RunStepAsync("最終檢查", _ =>
        {
            // 步驟沒有覆蓋某根軸時在這裡發現，不要等到運行中才報未回零
            var problems = _config.Axes.Select(a => DescribeUnhealthy(a.Axis)).OfType<string>()
                .Concat(GuardBlockers(MachineGuardScope.Initialize)).ToList();
            return problems.Count == 0
                ? Task.CompletedTask
                : throw new InvalidOperationException(string.Join("；", problems));
        }, token).ConfigureAwait(false);
    }

    private static async Task RunStepAsync(string name, Func<CancellationToken, Task> action, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await action(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MachineInitializationException(name, ex);
        }
    }

    private static string? DescribeUnhealthy(IAxis axis)
    {
        var status = axis.GetStatus();
        return status.State switch
        {
            AxisState.Alarm => $"軸 {axis.Name} 報警：{status.AlarmReason}",
            AxisState.Disabled => $"軸 {axis.Name} 未使能",
            _ => status.IsHomed ? null : $"軸 {axis.Name} 未回零",
        };
    }

    private static async Task StopStationAsync(Station station)
    {
        try
        {
            await station.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Station.StopAsync 只等循環結束，循環的異常已經在工站內部轉成報警
        }
    }

    /// <summary>
    /// 停一根軸。放到線程池並單獨限時：<see cref="IAxis.StopAsync"/> 可能同步拋異常，也可能卡在卡鎖上，
    /// 不能讓一根軸影響其他軸停下。永不拋異常，失敗報 MCH-003。
    /// </summary>
    private async Task StopAxisAsync(IAxis axis, StopMode mode)
    {
        try
        {
            await Task.Run(() => axis.StopAsync(mode)).WaitAsync(_config.StopTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _alarms.Raise(MachineAlarms.StopTimeout, $"{Name}/{axis.Name}", $"軸 {axis.Name} 停止失敗：{ex.Message}");
        }
    }

    private void Transition(MachineState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        NotifyState(state);
    }

    /// <summary>隔離訂閱者異常：界面的一個錯誤不能打斷停機流程。</summary>
    private void NotifyState(MachineState state)
    {
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _alarms.Raise(AlarmCatalog.SubscriberFaultCode, Name, ex.Message);
        }
    }
}
