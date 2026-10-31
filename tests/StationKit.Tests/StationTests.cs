using StationKit.Alarms;
using StationKit.Stations;

namespace StationKit.Tests;

public class StationTests
{
    private readonly AlarmCenter _alarms = new(new AlarmCatalog().AddStationAlarms());

    [Fact]
    public async Task 跑完指定循環數後回到空閒並統計節拍()
    {
        var station = new ScriptedStation(_alarms, autoComplete: true);

        await station.RunAsync(cycleLimit: 3);

        Assert.Equal(StationState.Idle, station.State);
        Assert.Equal(3, station.Statistics.Snapshot().Count);
    }

    [Fact]
    public async Task 暫停只在安全點生效()
    {
        var station = new ScriptedStation(_alarms);
        var run = station.RunAsync();
        await station.CycleStarted.WaitAsync(TimeSpan.FromSeconds(5));

        station.Pause();
        Assert.Equal(StationState.Pausing, station.State);

        station.CompleteCycle();
        await WaitForAsync(station, StationState.Paused);
        Assert.Equal(1, station.Statistics.Snapshot().Count);

        station.Resume();
        await station.NextCycleStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StationState.Running, station.State);

        await station.StopAsync();
        await run;
        Assert.Equal(StationState.Idle, station.State);
    }

    [Fact]
    public async Task 暫停中也能停止()
    {
        var station = new ScriptedStation(_alarms);
        var run = station.RunAsync();
        await station.CycleStarted.WaitAsync(TimeSpan.FromSeconds(5));
        station.Pause();
        station.CompleteCycle();
        await WaitForAsync(station, StationState.Paused);

        await station.StopAsync();

        await run;
        Assert.Equal(StationState.Idle, station.State);
    }

    [Fact]
    public async Task 異常轉為故障和報警_清除報警前不能復位()
    {
        var station = new ScriptedStation(_alarms) { Failure = new InvalidOperationException("Z 軸未到位") };

        await station.RunAsync();

        Assert.Equal(StationState.Faulted, station.State);
        var alarm = Assert.Single(_alarms.Active);
        Assert.Equal(StationAlarms.CycleFault, alarm.Definition.Code);
        Assert.Equal("Z 軸未到位", alarm.Detail);
        Assert.Same(station.Failure, station.FaultHandled);
        Assert.Throws<InvalidOperationException>(() => station.Reset());
        Assert.Throws<InvalidOperationException>(() => { _ = station.RunAsync(); });

        _alarms.ClearSource(station.Name);
        station.Reset();
        station.Failure = null;
        station.AutoComplete = true;

        await station.RunAsync(cycleLimit: 1);
        Assert.Equal(StationState.Idle, station.State);
    }

    [Fact]
    public async Task 資源等待超時映射為專門的報警代碼()
    {
        var timeout = new ResourceTimeoutException("相機", "上工位", TimeSpan.FromSeconds(1),
            new ResourceState("相機", "下工位", null));
        var station = new ScriptedStation(_alarms) { Failure = timeout };

        await station.RunAsync();

        Assert.Equal(StationAlarms.ResourceTimeout, Assert.Single(_alarms.Active).Definition.Code);
    }

    private static async Task WaitForAsync(Station station, StationState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (station.State != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"等待 {expected} 超時，當前 {station.State}");
            await Task.Delay(5);
        }
    }

    /// <summary>由測試控制每個循環何時結束的工站。</summary>
    private sealed class ScriptedStation(AlarmCenter alarms, bool autoComplete = false)
        : Station("上工位", alarms)
    {
        private readonly object _sync = new();
        private TaskCompletionSource _cycleGate = NewSignal();
        private TaskCompletionSource _started = NewSignal();
        private TaskCompletionSource _nextStarted = NewSignal();
        private int _cycles;

        public bool AutoComplete { get; set; } = autoComplete;

        public Exception? Failure { get; set; }

        public Exception? FaultHandled { get; private set; }

        public Task CycleStarted => _started.Task;

        public Task NextCycleStarted => _nextStarted.Task;

        public void CompleteCycle()
        {
            lock (_sync)
            {
                _cycleGate.TrySetResult();
            }
        }

        protected override async Task RunCycleAsync(StationContext context)
        {
            Task gate;
            lock (_sync)
            {
                (_cycles++ == 0 ? _started : _nextStarted).TrySetResult();
                _cycleGate = NewSignal();
                gate = _cycleGate.Task;
            }

            if (Failure != null)
            {
                throw Failure;
            }

            if (!AutoComplete)
            {
                await gate.WaitAsync(context.Token);
            }
        }

        protected override Task OnFaultedAsync(Exception exception)
        {
            FaultHandled = exception;
            return Task.CompletedTask;
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
