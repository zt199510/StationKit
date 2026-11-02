using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Machines;
using StationKit.Stations;

namespace StationKit.Tests.Machines;

public sealed class MachineRecoveryTests : IDisposable
{
    private const ushort DoorBit = 0;

    private static readonly MotionProfile Slow = MotionProfile.Symmetric(10, 100);

    private readonly MachineFixture _f = new();

    public void Dispose() => _f.Dispose();

    private MachineConfig WithDoor(IDigitalInput door, MachineGuardScope scope = MachineGuardScope.Run) => _f.Config() with
    {
        Guards = [new MachineGuard("安全門", door, true, StopMode.Immediate) { Scope = scope }],
    };

    private IDigitalInput ClosedDoor(TimeSpan activateDelay = default)
    {
        var door = _f.Io.CreateInput(new IoPointConfig("安全門關閉", DoorBit, ActiveHigh: true)
        {
            ActivateDelay = activateDelay,
        });
        _f.Io.SetInputLevel(DoorBit, true);
        return door;
    }

    /// <summary>進入 Faulted：工站 A 在持有相機時被異常停止。</summary>
    private async Task<Machine> FaultedMachineAsync(MachineConfig? config = null)
    {
        var machine = await _f.ReadyMachineAsync(config);
        var inResource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.StationA.Cycle = context => _f.Resources.UseAsync("相機", "A", TimeSpan.FromSeconds(1), async ct =>
        {
            inResource.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, context.Token);
        var run = machine.RunAsync();
        await inResource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _f.Alarms.Raise(MachineAlarms.MonitorFault, "視覺", "相機掉線");
        await MachineFixture.WaitUntilAsync(() => machine.State == MachineState.Faulted, "進入 Faulted");
        await run;
        return machine;
    }

    [Fact]
    public async Task 不是故障狀態_拒絕復位()
    {
        var machine = await _f.ReadyMachineAsync();

        await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public async Task 報警和資源都沒處理_拒絕並全部列出_不代為清除()
    {
        var machine = await FaultedMachineAsync();

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains(rejected.Reasons, r => r.Contains("故障報警", StringComparison.Ordinal));
        Assert.Contains(rejected.Reasons, r => r.Contains("相機", StringComparison.Ordinal));
        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.True(_f.Alarms.HasBlockingAlarm());
        Assert.NotNull(Assert.Single(_f.Resources.Snapshot()).UncertainReason);
    }

    [Fact]
    public async Task 只剩報警沒清除_拒絕()
    {
        var machine = await FaultedMachineAsync();
        _f.Resources.ResetUncertain("相機");

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("故障報警", Assert.Single(rejected.Reasons));
    }

    [Fact]
    public async Task 只剩資源沒解除_拒絕()
    {
        var machine = await FaultedMachineAsync();
        _f.Alarms.ClearSource("視覺");

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("相機", Assert.Single(rejected.Reasons));
    }

    [Fact]
    public async Task 門還開著_拒絕_關門後通過()
    {
        var door = ClosedDoor();
        var machine = await FaultedMachineAsync(WithDoor(door));
        _f.Alarms.ClearSource("視覺");
        _f.Resources.ResetUncertain("相機");
        _f.Io.SetInputLevel(DoorBit, false);

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("安全門", Assert.Single(rejected.Reasons));
        _f.Io.SetInputLevel(DoorBit, true);
        await machine.ResetAsync();
        Assert.Equal(MachineState.Uninitialized, machine.State);
    }

    [Fact]
    public async Task 手動運動還沒停_拒絕()
    {
        var machine = await FaultedMachineAsync();
        _f.Alarms.ClearSource("視覺");
        _f.Resources.ResetUncertain("相機");
        var moving = _f.AxisX.MoveAbsoluteAsync(200, Slow);

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("軸 X", Assert.Single(rejected.Reasons));
        await _f.AxisX.StopAsync();
        await Assert.ThrowsAsync<MotionFaultException>(() => moving);
        await machine.ResetAsync();
    }

    [Fact]
    public async Task 工站還沒結束_拒絕_結束後通過_運動閘重新打開()
    {
        var machine = await _f.ReadyMachineAsync();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.StationA.Cycle = _ => finish.Task;
        var run = await _f.StartRunningAsync(machine);
        var abort = machine.AbortAsync(StopMode.Decelerate);
        await _f.AdvanceUntilDoneAsync(abort);
        await abort;
        _f.Alarms.ClearSource("整機");
        Assert.True(_f.Gate.IsClosed);

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("工站還沒有結束：A", Assert.Single(rejected.Reasons));
        Assert.True(_f.Gate.IsClosed);

        finish.SetResult();
        await run;
        await machine.ResetAsync();

        Assert.False(_f.Gate.IsClosed);
        Assert.Equal(MachineState.Uninitialized, machine.State);
    }

    [Fact]
    public async Task 善後失敗的報警_也阻止整機復位()
    {
        var machine = await FaultedMachineAsync();
        _f.Resources.ResetUncertain("相機");
        _f.Alarms.ClearSource("視覺");
        _f.Alarms.Raise(StationAlarms.CycleFault, "A/善後", "停軸失敗");

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("故障報警", Assert.Single(rejected.Reasons));
    }

    [Fact]
    public async Task 通過後回到未初始化_工站已復位_首出已清除_不能直接啟動()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = await _f.StartRunningAsync(machine);
        _f.StationA.FailCycle(new InvalidOperationException("Z 軸未到位"));
        await MachineFixture.WaitUntilAsync(() => machine.State == MachineState.Faulted, "進入 Faulted");
        await run;
        _f.Alarms.ClearSource("A");

        await machine.ResetAsync();

        Assert.Equal(MachineState.Uninitialized, machine.State);
        Assert.Equal(StationState.Idle, _f.StationA.State);
        Assert.Null(machine.FirstFault);
        Assert.True(_f.AxisX.GetStatus().IsHomed);
        Assert.Throws<MachineRejectedException>(() => { _ = machine.RunAsync(); });
    }

    [Fact]
    public async Task 卡故障_硬件復位在檢查條件之前_恢復後條件通過()
    {
        // 反證：先查條件的話，卡故障時門的數據不可信，條件永遠不過，硬件復位永遠輪不到
        var door = ClosedDoor();
        var resets = 0;
        var config = WithDoor(door) with
        {
            ResetHardware = _ =>
            {
                resets++;
                _f.Io.ClearFault();
                return Task.CompletedTask;
            },
        };
        var machine = await _f.ReadyMachineAsync(config);
        var run = await _f.StartRunningAsync(machine);
        _f.Io.InjectFault("網線斷開");
        await _f.AdvanceUntilAsync(() => machine.State == MachineState.Faulted, "進入 Faulted");
        await run;
        _f.Alarms.ClearSource("整機");

        await machine.ResetAsync();

        Assert.Equal(1, resets);
        Assert.Equal(MachineState.Uninitialized, machine.State);
    }

    [Fact]
    public async Task 硬件復位後信號還在消抖_拒絕_稍後重試通過()
    {
        var door = ClosedDoor(activateDelay: TimeSpan.FromMilliseconds(50));
        await _f.AdvanceSomeTicksAsync();
        var resets = 0;
        var config = WithDoor(door) with
        {
            ResetHardware = _ =>
            {
                resets++;
                _f.Io.ClearFault();
                return Task.CompletedTask;
            },
        };
        var machine = await _f.ReadyMachineAsync(config);
        var run = await _f.StartRunningAsync(machine);
        _f.Io.InjectFault("網線斷開");
        await _f.AdvanceUntilAsync(() => machine.State == MachineState.Faulted, "進入 Faulted");
        await run;
        _f.Alarms.ClearSource("整機");

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("安全門", Assert.Single(rejected.Reasons));
        Assert.Empty(_f.Alarms.Active);

        await _f.AdvanceSomeTicksAsync();
        await machine.ResetAsync();

        Assert.Equal(2, resets);
        Assert.Equal(MachineState.Uninitialized, machine.State);
    }

    [Fact]
    public async Task 硬件復位失敗_拒絕並寫明原因_保持故障()
    {
        var config = _f.Config() with
        {
            ResetHardware = _ => throw new InvalidOperationException("卡沒有響應"),
        };
        var machine = await FaultedMachineAsync(config);

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.ResetAsync());

        Assert.Contains("卡沒有響應", Assert.Single(rejected.Reasons));
        Assert.Equal(MachineState.Faulted, machine.State);
    }

    [Fact]
    public async Task 完整流程_故障_人工處理_復位_初始化_再運行()
    {
        var machine = await _f.ReadyMachineAsync();
        _f.AxisX.InjectAlarm("Err 16.0");
        await _f.AdvanceUntilAsync(() => machine.State == MachineState.Faulted, "進入 Faulted");
        Assert.Contains("Err 16.0", machine.FirstFault!.Description);

        _f.Alarms.ClearSource("整機");
        await machine.ResetAsync();

        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;
        Assert.Equal(MachineState.Ready, machine.State);
        Assert.True(_f.AxisX.GetStatus().IsHomed);

        _f.StationA.Cycle = _ => Task.CompletedTask;
        _f.StationB.Cycle = _ => Task.CompletedTask;
        await machine.RunAsync(cycleLimit: 1);

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Equal(1, _f.StationA.CyclesStarted);
    }
}
