using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Io;
using StationKit.Io.Simulation;

namespace StationKit.Tests;

public sealed class CylinderTests : IDisposable
{
    private static readonly TimeSpan Travel = TimeSpan.FromMilliseconds(100);

    private static readonly IoPointConfig ExtendValve = new("伸出閥", 0, ActiveHigh: true);
    private static readonly IoPointConfig RetractValve = new("縮回閥", 1, ActiveHigh: true);
    private static readonly IoPointConfig ExtendedSensor = new("伸出到位", 0, ActiveHigh: true);

    // 低電平有效的感應器，驗證模擬氣缸和氣缸用的是同一套電平換算
    private static readonly IoPointConfig RetractedSensor = new("縮回到位", 1, ActiveHigh: false);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeClockStepper _clock;
    private readonly SimIoBoard _board;
    private readonly List<IDisposable> _disposables = [];

    public CylinderTests()
    {
        _board = new SimIoBoard(timeProvider: _time);
        _clock = new FakeClockStepper(_time, SimIoBoard.DefaultPeriod);
    }

    public void Dispose()
    {
        _disposables.ForEach(d => d.Dispose());
        _board.Dispose();
    }

    [Fact]
    public async Task 雙電控雙感應器_伸出縮回到位()
    {
        var (cylinder, _) = Create(doubleValve: true);
        Assert.Equal(CylinderState.Retracted, cylinder.GetStatus().State);

        var extend = cylinder.ExtendAsync();
        await _clock.AdvanceAsync(Travel / 2);
        Assert.False(extend.IsCompleted);
        Assert.Equal(CylinderState.Moving, cylinder.GetStatus().State);
        await _clock.AdvanceUntilDoneAsync(extend, Travel);
        await extend;
        Assert.Equal(CylinderState.Extended, cylinder.GetStatus().State);
        Assert.True(_board.GetOutputLevel(ExtendValve.Bit));
        Assert.False(_board.GetOutputLevel(RetractValve.Bit));

        var retract = cylinder.RetractAsync();
        await _clock.AdvanceUntilDoneAsync(retract, Travel * 2);
        await retract;
        Assert.Equal(CylinderState.Retracted, cylinder.GetStatus().State);
        Assert.False(_board.GetOutputLevel(ExtendValve.Bit));
        Assert.True(_board.GetOutputLevel(RetractValve.Bit));
    }

    [Fact]
    public async Task 雙電控_先關反方向的閥再開目標閥_不會兩個閥同時打開()
    {
        var (cylinder, _) = Create(doubleValve: true);
        var both = false;
        _board.OutputWritten += (_, _) =>
            both |= _board.GetOutputLevel(ExtendValve.Bit) && _board.GetOutputLevel(RetractValve.Bit);

        var extend = cylinder.ExtendAsync();
        await _clock.AdvanceUntilDoneAsync(extend, Travel * 2);
        await extend;
        var retract = cylinder.RetractAsync();
        await _clock.AdvanceUntilDoneAsync(retract, Travel * 2);
        await retract;

        Assert.False(both);
    }

    [Fact]
    public async Task 單電控_斷電縮回()
    {
        var (cylinder, _) = Create(doubleValve: false);

        var extend = cylinder.ExtendAsync();
        await _clock.AdvanceUntilDoneAsync(extend, Travel * 2);
        await extend;
        var retract = cylinder.RetractAsync();
        await _clock.AdvanceUntilDoneAsync(retract, Travel * 2);
        await retract;

        Assert.False(_board.GetOutputLevel(ExtendValve.Bit));
        Assert.Equal(CylinderState.Retracted, cylinder.GetStatus().State);
    }

    [Fact]
    public async Task 氣缸卡住_超時報故障並指明哪個感應器沒到()
    {
        var (cylinder, sim) = Create(doubleValve: true);
        sim.Jammed = true;

        var extend = cylinder.ExtendAsync();
        await _clock.AdvanceUntilDoneAsync(extend, TimeSpan.FromMilliseconds(600));

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => extend);
        Assert.Equal("上料氣缸", ex.Signal);
        Assert.Contains("伸出超時", ex.Message);
        Assert.Contains("伸出到位 未有效", ex.Message);
        Assert.Contains("檢查氣壓", ex.Message);
    }

    [Fact]
    public async Task 目標感應器粘連_不會在原地立即到位()
    {
        // 單感應器時粘連無法從兩端一致性發現，靠「先等原位感應器消失」兜住：氣缸沒動就不算到位
        var (cylinder, sim) = Create(doubleValve: true);
        sim.StickSensor(RetractedSensor, true);
        Assert.Equal(CylinderState.Retracted, cylinder.GetStatus().State);

        var extend = cylinder.ExtendAsync();
        await _clock.AdvanceUntilDoneAsync(extend, TimeSpan.FromMilliseconds(600));

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => extend);
        Assert.Contains("縮回到位 未消失", ex.Message);
    }

    [Fact]
    public async Task 兩個到位同時有效_拒絕動作_閥不動()
    {
        var (cylinder, sim) = Create(doubleValve: true);
        sim.StickSensor(ExtendedSensor, true);

        var status = cylinder.GetStatus();
        Assert.Equal(CylinderState.Fault, status.State);
        Assert.Contains("同時有效", status.FaultReason);

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => cylinder.ExtendAsync());
        Assert.Contains("拒絕伸出", ex.Message);
        Assert.False(_board.GetOutputLevel(ExtendValve.Bit));
    }

    [Fact]
    public void 卡故障_狀態為故障並帶原因()
    {
        var (cylinder, _) = Create(doubleValve: true);
        _board.InjectFault("通訊中斷");

        var status = cylinder.GetStatus();
        Assert.Equal(CylinderState.Fault, status.State);
        Assert.Contains("通訊中斷", status.FaultReason);
    }

    [Fact]
    public void 缺感應器又沒配延時_拒絕創建()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Cylinder(new CylinderConfig
        {
            Name = "頂升氣缸",
            ExtendValve = _board.CreateOutput(ExtendValve),
            ExtendedSensor = _board.CreateInput(ExtendedSensor),
            Timeout = TimeSpan.FromSeconds(1),
        }, _time));
        Assert.Contains("NoSensorDelay", ex.Message);
    }

    [Fact]
    public void 超時未配置_拒絕創建()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Cylinder(new CylinderConfig
        {
            Name = "頂升氣缸",
            ExtendValve = _board.CreateOutput(ExtendValve),
            NoSensorDelay = TimeSpan.FromMilliseconds(200),
        }, _time));
        Assert.Contains("動作超時未配置", ex.Message);
    }

    [Fact]
    public async Task 沒有感應器_按延時判定到位()
    {
        var cylinder = new Cylinder(new CylinderConfig
        {
            Name = "頂升氣缸",
            ExtendValve = _board.CreateOutput(ExtendValve),
            Timeout = TimeSpan.FromSeconds(1),
            NoSensorDelay = TimeSpan.FromMilliseconds(200),
        }, _time);
        Assert.Equal(CylinderState.Unknown, cylinder.GetStatus().State);

        var extend = cylinder.ExtendAsync();
        await _clock.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        Assert.False(extend.IsCompleted);
        await _clock.AdvanceUntilDoneAsync(extend, TimeSpan.FromMilliseconds(100));
        await extend;
    }

    private (Cylinder Cylinder, SimCylinder Sim) Create(bool doubleValve)
    {
        var sim = new SimCylinder(_board, ExtendValve, doubleValve ? RetractValve : null, ExtendedSensor,
            RetractedSensor, Travel, _time);
        _disposables.Add(sim);
        var cylinder = new Cylinder(new CylinderConfig
        {
            Name = "上料氣缸",
            ExtendValve = _board.CreateOutput(ExtendValve),
            RetractValve = doubleValve ? _board.CreateOutput(RetractValve) : null,
            ExtendedSensor = _board.CreateInput(ExtendedSensor),
            RetractedSensor = _board.CreateInput(RetractedSensor),
            Timeout = TimeSpan.FromMilliseconds(500),
        }, _time);
        return (cylinder, sim);
    }
}
