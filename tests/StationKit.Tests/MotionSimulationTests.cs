using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Motion;
using StationKit.Motion.Simulation;

namespace StationKit.Tests;

public class TrapezoidProfileTests
{
    [Fact]
    public void 距離足夠時為梯形()
    {
        // v=100, a=1000：加速 0.1s 走 5，減速 0.1s 走 5，勻速 0.9s 走 90
        var p = TrapezoidProfile.Plan(0, 100, MotionProfile.Symmetric(100, 1000));

        Assert.Equal(1.1, p.Duration.TotalSeconds, 6);
        Assert.Equal(5, p.PositionAt(TimeSpan.FromSeconds(0.1)), 6);
        Assert.Equal(50, p.PositionAt(TimeSpan.FromSeconds(0.55)), 6);
        Assert.Equal(100, p.PositionAt(TimeSpan.FromSeconds(2)), 6);
    }

    [Fact]
    public void 距離不足時退化為三角形()
    {
        var p = TrapezoidProfile.Plan(0, 1, MotionProfile.Symmetric(100, 1000));

        Assert.Equal(Math.Sqrt(1000), p.PeakVelocity, 6);
        Assert.Equal(0, p.CruiseTime);
        // TimeSpan 精度為 100ns，AccelTime 被截斷到刻度，在峰值速度下約有 2.4e-6 的偏差
        Assert.Equal(0.5, p.PositionAt(TimeSpan.FromSeconds(p.AccelTime)), 5);
    }

    [Fact]
    public void 負方向運動()
    {
        var p = TrapezoidProfile.Plan(100, 0, MotionProfile.Symmetric(100, 1000));

        Assert.Equal(50, p.PositionAt(TimeSpan.FromSeconds(0.55)), 6);
    }

    [Fact]
    public void 零距離耗時為零()
    {
        Assert.Equal(TimeSpan.Zero, TrapezoidProfile.Plan(3, 3, MotionProfile.Symmetric(1, 1)).Duration);
    }
}

public class SimAxisTests
{
    private static readonly MotionProfile Profile = MotionProfile.Symmetric(100, 1000);
    private readonly FakeTimeProvider _time = new();

    private async Task<SimAxis> EnabledAxisAsync(double positiveLimit = double.PositiveInfinity)
    {
        var axis = new SimAxis("X", _time) { PositiveLimitSwitch = positiveLimit };
        await axis.EnableAsync();
        return axis;
    }

    [Fact]
    public async Task 運動在到位時才完成()
    {
        var axis = await EnabledAxisAsync();

        var move = axis.MoveAbsoluteAsync(100, Profile);
        _time.Advance(TimeSpan.FromSeconds(0.55));

        Assert.False(move.IsCompleted);
        Assert.Equal(AxisState.Moving, axis.GetStatus().State);
        Assert.Equal(50, axis.GetStatus().Position, 6);

        _time.Advance(TimeSpan.FromSeconds(0.6));
        await move;

        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        Assert.Equal(100, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 下發前已取消的運動不會下發()
    {
        var axis = await EnabledAxisAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => axis.MoveAbsoluteAsync(100, Profile, new CancellationToken(canceled: true)));

        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        Assert.Equal(0, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 運動中取消會先停軸再報告取消()
    {
        var axis = await EnabledAxisAsync();
        using var cts = new CancellationTokenSource();

        var move = axis.MoveAbsoluteAsync(100, Profile, cts.Token);
        _time.Advance(TimeSpan.FromSeconds(0.55));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        Assert.Equal(50, axis.GetStatus().Position, 6);

        // 停止後位置不再變化
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(50, axis.GetStatus().Position, 6);
    }

    [Fact]
    public async Task 運動中報警以故障結束且需復位並重新回零()
    {
        var axis = await EnabledAxisAsync();
        await HomeAsync(axis);

        var move = axis.MoveAbsoluteAsync(100, Profile);
        _time.Advance(TimeSpan.FromSeconds(0.3));
        axis.InjectAlarm("伺服過載");

        var fault = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("伺服過載", fault.Message);
        Assert.Equal(AxisState.Alarm, axis.GetStatus().State);
        await Assert.ThrowsAsync<MotionFaultException>(() => axis.EnableAsync());

        await axis.ResetAlarmAsync();

        Assert.Equal(AxisState.Disabled, axis.GetStatus().State);
        Assert.False(axis.GetStatus().IsHomed);
    }

    [Fact]
    public async Task 超出硬限位時停在限位並報警()
    {
        var axis = await EnabledAxisAsync(positiveLimit: 50);

        var move = axis.MoveAbsoluteAsync(100, Profile);
        _time.Advance(TimeSpan.FromSeconds(2));

        var fault = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("正限位", fault.Message);
        var status = axis.GetStatus();
        Assert.Equal(50, status.Position);
        Assert.True(status.PositiveLimit);
        Assert.Equal(AxisState.Alarm, status.State);
    }

    [Fact]
    public async Task 未使能時拒絕運動()
    {
        var axis = new SimAxis("X", _time);

        var fault = await Assert.ThrowsAsync<MotionFaultException>(() => axis.MoveAbsoluteAsync(10, Profile));
        Assert.Contains("未使能", fault.Message);
    }

    [Fact]
    public async Task 運動中再次下發被拒絕()
    {
        var axis = await EnabledAxisAsync();
        var first = axis.MoveAbsoluteAsync(100, Profile);

        await Assert.ThrowsAsync<MotionFaultException>(() => axis.MoveAbsoluteAsync(10, Profile));

        _time.Advance(TimeSpan.FromSeconds(2));
        await first;
    }

    private async Task HomeAsync(SimAxis axis)
    {
        var home = axis.HomeAsync();
        _time.Advance(axis.HomeDuration);
        await home;
    }
}
