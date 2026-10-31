using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Dmc;
using StationKit.Safety;

namespace StationKit.Tests.Dmc;

public class DmcAxisTests
{
    private static readonly MotionProfile Profile = new(100, 1000, 500);

    private static readonly AxisSafetyConfig Safety = new()
    {
        SoftLimitMin = -1,
        SoftLimitMax = 300,
        MaxVelocity = 500,
        MaxAcceleration = 5000,
        EmergencyStopEnabled = true,
        EmergencyStopActiveHigh = false,
        ConfirmedOnMachine = true,
    };

    private readonly FakeDmcNativeApi _api = new();
    private readonly List<string> _log = [];

    private DmcAxis CreateAxis(ushort axisNo = 0, TimeSpan? motionTimeout = null) => new(_api, new DmcAxisOptions
    {
        CardNo = 0,
        AxisNo = axisNo,
        Name = $"X{axisNo}",
        PulseEquivalent = 1000,
        HomeProfile = new DmcHomeProfile(Mode: 17, LowVelocity: 5, HighVelocity: 20, AccelerationTime: 0.1,
            DecelerationTime: 0.1, Offset: 0),
        PollInterval = TimeSpan.FromMilliseconds(1),
        MotionTimeout = motionTimeout ?? TimeSpan.FromSeconds(5),
    }, log: _log.Add);

    private async Task<DmcAxis> EnabledAxisAsync(TimeSpan? motionTimeout = null)
    {
        var axis = CreateAxis(motionTimeout: motionTimeout);
        await axis.EnableAsync();
        return axis;
    }

    [Fact]
    public async Task 絕對運動_按加減速時間設速度後下發_到位才完成()
    {
        using var axis = await EnabledAxisAsync();

        await axis.MoveAbsoluteAsync(120, Profile);

        Assert.Equal(["nmc_set_axis_enable", "dmc_set_profile_unit", "dmc_pmove_unit"], _api.Calls);
        // 雷賽用加減速時間：t = v / a
        Assert.Equal((0, 100, 0.1, 0.2, 0), _api.LastProfile);
        Assert.Equal(120, axis.GetStatus().Position);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 下發前已取消_不調用dmc_pmove_unit()
    {
        using var axis = await EnabledAxisAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => axis.MoveAbsoluteAsync(120, Profile, new CancellationToken(canceled: true)));

        Assert.DoesNotContain("dmc_pmove_unit", _api.Calls);
    }

    [Fact]
    public async Task 運動中取消_先減速停軸再拋出取消()
    {
        _api.PollsPerMove = int.MaxValue;
        using var axis = await EnabledAxisAsync();
        using var cts = new CancellationTokenSource();

        var move = axis.MoveAbsoluteAsync(120, Profile, cts.Token);
        await WaitForCallAsync("dmc_pmove_unit");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal("dmc_stop", _api.Calls[^1]);
        Assert.Equal(DmcStopMode.Decelerated, _api.LastStopMode);
    }

    [Fact]
    public async Task 返回碼非零_異常和日誌帶上卡號軸號函數名和返回碼()
    {
        _api.ReturnCodes["dmc_pmove_unit"] = 9;
        using var axis = await EnabledAxisAsync();

        var ex = await Assert.ThrowsAsync<DmcCallException>(() => axis.MoveAbsoluteAsync(120, Profile));

        Assert.Equal("dmc_pmove_unit", ex.ApiName);
        Assert.Equal(9, ex.ReturnCode);
        Assert.Contains("CardId=0, AxisIndex=0, AxisName=X0, ApiName=dmc_pmove_unit, ReturnCode=9", ex.Message);
        Assert.Contains(_log, line => line.StartsWith("[報警]", StringComparison.Ordinal) && line.Contains("ReturnCode=9"));
    }

    [Fact]
    public async Task 運動中伺服報警_立即急停並報出驅動器錯誤碼()
    {
        _api.PollsPerMove = int.MaxValue;
        _api.AxisErrorCode = 0x7500;
        using var axis = await EnabledAxisAsync();

        var move = axis.MoveAbsoluteAsync(120, Profile);
        await WaitForCallAsync("dmc_pmove_unit");
        _api.IoStatus = 1; // ALM

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("伺服報警 ALM，驅動器錯誤碼 0x7500", ex.Message);
        Assert.Equal(DmcStopMode.Immediate, _api.LastStopMode);
    }

    [Fact]
    public async Task 被外部停止未到位_報故障而不是當作完成()
    {
        _api.PollsPerMove = int.MaxValue;
        using var axis = await EnabledAxisAsync();

        var move = axis.MoveAbsoluteAsync(120, Profile);
        await WaitForCallAsync("dmc_pmove_unit");
        await axis.StopAsync();

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("運動已停止但未到位：目標 120，實際 0", ex.Message);
    }

    [Fact]
    public async Task 運動超時_減速停止並報故障()
    {
        _api.PollsPerMove = int.MaxValue;
        using var axis = await EnabledAxisAsync(motionTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => axis.MoveAbsoluteAsync(120, Profile));

        Assert.Contains("超時", ex.Message);
        Assert.Contains("dmc_stop", _api.Calls);
    }

    [Fact]
    public async Task 觸發硬限位_報故障()
    {
        _api.PollsPerMove = int.MaxValue;
        using var axis = await EnabledAxisAsync();

        var move = axis.MoveAbsoluteAsync(120, Profile);
        await WaitForCallAsync("dmc_pmove_unit");
        _api.IoStatus = 1u << 1; // EL+

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("正限位", ex.Message);
    }

    [Fact]
    public async Task 報警中拒絕使能_不調用nmc_set_axis_enable()
    {
        _api.IoStatus = 1;
        using var axis = CreateAxis();

        await Assert.ThrowsAsync<MotionFaultException>(() => axis.EnableAsync());

        Assert.DoesNotContain("nmc_set_axis_enable", _api.Calls);
    }

    [Fact]
    public async Task 回零_回讀一致後下發_回零結果為1才算完成()
    {
        using var axis = await EnabledAxisAsync();
        _api.Position = 37;

        await axis.HomeAsync();

        Assert.Equal(["nmc_set_axis_enable", "nmc_set_home_profile", "nmc_get_home_profile", "dmc_home_move"], _api.Calls);
        Assert.True(axis.GetStatus().IsHomed);
        Assert.Equal(0, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 回零參數回讀不一致_不下發回零()
    {
        _api.HomeProfileReadback = p => p with { Mode = 35 };
        using var axis = await EnabledAxisAsync();

        var ex = await Assert.ThrowsAsync<DmcCallException>(() => axis.HomeAsync());

        Assert.Contains("回讀不一致", ex.Message);
        Assert.DoesNotContain("dmc_home_move", _api.Calls);
        Assert.False(axis.GetStatus().IsHomed);
    }

    [Fact]
    public async Task 回零參數返回16088_按兼容碼處理且以回讀為準()
    {
        _api.ReturnCodes["nmc_set_home_profile"] = 16088;
        using var axis = await EnabledAxisAsync();

        await axis.HomeAsync();

        Assert.True(axis.GetStatus().IsHomed);
        Assert.Contains(_log, line => line.StartsWith("[警告]", StringComparison.Ordinal) && line.Contains("16088"));
    }

    [Fact]
    public async Task 復位報警後需要重新使能和回零()
    {
        using var axis = await EnabledAxisAsync();
        await axis.HomeAsync();
        _api.IoStatus = 1;
        Assert.Equal(AxisState.Alarm, axis.GetStatus().State);

        await axis.ResetAlarmAsync();

        var status = axis.GetStatus();
        Assert.Equal(AxisState.Disabled, status.State);
        Assert.False(status.IsHomed);
    }

    [Fact]
    public void 脈衝當量回讀不一致_拋出()
    {
        _api.EquivReadback = 100;
        using var axis = CreateAxis();

        var ex = Assert.Throws<DmcCallException>(axis.ApplyBaseParameters);

        Assert.Equal("dmc_get_equiv", ex.ApiName);
        Assert.Contains("Expected=equiv=1000, Actual=equiv=100", ex.Message);
    }

    [Fact]
    public async Task 安全初始化_急停和軟限位寫入回讀一致後才使能()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1);

        await SafetyStartup.RunAsync([new(x, x, Safety), new(y, y, Safety)]);

        var calls = _api.Calls;
        Assert.True(calls.ToList().LastIndexOf("dmc_get_softlimit_unit") < calls.ToList().IndexOf("nmc_set_axis_enable"));
        Assert.Equal(2, calls.Count(c => c == "nmc_set_axis_enable"));
    }

    [Fact]
    public async Task 安全初始化_急停電平回讀不一致時不使能任何軸()
    {
        _api.EmergencyLogicReadback = _ => 1;
        using var x = CreateAxis();

        var ex = await Assert.ThrowsAsync<SafetyStartupException>(() => SafetyStartup.RunAsync([new(x, x, Safety)]));

        Assert.Contains(ex.Errors, e => e.Contains("期望 低電平有效，實際 高電平有效"));
        Assert.DoesNotContain("nmc_set_axis_enable", _api.Calls);
    }

    [Fact]
    public async Task 啟用急停但未指定電平_拒絕寫入()
    {
        using var axis = CreateAxis();

        await Assert.ThrowsAsync<ArgumentException>(
            () => axis.WriteSafetyConfigAsync(Safety with { EmergencyStopActiveHigh = null }));

        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData(0b0000_0001u, true, false, false, false)]
    [InlineData(0b0000_0010u, false, true, false, false)]
    [InlineData(0b0100_0000u, false, true, false, false)]
    [InlineData(0b1000_0100u, false, false, true, false)]
    [InlineData(0b0000_1000u, false, false, false, true)]
    public void 軸IO狀態字解析(uint raw, bool alarm, bool positive, bool negative, bool emergency)
    {
        var io = DmcAxisIo.Parse(raw);

        Assert.Equal(alarm, io.Alarm);
        Assert.Equal(positive, io.PositiveLimit);
        Assert.Equal(negative, io.NegativeLimit);
        Assert.Equal(emergency, io.EmergencyStop);
    }

    [Fact]
    public void 控制卡連接_按關閉初始化以太網的順序()
    {
        var controller = new DmcController(_api, 0, "192.168.5.11");

        Assert.Equal(4u, controller.Open());
        Assert.Equal(["dmc_board_close", "dmc_board_init", "dmc_board_init_eth", "nmc_get_total_axes"], _api.Calls);
    }

    private async Task WaitForCallAsync(string apiName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_api.Calls.Contains(apiName))
        {
            Assert.True(DateTime.UtcNow < deadline, $"等待 {apiName} 調用超時");
            await Task.Delay(1);
        }
    }
}
