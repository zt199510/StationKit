using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Machines;
using StationKit.Safety;

namespace StationKit.Tests.Machines;

public sealed class MachineMonitorTests : IDisposable
{
    private const ushort DoorBit = 0;

    private static readonly MotionProfile Slow = MotionProfile.Symmetric(10, 100);

    private readonly MachineFixture _f = new();
    private readonly IDigitalInput _door;

    public MachineMonitorTests()
    {
        _door = _f.Io.CreateInput(new IoPointConfig("安全門關閉", DoorBit, ActiveHigh: true));
        CloseDoor();
    }

    public void Dispose() => _f.Dispose();

    private void CloseDoor() => _f.Io.SetInputLevel(DoorBit, true);

    private void OpenDoor() => _f.Io.SetInputLevel(DoorBit, false);

    private MachineConfig WithDoor(MachineGuardScope scope, StopMode mode = StopMode.Immediate) => _f.Config() with
    {
        Guards = [new MachineGuard("安全門", _door, true, mode) { Scope = scope }],
    };

    private Task UntilFaultedAsync(Machine machine) =>
        _f.AdvanceUntilAsync(() => machine.State == MachineState.Faulted, "進入 Faulted");

    [Fact]
    public async Task 運行中開門_異常停止並報警_關門後不自動恢復()
    {
        // 反證：互鎖只在下發前檢查，軸會繼續走到目標，沒有任何報警
        var machine = await _f.ReadyMachineAsync(WithDoor(MachineGuardScope.Run));
        var moving = _f.AxisX.MoveAbsoluteAsync(200, Slow);
        var run = await _f.StartRunningAsync(machine);

        OpenDoor();
        await UntilFaultedAsync(machine);
        await run;

        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        var alarm = Assert.Single(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.GuardViolated);
        Assert.Equal("整機/安全門", alarm.Source);
        Assert.Equal("整機/安全門", machine.FirstFault!.Source);
        Assert.Contains("安全門關閉", machine.FirstFault.Description);

        CloseDoor();
        await _f.AdvanceSomeTicksAsync();

        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.GuardViolated);
    }

    [Fact]
    public async Task 信號數據不可信_按不滿足處理()
    {
        var machine = await _f.ReadyMachineAsync(WithDoor(MachineGuardScope.Run));
        var run = await _f.StartRunningAsync(machine);

        _f.Io.InjectFault("網線斷開");
        await UntilFaultedAsync(machine);
        await run;

        Assert.Contains("不可信", machine.FirstFault!.Description);
        Assert.Contains("網線斷開", machine.FirstFault.Description);
    }

    [Fact]
    public async Task 初始化中違反範圍內的條件_中止()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = WithDoor(MachineGuardScope.Initialize) with
        {
            StartupSteps =
            [
                new("等待", async ct =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }),
            ],
        };
        var machine = _f.Create(config);
        var init = machine.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        OpenDoor();
        await UntilFaultedAsync(machine);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => init);
        Assert.Equal("整機/安全門", machine.FirstFault!.Source);
    }

    [Fact]
    public async Task 初始化中違反範圍外的條件_不中止()
    {
        var config = WithDoor(MachineGuardScope.Run);
        var machine = _f.Create(config);
        OpenDoor();

        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Empty(_f.Alarms.Active);
    }

    [Fact]
    public async Task 範圍內的條件不滿足_拒絕初始化_狀態不變_不報警()
    {
        var machine = _f.Create(WithDoor(MachineGuardScope.Initialize));
        OpenDoor();

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.InitializeAsync());

        Assert.Contains("安全門", Assert.Single(rejected.Reasons));
        Assert.Equal(MachineState.Uninitialized, machine.State);
        Assert.Empty(_f.Alarms.Active);
    }

    [Fact]
    public async Task Ready時開門且軸不動_不報警_啟動被拒絕並寫明原因()
    {
        var machine = await _f.ReadyMachineAsync(WithDoor(MachineGuardScope.Run));

        OpenDoor();
        await _f.AdvanceSomeTicksAsync();

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Empty(_f.Alarms.Active);
        var rejected = Assert.Throws<MachineRejectedException>(() => { _ = machine.RunAsync(); });
        Assert.Contains("安全門", Assert.Single(rejected.Reasons));
    }

    [Fact]
    public async Task Paused時開門且軸不動_不報警_恢復被拒絕_關門後可以恢復()
    {
        var machine = await _f.ReadyMachineAsync(WithDoor(MachineGuardScope.Run));
        var run = await _f.StartRunningAsync(machine);
        var pause = machine.PauseAsync();
        _f.StationA.CompleteCycle();
        _f.StationB.CompleteCycle();
        await pause;

        OpenDoor();
        await _f.AdvanceSomeTicksAsync();

        Assert.Equal(MachineState.Paused, machine.State);
        Assert.Empty(_f.Alarms.Active);
        Assert.Throws<MachineRejectedException>(machine.Resume);
        Assert.Equal(MachineState.Paused, machine.State);

        CloseDoor();
        machine.Resume();
        Assert.Equal(MachineState.Running, machine.State);

        await machine.AbortAsync(StopMode.Decelerate);
        await run;
    }

    [Fact]
    public async Task 手動運動中開門_範圍內停軸並報警()
    {
        var machine = await _f.ReadyMachineAsync(WithDoor(MachineGuardScope.ManualMotion));
        var moving = _f.AxisX.MoveAbsoluteAsync(200, Slow);

        OpenDoor();
        await UntilFaultedAsync(machine);

        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.GuardViolated);
    }

    [Fact]
    public async Task 手動運動中開門_範圍外不干預()
    {
        var machine = await _f.ReadyMachineAsync(WithDoor(MachineGuardScope.Run));
        var moving = _f.AxisX.MoveAbsoluteAsync(1, Slow);

        OpenDoor();
        await _f.AdvanceUntilDoneAsync(moving);
        await moving;

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Empty(_f.Alarms.Active);
    }

    [Fact]
    public async Task 未初始化時手動運動中開門_停軸報警_狀態不變()
    {
        var machine = _f.Create(WithDoor(MachineGuardScope.ManualMotion));
        await _f.AxisX.EnableAsync();
        var moving = _f.AxisX.JogAsync(JogDirection.Positive, Slow, CancellationToken.None);

        OpenDoor();
        await _f.AdvanceUntilAsync(() => moving.IsCompleted, "點動被停下");

        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        Assert.Equal(MachineState.Uninitialized, machine.State);
        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.GuardViolated);
    }

    [Fact]
    public async Task Ready時軸報警_報MCH002並進入Faulted()
    {
        // 反證：空閒時卡故障沒有任何報警，要等到下一次運動才報出
        var machine = await _f.ReadyMachineAsync();

        _f.AxisY.InjectAlarm("快照過期");
        await UntilFaultedAsync(machine);

        var alarm = Assert.Single(_f.Alarms.Active);
        Assert.Equal(MachineAlarms.AxisUnhealthy, alarm.Definition.Code);
        Assert.Contains("快照過期", alarm.Detail);
        Assert.Equal("整機/Y", machine.FirstFault!.Source);
    }

    [Fact]
    public async Task 運行中軸掉使能_報MCH002並異常停止()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = await _f.StartRunningAsync(machine);

        await _f.AxisX.DisableAsync();
        await UntilFaultedAsync(machine);
        await run;

        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.AxisUnhealthy);
    }

    [Fact]
    public async Task 未初始化時軸未使能_不算異常()
    {
        var machine = _f.Create(_f.Config());

        await _f.AdvanceSomeTicksAsync();

        Assert.Equal(MachineState.Uninitialized, machine.State);
        Assert.Empty(_f.Alarms.Active);
    }

    [Fact]
    public async Task 監視內部異常_報MCH005並異常停止_之後繼續監視()
    {
        var broken = new ThrowingInput();
        var config = _f.Config() with
        {
            Guards =
            [
                new MachineGuard("壞信號", broken, true, StopMode.Decelerate) { Scope = MachineGuardScope.Run },
                new MachineGuard("安全門", _door, true, StopMode.Decelerate) { Scope = MachineGuardScope.ManualMotion },
            ],
        };
        var machine = await _f.ReadyMachineAsync(config);
        broken.Fail = true;

        await UntilFaultedAsync(machine);

        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.MonitorFault);

        // 循環還活著：讀取恢復後，手動運動的違反照樣被發現
        broken.Fail = false;
        var moving = _f.AxisX.MoveAbsoluteAsync(200, Slow);
        OpenDoor();
        await _f.AdvanceUntilAsync(() => moving.IsCompleted, "運動被停下");
        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
    }

    [Fact]
    public async Task 減速異常停止中違反立即停止的條件_升級為立即停止()
    {
        var modes = new System.Collections.Concurrent.ConcurrentQueue<StopMode>();
        var axis = new RecordingAxis(_f.AxisX, modes);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = WithDoor(MachineGuardScope.Run) with
        {
            Axes = [new AxisSafetyBinding(axis, _f.AxisX, MachineFixture.Safety),
                new AxisSafetyBinding(_f.AxisY, _f.AxisY, MachineFixture.Safety)],
            StartupSteps = [new("X 回零", ct => _f.AxisX.HomeAsync(ct)), new("Y 回零", ct => _f.AxisY.HomeAsync(ct))],
        };
        var machine = await _f.ReadyMachineAsync(config);
        _f.StationA.Cycle = _ => finish.Task;
        var run = await _f.StartRunningAsync(machine);

        var abort = machine.AbortAsync(StopMode.Decelerate);
        await MachineFixture.WaitUntilAsync(() => modes.Contains(StopMode.Decelerate), "第一次減速停止");

        OpenDoor();
        await _f.AdvanceUntilAsync(() => modes.Contains(StopMode.Immediate), "升級為立即停止");

        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.GuardViolated);
        Assert.Contains("人工", machine.FirstFault!.Description);
        finish.SetResult();
        await _f.AdvanceUntilDoneAsync(abort);
        await abort;
        await run;
    }

    [Fact]
    public async Task 最後一步之後條件不滿足_初始化失敗_不進入Ready()
    {
        var config = WithDoor(MachineGuardScope.Initialize) with
        {
            StartupSteps =
            [
                new("回零並開門", async ct =>
                {
                    await _f.AxisX.HomeAsync(ct);
                    await _f.AxisY.HomeAsync(ct);
                    OpenDoor();
                }),
            ],
        };
        var machine = _f.Create(config);

        var failure = await Assert.ThrowsAsync<MachineInitializationException>(() => machine.InitializeAsync());

        Assert.Equal("最終檢查", failure.Step);
        Assert.Equal(MachineState.Faulted, machine.State);
    }

    [Fact]
    public async Task 釋放後不再監視()
    {
        var config = WithDoor(MachineGuardScope.ManualMotion);
        var machine = await _f.ReadyMachineAsync(config);
        machine.Dispose();

        OpenDoor();
        var moving = _f.AxisX.MoveAbsoluteAsync(1, Slow);
        await _f.AdvanceUntilDoneAsync(moving);
        await moving;

        Assert.Equal(MachineState.Ready, machine.State);
    }

    private sealed class ThrowingInput : IDigitalInput
    {
        public volatile bool Fail;

        public string Name => "壞信號";

        public bool Read() => true;

        public IoReading GetReading() =>
            Fail ? throw new InvalidOperationException("接口實現有問題") : new IoReading(true, null);

        public Task WaitAsync(bool expected, TimeSpan timeout, TimeSpan stableTime = default,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingAxis(IAxis inner, System.Collections.Concurrent.ConcurrentQueue<StopMode> modes) : IAxis
    {
        public string Name => inner.Name;

        public AxisStatus GetStatus() => inner.GetStatus();

        public Task EnableAsync(CancellationToken cancellationToken = default) => inner.EnableAsync(cancellationToken);

        public Task DisableAsync(CancellationToken cancellationToken = default) => inner.DisableAsync(cancellationToken);

        public Task HomeAsync(CancellationToken cancellationToken = default) => inner.HomeAsync(cancellationToken);

        public Task MoveAbsoluteAsync(double position, MotionProfile profile,
            CancellationToken cancellationToken = default) => inner.MoveAbsoluteAsync(position, profile, cancellationToken);

        public Task JogAsync(JogDirection direction, MotionProfile profile, CancellationToken cancellationToken) =>
            inner.JogAsync(direction, profile, cancellationToken);

        public Task StopAsync(StopMode mode = StopMode.Decelerate)
        {
            modes.Enqueue(mode);
            return inner.StopAsync(mode);
        }

        public Task ResetAlarmAsync(CancellationToken cancellationToken = default) => inner.ResetAlarmAsync(cancellationToken);
    }
}
