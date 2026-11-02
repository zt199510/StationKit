using StationKit.Abstractions.Motion;
using StationKit.Alarms;
using StationKit.Stations;

namespace StationKit.Machines;

/// <summary>異常停止。設計說明見 docs/design/machine-coordinator.md 3.3 和第 4 節。</summary>
public sealed partial class Machine
{
    private StopMode _abortMode;
    private bool _abortEscalated;
    private Task _abortTask = Task.CompletedTask;
    private List<Task> _activeWork = [];
    private CancellationTokenSource? _initCts;

    /// <summary>
    /// 人工異常停止：立即取消工站並停軸，之後必須人工恢復並重新初始化。沒有默認的停軸方式，由界面按操作決定。
    /// 未初始化或已故障時沒有工站在跑，只停軸（例如手動點動中），狀態不變。
    /// 進行中再次調用可以從減速升級為立即停止。
    /// </summary>
    public Task AbortAsync(StopMode mode, string reason = "人工異常停止")
    {
        lock (_sync)
        {
            if (_state is MachineState.Uninitialized or MachineState.Faulted)
            {
                return StopAllAxesAsync(mode);
            }
        }

        return TriggerAbort(Name, reason, mode, toSafePoint: false);
    }

    /// <summary>
    /// 觸發異常停止並立即返回。可能在報警回調裡被調用，此時工站還在 catch 裡等回調返回：
    /// 這裡不能等工站，否則死鎖，所以真正的停機放到線程池。多次觸發只執行一次流程，首出原因不被覆蓋。
    /// </summary>
    private Task TriggerAbort(string source, string description, StopMode mode, bool toSafePoint)
    {
        var entered = false;
        var upgraded = false;
        Task abort;
        lock (_sync)
        {
            switch (_state)
            {
                case MachineState.Uninitialized or MachineState.Faulted:
                    return Task.CompletedTask;
                case MachineState.Aborting:
                    _abortEscalated |= !toSafePoint;
                    upgraded = mode == StopMode.Immediate && _abortMode != StopMode.Immediate;
                    if (upgraded)
                    {
                        _abortMode = mode;
                    }

                    break;
                default:
                    _firstFault ??= new MachineFault(_time.GetUtcNow(), source, description);
                    _state = MachineState.Aborting;
                    _abortMode = mode;
                    _abortEscalated = !toSafePoint;
                    if (!toSafePoint)
                    {
                        // 在觸發時就關：放到線程池任務裡關，這段空窗內工站還能下發新運動
                        CloseMotionGate();
                    }

                    _abortTask = Task.Run(RunAbortAsync);
                    entered = true;
                    break;
            }

            abort = _abortTask;
        }

        if (entered)
        {
            NotifyState(MachineState.Aborting);
        }

        if (upgraded)
        {
            _ = StopAllAxesAsync(StopMode.Immediate);
        }

        Pulse();
        return abort;
    }

    /// <summary>順序見提案 3.3。永不拋異常，無論哪一步出問題，最後都進入 Faulted。</summary>
    private async Task RunAbortAsync()
    {
        try
        {
            await StationsToSafePointAsync().ConfigureAwait(false);

            // 先關閘、先取消、再停軸：先停軸的話，被外部停止的軸拋 MotionFaultException，工站會當成一次新的故障。
            // 不能等工站取消完再停軸：循環不響應令牌時軸就一直走
            CloseMotionGate();
            CancelInitialization();
            var stations = _config.Stations.Select(StopStationAsync).ToList();
            await StopAllAxesAsync(CurrentAbortMode()).ConfigureAwait(false);

            var allFinished = await WaitForWorkAsync(stations).ConfigureAwait(false);

            // 再停一次：補上取消和停軸之間已通過檢查、正要下發的運動
            await StopAllAxesAsync(CurrentAbortMode()).ConfigureAwait(false);

            // 還有工站沒結束就不開閘：它隨時可能繼續下發運動
            if (allFinished)
            {
                _config.MotionGate.Open();
            }
        }
        catch (Exception ex)
        {
            _alarms.Raise(MachineAlarms.MonitorFault, Name, $"異常停止流程出錯：{ex.Message}");
        }
        finally
        {
            Transition(MachineState.Faulted);
            Pulse();
        }
    }

    private async Task StationsToSafePointAsync()
    {
        if (IsAbortEscalated())
        {
            return;
        }

        foreach (var station in _config.Stations)
        {
            station.Pause();
        }

        var settled = await WaitUntilAsync(() => IsAbortEscalated() || AllStationsSettled(),
            _config.CycleStopTimeout, CancellationToken.None).ConfigureAwait(false);
        if (!settled)
        {
            var unfinished = string.Join("、",
                _config.Stations.Where(s => !IsSettled(s)).Select(s => s.Name));
            _alarms.Raise(MachineAlarms.StopTimeout, Name,
                $"工站沒有在 {_config.CycleStopTimeout.TotalSeconds:0.#} 秒內到達安全點：{unfinished}，改為立即停止");
        }
    }

    private async Task<bool> WaitForWorkAsync(List<Task> stationStops)
    {
        List<Task> work;
        lock (_sync)
        {
            work = [.. stationStops, .. _activeWork];
        }

        try
        {
            await Task.WhenAll(work).WaitAsync(_config.StopTimeout, _time).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            var unfinished = string.Join("、", _config.Stations.Where(s => !IsSettled(s)).Select(s => s.Name));
            _alarms.Raise(MachineAlarms.StopTimeout, Name,
                $"工站沒有在 {_config.StopTimeout.TotalSeconds:0.#} 秒內結束：{unfinished}");
            return false;
        }
        catch (Exception)
        {
            // 初始化的失敗由 InitializeAsync 報告，這裡只等它結束；WhenAll 要等全部結束才會完成
            return true;
        }
    }

    private void CloseMotionGate() => _config.MotionGate.Close($"整機異常停止中：{FirstFault?.Description}");

    private void CancelInitialization()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _initCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 初始化剛好結束
        }
    }

    private Task StopAllAxesAsync(StopMode mode) =>
        Task.WhenAll(_config.Axes.Select(a => StopAxisAsync(a.Axis, mode)));

    private StopMode CurrentAbortMode()
    {
        lock (_sync)
        {
            return _abortMode;
        }
    }

    private bool IsAbortEscalated()
    {
        lock (_sync)
        {
            return _abortEscalated;
        }
    }

    private bool AllStationsSettled() => _config.Stations.All(IsSettled);

    private static bool IsSettled(Station station) =>
        station.State is StationState.Paused or StationState.Idle or StationState.Faulted;

    private void OnAlarmRaised(object? sender, ActiveAlarm alarm)
    {
        if (alarm.Definition.Severity != AlarmSeverity.Error)
        {
            return;
        }

        _ = TriggerAbort(alarm.Source, $"{alarm.Definition.Code} {alarm.Definition.Message}：{alarm.Detail}",
            StopMode.Decelerate, toSafePoint: IsStationSource(alarm.Source) && OthersGoToSafePoint);
    }

    private bool OthersGoToSafePoint => _config.OnStationFault == StationFaultPolicy.OthersToSafePoint;

    private bool IsStationSource(string source) => _config.Stations.Any(s =>
        source == s.Name || source.StartsWith(s.Name + "/", StringComparison.Ordinal));

    /// <summary>
    /// 只訂閱報警會漏：Raise 對相同代碼和來源去重，已有未清除的報警時不會再觸發事件。
    /// 正常順序是工站先報警再進入 Faulted，首出原因帶報警詳情；這裡是兜底。
    /// </summary>
    private void TriggerIfStationFaulted(Station station)
    {
        if (station.State == StationState.Faulted)
        {
            _ = TriggerAbort(station.Name, $"工站 {station.Name} 故障", StopMode.Decelerate, OthersGoToSafePoint);
        }
    }
}
