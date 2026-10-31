using StationKit.Abstractions.Motion;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;

namespace StationKit.Tests.Leadshine;

public sealed class LeadshineStatusPollerTests : IDisposable
{
    private readonly FakeDmcNativeApi _api = new();
    private readonly List<string> _log = [];
    private readonly LeadshineStatusPoller _poller;

    // 手動推動輪詢的測試之間有 Task.Delay，過期時間放寬，避免 CI 負載下誤報
    public LeadshineStatusPollerTests() =>
        _poller = new LeadshineStatusPoller(_api, 0, "DMC", log: AddLog, staleTimeout: TimeSpan.FromSeconds(1));

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
        Assert.Single(Logs(), line => line.StartsWith("[報警] DMC 輪詢：CardId=0, AxisIndex=1", StringComparison.Ordinal));

        _api.ThrowOnRead = null;
        _poller.RefreshOnce();

        Assert.Equal(4, _poller.GetSnapshot(1)!.Cycle);
        Assert.Contains(Logs(), line => line.Contains("AxisIndex=1, Stage=Poll, 狀態讀取已恢復"));
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
    public async Task 後台輪詢在專用線程上運行_不佔線程池()
    {
        using var axis = CreateAxis(0);
        string? threadName = null;
        var onThreadPool = true;
        _api.ThrowOnRead = _ =>
        {
            threadName = Thread.CurrentThread.Name;
            onThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            return false;
        };

        _poller.Start(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => threadName != null);
        _poller.Stop();

        Assert.Equal("DMC 輪詢 卡0", threadName);
        Assert.False(onThreadPool);
    }

    [Fact]
    public async Task 後台輪詢定期輸出週期統計()
    {
        using var axis = CreateAxis(0);
        _poller.StatsInterval = TimeSpan.FromMilliseconds(50);

        _poller.Start(TimeSpan.FromMilliseconds(10));
        await WaitUntilAsync(() => Logs().Any(l => l.Contains("週期統計", StringComparison.Ordinal)));
        _poller.Stop();

        var line = Logs().First(l => l.Contains("週期統計", StringComparison.Ordinal));
        Assert.Contains("DMC 輪詢：CardId=0, Stage=Poll", line, StringComparison.Ordinal);
        Assert.Contains("PeriodMs=10, AvgMs=", line, StringComparison.Ordinal);
        Assert.Contains("MaxGapMs=", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 讀取耗時超過週期_統計日誌帶警告()
    {
        using var axis = CreateAxis(0);
        _api.ReadDuration = TimeSpan.FromMilliseconds(2);
        _poller.StatsInterval = TimeSpan.FromMilliseconds(50);

        // 一輪讀 3 個值，每個 2 ms，必然超過 1 ms 的週期
        _poller.Start(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => Logs().Any(l => l.StartsWith("[警告] ", StringComparison.Ordinal)));
        _poller.Stop();

        var line = Logs().First(l => l.StartsWith("[警告] ", StringComparison.Ordinal));
        Assert.Contains("週期統計", line, StringComparison.Ordinal);
        Assert.Contains("需加大輪詢週期", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Overruns=0", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 統計間隔必須大於0()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _poller.StatsInterval = TimeSpan.Zero);
    }

    [Fact]
    public async Task 輪詢正在讀其他軸時_停止不用等整輪結束()
    {
        // 卡鎖只在讀單根軸時持有：一輪 4 根軸 × 3 個值 × 30 ms = 360 ms，停止最多等一根軸（90 ms）
        var axes = Enumerable.Range(0, 4).Select(i => CreateAxis((ushort)i)).ToList();
        try
        {
            _api.ReadDuration = TimeSpan.FromMilliseconds(30);
            var round = Task.Run(_poller.RefreshOnce);
            await WaitUntilAsync(() => _api.TotalReads > 0);

            await axes[3].StopAsync();

            Assert.False(round.IsCompleted);
            Assert.Equal("dmc_stop", _api.Calls[^1]);
            await round;
        }
        finally
        {
            axes.ForEach(axis => axis.Dispose());
        }
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

    [Fact]
    public async Task 下發後的舊快照即使顯示已停止_也不能當作到位()
    {
        using var axis = CreateAxis(0);
        await EnableWithOneCycleAsync(axis);
        // 再讀一輪，讓運動前檢查不需要等待，下發立即發生
        _poller.RefreshOnce();
        Assert.Equal(1, _poller.GetSnapshot(0)!.Done);

        // 不刷新：快照仍是下發前的「已停止」，沒有圍欄就會被誤判為到位
        var move = axis.MoveAbsoluteAsync(10, new MotionProfile(100, 1000, 500));
        await Task.Delay(20);
        Assert.False(move.IsCompleted);
        Assert.Contains("dmc_pmove_unit", _api.Calls);

        await PumpAsync(move);
        Assert.Equal(10, _api.Position);
    }

    [Fact]
    public async Task 復位後立即使能_等新一輪快照_不被復位前的報警快照擋住()
    {
        using var axis = CreateAxis(0);
        _api.IoStatus = 1;
        _poller.RefreshOnce();
        await axis.ResetAlarmAsync();

        var enable = axis.EnableAsync();
        await Task.Delay(20);
        Assert.False(enable.IsCompleted);

        await PumpAsync(enable);
        Assert.Contains("nmc_set_axis_enable", _api.Calls);
    }

    [Fact]
    public void 報警詳情由輪詢讀取_GetStatus不再調用錯誤碼函數()
    {
        using var axis = CreateAxis(0);
        _api.IoStatus = 1;
        _api.AxisErrorCode = 0x7380;
        _poller.RefreshOnce();
        Assert.Equal(1, _api.ReadCount("nmc_get_axis_errcode"));

        var status = axis.GetStatus();
        axis.GetStatus();

        Assert.Equal(AxisState.Alarm, status.State);
        Assert.Contains("0x7380", status.AlarmReason, StringComparison.Ordinal);
        Assert.Equal(1, _api.ReadCount("nmc_get_axis_errcode"));
    }

    [Fact]
    public async Task 等待週期時取消_已下發的運動先停軸再拋出取消()
    {
        using var axis = CreateAxis(0);
        await EnableWithOneCycleAsync(axis);
        _poller.RefreshOnce();
        using var cts = new CancellationTokenSource();

        var move = axis.MoveAbsoluteAsync(10, new MotionProfile(100, 1000, 500), cts.Token);
        Assert.Equal("dmc_pmove_unit", _api.Calls[^1]);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PumpAsync(move));

        Assert.Equal("dmc_stop", _api.Calls[^1]);
    }

    [Fact]
    public async Task 運動前等新快照時取消_不下發運動也不停軸()
    {
        using var axis = CreateAxis(0);
        await EnableWithOneCycleAsync(axis);
        using var cts = new CancellationTokenSource();

        // 使能後還沒有新一輪快照，運動前檢查在等
        var move = axis.MoveAbsoluteAsync(10, new MotionProfile(100, 1000, 500), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.Equal(["nmc_set_axis_enable"], _api.Calls);
    }

    /// <summary>日誌由輪詢線程寫入，讀寫都要加鎖。</summary>
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

    /// <summary>
    /// 使能並且只推進一輪輪詢：新軸沒有快照，使能等到這一輪才繼續。
    /// 之後不會再有新週期，最新快照不晚於使能的圍欄，測試前置狀態是確定的。
    /// </summary>
    private async Task EnableWithOneCycleAsync(DmcAxis axis)
    {
        var enable = axis.EnableAsync();
        _poller.RefreshOnce();
        await enable;
    }

    /// <summary>手動推動輪詢直到任務結束：每輪之間讓出時間，便於斷言「沒有新週期就不前進」。</summary>
    private async Task PumpAsync(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "等待任務超時");
            _poller.RefreshOnce();
            await Task.Delay(1);
        }

        await task;
    }

    private DmcAxis CreateAxis(ushort axisNo, LeadshineFeedback feedback = LeadshineFeedback.None) =>
        new(_api, Options(axisNo) with { Feedback = feedback }, _poller, log: AddLog);

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
