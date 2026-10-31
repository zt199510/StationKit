using StationKit.Abstractions.Motion;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;

namespace StationKit.Tests.Leadshine;

public sealed class LeadshineStatusPollerTests : IDisposable
{
    private readonly FakeDmcNativeApi _api = new();
    private readonly List<string> _log = [];
    private readonly LeadshineStatusPoller _poller;

    public LeadshineStatusPollerTests() => _poller = new LeadshineStatusPoller(_api, 0, "DMC", log: _log.Add);

    public void Dispose() => _poller.Dispose();

    [Fact]
    public void 一輪輪詢_每根軸每個函數各讀一次_有編碼器的軸多讀編碼器()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1);
        using var z = CreateAxis(2, LeadshineFeedback.Encoder);

        _poller.RefreshOnce();

        Assert.Equal(3, _api.ReadCount("dmc_get_position_unit"));
        Assert.Equal(3, _api.ReadCount("dmc_axis_io_status"));
        Assert.Equal(3, _api.ReadCount("dmc_check_done"));
        Assert.Equal(["dmc_get_encoder_unit"], _api.Calls);
        Assert.Equal(1, _poller.CurrentCycle);
    }

    [Fact]
    public void GetStatus只讀快照_不產生任何DLL調用()
    {
        using var axis = CreateAxis(0, LeadshineFeedback.Encoder);
        _poller.RefreshOnce();
        var (reads, calls) = (_api.TotalReads, _api.Calls.Count);

        for (var i = 0; i < 100; i++)
        {
            axis.GetStatus();
        }

        Assert.Equal(reads, _api.TotalReads);
        Assert.Equal(calls, _api.Calls.Count);
    }

    [Fact]
    public void 快照記錄開始讀取時的週期號和讀到的值()
    {
        using var axis = CreateAxis(0, LeadshineFeedback.Encoder);
        _api.Position = 12.5;
        _api.EncoderOffset = -0.01;
        _api.IoStatus = 1u << 8;

        _poller.RefreshOnce();
        _poller.RefreshOnce();

        var snapshot = _poller.GetSnapshot(0)!;
        Assert.Equal(2, snapshot.Cycle);
        Assert.Equal(12.5, snapshot.Command);
        Assert.Equal(12.49, snapshot.Feedback, 9);
        Assert.Equal(1u << 8, snapshot.Io);
    }

    [Fact]
    public void 還沒有快照時_狀態為報警而不是靜止()
    {
        using var axis = CreateAxis(0);

        var status = axis.GetStatus();

        Assert.Equal(AxisState.Alarm, status.State);
        Assert.Contains("尚無狀態數據", status.AlarmReason);
    }

    [Fact]
    public void 一根軸讀取異常_其他軸照常輪詢_只記錄一次報警_恢復時記錄()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1);
        _api.ThrowOnRead = axis => axis == 1;

        _poller.RefreshOnce();
        _poller.RefreshOnce();
        _poller.RefreshOnce();

        Assert.Equal(3, _poller.GetSnapshot(0)!.Cycle);
        Assert.Null(_poller.GetSnapshot(1));
        Assert.Single(_log, line => line.StartsWith("[報警] DMC 輪詢：CardId=0, AxisIndex=1", StringComparison.Ordinal));

        _api.ThrowOnRead = null;
        _poller.RefreshOnce();

        Assert.Equal(4, _poller.GetSnapshot(1)!.Cycle);
        Assert.Contains(_log, line => line.Contains("AxisIndex=1, Stage=Poll, 狀態讀取已恢復"));
    }

    [Fact]
    public void 同一根軸創建兩個對象_拒絕_釋放後可以重新創建()
    {
        var first = CreateAxis(0);

        Assert.Throws<InvalidOperationException>(() => CreateAxis(0));

        first.Dispose();
        using var second = CreateAxis(0);
    }

    [Fact]
    public void 軸的卡號和輪詢器不一致_拒絕()
    {
        var ex = Assert.Throws<ArgumentException>(() => new DmcAxis(_api, Options(0) with { CardNo = 1 }, _poller));

        Assert.Contains("與輪詢器的卡號 0 不一致", ex.Message);
    }

    [Fact]
    public async Task 後台輪詢啟動後持續刷新_停止後不再刷新()
    {
        using var axis = CreateAxis(0);

        _poller.Start(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => _poller.CurrentCycle >= 3);
        _poller.Stop();
        var stoppedAt = _poller.CurrentCycle;
        await Task.Delay(20);

        Assert.False(_poller.IsRunning);
        Assert.Equal(stoppedAt, _poller.CurrentCycle);
    }

    [Fact]
    public void 控制器連接成功後啟動輪詢_斷開前停止()
    {
        var controller = new DmcController(_api, 0, "192.168.5.11", pollPeriod: TimeSpan.FromMilliseconds(1));

        controller.Open();
        Assert.True(controller.Poller.IsRunning);

        controller.Close();
        Assert.False(controller.Poller.IsRunning);
    }

    private DmcAxis CreateAxis(ushort axisNo, LeadshineFeedback feedback = LeadshineFeedback.None) =>
        new(_api, Options(axisNo) with { Feedback = feedback }, _poller, log: _log.Add);

    private static DmcAxisOptions Options(ushort axisNo) => new()
    {
        CardNo = 0,
        AxisNo = axisNo,
        Name = $"X{axisNo}",
        PulseEquivalent = 1000,
        InPositionSignal = LeadshineInpMode.Disabled,
        Feedback = LeadshineFeedback.None,
        HomeProfile = new DmcHomeProfile(17, 5, 20, 0.1, 0.1, 0),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待條件超時");
            await Task.Delay(1);
        }
    }
}
