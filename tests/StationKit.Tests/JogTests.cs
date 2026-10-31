using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Simulation;
using StationKit.Safety;

namespace StationKit.Tests;

public class JogTests
{
    private static readonly MotionProfile Profile = MotionProfile.Symmetric(100, 1000);
    private static readonly MotionProfile SlowProfile = MotionProfile.Symmetric(10, 1000);
    private static readonly TimeSpan Watchdog = TimeSpan.FromMilliseconds(300);

    private static readonly AxisSafetyConfig Config = new()
    {
        SoftLimitMin = -1,
        SoftLimitMax = 200,
        MaxVelocity = 500,
        MaxAcceleration = 5000,
        UnhomedJogMaxVelocity = 20,
        EmergencyStopEnabled = true,
        EmergencyStopActiveHigh = false,
        ConfirmedOnMachine = true,
    };

    private readonly FakeTimeProvider _time = new();

    //--------------------- 模擬軸：速度模式點動 ---------------------

    [Fact]
    public async Task 按住期間持續運動_鬆開後停住並正常完成()
    {
        var axis = await EnabledAxisAsync();
        using var hold = new CancellationTokenSource();

        var jog = axis.JogAsync(JogDirection.Positive, Profile, hold.Token);
        _time.Advance(TimeSpan.FromSeconds(0.55));
        Assert.Equal(AxisState.Moving, axis.GetStatus().State);
        Assert.Equal(50, axis.GetStatus().Position, 6);

        await hold.CancelAsync();
        await jog;

        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(50, axis.GetStatus().Position, 6);
    }

    [Fact]
    public async Task 下發前已鬆開_不運動直接完成()
    {
        var axis = await EnabledAxisAsync();

        await axis.JogAsync(JogDirection.Negative, Profile, new CancellationToken(canceled: true));

        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        Assert.Equal(0, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 點動到硬限位_報故障()
    {
        var axis = new SimAxis("X", _time) { NegativeLimitSwitch = -5 };
        await axis.EnableAsync();
        using var hold = new CancellationTokenSource();

        var jog = axis.JogAsync(JogDirection.Negative, Profile, hold.Token);
        _time.Advance(TimeSpan.FromSeconds(1));

        var fault = await Assert.ThrowsAsync<MotionFaultException>(() => jog);
        Assert.Contains("負限位", fault.Message);
        Assert.Equal(-5, axis.GetStatus().Position);
    }

    //--------------------- GuardedAxis：點動的安全檢查 ---------------------

    [Fact]
    public async Task 未回零且未配置未回零點動速度_拒絕點動()
    {
        var axis = await EnabledAxisAsync();
        var guarded = new GuardedAxis(axis, Config with { UnhomedJogMaxVelocity = double.NaN });

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.JogAsync(JogDirection.Positive, SlowProfile, CancellationToken.None));

        Assert.Contains("未配置未回零點動速度", ex.Message);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 未回零時點動速度超過未回零上限_拒絕()
    {
        var axis = await EnabledAxisAsync();
        var guarded = new GuardedAxis(axis, Config);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.JogAsync(JogDirection.Positive, Profile, CancellationToken.None));

        Assert.Contains("超過未回零上限 20", ex.Message);
    }

    [Fact]
    public async Task 未回零時低速點動_允許且不受軟限位限制()
    {
        var axis = await EnabledAxisAsync();
        var guarded = new GuardedAxis(axis, Config);
        using var hold = new CancellationTokenSource();

        var jog = guarded.JogAsync(JogDirection.Negative, SlowProfile, hold.Token);
        _time.Advance(TimeSpan.FromSeconds(1));
        await hold.CancelAsync();
        await jog;

        // 未回零時坐標不可信，軟限位 -1 不起作用，靠低速和硬限位兜底
        Assert.True(axis.GetStatus().Position < -1);
    }

    [Fact]
    public async Task 已回零時點動_走到軟限位邊界自動停下()
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, Config);
        using var hold = new CancellationTokenSource();

        var jog = guarded.JogAsync(JogDirection.Positive, Profile, hold.Token);
        _time.Advance(TimeSpan.FromSeconds(5));
        await jog;

        Assert.Equal(200, axis.GetStatus().Position);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 已回零時點動中鬆開_停在當前位置()
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, Config);
        using var hold = new CancellationTokenSource();

        var jog = guarded.JogAsync(JogDirection.Positive, Profile, hold.Token);
        _time.Advance(TimeSpan.FromSeconds(0.55));
        await hold.CancelAsync();
        await jog;

        Assert.Equal(50, axis.GetStatus().Position, 6);
    }

    [Fact]
    public async Task 已在軟限位邊界_拒絕繼續同向點動()
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, Config with { SoftLimitMin = 0 });

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.JogAsync(JogDirection.Negative, Profile, CancellationToken.None));

        Assert.Contains("已在軟限位邊界 0", ex.Message);
    }

    [Fact]
    public async Task 點動同樣經過互鎖檢查_請求帶方向和最遠位置()
    {
        var axis = await HomedAxisAsync();
        MotionRequest? seen = null;
        var block = Interlock.Create("門禁", r =>
        {
            seen = r;
            return "安全門未關";
        });
        var guarded = new GuardedAxis(axis, Config, [block]);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.JogAsync(JogDirection.Positive, Profile, CancellationToken.None));

        Assert.Contains("[門禁] 安全門未關", ex.Message);
        Assert.Equal(new MotionRequest("X", MotionKind.Jog, 200) { Direction = JogDirection.Positive }, seen);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 點動速度超過最大速度_拒絕()
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, Config);

        await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.JogAsync(JogDirection.Positive, MotionProfile.Symmetric(600, 1000), CancellationToken.None));
    }

    [Fact]
    public void 未回零點動速度超過最大速度_配置校驗報錯()
    {
        var errors = SafetyConfigValidator.Validate("X", Config with { UnhomedJogMaxVelocity = 600 });

        Assert.Single(errors);
        Assert.Contains("未回零點動速度", errors[0]);
    }

    //--------------------- 相對運動（寸動） ---------------------

    [Fact]
    public async Task 相對運動_以當前位置為起點()
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, Config);
        await RunAsync(guarded.MoveAbsoluteAsync(100, Profile));

        await RunAsync(guarded.MoveRelativeAsync(-0.5, Profile));

        Assert.Equal(99.5, axis.GetStatus().Position, 6);
    }

    [Fact]
    public async Task 相對運動換算後超出軟限位_被拒絕()
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, Config);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(() => guarded.MoveRelativeAsync(250, Profile));

        Assert.Contains("軟限位", ex.Message);
    }

    [Fact]
    public async Task 運動中相對運動_被拒絕()
    {
        var axis = await HomedAxisAsync();
        var move = axis.MoveAbsoluteAsync(100, Profile);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(() => axis.MoveRelativeAsync(1, Profile));

        Assert.Contains("要求軸靜止", ex.Message);
        _time.Advance(TimeSpan.FromSeconds(2));
        await move;
    }

    //--------------------- JogSession：看門狗 ---------------------

    [Fact]
    public async Task 持續KeepAlive_軸一直走_鬆開後正常完成()
    {
        var axis = await EnabledAxisAsync();
        using var session = JogSession.Start(axis, JogDirection.Positive, Profile, Watchdog, _time);

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            session.KeepAlive();
        }

        Assert.Equal(AxisState.Moving, axis.GetStatus().State);
        session.Release();
        await session.Completion;
        Assert.False(session.TimedOut);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 鬆開事件丟失_看門狗超時自動停軸並報故障()
    {
        var axis = await EnabledAxisAsync();
        using var session = JogSession.Start(axis, JogDirection.Positive, Profile, Watchdog, _time);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        session.KeepAlive();

        // 之後再也沒有 KeepAlive，也沒有 Release
        _time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(AxisState.Moving, axis.GetStatus().State);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        var fault = await Assert.ThrowsAsync<MotionFaultException>(() => session.Completion);
        Assert.Contains("看門狗超時", fault.Message);
        Assert.True(session.TimedOut);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 鬆開後再KeepAlive_軸不會重新運動()
    {
        var axis = await EnabledAxisAsync();
        using var session = JogSession.Start(axis, JogDirection.Positive, Profile, Watchdog, _time);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        session.Release();
        session.KeepAlive();
        await session.Completion;
        var stoppedAt = axis.GetStatus().Position;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        Assert.Equal(stoppedAt, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 會話被釋放_等同鬆開()
    {
        var axis = await EnabledAxisAsync();
        var session = JogSession.Start(axis, JogDirection.Positive, Profile, Watchdog, _time);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        session.Dispose();

        await session.Completion;
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 安全檢查拒絕的點動_會話以拒絕結束()
    {
        var axis = await EnabledAxisAsync();
        var guarded = new GuardedAxis(axis, Config with { UnhomedJogMaxVelocity = double.NaN });

        using var session = JogSession.Start(guarded, JogDirection.Positive, SlowProfile, Watchdog, _time);

        await Assert.ThrowsAsync<MotionRejectedException>(() => session.Completion);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    private async Task<SimAxis> EnabledAxisAsync()
    {
        var axis = new SimAxis("X", _time);
        await axis.EnableAsync();
        return axis;
    }

    private async Task<SimAxis> HomedAxisAsync()
    {
        var axis = await EnabledAxisAsync();
        await RunAsync(axis.HomeAsync());
        return axis;
    }

    private async Task RunAsync(Task motion)
    {
        _time.Advance(TimeSpan.FromSeconds(5));
        await motion;
    }
}
