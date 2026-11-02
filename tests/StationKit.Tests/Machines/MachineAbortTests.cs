using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Alarms;
using StationKit.Machines;
using StationKit.Safety;
using StationKit.Stations;

namespace StationKit.Tests.Machines;

public sealed class MachineAbortTests : IDisposable
{
    private static readonly MotionProfile Slow = MotionProfile.Symmetric(10, 100);

    private readonly MachineFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static Task WaitForStateAsync(Machine machine, MachineState state) =>
        MachineFixture.WaitUntilAsync(() => machine.State == state, $"進入 {state}，當前 {machine.State}");

    [Fact]
    public async Task 工站故障_其他工站立即結束_軸停下_首出是最初的故障()
    {
        // 反證：沒有整機層時，B 要等到需要共享資源、超時後才停
        var machine = await _f.ReadyMachineAsync();
        var moving = _f.AxisY.MoveAbsoluteAsync(200, Slow);
        var run = await _f.StartRunningAsync(machine);

        _f.StationA.FailCycle(new InvalidOperationException("Z 軸未到位"));
        await WaitForStateAsync(machine, MachineState.Faulted);
        await run;

        Assert.Equal(StationState.Faulted, _f.StationA.State);
        Assert.Equal(StationState.Idle, _f.StationB.State);
        Assert.NotEqual(AxisState.Moving, _f.AxisY.GetStatus().State);
        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        var first = machine.FirstFault!;
        Assert.Equal("A", first.Source);
        Assert.Contains("Z 軸未到位", first.Description);
        Assert.Contains(StationAlarms.CycleFault, first.Description);
    }

    [Fact]
    public async Task 其他工站到安全點策略_當前循環走完_資源不進入不確定()
    {
        var machine = await _f.ReadyMachineAsync(_f.Config() with { OnStationFault = StationFaultPolicy.OthersToSafePoint });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycles = 0;
        _f.StationB.Cycle = context => _f.Resources.UseAsync("相機", "B", TimeSpan.FromSeconds(1), async _ =>
        {
            Interlocked.Increment(ref cycles);
            await release.Task;
        }, context.Token);
        var run = await _f.StartRunningAsync(machine);

        _f.StationA.FailCycle(new InvalidOperationException("Z 軸未到位"));
        await WaitForStateAsync(machine, MachineState.Aborting);
        await MachineFixture.WaitUntilAsync(() => _f.StationA.State == StationState.Faulted, "A 故障");
        Assert.Equal(StationState.Pausing, _f.StationB.State);
        Assert.False(_f.Gate.IsClosed);

        release.SetResult();
        await WaitForStateAsync(machine, MachineState.Faulted);
        await run;

        Assert.Equal(1, cycles);
        Assert.All(_f.Resources.Snapshot(), r => Assert.Null(r.UncertainReason));
    }

    [Fact]
    public async Task 其他工站到安全點策略_超時升級為立即取消並報警()
    {
        var machine = await _f.ReadyMachineAsync(_f.Config() with { OnStationFault = StationFaultPolicy.OthersToSafePoint });
        var run = await _f.StartRunningAsync(machine);

        _f.StationA.FailCycle(new InvalidOperationException("Z 軸未到位"));
        await _f.AdvanceUntilDoneAsync(run);
        await run;

        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.Equal(StationState.Idle, _f.StationB.State);
        Assert.Contains(_f.Alarms.Active,
            a => a.Definition.Code == MachineAlarms.StopTimeout && a.Detail.Contains('B', StringComparison.Ordinal));
    }

    [Fact]
    public async Task 其他工站到安全點策略_等待中人工異常停止_立即升級()
    {
        var machine = await _f.ReadyMachineAsync(_f.Config() with { OnStationFault = StationFaultPolicy.OthersToSafePoint });
        var run = await _f.StartRunningAsync(machine);
        _f.StationA.FailCycle(new InvalidOperationException("Z 軸未到位"));
        await WaitForStateAsync(machine, MachineState.Aborting);

        await machine.AbortAsync(StopMode.Immediate);
        await run;

        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.Equal(StationState.Idle, _f.StationB.State);
        Assert.Contains("Z 軸未到位", machine.FirstFault!.Description);
    }

    [Fact]
    public async Task 持有資源時被取消_資源進入不確定()
    {
        var machine = await _f.ReadyMachineAsync();
        var inResource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.StationA.Cycle = context => _f.Resources.UseAsync("相機", "A", TimeSpan.FromSeconds(1), async ct =>
        {
            inResource.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, context.Token);
        var run = machine.RunAsync();
        await inResource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await machine.AbortAsync(StopMode.Decelerate);
        await run;

        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.NotNull(Assert.Single(_f.Resources.Snapshot()).UncertainReason);
    }

    [Fact]
    public async Task 多個故障同時觸發_只執行一次流程_首出不被覆蓋()
    {
        var machine = await _f.ReadyMachineAsync();
        var aborting = 0;
        machine.StateChanged += (_, state) =>
        {
            if (state == MachineState.Aborting)
            {
                Interlocked.Increment(ref aborting);
            }
        };
        var run = await _f.StartRunningAsync(machine);

        _f.Alarms.Raise(MachineAlarms.MonitorFault, "來源一", "第一個");
        _f.Alarms.Raise(MachineAlarms.MonitorFault, "來源二", "第二個");
        _f.StationA.FailCycle(new InvalidOperationException("第三個"));
        await machine.AbortAsync(StopMode.Decelerate);
        await run;

        Assert.Equal(1, aborting);
        Assert.Equal("來源一", machine.FirstFault!.Source);
    }

    [Fact]
    public async Task 其他來源的故障報警_觸發異常停止_提示級報警不觸發()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = await _f.StartRunningAsync(machine);

        _f.Alarms.Raise(AlarmCatalog.SubscriberFaultCode, "界面", "提示");
        Assert.Equal(MachineState.Running, machine.State);

        _f.Alarms.Raise(MachineAlarms.MonitorFault, "視覺", "相機掉線");
        await WaitForStateAsync(machine, MachineState.Faulted);
        await run;

        Assert.Equal("視覺", machine.FirstFault!.Source);
    }

    [Fact]
    public async Task 在報警回調線程上觸發_不等停機完成_不會死鎖()
    {
        // 一根軸的停止永遠不返回：如果回調等停機，Raise 要等到停止超時才返回，工站循環就卡在 catch 裡
        var hung = new StopStubAxis("卡住", () => new TaskCompletionSource().Task);
        var config = _f.Config() with
        {
            Axes = [new AxisSafetyBinding(hung, _f.AxisX, MachineFixture.Safety)],
            StartupSteps = [],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);
        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;
        var run = await _f.StartRunningAsync(machine);

        _f.Alarms.Raise(MachineAlarms.MonitorFault, "視覺", "相機掉線");

        Assert.Equal(MachineState.Aborting, machine.State);
        await _f.AdvanceUntilDoneAsync(run);
        await run;
    }

    [Fact]
    public async Task 一根軸停止同步拋異常_一根卡住_其他軸都停下_報MCH003()
    {
        var throwing = new StopStubAxis("拋異常", () => throw new InvalidOperationException("DLL 調用失敗"));
        var hung = new StopStubAxis("卡住", () => new TaskCompletionSource().Task);
        var config = _f.Config() with
        {
            Axes =
            [
                new AxisSafetyBinding(throwing, _f.AxisX, MachineFixture.Safety),
                new AxisSafetyBinding(hung, _f.AxisX, MachineFixture.Safety),
                new AxisSafetyBinding(_f.AxisY, _f.AxisY, MachineFixture.Safety),
            ],
            StartupSteps = [new("Y 回零", ct => _f.AxisY.HomeAsync(ct))],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);
        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;
        var moving = _f.AxisY.MoveAbsoluteAsync(200, Slow);

        var abort = machine.AbortAsync(StopMode.Immediate);
        await _f.AdvanceUntilDoneAsync(abort);
        await abort;

        Assert.Equal(MachineState.Faulted, machine.State);
        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        Assert.Contains(_f.Alarms.Active,
            a => a.Definition.Code == MachineAlarms.StopTimeout && a.Source == "整機/拋異常");
        Assert.Contains(_f.Alarms.Active,
            a => a.Definition.Code == MachineAlarms.StopTimeout && a.Source == "整機/卡住");
    }

    [Fact]
    public async Task 工站不響應令牌_超時報MCH003_進入Faulted_新運動被運動閘拒絕()
    {
        var machine = await _f.ReadyMachineAsync();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.StationA.Cycle = _ => finish.Task;
        var run = await _f.StartRunningAsync(machine);

        var abort = machine.AbortAsync(StopMode.Decelerate);
        Assert.True(_f.Gate.IsClosed);
        await _f.AdvanceUntilDoneAsync(abort);
        await abort;

        Assert.Equal(MachineState.Faulted, machine.State);
        var alarm = Assert.Single(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.StopTimeout);
        Assert.Contains('A', alarm.Detail);

        // 它還在跑，閘不能開
        Assert.True(_f.Gate.IsClosed);
        var guarded = new GuardedAxis(_f.AxisX, MachineFixture.Safety, [_f.Gate]);
        var rejected = Assert.Throws<MotionRejectedException>(() => { _ = guarded.MoveAbsoluteAsync(10, Slow); });
        Assert.Contains("異常停止", rejected.Message);

        finish.SetResult();
        await run;
    }

    [Fact]
    public async Task 工站取消後還下發了運動_第二次停軸把它停下()
    {
        var machine = await _f.ReadyMachineAsync();
        Task? lateMove = null;
        _f.StationA.Cycle = async context =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, context.Token);
            }
            catch (OperationCanceledException)
            {
                // 模擬「檢查之後、下發之前」的窗口：繞過運動閘，第一次停軸之後才下發
                await MachineFixture.WaitUntilAsync(
                    () => _f.AxisY.GetStatus().State != AxisState.Moving, "第一次停軸");
                lateMove = _f.AxisX.MoveAbsoluteAsync(200, Slow);
                throw;
            }
        };
        var moving = _f.AxisY.MoveAbsoluteAsync(200, Slow);
        var run = await _f.StartRunningAsync(machine);

        await machine.AbortAsync(StopMode.Decelerate);
        await run;

        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        Assert.NotNull(lateMove);
        Assert.NotEqual(AxisState.Moving, _f.AxisX.GetStatus().State);
        await Assert.ThrowsAsync<MotionFaultException>(() => lateMove!);
        Assert.False(_f.Gate.IsClosed);
    }

    [Fact]
    public async Task 正常結束異常停止後_運動閘重新打開_可以手動移軸()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = await _f.StartRunningAsync(machine);

        await machine.AbortAsync(StopMode.Decelerate);
        await run;

        Assert.False(_f.Gate.IsClosed);
    }

    [Fact]
    public async Task 未初始化或已故障時異常停止_只停軸_狀態不變()
    {
        var machine = new Machine(_f.Config(), _f.Alarms, _f.Time);
        await _f.AxisX.EnableAsync();
        await _f.AxisX.HomeAsync();
        var moving = _f.AxisX.MoveAbsoluteAsync(200, Slow);

        await machine.AbortAsync(StopMode.Immediate);

        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        Assert.Equal(MachineState.Uninitialized, machine.State);
    }

    [Fact]
    public async Task 初始化中異常停止_取消步驟_進入Faulted()
    {
        var stepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = _f.Config() with
        {
            StartupSteps =
            [
                new("等待", async ct =>
                {
                    stepStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }),
            ],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);
        var init = machine.InitializeAsync();
        await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await machine.AbortAsync(StopMode.Decelerate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => init);
        Assert.Equal(MachineState.Faulted, machine.State);
    }

    /// <summary>只有 StopAsync 行為特殊的軸。</summary>
    private sealed class StopStubAxis(string name, Func<Task> stop) : IAxis
    {
        public string Name => name;

        public AxisStatus GetStatus() => new(AxisState.Idle, 0, true, false, false, null);

        public Task EnableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task HomeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MoveAbsoluteAsync(double position, MotionProfile profile,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task JogAsync(JogDirection direction, MotionProfile profile, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAsync(StopMode mode = StopMode.Decelerate) => stop();

        public Task ResetAlarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
