using StationKit.Machines;
using StationKit.Stations;

namespace StationKit.Tests.Machines;

public sealed class MachineRunTests : IDisposable
{
    private readonly MachineFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void 未初始化時不能啟動()
    {
        var machine = _f.Create(_f.Config());

        Assert.Throws<MachineRejectedException>(() => { _ = machine.RunAsync(); });

        Assert.Equal(0, _f.StationA.CyclesStarted);
        Assert.Equal(MachineState.Uninitialized, machine.State);
    }

    [Fact]
    public async Task 條件不滿足_拒絕並列出全部原因_沒有工站啟動()
    {
        var machine = await _f.ReadyMachineAsync();
        _f.AxisY.InjectAlarm("Err 16.0");
        using (var lease = await _f.Resources.AcquireAsync("相機", "A", TimeSpan.FromSeconds(1)))
        {
            lease.MarkUncertain("A 持有期間異常");
        }

        var rejected = Assert.Throws<MachineRejectedException>(() => { _ = machine.RunAsync(); });

        Assert.Equal(2, rejected.Reasons.Count);
        Assert.Contains(rejected.Reasons, r => r.Contains("相機", StringComparison.Ordinal));
        Assert.Contains(rejected.Reasons, r => r.Contains("軸 Y", StringComparison.Ordinal));
        Assert.Equal(0, _f.StationA.CyclesStarted);
        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public async Task 任何工站不能啟動_其他工站都沒有啟動()
    {
        var machine = await _f.ReadyMachineAsync();
        // 第二個工站已經被直接啟動：不是空閒，不能再啟動。第一個工站也不能先跑起來
        var direct = _f.StationB.RunAsync();
        await MachineFixture.WaitUntilAsync(() => _f.StationB.CyclesStarted == 1, "B 開始循環");

        var rejected = Assert.Throws<MachineRejectedException>(() => { _ = machine.RunAsync(); });

        Assert.Contains("工站 B", Assert.Single(rejected.Reasons));
        Assert.Equal(0, _f.StationA.CyclesStarted);
        await _f.StationB.StopAsync();
        await direct;
    }

    [Fact]
    public async Task 全部工站跑完指定循環數_回到Ready_可以再次啟動()
    {
        var machine = await _f.ReadyMachineAsync();
        _f.StationA.Cycle = _ => Task.CompletedTask;
        _f.StationB.Cycle = _ => Task.CompletedTask;

        await machine.RunAsync(cycleLimit: 2);

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Equal(2, _f.StationA.CyclesStarted);

        await machine.RunAsync(cycleLimit: 1);

        Assert.Equal(3, _f.StationB.CyclesStarted);
    }

    [Fact]
    public async Task 所有工站到達安全點才算Paused_恢復後繼續()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = machine.RunAsync();
        await MachineFixture.WaitUntilAsync(
            () => _f.StationA.CyclesStarted == 1 && _f.StationB.CyclesStarted == 1, "兩個工站開始循環");

        var pause = machine.PauseAsync();
        Assert.Equal(MachineState.Pausing, machine.State);

        _f.StationA.CompleteCycle();
        await MachineFixture.WaitUntilAsync(() => _f.StationA.State == StationState.Paused, "A 到達安全點");
        Assert.Equal(MachineState.Pausing, machine.State);
        Assert.False(pause.IsCompleted);

        _f.StationB.CompleteCycle();
        await pause;
        Assert.Equal(MachineState.Paused, machine.State);

        machine.Resume();
        Assert.Equal(MachineState.Running, machine.State);
        await MachineFixture.WaitUntilAsync(
            () => _f.StationA.CyclesStarted == 2 && _f.StationB.CyclesStarted == 2, "兩個工站開始下一循環");

        _f.StationA.CompleteCycle();
        _f.StationB.CompleteCycle();
        await machine.CycleStopAsync();
        await run;
    }

    [Fact]
    public async Task 循環停止_等工站到安全點_資源不進入不確定_回到Ready可再啟動()
    {
        var machine = await _f.ReadyMachineAsync();
        var inResource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.StationA.Cycle = context => _f.Resources.UseAsync("相機", "A", TimeSpan.FromSeconds(1), async _ =>
        {
            inResource.TrySetResult();
            await release.Task;
        }, context.Token);
        var run = machine.RunAsync();
        await inResource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await MachineFixture.WaitUntilAsync(() => _f.StationB.CyclesStarted == 1, "B 開始循環");

        var stop = machine.CycleStopAsync();
        Assert.Equal(MachineState.CycleStopping, machine.State);
        Assert.False(stop.IsCompleted);

        release.SetResult();
        _f.StationB.CompleteCycle();
        await stop;
        await run;

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.All(_f.Resources.Snapshot(), r => Assert.Null(r.UncertainReason));
        Assert.Empty(_f.Alarms.Active);

        _f.StationA.Cycle = _ => Task.CompletedTask;
        _f.StationB.Cycle = _ => Task.CompletedTask;
        await machine.RunAsync(cycleLimit: 1);
        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public async Task 循環停止超時_升級為異常停止_報警並進入Faulted()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = machine.RunAsync();
        await MachineFixture.WaitUntilAsync(
            () => _f.StationA.CyclesStarted == 1 && _f.StationB.CyclesStarted == 1, "兩個工站開始循環");
        _f.StationB.CompleteCycle();

        var stop = machine.CycleStopAsync();
        await _f.AdvanceUntilDoneAsync(stop);

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => stop);
        Assert.Contains("A", failure.Message);
        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.Contains(_f.Alarms.Active, a => a.Definition.Code == MachineAlarms.StopTimeout);
        await run;
    }

    [Fact]
    public async Task 循環停止_從暫停狀態也可以()
    {
        var machine = await _f.ReadyMachineAsync();
        var run = machine.RunAsync();
        await MachineFixture.WaitUntilAsync(
            () => _f.StationA.CyclesStarted == 1 && _f.StationB.CyclesStarted == 1, "兩個工站開始循環");
        var pause = machine.PauseAsync();
        _f.StationA.CompleteCycle();
        _f.StationB.CompleteCycle();
        await pause;

        await machine.CycleStopAsync();
        await run;

        Assert.Equal(MachineState.Ready, machine.State);
    }
}
