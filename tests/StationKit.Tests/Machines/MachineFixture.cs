using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Alarms;
using StationKit.Io.Simulation;
using StationKit.Machines;
using StationKit.Motion.Simulation;
using StationKit.Safety;
using StationKit.Stations;

namespace StationKit.Tests.Machines;

/// <summary>整機測試的公共裝配：兩個由測試控制的工站、兩根模擬軸、一塊模擬 IO 板，全部用假時鐘。</summary>
internal sealed class MachineFixture : IDisposable
{
    public static readonly AxisSafetyConfig Safety = new()
    {
        SoftLimitMin = -1,
        SoftLimitMax = 250,
        MaxVelocity = 800,
        MaxAcceleration = 8000,
        EmergencyStopEnabled = true,
        EmergencyStopActiveHigh = false,
        ConfirmedOnMachine = true,
    };

    private readonly List<Machine> _machines = [];

    public MachineFixture()
    {
        AxisX = new SimAxis("X", Time) { HomeDuration = TimeSpan.Zero };
        AxisY = new SimAxis("Y", Time) { HomeDuration = TimeSpan.Zero };
        Io = new SimIoBoard("IO", timeProvider: Time);
        Alarms = new AlarmCenter(new AlarmCatalog().AddStationAlarms().AddMachineAlarms(), Time);
        StationA = new TestStation("A", Alarms);
        StationB = new TestStation("B", Alarms);
    }

    public FakeTimeProvider Time { get; } = new();

    public SimAxis AxisX { get; }

    public SimAxis AxisY { get; }

    public SimIoBoard Io { get; }

    public AlarmCenter Alarms { get; }

    public TestStation StationA { get; }

    public TestStation StationB { get; }

    public MachineMotionGate Gate { get; } = new();

    public ResourceArbiter Resources { get; } = new();

    public MachineConfig Config() => new()
    {
        Name = "整機",
        Stations = [StationA, StationB],
        Axes = [new AxisSafetyBinding(AxisX, AxisX, Safety), new AxisSafetyBinding(AxisY, AxisY, Safety)],
        StartupSteps =
        [
            new("X 回零", ct => AxisX.HomeAsync(ct)),
            new("Y 回零", ct => AxisY.HomeAsync(ct)),
        ],
        Guards = [],
        MotionGate = Gate,
        OnStationFault = StationFaultPolicy.AbortAll,
        MonitorPeriod = TimeSpan.FromMilliseconds(20),
        StopTimeout = TimeSpan.FromSeconds(1),
        CycleStopTimeout = TimeSpan.FromSeconds(5),
        Resources = Resources,
    };

    /// <summary>創建機器，測試結束時釋放。</summary>
    public Machine Create(MachineConfig config)
    {
        var machine = new Machine(config, Alarms, Time);
        _machines.Add(machine);
        return machine;
    }

    /// <summary>創建並初始化完成，機器處於 Ready。</summary>
    public async Task<Machine> ReadyMachineAsync(MachineConfig? config = null)
    {
        var machine = Create(config ?? Config());
        var init = machine.InitializeAsync();
        await AdvanceUntilDoneAsync(init);
        await init;
        return machine;
    }

    /// <summary>啟動並等到兩個工站都開始第一個循環。</summary>
    public async Task<Task> StartRunningAsync(Machine machine)
    {
        var run = machine.RunAsync();
        await WaitUntilAsync(() => StationA.CyclesStarted >= 1 && StationB.CyclesStarted >= 1, "兩個工站開始循環");
        return run;
    }

    public async Task AdvanceUntilDoneAsync(Task task, TimeSpan? max = null) =>
        await new FakeClockStepper(Time, TimeSpan.FromMilliseconds(5))
            .AdvanceUntilDoneAsync(task, max ?? TimeSpan.FromSeconds(10));

    /// <summary>推進假時鐘，直到條件成立。給監視循環用：它只在時鐘前進時才檢查。</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"等待超時：{description}");
            Time.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(2);
        }
    }

    /// <summary>推進足夠多的監視週期，用來斷言「什麼都沒有發生」。</summary>
    public Task AdvanceSomeTicksAsync() =>
        new FakeClockStepper(Time, TimeSpan.FromMilliseconds(5)).AdvanceAsync(TimeSpan.FromMilliseconds(200));

    public void Dispose()
    {
        _machines.ForEach(m => m.Dispose());
        Io.Dispose();
    }

    /// <summary>真實時間內等待條件成立：工站循環在線程池上執行，不受假時鐘控制。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"等待超時：{description}");
            await Task.Delay(2);
        }
    }
}

/// <summary>由測試控制每個循環何時結束的工站。</summary>
internal sealed class TestStation(string name, AlarmCenter alarms) : Station(name, alarms)
{
    private volatile TaskCompletionSource _cycleGate = NewSignal();
    private int _cyclesStarted;

    /// <summary>循環內要做的事（例如持有資源或運動）。為 null 時等 <see cref="CompleteCycle"/>。</summary>
    public Func<StationContext, Task>? Cycle { get; set; }

    /// <summary>下一個循環拋出這個異常。</summary>
    public void FailCycle(Exception exception) => _cycleGate.TrySetException(exception);

    public int CyclesStarted => Volatile.Read(ref _cyclesStarted);

    public bool FaultCleanupRan { get; private set; }

    public void CompleteCycle() => _cycleGate.TrySetResult();

    protected override async Task RunCycleAsync(StationContext context)
    {
        var gate = _cycleGate = NewSignal();
        Interlocked.Increment(ref _cyclesStarted);
        if (Cycle is { } cycle)
        {
            await cycle(context);
            return;
        }

        await gate.Task.WaitAsync(context.Token);
    }

    protected override Task OnFaultedAsync(Exception exception)
    {
        FaultCleanupRan = true;
        return Task.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
