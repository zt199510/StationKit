using StationKit.Alarms;

namespace StationKit.Stations;

public enum StationState
{
    Idle,
    Running,

    /// <summary>已請求暫停，等待運行到下一個安全點。</summary>
    Pausing,
    Paused,
    Stopping,
    Faulted,
}

public sealed class StationContext
{
    private readonly Station _station;

    internal StationContext(Station station, CancellationToken token)
    {
        _station = station;
        Token = token;
    }

    public string StationName => _station.Name;

    public CancellationToken Token { get; }

    /// <summary>安全點：在這裡響應暫停和停止。只在機構處於安全位置、且不持有共享資源時調用。</summary>
    public Task CheckpointAsync() => _station.CheckpointAsync(Token);
}

/// <summary>
/// 工站基類：循環執行 <see cref="RunCycleAsync"/>，負責狀態機、暫停 / 停止、故障轉報警和節拍統計。
/// 子類只寫一個循環的工藝步驟。
/// <para>
/// 狀態規則：暫停只在安全點生效；任何未處理的異常都會使工站進入 Faulted 並觸發報警，
/// 必須先清除本工站的報警才能復位。
/// </para>
/// </summary>
public abstract class Station
{
    private readonly object _sync = new();
    private StationState _state = StationState.Idle;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource? _resumeSignal;
    private Task _runTask = Task.CompletedTask;

    protected Station(string name, AlarmCenter alarms, TimeProvider? timeProvider = null)
    {
        Name = name;
        Alarms = alarms;
        Time = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    public CycleStatistics Statistics { get; } = new();

    protected AlarmCenter Alarms { get; }

    protected TimeProvider Time { get; }

    public event EventHandler<StationState>? StateChanged;

    public StationState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 啟動工站。返回的 Task 在工站停止、完成指定循環數或故障時完成；故障不會以異常形式拋出，
    /// 而是體現為 <see cref="State"/> 為 Faulted 並觸發報警。
    /// </summary>
    public Task RunAsync(int? cycleLimit = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_state != StationState.Idle)
            {
                throw new InvalidOperationException($"工站 {Name} 當前為 {_state}，不能啟動");
            }

            if (Alarms.HasBlockingAlarm(Name))
            {
                throw new InvalidOperationException($"工站 {Name} 存在未清除的報警，不能啟動");
            }

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _state = StationState.Running;
            _runTask = LoopAsync(cycleLimit, _runCts.Token);
        }

        StateChanged?.Invoke(this, StationState.Running);
        return _runTask;
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_state != StationState.Running)
            {
                return;
            }

            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _state = StationState.Pausing;
        }

        StateChanged?.Invoke(this, StationState.Pausing);
    }

    public void Resume()
    {
        TaskCompletionSource? signal;
        lock (_sync)
        {
            if (_state is not (StationState.Pausing or StationState.Paused))
            {
                return;
            }

            signal = _resumeSignal;
            _resumeSignal = null;
            _state = StationState.Running;
        }

        signal?.TrySetResult();
        StateChanged?.Invoke(this, StationState.Running);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task runTask;
        lock (_sync)
        {
            if (_state is StationState.Idle or StationState.Faulted)
            {
                return;
            }

            _state = StationState.Stopping;
            cts = _runCts;
            runTask = _runTask;
        }

        StateChanged?.Invoke(this, StationState.Stopping);
        await (cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
    }

    /// <summary>故障復位。要求本工站的報警已被清除（即故障已被人處理過）。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            if (_state != StationState.Faulted)
            {
                return;
            }

            if (Alarms.HasBlockingAlarm(Name))
            {
                throw new InvalidOperationException($"工站 {Name} 仍有未清除的報警，請先處理並清除報警");
            }

            _state = StationState.Idle;
        }

        StateChanged?.Invoke(this, StationState.Idle);
    }

    /// <summary>一個循環的工藝步驟。</summary>
    protected abstract Task RunCycleAsync(StationContext context);

    /// <summary>故障後的善後動作，例如停止本工站所有軸。不應拋出異常。</summary>
    protected virtual Task OnFaultedAsync(Exception exception) => Task.CompletedTask;

    /// <summary>把異常映射成報警代碼。子類可覆蓋以細分。</summary>
    protected virtual string ClassifyFault(Exception exception) =>
        exception is ResourceTimeoutException ? StationAlarms.ResourceTimeout : StationAlarms.CycleFault;

    internal async Task CheckpointAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TaskCompletionSource? signal;
        var enteredPause = false;
        lock (_sync)
        {
            signal = _resumeSignal;
            if (signal != null && _state == StationState.Pausing)
            {
                _state = StationState.Paused;
                enteredPause = true;
            }
        }

        if (enteredPause)
        {
            StateChanged?.Invoke(this, StationState.Paused);
        }

        if (signal != null)
        {
            await signal.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private async Task LoopAsync(int? cycleLimit, CancellationToken token)
    {
        // 讓 RunAsync 先返回，循環在線程池上執行
        await Task.Yield();
        var context = new StationContext(this, token);
        try
        {
            for (var completed = 0; cycleLimit is null || completed < cycleLimit; completed++)
            {
                await context.CheckpointAsync().ConfigureAwait(false);
                var start = Time.GetTimestamp();
                await RunCycleAsync(context).ConfigureAwait(false);
                Statistics.Record(Time.GetElapsedTime(start));
            }

            Transition(StationState.Idle);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Transition(StationState.Idle);
        }
        catch (Exception ex)
        {
            Alarms.Raise(ClassifyFault(ex), Name, ex.Message);
            Transition(StationState.Faulted);
            try
            {
                await OnFaultedAsync(ex).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                Alarms.Raise(StationAlarms.CycleFault, Name + "/善後", cleanupError.Message);
            }
        }
    }

    private void Transition(StationState state)
    {
        lock (_sync)
        {
            _state = state;
            _resumeSignal?.TrySetCanceled();
            _resumeSignal = null;
        }

        StateChanged?.Invoke(this, state);
    }
}
