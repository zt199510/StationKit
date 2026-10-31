using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Simulation;
using StationKit.Safety;

namespace StationKit.Tests;

public class MotionGroupTests
{
    private static readonly MotionProfile Profile = MotionProfile.Symmetric(100, 1000);

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

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task 多軸同時下發_全部到位才完成()
    {
        var (x, y) = (await EnabledAxisAsync("X"), await EnabledAxisAsync("Y"));

        var group = MotionGroup.MoveAllAsync([new(x, 100, Profile), new(y, 50, Profile)]);
        Assert.Equal(AxisState.Moving, x.GetStatus().State);
        Assert.Equal(AxisState.Moving, y.GetStatus().State);

        _time.Advance(TimeSpan.FromSeconds(0.6));
        Assert.Equal(50, y.GetStatus().Position);
        Assert.False(group.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(0.5));
        await group;
        Assert.Equal(100, x.GetStatus().Position);
    }

    [Fact]
    public async Task 一軸報警_其餘軸立即停下_只拋出報警軸的故障()
    {
        var (x, y) = (await EnabledAxisAsync("X"), await EnabledAxisAsync("Y"));

        var group = MotionGroup.MoveAllAsync([new(x, 100, Profile), new(y, 100, Profile)]);
        _time.Advance(TimeSpan.FromSeconds(0.55));
        x.InjectAlarm("過載");

        var fault = await Assert.ThrowsAsync<MotionFaultException>(() => group);
        Assert.Equal("X", fault.Axis);
        Assert.Contains("過載", fault.Message);

        // Y 沒有繼續走到 100，而是停在報警發生時的位置
        Assert.Equal(AxisState.Idle, y.GetStatus().State);
        Assert.Equal(50, y.GetStatus().Position, 6);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(50, y.GetStatus().Position, 6);
    }

    [Fact]
    public async Task 排在前面的軸被拒絕_後面的軸不下發()
    {
        var (x, y) = (await EnabledAxisAsync("X"), await EnabledAxisAsync("Y"));
        var unhomedX = new GuardedAxis(x, Safety);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => MotionGroup.MoveAllAsync([new(unhomedX, 100, Profile), new(y, 100, Profile)]));

        Assert.Contains("未回零", ex.Message);
        Assert.Equal(AxisState.Idle, y.GetStatus().State);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, y.GetStatus().Position);
    }

    [Fact]
    public async Task 排在後面的軸被拒絕_前面已下發的軸被停下()
    {
        var (x, y) = (await EnabledAxisAsync("X"), await EnabledAxisAsync("Y"));
        var unhomedX = new GuardedAxis(x, Safety);

        await Assert.ThrowsAsync<MotionRejectedException>(
            () => MotionGroup.MoveAllAsync([new(y, 100, Profile), new(unhomedX, 100, Profile)]));

        Assert.Equal(AxisState.Idle, y.GetStatus().State);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, y.GetStatus().Position);
    }

    [Fact]
    public async Task 調用方取消_全部停下並拋出取消()
    {
        var (x, y) = (await EnabledAxisAsync("X"), await EnabledAxisAsync("Y"));
        using var cts = new CancellationTokenSource();

        var group = MotionGroup.MoveAllAsync([new(x, 100, Profile), new(y, 100, Profile)], cts.Token);
        _time.Advance(TimeSpan.FromSeconds(0.55));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => group);
        Assert.Equal(50, x.GetStatus().Position, 6);
        Assert.Equal(50, y.GetStatus().Position, 6);
        Assert.Equal(AxisState.Idle, x.GetStatus().State);
        Assert.Equal(AxisState.Idle, y.GetStatus().State);
    }

    [Fact]
    public async Task 多軸各自故障_全部報出()
    {
        var group = MotionGroup.RunAllAsync(
        [
            _ => Task.FromException(new MotionFaultException("X", "伺服報警")),
            _ => Task.FromException(new MotionFaultException("Y", "伺服報警")),
        ]);

        var fault = await Assert.ThrowsAsync<MotionGroupFaultException>(() => group);
        Assert.Equal(2, fault.Faults.Count);
        Assert.Equal("X,Y", fault.Axis);
    }

    [Fact]
    public async Task 同一根軸出現兩次_下發前拒絕()
    {
        var x = await EnabledAxisAsync("X");

        await Assert.ThrowsAsync<ArgumentException>(
            () => MotionGroup.MoveAllAsync([new(x, 10, Profile), new(new GuardedAxis(x, Safety), 20, Profile)]));

        Assert.Equal(AxisState.Idle, x.GetStatus().State);
    }

    [Fact]
    public async Task 多軸同時回零()
    {
        var (x, y) = (await EnabledAxisAsync("X"), await EnabledAxisAsync("Y"));

        var group = MotionGroup.RunAllAsync([x.HomeAsync, y.HomeAsync]);
        _time.Advance(x.HomeDuration);
        await group;

        Assert.True(x.GetStatus().IsHomed);
        Assert.True(y.GetStatus().IsHomed);
    }

    [Fact]
    public async Task 空的運動組_直接完成()
    {
        await MotionGroup.MoveAllAsync([]);
    }

    private async Task<SimAxis> EnabledAxisAsync(string name)
    {
        var axis = new SimAxis(name, _time);
        await axis.EnableAsync();
        return axis;
    }
}
