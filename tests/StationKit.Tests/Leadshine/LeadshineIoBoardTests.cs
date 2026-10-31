using StationKit.Abstractions.Io;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;
using StationKit.Motion.Leadshine.Smc;

namespace StationKit.Tests.Leadshine;

/// <summary>
/// 雷賽通用 IO：讀快照、經卡鎖寫輸出並回讀核對、卡故障時禁止操作、SMC 報警復位位登記。
/// 輪詢器在後台以 1 ms 週期運行；過期時間放寬到 1 s，避免 CI 負載下誤報。
/// </summary>
public sealed class LeadshineIoBoardTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeDmcNativeApi _api = new();
    private readonly List<string> _log = [];
    private readonly DmcController _controller;

    public LeadshineIoBoardTests() =>
        _controller = new DmcController(_api, 0, "192.168.5.11", AddLog, TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(1));

    public void Dispose() => _controller.Poller.Dispose();

    [Fact]
    public void 輪詢未啟動_讀取報尚無數據()
    {
        var io = _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);
        var door = io.CreateInput(new("安全門關閉", 0, ActiveHigh: false));

        var ex = Assert.Throws<IoFaultException>(() => door.Read());
        Assert.Contains("尚無 IO 數據", ex.Message);
    }

    [Fact]
    public async Task 讀輸入_按有效電平換算()
    {
        var io = _controller.CreateIoBoard(inputPorts: 2, outputPorts: 1);
        var door = io.CreateInput(new("安全門關閉", 0, ActiveHigh: false));
        var vacuum = io.CreateInput(new("真空", 40, ActiveHigh: true));
        _api.InputPorts[1] = 1u << 8;
        _controller.Poller.Start(TimeSpan.FromMilliseconds(1));

        await door.WaitAsync(true, Wait);
        await vacuum.WaitAsync(true, Wait);
    }

    [Fact]
    public async Task 寫輸出_經卡鎖下發_回讀到新值才完成()
    {
        var io = _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);
        var valve = io.CreateOutput(new("夾爪閥", 5, ActiveHigh: false));
        _api.OutputPorts[0] = 1u << 5;
        _controller.Poller.Start(TimeSpan.FromMilliseconds(1));
        await valve.WaitAsync(false, Wait);

        await valve.SetAsync(true).WaitAsync(Wait);

        Assert.Contains("dmc_write_outbit", _api.Calls);
        Assert.Equal(0u, _api.OutputPorts[0] & (1u << 5));
        Assert.True(valve.Read());
        Assert.Equal(1, _api.MaxConcurrentCalls);
    }

    [Fact]
    public async Task 輸出口卡死_回讀不一致報故障()
    {
        var io = _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);
        var valve = io.CreateOutput(new("夾爪閥", 5, ActiveHigh: true));
        _api.StuckOutputBits.Add(5);
        _controller.Poller.Start(TimeSpan.FromMilliseconds(1));
        await valve.WaitAsync(false, Wait);

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => valve.SetAsync(true).WaitAsync(Wait));
        Assert.Contains("回讀仍為無效", ex.Message);
    }

    [Fact]
    public async Task 寫輸出返回錯誤碼_拋故障並記報警日誌()
    {
        var io = _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);
        var valve = io.CreateOutput(new("夾爪閥", 5, ActiveHigh: true));
        _api.ReturnCodes["dmc_write_outbit"] = 3;
        _controller.Poller.Start(TimeSpan.FromMilliseconds(1));
        await valve.WaitAsync(false, Wait);

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => valve.SetAsync(true));
        Assert.Contains("write_outbit 返回錯誤碼 3", ex.Message);
        Assert.Contains(Logs(), line => line.StartsWith("[報警] DMC IO：CardId=0, Bit=5, Stage=WriteOutput",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task 卡故障_讀取拋異常_寫入不調用DLL()
    {
        var io = _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);
        var door = io.CreateInput(new("安全門關閉", 0, ActiveHigh: true));
        var valve = io.CreateOutput(new("夾爪閥", 5, ActiveHigh: true));
        _controller.Poller.Start(TimeSpan.FromMilliseconds(1));
        await door.WaitAsync(false, Wait);

        _api.ThrowOnIoRead = true;
        await Assert.ThrowsAsync<IoFaultException>(() => door.WaitAsync(true, Wait));

        Assert.Contains("本卡 IO 禁止操作", Assert.Throws<IoFaultException>(() => door.Read()).Message);
        var ex = await Assert.ThrowsAsync<IoFaultException>(() => valve.SetAsync(true));
        Assert.Contains("拒絕寫入", ex.Message);
        Assert.DoesNotContain("dmc_write_outbit", _api.Calls);
    }

    [Fact]
    public void 位號超出輪詢的端口_拒絕創建()
    {
        var io = _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => io.CreateInput(new("真空", 32, ActiveHigh: true)));
        Assert.Contains("只輪詢了 1 個輸入端口", ex.Message);
    }

    [Fact]
    public void 每卡只能創建一次IO()
    {
        _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);

        Assert.Throws<InvalidOperationException>(() => _controller.CreateIoBoard(inputPorts: 1, outputPorts: 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SMC報警復位用的輸出位_不能再配置成輸出(bool axisFirst)
    {
        var smc = new SmcController(new FakeSmcNativeApi(), 0, "192.168.5.11");
        var options = SmcOptions() with
        {
            AlarmReset = new SmcAlarmReset
            {
                Source = SmcAlarmResetSource.OutputBit,
                OutputBit = 7,
                ActiveLevel = 0,
                PulseWidth = TimeSpan.FromMilliseconds(1),
                ClearTimeout = TimeSpan.FromMilliseconds(50),
            },
        };
        LeadshineIoBoard io;
        if (axisFirst)
        {
            using var axis = smc.CreateAxis(options);
            io = smc.CreateIoBoard(inputPorts: 1, outputPorts: 1);
        }
        else
        {
            io = smc.CreateIoBoard(inputPorts: 1, outputPorts: 1);
            using var axis = smc.CreateAxis(options);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => io.CreateOutput(new("夾爪閥", 7, ActiveHigh: true)));
        Assert.Contains("Y0 報警復位", ex.Message);
        smc.Poller.Dispose();
    }

    private static SmcAxisOptions SmcOptions() => new()
    {
        CardNo = 0,
        AxisNo = 0,
        Name = "Y0",
        PulseEquivalent = 1000,
        InPositionSignal = LeadshineInpMode.Disabled,
        Feedback = LeadshineFeedback.None,
        ServoOnLevel = 0,
        ServoSettleTime = TimeSpan.Zero,
        AlarmSignal = new SmcSignalMode(Enabled: true, Logic: 0, Action: 0),
        LimitSignal = new SmcSignalMode(Enabled: true, Logic: 0, Action: 0),
        HomeMode = new SmcHomeMode(Direction: 0, VelocityMode: 1, Mode: 2, PositionSource: 0),
        HomeSpeed = new SmcHomeSpeed(LowVelocity: 5, HighVelocity: 20, AccelerationTime: 0.1, DecelerationTime: 0.1),
        HomePinLogic = new SmcHomePinLogic(Logic: 0, Filter: 0),
        MotionTimeout = TimeSpan.FromSeconds(5),
    };

    private void AddLog(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    private List<string> Logs()
    {
        lock (_log)
        {
            return [.. _log];
        }
    }
}
