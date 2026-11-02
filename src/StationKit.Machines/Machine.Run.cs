using StationKit.Stations;

namespace StationKit.Machines;

/// <summary>運行、暫停和循環停止。</summary>
public sealed partial class Machine
{
    private TaskCompletionSource _changed = NewSignal();
    private Task _runCompletion = Task.CompletedTask;

    /// <summary>
    /// 啟動所有工站。條件不滿足時同步拋 <see cref="MachineRejectedException"/>，沒有任何工站被啟動。
    /// 返回的 Task 在這一輪運行結束時完成；故障不會以異常形式拋出，而是體現為 <see cref="State"/> 為 Faulted 並觸發報警。
    /// </summary>
    public Task RunAsync(int? cycleLimit = null)
    {
        Task completion;
        lock (_sync)
        {
            if (_state != MachineState.Ready)
            {
                throw new MachineRejectedException("啟動", [$"當前狀態為 {_state}，只有就緒時才能啟動"]);
            }

            var reasons = StartBlockers();
            if (reasons.Count > 0)
            {
                throw new MachineRejectedException("啟動", reasons);
            }

            _state = MachineState.Running;
            // 在鎖內逐個啟動：否則異常停止可能插在中間，它取消了已啟動的工站，後面的工站卻還會被啟動
            var runs = new List<Task>();
            foreach (var station in _config.Stations)
            {
                runs.Add(station.RunAsync(cycleLimit));
            }

            completion = _runCompletion = WatchRunAsync(runs);
        }

        NotifyState(MachineState.Running);
        return completion;
    }

    /// <summary>
    /// 暫停：所有未結束的工站都到達安全點才算 Paused，Task 此時完成。取消只放棄等待，暫停請求仍然有效。
    /// </summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        lock (_sync)
        {
            if (_state == MachineState.Running)
            {
                _state = MachineState.Pausing;
                entered = true;
            }
            else if (_state is not (MachineState.Pausing or MachineState.Paused))
            {
                throw new MachineRejectedException("暫停", [$"當前狀態為 {_state}，只有運行中才能暫停"]);
            }
        }

        if (entered)
        {
            NotifyState(MachineState.Pausing);
            foreach (var station in _config.Stations)
            {
                station.Pause();
            }

            UpdatePausedState();
        }

        await WaitUntilAsync(() => State != MachineState.Pausing, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>從暫停恢復運行。條件不滿足時拋 <see cref="MachineRejectedException"/>，保持暫停。</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (_state is not (MachineState.Pausing or MachineState.Paused))
            {
                throw new MachineRejectedException("恢復", [$"當前狀態為 {_state}，只有暫停時才能恢復"]);
            }

            var reasons = ResumeBlockers();
            if (reasons.Count > 0)
            {
                throw new MachineRejectedException("恢復", reasons);
            }

            _state = MachineState.Running;
        }

        NotifyState(MachineState.Running);
        foreach (var station in _config.Stations)
        {
            station.Resume();
        }
    }

    /// <summary>
    /// 循環停止（操作員按停止）：工站運行到下一個安全點後結束，安全點不持有資源，所以不會產生不確定資源，
    /// 結束後回到 Ready 可以直接再啟動。<see cref="MachineConfig.CycleStopTimeout"/> 內沒停下，升級為異常停止並拋
    /// <see cref="TimeoutException"/>。
    /// </summary>
    public async Task CycleStopAsync()
    {
        lock (_sync)
        {
            if (_state is not (MachineState.Running or MachineState.Pausing or MachineState.Paused))
            {
                throw new MachineRejectedException("循環停止", [$"當前狀態為 {_state}，只有運行或暫停時才能循環停止"]);
            }

            _state = MachineState.CycleStopping;
        }

        NotifyState(MachineState.CycleStopping);
        foreach (var station in _config.Stations)
        {
            station.Pause();
        }

        var settled = await WaitUntilAsync(
            () => State != MachineState.CycleStopping || AllStationsAtSafePoint(),
            _config.CycleStopTimeout, CancellationToken.None).ConfigureAwait(false);
        if (!settled)
        {
            var unfinished = string.Join("、", _config.Stations.Where(s => !IsAtSafePoint(s)).Select(s => s.Name));
            var message = $"循環停止超時（{_config.CycleStopTimeout.TotalSeconds:0.#} 秒）：{unfinished} 沒有到達安全點";
            _alarms.Raise(MachineAlarms.StopTimeout, Name, message);
            await FailAsync(message).ConfigureAwait(false);
            throw new TimeoutException(message);
        }

        // 都在安全點，取消不會打斷工藝，只是結束循環
        await Task.WhenAll(_config.Stations.Select(StopStationAsync)).ConfigureAwait(false);
        await _runCompletion.ConfigureAwait(false);
    }

    private List<string> StartBlockers()
    {
        var reasons = ResumeBlockers();
        reasons.AddRange(_config.Stations.Where(s => s.State != StationState.Idle)
            .Select(s => $"工站 {s.Name} 當前為 {s.State}"));
        reasons.AddRange(_config.Axes.Select(a => DescribeUnhealthy(a.Axis)).OfType<string>());
        return reasons;
    }

    private List<string> ResumeBlockers()
    {
        var reasons = new List<string>();
        if (_alarms.HasBlockingAlarm())
        {
            reasons.Add("存在未清除的故障報警");
        }

        if (_config.Resources is { } resources)
        {
            reasons.AddRange(resources.Snapshot().Where(r => r.UncertainReason != null)
                .Select(r => $"資源 {r.Name} 處於不確定狀態：{r.UncertainReason}"));
        }

        return reasons;
    }

    private async Task WatchRunAsync(List<Task> runs)
    {
        await Task.WhenAll(runs).ConfigureAwait(false);
        var finished = false;
        lock (_sync)
        {
            if (_state is MachineState.Running or MachineState.Pausing or MachineState.Paused
                    or MachineState.CycleStopping &&
                _config.Stations.All(s => s.State == StationState.Idle))
            {
                _state = MachineState.Ready;
                finished = true;
            }
        }

        if (finished)
        {
            NotifyState(MachineState.Ready);
        }

        Pulse();
    }

    private void OnStationStateChanged(object? sender, StationState state)
    {
        // 不用事件參數判斷：StateChanged 在鎖外觸發，併發時順序可能和實際轉換相反
        UpdatePausedState();
        Pulse();
    }

    private void UpdatePausedState()
    {
        var entered = false;
        lock (_sync)
        {
            if (_state == MachineState.Pausing && AllStationsAtSafePoint() &&
                _config.Stations.Any(s => s.State == StationState.Paused))
            {
                _state = MachineState.Paused;
                entered = true;
            }
        }

        if (entered)
        {
            NotifyState(MachineState.Paused);
            Pulse();
        }
    }

    private bool AllStationsAtSafePoint() => _config.Stations.All(IsAtSafePoint);

    private static bool IsAtSafePoint(Station station) =>
        station.State is StationState.Paused or StationState.Idle;

    /// <summary>等條件成立。<paramref name="timeout"/> 為 null 時不限時；超時返回 false。</summary>
    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            // 先取信號再判斷：判斷之後才發生的變化一定會設置這個信號，不會漏掉
            var signal = Volatile.Read(ref _changed);
            if (condition())
            {
                return true;
            }

            var remaining = timeout is { } limit ? limit - _time.GetElapsedTime(start) : Timeout.InfiniteTimeSpan;
            if (timeout != null && remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await signal.Task.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 下一輪判斷超時
            }
        }
    }

    private void Pulse() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
