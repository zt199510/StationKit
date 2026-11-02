using StationKit.Abstractions.Motion;
using StationKit.Motion.Simulation;

namespace StationKit.Tests;

public class MotionEstimateTests
{
    [Fact]
    public void 距離足夠_梯形含加減速時間()
    {
        // v=100, a=1000, d=500：100/100 + 100/2000 + 100/1000
        Assert.Equal(1.15, new MotionProfile(100, 1000, 500).EstimateMoveSeconds(100), 9);
    }

    [Fact]
    public void 距離不足_三角形按峰值速度計算()
    {
        // 峰值 100：加速段 5 + 減速段 5 = 10；只用距離除以速度會得到 0.02
        Assert.Equal(0.2, MotionProfile.Symmetric(500, 1000).EstimateMoveSeconds(10), 9);
    }

    [Fact]
    public void 加減速不對稱_三角形()
    {
        // 峰值 100：100²/2000 + 100²/1000 = 15
        Assert.Equal(0.3, new MotionProfile(200, 1000, 500).EstimateMoveSeconds(15), 9);
    }

    [Fact]
    public void 恰好在臨界距離_梯形和三角形結果相同()
    {
        // v=100 時加減速剛好走完 15，沒有勻速段
        Assert.Equal(0.3, new MotionProfile(100, 1000, 500).EstimateMoveSeconds(15), 9);
    }

    [Fact]
    public void 距離為零_時間為零()
    {
        Assert.Equal(0, MotionProfile.Symmetric(100, 1000).EstimateMoveSeconds(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 距離非法_拋異常(double distance)
    {
        Assert.Throws<ArgumentException>(() => MotionProfile.Symmetric(100, 1000).EstimateMoveSeconds(distance));
    }

    [Fact]
    public void 運動參數非法_拋異常()
    {
        Assert.Throws<ArgumentException>(() => new MotionProfile(0, 1000, 1000).EstimateMoveSeconds(10));
    }

    [Theory]
    [InlineData(100, 100, 1000, 500)]
    [InlineData(1, 100, 1000, 500)]
    [InlineData(15, 100, 1000, 500)]
    [InlineData(300, 5, 100, 100)]
    [InlineData(10, 500, 1000, 1000)]
    [InlineData(0.001, 2000, 20000, 5000)]
    public void 與模擬軸的速度規劃一致(double distance, double velocity, double acceleration, double deceleration)
    {
        var profile = new MotionProfile(velocity, acceleration, deceleration);

        var plan = TrapezoidProfile.Plan(0, distance, profile);

        Assert.Equal(plan.AccelTime + plan.CruiseTime + plan.DecelTime, profile.EstimateMoveSeconds(distance), 9);
    }
}
