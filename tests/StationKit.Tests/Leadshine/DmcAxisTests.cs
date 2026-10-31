using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;
using StationKit.Safety;

namespace StationKit.Tests.Leadshine;

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

    private DmcAxis CreateAxis(ushort axisNo = 0, TimeSpan? motionTimeout = null) =>
        new(_api, CreateOptions(axisNo, motionTimeout), log: _log.Add);

    private static DmcAxisOptions CreateOptions(ushort axisNo = 0, TimeSpan? motionTimeout = null) => new()
    {
        CardNo = 0,
        AxisNo = axisNo,
        Name = $"X{axisNo}",
        PulseEquivalent = 1000,
        InPositionSignal = LeadshineInpMode.Disabled,
        Feedback = LeadshineFeedback.None,
        HomeProfile = new DmcHomeProfile(Mode: 17, LowVelocity: 5, HighVelocity: 20, AccelerationTime: 0.1,
            DecelerationTime: 0.1, Offset: 0),
        PollInterval = TimeSpan.FromMilliseconds(1),
        MotionTimeout = motionTimeout ?? TimeSpan.FromSeconds(5),
    };

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
        Assert.Equal(LeadshineStopMode.Decelerated, _api.LastStopMode);
    }

    [Fact]
    public async Task 返回碼非零_異常和日誌帶上卡號軸號函數名和返回碼()
    {
        _api.ReturnCodes["dmc_pmove_unit"] = 9;
        using var axis = await EnabledAxisAsync();

        var ex = await Assert.ThrowsAsync<LeadshineCallException>(() => axis.MoveAbsoluteAsync(120, Profile));

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
        Assert.Equal(LeadshineStopMode.Immediate, _api.LastStopMode);
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

        var ex = await Assert.ThrowsAsync<LeadshineCallException>(() => axis.HomeAsync());

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

        var ex = Assert.Throws<LeadshineCallException>(axis.ApplyBaseParameters);

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
        var io = LeadshineAxisIo.Parse(raw);

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

    [Fact]
    public async Task 點動_vmove下發_鬆開後減速停止並等停穩才完成()
    {
        _api.PollsAfterStop = 5;
        using var axis = await EnabledAxisAsync();
        using var hold = new CancellationTokenSource();

        var jog = axis.JogAsync(JogDirection.Positive, Profile, hold.Token);
        await WaitForCallAsync("dmc_vmove");
        await hold.CancelAsync();
        await jog;

        Assert.Equal(["nmc_set_axis_enable", "dmc_set_profile_unit", "dmc_vmove", "dmc_stop"], _api.Calls);
        Assert.Equal(JogDirection.Positive, _api.LastVmoveDirection);
        Assert.Equal(LeadshineStopMode.Decelerated, _api.LastStopMode);
        // 減速段走完才返回：返回時 check_done 已經是 1
        Assert.Equal(1, _api.CheckDone(0, 0));
        Assert.Equal(AxisState.Idle, axis.GetStatus().State);
    }

    [Fact]
    public async Task 點動下發前已鬆開_不調用dmc_vmove()
    {
        using var axis = await EnabledAxisAsync();

        await axis.JogAsync(JogDirection.Positive, Profile, new CancellationToken(canceled: true));

        Assert.DoesNotContain("dmc_vmove", _api.Calls);
    }

    [Fact]
    public async Task 停在正限位上_拒絕正向點動_允許負向點動移出()
    {
        _api.IoStatus = 0b10; // EL+
        using var axis = await EnabledAxisAsync();

        var ex = await Assert.ThrowsAsync<MotionFaultException>(
            () => axis.JogAsync(JogDirection.Positive, Profile, CancellationToken.None));
        Assert.Contains("已在正限位上", ex.Message);
        Assert.DoesNotContain("dmc_vmove", _api.Calls);

        using var hold = new CancellationTokenSource();
        var jog = axis.JogAsync(JogDirection.Negative, Profile, hold.Token);
        await WaitForCallAsync("dmc_vmove");
        await Task.Delay(20);
        Assert.False(jog.IsCompleted);   // 正限位仍有效，但負向點動不應被它打斷
        await hold.CancelAsync();
        await jog;
        Assert.Equal(JogDirection.Negative, _api.LastVmoveDirection);
    }

    [Fact]
    public async Task 點動中觸發同向限位_報故障()
    {
        using var axis = await EnabledAxisAsync();
        using var hold = new CancellationTokenSource();

        var jog = axis.JogAsync(JogDirection.Negative, Profile, hold.Token);
        await WaitForCallAsync("dmc_vmove");
        _api.IoStatus = 0b1000_0000; // SL-（卡上的軟限位）

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => jog);
        Assert.Contains("Jog 過程中觸發負限位", ex.Message);
    }

    [Fact]
    public async Task 點動中未鬆開卻被外部停止_報故障()
    {
        using var axis = await EnabledAxisAsync();
        using var hold = new CancellationTokenSource();

        var jog = axis.JogAsync(JogDirection.Positive, Profile, hold.Token);
        await WaitForCallAsync("dmc_vmove");
        _api.StopAxis(0, 0, LeadshineStopMode.Decelerated);

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => jog);
        Assert.Contains("未鬆開按鈕", ex.Message);
    }

    [Fact]
    public async Task 停在負限位上往正方向運動_不誤報負限位()
    {
        _api.IoStatus = 0b100; // EL-
        using var axis = await EnabledAxisAsync();

        await axis.MoveAbsoluteAsync(10, Profile);

        Assert.Equal(10, axis.GetStatus().Position);
    }

    [Fact]
    public async Task 總線軸回零_不改寫編碼器_反饋由驅動器維護()
    {
        _api.EncoderOffset = 0.003;
        using var axis = new DmcAxis(_api, CreateOptions() with { Feedback = LeadshineFeedback.Encoder }, log: _log.Add);
        await axis.EnableAsync();

        await axis.HomeAsync();

        Assert.DoesNotContain("dmc_set_encoder_unit", _api.Calls);
        Assert.Equal(0.003, axis.GetStatus().FeedbackPosition, 9);
    }

    private DmcAxis AbsoluteAxis(DmcAxisOptions? options = null) => new(_api, options ?? CreateOptions() with
    {
        Feedback = LeadshineFeedback.Encoder,
        HomeMethod = LeadshineHomeMethod.AbsoluteEncoder,
        HomeProfile = null,
        AbsoluteHomeProfile = MotionProfile.Symmetric(50, 500),
    }, log: _log.Add);

    /// <summary>上電狀態：指令位置 0，編碼器絕對位置為 <paramref name="absolute"/>。</summary>
    private void PowerOnAt(double absolute)
    {
        _api.Position = 0;
        _api.EncoderOffset = absolute;
    }

    [Fact]
    public async Task 絕對值回零_讀編碼器同步指令位置_再絕對運動到零點_不觸碰感應器()
    {
        PowerOnAt(123.4);
        using var axis = AbsoluteAxis();
        await axis.WriteSafetyConfigAsync(Safety);
        await axis.EnableAsync();

        await axis.HomeAsync();

        // 狀態讀取也會讀編碼器，這裡去掉它們只看動作順序：讀絕對位置 → 同步指令位置 → 走零點
        var calls = _api.Calls.SkipWhile(c => c != "nmc_set_axis_enable").Skip(1).ToList();
        Assert.Equal("dmc_get_encoder_unit", calls[0]);
        Assert.Equal(["dmc_set_position_unit", "dmc_set_profile_unit", "dmc_pmove_unit"],
            calls.Where(c => c != "dmc_get_encoder_unit"));
        Assert.DoesNotContain("dmc_home_move", _api.Calls);
        var status = axis.GetStatus();
        Assert.True(status.IsHomed);
        Assert.Equal(0, status.Position);
        Assert.Equal(0, status.FeedbackPosition, 9);
        Assert.Contains(_log, line => line.Contains("ApiName=dmc_get_encoder_unit") && line.Contains("encoder=123.4"));
    }

    [Fact]
    public async Task 絕對位置超出軟限位_判定不可信_不同步也不運動()
    {
        PowerOnAt(5000);   // 例如編碼器電池沒電，多圈數據丟失
        using var axis = AbsoluteAxis();
        await axis.WriteSafetyConfigAsync(Safety);
        await axis.EnableAsync();

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => axis.HomeAsync());

        Assert.Contains("絕對位置 5000 不在軟限位 [-1, 300] 內", ex.Message);
        Assert.DoesNotContain("dmc_set_position_unit", _api.Calls);
        Assert.DoesNotContain("dmc_pmove_unit", _api.Calls);
        Assert.False(axis.GetStatus().IsHomed);
    }

    [Fact]
    public async Task 未寫入安全配置_拒絕絕對值回零()
    {
        PowerOnAt(10);
        using var axis = AbsoluteAxis();
        await axis.EnableAsync();

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => axis.HomeAsync());

        Assert.Contains("未寫入安全配置", ex.Message);
        Assert.DoesNotContain("dmc_pmove_unit", _api.Calls);
    }

    [Fact]
    public async Task 指令位置同步回讀不一致_不運動()
    {
        PowerOnAt(10);
        _api.PositionWriteIgnored = true;
        using var axis = AbsoluteAxis();
        await axis.WriteSafetyConfigAsync(Safety);
        await axis.EnableAsync();

        var ex = await Assert.ThrowsAsync<LeadshineCallException>(() => axis.HomeAsync());

        Assert.Equal("dmc_get_position_unit", ex.ApiName);
        Assert.DoesNotContain("dmc_pmove_unit", _api.Calls);
        Assert.False(axis.GetStatus().IsHomed);
    }

    [Fact]
    public async Task 走零點途中報警_不算回零完成()
    {
        PowerOnAt(100);
        _api.PollsPerMove = int.MaxValue;
        using var axis = AbsoluteAxis();
        await axis.WriteSafetyConfigAsync(Safety);
        await axis.EnableAsync();

        var home = axis.HomeAsync();
        await WaitForCallAsync("dmc_pmove_unit");
        _api.IoStatus = 1;

        await Assert.ThrowsAsync<MotionFaultException>(() => home);
        Assert.False(axis.GetStatus().IsHomed);
    }

    [Fact]
    public void 絕對值回零但沒有編碼器反饋_構造時拒絕()
    {
        var options = CreateOptions() with
        {
            HomeMethod = LeadshineHomeMethod.AbsoluteEncoder,
            AbsoluteHomeProfile = MotionProfile.Symmetric(50, 500),
        };

        var ex = Assert.Throws<ArgumentException>(() => AbsoluteAxis(options));

        Assert.Contains("需要編碼器反饋", ex.Message);
    }

    [Fact]
    public void 絕對值回零未配置走零點速度_構造時拒絕()
    {
        var options = CreateOptions() with
        {
            Feedback = LeadshineFeedback.Encoder,
            HomeMethod = LeadshineHomeMethod.AbsoluteEncoder,
        };

        Assert.Throws<ArgumentException>(() => AbsoluteAxis(options));
    }

    [Fact]
    public void 感應器回零未配置回零參數_構造時拒絕()
    {
        Assert.Throws<ArgumentException>(() => AbsoluteAxis(CreateOptions() with { HomeProfile = null }));
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
