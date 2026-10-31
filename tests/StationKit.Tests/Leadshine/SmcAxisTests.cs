using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Smc;
using StationKit.Safety;

namespace StationKit.Tests.Leadshine;

public class SmcAxisTests
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

    private static readonly SmcAlarmReset OutputBitReset = new()
    {
        Source = SmcAlarmResetSource.OutputBit,
        OutputBit = 7,
        ActiveLevel = 0,
        PulseWidth = TimeSpan.FromMilliseconds(1),
        ClearTimeout = TimeSpan.FromMilliseconds(50),
    };

    private readonly FakeSmcNativeApi _api = new();
    private readonly List<string> _log = [];

    private static SmcAxisOptions Options(ushort axisNo = 0) => new()
    {
        CardNo = 0,
        AxisNo = axisNo,
        Name = $"Y{axisNo}",
        PulseEquivalent = 1000,
        ServoOnLevel = 0,
        ServoSettleTime = TimeSpan.Zero,
        AlarmSignal = new SmcSignalMode(Enabled: true, Logic: 0, Action: 0),
        LimitSignal = new SmcSignalMode(Enabled: true, Logic: 0, Action: 0),
        HomeMode = new SmcHomeMode(Direction: 0, VelocityMode: 1, Mode: 2, PositionSource: 0),
        HomeSpeed = new SmcHomeSpeed(LowVelocity: 5, HighVelocity: 20, AccelerationTime: 0.1, DecelerationTime: 0.1),
        HomePinLogic = new SmcHomePinLogic(Logic: 0, Filter: 0),
        PollInterval = TimeSpan.FromMilliseconds(1),
        MotionTimeout = TimeSpan.FromSeconds(5),
    };

    private SmcAxis CreateAxis(SmcAxisOptions? options = null) => new(_api, options ?? Options(), log: _log.Add);

    private async Task<SmcAxis> EnabledAxisAsync(SmcAxisOptions? options = null)
    {
        var axis = CreateAxis(options);
        await axis.EnableAsync();
        return axis;
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    public async Task 使能_按配置電平寫SEVON並回讀_失能寫相反電平(ushort servoOnLevel)
    {
        using var axis = await EnabledAxisAsync(Options() with { ServoOnLevel = servoOnLevel });

        Assert.Equal(["smc_write_sevon_pin", "smc_read_sevon_pin"], _api.Calls);
        Assert.Equal(servoOnLevel, _api.ServoOn);
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);

        await axis.DisableAsync();

        Assert.Equal(1 - servoOnLevel, _api.ServoOn);
        Assert.Equal(AxisState.Disabled, axis.GetStatus().State);
    }

    [Fact]
    public async Task SEVON回讀不一致_使能失敗且軸保持未使能()
    {
        _api.ServoOnReadback = level => (ushort)(1 - level);
        using var axis = CreateAxis();

        var ex = await Assert.ThrowsAsync<LeadshineCallException>(() => axis.EnableAsync());

        Assert.Equal("smc_read_sevon_pin", ex.ApiName);
        Assert.Contains("Expected=sevon=0, Actual=sevon=1", ex.Message);
        Assert.Equal(AxisState.Disabled, axis.GetStatus().State);
    }

    [Fact]
    public void SEVON電平超出0和1_構造時拒絕()
    {
        Assert.Throws<ArgumentException>(() => CreateAxis(Options() with { ServoOnLevel = 2 }));
    }

    [Fact]
    public async Task 絕對運動_調用smc系列函數_到位才完成()
    {
        using var axis = await EnabledAxisAsync();

        await axis.MoveAbsoluteAsync(80, Profile);

        Assert.Equal(["smc_set_profile_unit", "smc_pmove_unit"], _api.Calls.Skip(2));
        Assert.Equal((0, 100, 0.1, 0.2, 0), _api.LastProfile);
        Assert.Equal(80, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 下發前已取消_不調用smc_pmove_unit()
    {
        using var axis = await EnabledAxisAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => axis.MoveAbsoluteAsync(80, Profile, new CancellationToken(canceled: true)));

        Assert.DoesNotContain("smc_pmove_unit", _api.Calls);
    }

    [Fact]
    public async Task 運動中伺服報警_立即停止並報ALM()
    {
        _api.PollsPerMove = int.MaxValue;
        using var axis = await EnabledAxisAsync();

        var move = axis.MoveAbsoluteAsync(80, Profile);
        await WaitForCallAsync("smc_pmove_unit");
        _api.IoStatus = 1;

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("伺服報警 ALM", ex.Message);
        Assert.Equal(LeadshineStopMode.Immediate, _api.LastStopMode);
    }

    [Fact]
    public void 基礎參數_ALM和EL電平寫入後回讀核對()
    {
        using var axis = CreateAxis();

        axis.ApplyBaseParameters();

        Assert.Equal(
        [
            "smc_set_equiv", "smc_get_equiv", "smc_set_s_profile",
            "smc_set_alm_mode", "smc_get_alm_mode", "smc_set_el_mode", "smc_get_el_mode",
        ], _api.Calls);
    }

    [Theory]
    [InlineData("smc_get_alm_mode")]
    [InlineData("smc_get_el_mode")]
    public void 信號電平回讀不一致_拋出並記錄報警(string getter)
    {
        _api.Readback[getter] = m => (SmcSignalMode)m with { Logic = 1 };
        using var axis = CreateAxis();

        var ex = Assert.Throws<LeadshineCallException>(axis.ApplyBaseParameters);

        Assert.Equal(getter, ex.ApiName);
        Assert.Contains("logic=0", ex.Message);
        Assert.Contains("Actual=enable=True, logic=1", ex.Message);
        Assert.Contains(_log, line => line.StartsWith("[報警]", StringComparison.Ordinal) && line.Contains("回讀不一致"));
    }

    [Fact]
    public async Task 回零_四組參數全部回讀一致後才下發_回零結果為1才算完成()
    {
        using var axis = await EnabledAxisAsync();

        await axis.HomeAsync();

        Assert.Equal(
        [
            "smc_set_homemode", "smc_get_homemode",
            "smc_set_home_profile_unit", "smc_get_home_profile_unit",
            "smc_set_home_pin_logic", "smc_get_home_pin_logic",
            "smc_set_home_position_unit", "smc_get_home_position_unit",
            "smc_home_move",
        ], _api.Calls.Skip(2));
        Assert.True(axis.GetStatus().IsHomed);
    }

    [Theory]
    [InlineData("smc_get_homemode")]
    [InlineData("smc_get_home_profile_unit")]
    [InlineData("smc_get_home_pin_logic")]
    [InlineData("smc_get_home_position_unit")]
    public async Task 回零參數任一回讀不一致_不下發回零(string getter)
    {
        _api.Readback[getter] = value => value switch
        {
            SmcHomeMode m => m with { Mode = 9 },
            SmcHomeSpeed s => s with { HighVelocity = 200 },
            SmcHomePinLogic l => l with { Logic = 1 },
            SmcHomePosition p => p with { Enabled = true },
            _ => value,
        };
        using var axis = await EnabledAxisAsync();

        var ex = await Assert.ThrowsAsync<LeadshineCallException>(() => axis.HomeAsync());

        Assert.Equal(getter, ex.ApiName);
        Assert.DoesNotContain("smc_home_move", _api.Calls);
        Assert.False(axis.GetStatus().IsHomed);
    }

    [Fact]
    public async Task 未配置報警復位輸出_報故障而不是假裝復位成功()
    {
        using var axis = await EnabledAxisAsync();
        _api.IoStatus = 1;

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => axis.ResetAlarmAsync());

        Assert.Contains("未配置報警復位輸出", ex.Message);
        Assert.Empty(_api.ResetWrites);
        Assert.Equal(AxisState.Alarm, axis.GetStatus().State);
    }

    [Fact]
    public async Task 報警復位_先斷使能再輸出脈衝_ALM消失後需重新使能和回零()
    {
        using var axis = await EnabledAxisAsync(Options() with { AlarmReset = OutputBitReset });
        await axis.HomeAsync();
        _api.IoStatus = 1;

        await axis.ResetAlarmAsync();

        Assert.Equal(1, _api.ServoOn);
        Assert.Equal([("smc_write_outbit", (ushort)7, (ushort)0), ("smc_write_outbit", (ushort)7, (ushort)1)],
            _api.ResetWrites);
        var calls = _api.Calls.ToList();
        Assert.True(calls.LastIndexOf("smc_write_sevon_pin") < calls.IndexOf("smc_write_outbit"));
        var status = axis.GetStatus();
        Assert.Equal(AxisState.Disabled, status.State);
        Assert.False(status.IsHomed);
    }

    [Fact]
    public async Task 報警復位後ALM仍在_報故障且復位信號已撤銷()
    {
        _api.ResetClearsAlarm = false;
        var reset = OutputBitReset with { Source = SmcAlarmResetSource.ErcPin, ActiveLevel = 1 };
        using var axis = await EnabledAxisAsync(Options() with { AlarmReset = reset });
        _api.IoStatus = 1;

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => axis.ResetAlarmAsync());

        Assert.Contains("ALM 仍未消失", ex.Message);
        Assert.Equal(("smc_write_erc_pin", (ushort)0, (ushort)0), _api.ResetWrites[^1]);
    }

    [Fact]
    public async Task 報警復位中途取消_復位信號仍然撤銷()
    {
        var reset = OutputBitReset with { PulseWidth = TimeSpan.FromSeconds(30) };
        using var axis = await EnabledAxisAsync(Options() with { AlarmReset = reset });
        _api.IoStatus = 1;
        using var cts = new CancellationTokenSource();

        var task = axis.ResetAlarmAsync(cts.Token);
        await WaitForCallAsync("smc_write_outbit");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(("smc_write_outbit", (ushort)7, (ushort)1), _api.ResetWrites[^1]);
    }

    [Fact]
    public async Task 安全初始化_急停和軟限位回讀一致後才寫SEVON()
    {
        using var x = CreateAxis(Options(0));
        using var y = CreateAxis(Options(1));

        await SafetyStartup.RunAsync([new(x, x, Safety), new(y, y, Safety)]);

        var calls = _api.Calls.ToList();
        Assert.True(calls.LastIndexOf("smc_get_softlimit_unit") < calls.IndexOf("smc_write_sevon_pin"));
        Assert.Equal(2, calls.Count(c => c == "smc_write_sevon_pin"));
    }

    [Fact]
    public void 控制器連接_先關閉再按以太網方式初始化()
    {
        var controller = new SmcController(_api, 0, "192.168.5.11");

        controller.Open();

        Assert.Equal(["smc_board_close", "smc_board_init"], _api.Calls);
        Assert.Equal((2, "192.168.5.11", 0u), _api.LastInit);
    }

    [Fact]
    public void 控制器創建軸_連接號不一致時拒絕()
    {
        var controller = new SmcController(_api, 1, "192.168.5.11");

        Assert.Throws<ArgumentException>(() => controller.CreateAxis(Options()));
    }

    [Fact]
    public async Task 點動_調用smc_vmove_方向正確()
    {
        using var axis = await EnabledAxisAsync();
        using var hold = new CancellationTokenSource();

        var jog = axis.JogAsync(JogDirection.Negative, Profile, hold.Token);
        await WaitForCallAsync("smc_vmove");
        await hold.CancelAsync();
        await jog;

        Assert.Equal(["smc_set_profile_unit", "smc_vmove", "smc_stop"], _api.Calls.Skip(2));
        Assert.Equal(JogDirection.Negative, _api.LastVmoveDirection);
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
