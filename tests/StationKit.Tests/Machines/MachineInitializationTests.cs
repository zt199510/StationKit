using StationKit.Abstractions.Motion;
using StationKit.Machines;

namespace StationKit.Tests.Machines;

public sealed class MachineInitializationTests : IDisposable
{
    private readonly MachineFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task 成功_步驟按序執行_軸已使能回零_進入Ready()
    {
        var order = new List<string>();
        var config = _f.Config() with
        {
            StartupSteps =
            [
                new("氣缸縮回", _ =>
                {
                    // 第一步之前安全初始化已完成：軸已使能
                    Assert.True(_f.AxisX.GetStatus().IsEnabled);
                    order.Add("氣缸縮回");
                    return Task.CompletedTask;
                }),
                new("回零", async ct =>
                {
                    order.Add("回零");
                    await _f.AxisX.HomeAsync(ct);
                    await _f.AxisY.HomeAsync(ct);
                }),
            ],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);
        Assert.Equal(MachineState.Uninitialized, machine.State);

        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;

        Assert.Equal(["氣缸縮回", "回零"], order);
        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public async Task 軸處於報警_先復位再安全初始化()
    {
        _f.AxisX.InjectAlarm("Err 16.0");
        var machine = new Machine(_f.Config(), _f.Alarms, _f.Time);

        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;

        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Null(_f.AxisX.GetStatus().AlarmReason);
    }

    [Fact]
    public async Task 某步失敗_後面不執行_軸停下_報警寫明步驟_進入Faulted()
    {
        var laterStepRan = false;
        var config = _f.Config() with
        {
            StartupSteps =
            [
                new("氣缸縮回", _ => throw new InvalidOperationException("氣壓不足")),
                new("回零", _ =>
                {
                    laterStepRan = true;
                    return Task.CompletedTask;
                }),
            ],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);

        var failure = await Assert.ThrowsAsync<MachineInitializationException>(() => machine.InitializeAsync());

        Assert.Equal("氣缸縮回", failure.Step);
        Assert.False(laterStepRan);
        Assert.Equal(MachineState.Faulted, machine.State);
        var alarm = Assert.Single(_f.Alarms.Active);
        Assert.Equal(MachineAlarms.InitializationFailed, alarm.Definition.Code);
        Assert.Contains("氣缸縮回", alarm.Detail);
        Assert.Contains("氣壓不足", machine.FirstFault!.Description);
    }

    [Fact]
    public async Task 步驟運動中失敗_其他在動的軸被停下()
    {
        Task? moving = null;
        var config = _f.Config() with
        {
            StartupSteps =
            [
                new("回零", async ct =>
                {
                    await _f.AxisX.HomeAsync(ct);
                    await _f.AxisY.HomeAsync(ct);
                }),
                new("走待機位", _ =>
                {
                    moving = _f.AxisX.MoveAbsoluteAsync(200, MotionProfile.Symmetric(10, 100), CancellationToken.None);
                    throw new InvalidOperationException("相機未就緒");
                }),
            ],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);

        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await Assert.ThrowsAsync<MachineInitializationException>(() => init);

        Assert.NotEqual(AxisState.Moving, _f.AxisX.GetStatus().State);
        await Assert.ThrowsAsync<MotionFaultException>(() => moving!);
    }

    [Fact]
    public async Task 啟動步驟沒有覆蓋某根軸_初始化失敗而不是運行中才報未回零()
    {
        var config = _f.Config() with { StartupSteps = [new("X 回零", ct => _f.AxisX.HomeAsync(ct))] };
        var machine = new Machine(config, _f.Alarms, _f.Time);

        var failure = await Assert.ThrowsAsync<MachineInitializationException>(() => machine.InitializeAsync());

        Assert.Contains("軸 Y 未回零", failure.Message);
        Assert.Equal(MachineState.Faulted, machine.State);
    }

    [Fact]
    public async Task 取消_軸停下_進入Faulted_不報初始化失敗()
    {
        using var cts = new CancellationTokenSource();
        var config = _f.Config() with
        {
            StartupSteps =
            [
                new("等待", async ct =>
                {
                    await cts.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                }),
            ],
        };
        var machine = new Machine(config, _f.Alarms, _f.Time);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.InitializeAsync(cts.Token));

        Assert.Equal(MachineState.Faulted, machine.State);
        Assert.Empty(_f.Alarms.Active);
        Assert.Equal("初始化被取消", machine.FirstFault!.Description);
    }

    [Fact]
    public async Task 有未清除的故障報警_拒絕且狀態不變()
    {
        _f.Alarms.Raise(MachineAlarms.MonitorFault, "其他");
        var machine = new Machine(_f.Config(), _f.Alarms, _f.Time);

        var rejected = await Assert.ThrowsAsync<MachineRejectedException>(() => machine.InitializeAsync());

        Assert.Contains("未清除的故障報警", Assert.Single(rejected.Reasons));
        Assert.Equal(MachineState.Uninitialized, machine.State);
        Assert.False(_f.AxisX.GetStatus().IsEnabled);
    }

    [Fact]
    public async Task 非未初始化狀態_拒絕再次初始化()
    {
        var machine = new Machine(_f.Config(), _f.Alarms, _f.Time);
        var init = machine.InitializeAsync();
        await _f.AdvanceUntilDoneAsync(init);
        await init;

        await Assert.ThrowsAsync<MachineRejectedException>(() => machine.InitializeAsync());

        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public void 配置缺項_拒絕創建_一次列出全部問題()
    {
        var door = _f.Io.CreateInput(new("門", 0, ActiveHigh: true));
        var config = _f.Config() with
        {
            OnStationFault = null,
            MonitorPeriod = TimeSpan.Zero,
            Guards =
            [
                new MachineGuard("門", door, true, StopMode: null) { Scope = MachineGuardScope.Run },
                new MachineGuard("光柵", door, true, StopMode.Immediate),
            ],
            Stations = [_f.StationA, _f.StationA],
        };

        var failure = Assert.Throws<MachineConfigException>(() => new Machine(config, _f.Alarms, _f.Time));

        Assert.Equal(5, failure.Errors.Count);
    }
}
