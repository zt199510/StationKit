using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;

namespace StationKit.Tests.Leadshine;

/// <summary>
/// 通用 IO 併入集中輪詢：每輪按端口讀取，讀取失敗與軸一樣整卡故障。
/// 用 RefreshOnce 手動推進，結果確定。
/// </summary>
public sealed class LeadshineIoPollingTests : IDisposable
{
    private readonly FakeDmcNativeApi _api = new();
    private readonly List<string> _log = [];
    private readonly LeadshineStatusPoller _poller;

    public LeadshineIoPollingTests() =>
        _poller = new LeadshineStatusPoller(_api, 0, "DMC", log: _log.Add, staleTimeout: TimeSpan.FromSeconds(5));

    public void Dispose() => _poller.Dispose();

    [Fact]
    public void 未配置IO_不讀端口()
    {
        _poller.RefreshOnce();

        Assert.Null(_poller.GetIoSnapshot());
        Assert.Equal(0, _api.ReadCount("dmc_read_inport"));
    }

    [Fact]
    public void 每輪每個端口讀一次_軸的讀取次數不變()
    {
        using var x = CreateAxis(0);
        _poller.ConfigureIo(inputPorts: 2, outputPorts: 1);

        _poller.RefreshOnce();
        _poller.RefreshOnce();

        Assert.Equal(4, _api.ReadCount("dmc_read_inport"));
        Assert.Equal(2, _api.ReadCount("dmc_read_outport"));
        Assert.Equal(2, _api.ReadCount("dmc_axis_io_status"));
        Assert.Equal(2, _api.ReadCount("dmc_check_done"));
    }

    [Fact]
    public void 快照按位號取電平_帶本輪週期號()
    {
        _poller.ConfigureIo(inputPorts: 2, outputPorts: 1);
        _api.InputPorts[0] = 1u << 3;
        _api.InputPorts[1] = 1u << 1;
        _api.OutputPorts[0] = 1u << 31;

        _poller.RefreshOnce();

        var snapshot = _poller.GetIoSnapshot()!;
        Assert.Equal(_poller.CurrentCycle, snapshot.Cycle);
        Assert.True(snapshot.Level(IoKind.Input, 3));
        Assert.False(snapshot.Level(IoKind.Input, 4));
        Assert.True(snapshot.Level(IoKind.Input, 33));
        Assert.True(snapshot.Level(IoKind.Output, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Level(IoKind.Output, 32));
    }

    [Fact]
    public void 讀IO拋異常_整卡故障_本卡軸報警()
    {
        using var x = CreateAxis(0);
        _poller.ConfigureIo(inputPorts: 1, outputPorts: 1);
        _poller.RefreshOnce();
        _api.ThrowOnIoRead = true;

        _poller.RefreshOnce();

        Assert.Contains("卡0 IO 狀態讀取失敗", _poller.Fault, StringComparison.Ordinal);
        Assert.Equal(AxisState.Alarm, x.GetStatus().State);
        Assert.Single(_log, line =>
            line.StartsWith("[報警] DMC 輪詢：CardId=0, Target=IO, Stage=Poll, 狀態讀取失敗", StringComparison.Ordinal));
    }

    [Fact]
    public void 通訊探針返回錯誤碼_端口值不可信_整卡故障且不發佈快照()
    {
        _poller.ConfigureIo(inputPorts: 1, outputPorts: 0);
        _poller.RefreshOnce();
        var before = _poller.GetIoSnapshot();
        _api.InputPorts[0] = 0xFF;
        _api.ReturnCodes["dmc_get_position_unit"] = 9;

        _poller.RefreshOnce();

        Assert.Contains("通訊探針 get_position_unit=9", _poller.Fault, StringComparison.Ordinal);
        // 失敗那一輪讀到的端口值沒有發佈出去
        Assert.Same(before, _poller.GetIoSnapshot());
    }

    private DmcAxis CreateAxis(ushort axisNo) => new(_api,
        new DmcAxisOptions
        {
            CardNo = 0,
            AxisNo = axisNo,
            Name = $"X{axisNo}",
            PulseEquivalent = 1000,
            InPositionSignal = LeadshineInpMode.Disabled,
            Feedback = LeadshineFeedback.None,
            HomeProfile = new DmcHomeProfile(17, 5, 20, 0.1, 0.1, 0),
        }, _poller);
}
