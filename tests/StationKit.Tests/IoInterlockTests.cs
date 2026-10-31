using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Io.Simulation;
using StationKit.Motion.Simulation;
using StationKit.Safety;
using StationKit.Stations;

namespace StationKit.Tests;

public sealed class IoInterlockTests : IDisposable
{
    private static readonly AxisSafetyConfig Safety = new()
    {
        SoftLimitMin = -1,
        SoftLimitMax = 200,
        MaxVelocity = 500,
        MaxAcceleration = 5000,
        EmergencyStopEnabled = true,
        EmergencyStopActiveHigh = false,
        ConfirmedOnMachine = true,
    };

    private static readonly MotionProfile Profile = MotionProfile.Symmetric(100, 1000);

    private readonly FakeTimeProvider _time = new();
    private readonly SimIoBoard _board;
    private readonly FakeClockStepper _clock;

    public IoInterlockTests()
    {
        _board = new SimIoBoard(timeProvider: _time);
        _clock = new FakeClockStepper(_time, SimIoBoard.DefaultPeriod);
    }

    public void Dispose() => _board.Dispose();

    [Fact]
    public async Task 安全門打開_運動被拒絕_關門後放行()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true));
        var x = await HomedAxisAsync();
        var guarded = new GuardedAxis(x, Safety, [Interlock.RequireInput("安全門", door)]);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(() => guarded.MoveAbsoluteAsync(50, Profile));
        Assert.Contains("[安全門] 需要 安全門關閉 有效，當前無效", ex.Message);

        _board.SetInputLevel(0, true);
        var move = guarded.MoveAbsoluteAsync(50, Profile);
        _time.Advance(TimeSpan.FromSeconds(5));
        await move;
        Assert.Equal(50, x.GetStatus().Position);
    }

    [Fact]
    public async Task 卡故障_互鎖按阻止處理並寫明原因()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true));
        _board.SetInputLevel(0, true);
        _board.InjectFault("通訊中斷");
        var guarded = new GuardedAxis(await HomedAxisAsync(), Safety, [Interlock.RequireInput("安全門", door)]);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(() => guarded.MoveAbsoluteAsync(50, Profile));
        Assert.Contains("安全門關閉 狀態不可信", ex.Message);
        Assert.Contains("通訊中斷", ex.Message);
    }

    [Fact]
    public async Task 互鎖只對指定運動類型生效()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true));
        var x = await HomedAxisAsync();
        var guarded = new GuardedAxis(x, Safety, [Interlock.RequireInput("安全門", door, kinds: [MotionKind.Move])]);

        var home = guarded.HomeAsync();
        _time.Advance(x.HomeDuration);
        await home;
        await Assert.ThrowsAsync<MotionRejectedException>(() => guarded.MoveAbsoluteAsync(50, Profile));
    }

    [Fact]
    public async Task 互鎖規則拋異常_轉為拒絕_軸不動()
    {
        var x = await HomedAxisAsync();
        var broken = Interlock.Create("壞規則", _ => throw new InvalidOperationException("配置錯誤"));
        var guarded = new GuardedAxis(x, Safety, [broken]);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(() => guarded.MoveAbsoluteAsync(50, Profile));
        Assert.Contains("互鎖檢查異常：配置錯誤", ex.Message);
        Assert.Equal(AxisState.Idle, x.GetStatus().State);
    }

    [Fact]
    public async Task 握手_正常完成後撤銷請求()
    {
        var (handshake, request) = CreateHandshake();

        var run = handshake.RunAsync();
        await _clock.AdvanceUntilDoneAsync(Task.Delay(Timeout.Infinite), TimeSpan.FromMilliseconds(20));
        Assert.True(_board.GetOutputLevel(0));
        _board.SetInputLevel(0, true);
        await _clock.AdvanceUntilDoneAsync(run, TimeSpan.FromMilliseconds(50));
        await run;
        Assert.False(_board.GetOutputLevel(0));
        Assert.False(request.Read());
    }

    [Fact]
    public async Task 握手_上一次的完成未撤銷_不發請求()
    {
        var (handshake, _) = CreateHandshake();
        _board.SetInputLevel(0, true);

        var run = handshake.RunAsync();
        await _clock.AdvanceUntilDoneAsync(run, TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<HandshakeFaultException>(() => run);
        Assert.Contains("未撤銷", ex.Message);
        Assert.False(_board.GetOutputLevel(0));
    }

    [Fact]
    public async Task 握手_對方未完成_超時並撤銷請求()
    {
        var (handshake, _) = CreateHandshake();

        var run = handshake.RunAsync();
        await _clock.AdvanceUntilDoneAsync(run, TimeSpan.FromMilliseconds(500));

        var ex = await Assert.ThrowsAsync<HandshakeFaultException>(() => run);
        Assert.Contains("請求已撤銷", ex.Message);
        Assert.False(_board.GetOutputLevel(0));
    }

    [Fact]
    public void IO異常_映射到對應報警代碼()
    {
        Assert.Equal(IoAlarms.CylinderFault, IoAlarms.Classify(new CylinderFaultException("氣缸", "超時")));
        Assert.Equal(IoAlarms.HandshakeFault, IoAlarms.Classify(new HandshakeFaultException("握手", "超時")));
        Assert.Equal(IoAlarms.SignalTimeout,
            IoAlarms.Classify(new IoTimeoutException("真空", true, false, TimeSpan.FromSeconds(1))));
        Assert.Equal(IoAlarms.IoFault, IoAlarms.Classify(new IoFaultException("真空", "卡故障")));
        Assert.Null(IoAlarms.Classify(new InvalidOperationException()));
    }

    private (Handshake Handshake, IDigitalOutput Request) CreateHandshake()
    {
        var request = _board.CreateOutput(new("請求取料", 0, ActiveHigh: true));
        var done = _board.CreateInput(new("下游完成", 0, ActiveHigh: true));
        return (new Handshake("下游取料", request, done, TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(100)), request);
    }

    private async Task<SimAxis> HomedAxisAsync()
    {
        var axis = new SimAxis("X", _time);
        await axis.EnableAsync();
        var home = axis.HomeAsync();
        _time.Advance(axis.HomeDuration);
        await home;
        return axis;
    }
}
