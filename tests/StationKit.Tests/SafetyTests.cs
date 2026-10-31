using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Simulation;
using StationKit.Safety;

namespace StationKit.Tests;

public class SafetyTests
{
    private static readonly AxisSafetyConfig ValidConfig = new()
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
    public void 未配置的參數全部報出來()
    {
        var errors = SafetyConfigValidator.Validate("X", new AxisSafetyConfig());

        Assert.Contains(errors, e => e.Contains("軟限位未配置"));
        Assert.Contains(errors, e => e.Contains("最大速度"));
        Assert.Contains(errors, e => e.Contains("最大加速度"));
        Assert.Contains(errors, e => e.Contains("未經實機確認"));
    }

    [Fact]
    public void 啟用急停但未指定電平被拒絕()
    {
        var errors = SafetyConfigValidator.Validate("X", ValidConfig with { EmergencyStopActiveHigh = null });

        Assert.Single(errors);
        Assert.Contains("急停有效電平", errors[0]);
    }

    [Fact]
    public async Task 校驗回讀都通過後才使能全部軸()
    {
        var (x, y) = (new SimAxis("X", _time), new SimAxis("Y", _time));

        var log = await SafetyStartup.RunAsync([Bind(x), Bind(y)]);

        Assert.True(x.GetStatus().IsEnabled);
        Assert.True(y.GetStatus().IsEnabled);
        Assert.Equal("全部軸已使能", log[^1]);
    }

    [Fact]
    public async Task 任一軸配置非法時不寫入也不使能任何軸()
    {
        var (x, y) = (new SimAxis("X", _time), new SimAxis("Y", _time));

        var ex = await Assert.ThrowsAsync<SafetyStartupException>(() => SafetyStartup.RunAsync(
            [Bind(x), new AxisSafetyBinding(y, y, ValidConfig with { MaxVelocity = double.NaN })]));

        Assert.Contains(ex.Errors, e => e.StartsWith("Y：最大速度", StringComparison.Ordinal));
        Assert.False(x.GetStatus().IsEnabled);
        Assert.False(y.GetStatus().IsEnabled);
        Assert.Equal(new AxisSafetyConfig(), await x.ReadSafetyConfigAsync());
    }

    [Fact]
    public async Task 回讀不一致時不使能任何軸()
    {
        var (x, y) = (new SimAxis("X", _time), new SimAxis("Y", _time));
        y.ReadBackTransform = c => c with { EmergencyStopActiveHigh = true };

        var ex = await Assert.ThrowsAsync<SafetyStartupException>(() => SafetyStartup.RunAsync([Bind(x), Bind(y)]));

        Assert.Contains(ex.Errors, e => e.Contains("Y：急停配置回讀不一致，期望 低電平有效，實際 高電平有效"));
        Assert.False(x.GetStatus().IsEnabled);
        Assert.False(y.GetStatus().IsEnabled);
    }

    [Fact]
    public async Task 未回零時拒絕運動()
    {
        var axis = await EnabledAxisAsync();
        var guarded = new GuardedAxis(axis, ValidConfig);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.MoveAbsoluteAsync(10, MotionProfile.Symmetric(100, 1000)));

        Assert.Contains("未回零", ex.Message);
    }

    [Theory]
    [InlineData(201, 100, "軟限位")]
    [InlineData(10, 600, "速度")]
    public async Task 超出軟限位或超速時拒絕且軸不動(double target, double velocity, string expected)
    {
        var axis = await HomedAxisAsync();
        var guarded = new GuardedAxis(axis, ValidConfig);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(
            () => guarded.MoveAbsoluteAsync(target, MotionProfile.Symmetric(velocity, 1000)));

        Assert.Contains(expected, ex.Message);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
        Assert.Equal(0, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 互鎖規則阻止運動並說明原因()
    {
        var z = await HomedAxisAsync("Z");
        var x = await HomedAxisAsync("X");
        var zSafeHeight = Interlock.Create("Z 安全高度", r =>
            r.Axis == "X" && z.GetStatus().Position < 10 ? $"Z 在 {z.GetStatus().Position}，低於安全高度 10" : null);
        var guardedX = new GuardedAxis(x, ValidConfig, [zSafeHeight]);
        var profile = MotionProfile.Symmetric(100, 1000);

        var ex = await Assert.ThrowsAsync<MotionRejectedException>(() => guardedX.MoveAbsoluteAsync(50, profile));
        Assert.Contains("[Z 安全高度] Z 在 0，低於安全高度 10", ex.Message);

        await MoveAsync(z, 20, profile);
        await MoveAsync(guardedX, 50, profile);
        Assert.Equal(50, x.GetStatus().Position);
    }

    private static AxisSafetyBinding Bind(SimAxis axis) => new(axis, axis, ValidConfig);

    private async Task<SimAxis> EnabledAxisAsync(string name = "X")
    {
        var axis = new SimAxis(name, _time);
        await axis.EnableAsync();
        return axis;
    }

    private async Task<SimAxis> HomedAxisAsync(string name = "X")
    {
        var axis = await EnabledAxisAsync(name);
        var home = axis.HomeAsync();
        _time.Advance(axis.HomeDuration);
        await home;
        return axis;
    }

    private async Task MoveAsync(IAxis axis, double position, MotionProfile profile)
    {
        var move = axis.MoveAbsoluteAsync(position, profile);
        _time.Advance(TimeSpan.FromSeconds(5));
        await move;
    }
}
