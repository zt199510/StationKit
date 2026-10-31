using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Motion;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;

namespace StationKit.Tests.Leadshine;

/// <summary>
/// 快照過期（docs/design/central-polling.md 4.2）：輪詢停了或某根軸一直讀取失敗，
/// 快照凍結在最後一刻，不能被當成「靜止、正常」。用假時鐘手動推進，不依賴真實時間。
/// </summary>
public sealed class LeadshineStaleStatusTests : IDisposable
{
    private static readonly TimeSpan StaleTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly MotionProfile Profile = new(100, 1000, 500);

    private readonly FakeDmcNativeApi _api = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LeadshineStatusPoller _poller;

    public LeadshineStaleStatusTests() =>
        _poller = new LeadshineStatusPoller(_api, 0, "DMC", _time, staleTimeout: StaleTimeout);

    public void Dispose() => _poller.Dispose();

    [Fact]
    public void 快照超過過期時間未更新_GetStatus報警並保留最後位置_恢復輪詢後正常()
    {
        using var axis = CreateAxis();
        _api.Position = 5;
        _poller.RefreshOnce();
        Assert.Equal(AxisState.Disabled, axis.GetStatus().State);

        _time.Advance(StaleTimeout);
        Assert.Equal(AxisState.Disabled, axis.GetStatus().State);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var stale = axis.GetStatus();
        Assert.Equal(AxisState.Alarm, stale.State);
        Assert.Contains("過期", stale.AlarmReason, StringComparison.Ordinal);
        Assert.Equal(5, stale.Position);

        _poller.RefreshOnce();
        Assert.Equal(AxisState.Disabled, axis.GetStatus().State);
    }

    [Fact]
    public async Task 快照已過期_拒絕下發運動()
    {
        using var axis = CreateAxis();
        await EnableWithOneCycleAsync(axis);

        _time.Advance(StaleTimeout + TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => axis.MoveAbsoluteAsync(10, Profile));

        Assert.Contains("過期", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(["nmc_set_axis_enable"], _api.Calls);
    }

    [Fact]
    public async Task 運動中輪詢停止_等待報故障並停軸()
    {
        using var axis = CreateAxis();
        await EnableWithOneCycleAsync(axis);
        _poller.RefreshOnce();

        var move = axis.MoveAbsoluteAsync(10, Profile);
        Assert.Equal("dmc_pmove_unit", _api.Calls[^1]);

        // 之後不再有新週期
        _time.Advance(StaleTimeout + TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);

        Assert.Contains("過期", ex.Reason, StringComparison.Ordinal);
        Assert.Equal("dmc_stop", _api.Calls[^1]);
    }

    [Fact]
    public async Task 運動中本軸一直讀取失敗_其他輪照常完成_等待不會卡死()
    {
        using var axis = CreateAxis();
        await EnableWithOneCycleAsync(axis);
        _poller.RefreshOnce();
        var move = axis.MoveAbsoluteAsync(10, Profile);

        // 輪詢還在跑、週期照常完成，只是這根軸的快照不再更新
        _api.ThrowOnRead = axisNo => axisNo == 0;
        for (var i = 0; i < 3; i++)
        {
            _poller.RefreshOnce();
            _time.Advance(TimeSpan.FromMilliseconds(60));
        }

        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);
        Assert.Contains("過期", ex.Reason, StringComparison.Ordinal);
        Assert.Equal("dmc_stop", _api.Calls[^1]);
    }

    [Fact]
    public async Task 從未有過快照_等待超時報故障()
    {
        using var axis = CreateAxis();

        var enable = axis.EnableAsync();
        _time.Advance(StaleTimeout + TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => enable);

        Assert.Contains("沒有狀態數據", ex.Reason, StringComparison.Ordinal);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void 輪詢週期太長_構造控制器時就拒絕()
    {
        // 過期時間至少是週期的 3 倍，否則正常輪詢也會被判為過期
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new DmcController(_api, 0, "192.168.5.11",
            pollPeriod: TimeSpan.FromMilliseconds(40), staleTimeout: StaleTimeout));

        Assert.Contains("太長", ex.Message, StringComparison.Ordinal);
        _ = new DmcController(_api, 0, "192.168.5.11", pollPeriod: TimeSpan.FromMilliseconds(30),
            staleTimeout: StaleTimeout);
    }

    private DmcAxis CreateAxis() => new(_api, new DmcAxisOptions
    {
        CardNo = 0,
        AxisNo = 0,
        Name = "X",
        PulseEquivalent = 1000,
        InPositionSignal = LeadshineInpMode.Disabled,
        Feedback = LeadshineFeedback.None,
        HomeProfile = new DmcHomeProfile(17, 5, 20, 0.1, 0.1, 0),
    }, _poller);

    /// <summary>使能並且只推進一輪輪詢，之後最新快照不晚於使能的圍欄。</summary>
    private async Task EnableWithOneCycleAsync(DmcAxis axis)
    {
        var enable = axis.EnableAsync();
        _poller.RefreshOnce();
        await enable;
    }
}
