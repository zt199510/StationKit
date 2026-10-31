using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;

namespace StationKit.Tests.Leadshine;

/// <summary>
/// 卡故障：任何一根軸讀取失敗（DLL 拋異常或返回錯誤碼），整張卡停止讀取，
/// 本卡所有軸禁止操作（停止除外），人工排查後復位，復位後需重新使能並回零。
/// </summary>
public sealed class LeadshineCardFaultTests : IDisposable
{
    private static readonly MotionProfile Profile = new(100, 1000, 500);

    private readonly FakeDmcNativeApi _api = new();
    private readonly List<string> _log = [];
    private readonly LeadshineStatusPoller _poller;

    // 過期時間放寬：這裡要證明卡故障是立即報出的，不是等到過期才報
    public LeadshineCardFaultTests() =>
        _poller = new LeadshineStatusPoller(_api, 0, "DMC", log: AddLog, staleTimeout: TimeSpan.FromSeconds(5));

    public void Dispose() => _poller.Dispose();

    [Fact]
    public void 一根軸讀取異常_本輪後面的軸和之後的週期都不再讀_所有軸報警()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1);
        using var z = CreateAxis(2);
        _poller.RefreshOnce();
        _api.ThrowOnRead = axisNo => axisNo == 1;

        _poller.RefreshOnce();
        _poller.RefreshOnce();

        // 第 1 輪 3 根軸各讀一次；第 2 輪軸 0 讀了，軸 1 出錯，軸 2 不讀；第 3 輪不讀
        Assert.Equal(4, _api.ReadCount("dmc_get_position_unit"));
        Assert.Contains("軸 1 狀態讀取失敗", _poller.Fault, StringComparison.Ordinal);
        foreach (var axis in new[] { x, y, z })
        {
            var status = axis.GetStatus();
            Assert.Equal(AxisState.Alarm, status.State);
            Assert.Contains("本卡所有軸禁止操作", status.AlarmReason, StringComparison.Ordinal);
        }

        Assert.Single(Logs(), line =>
            line.StartsWith("[報警] DMC 輪詢：CardId=0, AxisIndex=1, Stage=Poll, 狀態讀取失敗", StringComparison.Ordinal));
    }

    [Fact]
    public void 讀取返回錯誤碼_同樣整卡故障()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1, LeadshineFeedback.Encoder);
        _api.ReturnCodes["dmc_get_encoder_unit"] = 9;

        _poller.RefreshOnce();

        Assert.Contains("get_encoder_unit=9", _poller.Fault, StringComparison.Ordinal);
        Assert.Equal(AxisState.Alarm, x.GetStatus().State);
    }

    [Fact]
    public async Task 卡故障後_除停止外所有操作都被拒絕且不調用DLL()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1);
        await EnableWithOneCycleAsync(x);
        _api.ThrowOnRead = axisNo => axisNo == 1;
        _poller.RefreshOnce();
        var calls = _api.Calls.Count;

        var rejected = new Func<Task>[]
        {
            () => x.MoveAbsoluteAsync(10, Profile),
            () => x.MoveRelativeAsync(10, Profile),
            () => x.JogAsync(JogDirection.Positive, Profile, CancellationToken.None),
            () => x.HomeAsync(),
            () => x.EnableAsync(),
            () => x.DisableAsync(),
            () => x.ResetAlarmAsync(),
            () => x.WriteSafetyConfigAsync(new AxisSafetyConfig()),
            () => x.ReadSafetyConfigAsync(),
            () => Task.Run(x.ApplyBaseParameters),
        };
        foreach (var operation in rejected)
        {
            // 相對運動在擴展方法裡先讀狀態就被拒絕（MotionRejectedException 是它的子類）
            var ex = await Assert.ThrowsAnyAsync<MotionFaultException>(operation);
            Assert.Contains("本卡所有軸禁止操作", ex.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(calls, _api.Calls.Count);

        // 停止是安全動作，故障時必須仍然可用
        await x.StopAsync();
        Assert.Equal("dmc_stop", _api.Calls[^1]);
    }

    [Fact]
    public async Task 運動中其他軸讀取失敗_立即停軸報故障_不等過期()
    {
        using var x = CreateAxis(0);
        using var y = CreateAxis(1);
        await EnableWithOneCycleAsync(x);
        _poller.RefreshOnce();
        var move = x.MoveAbsoluteAsync(10, Profile);
        Assert.Equal("dmc_pmove_unit", _api.Calls[^1]);

        _api.ThrowOnRead = axisNo => axisNo == 1;
        _poller.RefreshOnce();
        var ex = await Assert.ThrowsAsync<MotionFaultException>(() => move);

        Assert.Contains("軸 1 狀態讀取失敗", ex.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("過期", ex.Reason, StringComparison.Ordinal);
        Assert.Equal("dmc_stop", _api.Calls[^1]);
    }

    [Fact]
    public async Task 復位後恢復輪詢_軸需要重新使能和回零()
    {
        using var x = CreateAxis(0);
        await EnableWithOneCycleAsync(x);
        await PumpAsync(x.HomeAsync());
        Assert.True(x.GetStatus().IsHomed);

        _api.ThrowOnRead = _ => true;
        _poller.RefreshOnce();
        _api.ThrowOnRead = null;
        _poller.ResetFault();
        _poller.RefreshOnce();

        Assert.Null(_poller.Fault);
        var status = x.GetStatus();
        Assert.Equal(AxisState.Disabled, status.State);
        Assert.False(status.IsHomed);
        Assert.Contains(Logs(), line => line.Contains("卡故障已手動復位", StringComparison.Ordinal));
    }

    [Fact]
    public void 故障原因還在_復位後下一輪再次故障()
    {
        using var x = CreateAxis(0);
        _api.ThrowOnRead = _ => true;
        _poller.RefreshOnce();

        _poller.ResetFault();
        _poller.RefreshOnce();

        Assert.NotNull(_poller.Fault);
        Assert.Equal(2, Logs().Count(line => line.StartsWith("[報警] ", StringComparison.Ordinal)));
    }

    private DmcAxis CreateAxis(ushort axisNo, LeadshineFeedback feedback = LeadshineFeedback.None) => new(_api,
        new DmcAxisOptions
        {
            CardNo = 0,
            AxisNo = axisNo,
            Name = $"X{axisNo}",
            PulseEquivalent = 1000,
            InPositionSignal = LeadshineInpMode.Disabled,
            Feedback = feedback,
            HomeProfile = new DmcHomeProfile(17, 5, 20, 0.1, 0.1, 0),
        }, _poller);

    /// <summary>使能並且只推進一輪輪詢，之後最新快照不晚於使能的圍欄。</summary>
    private async Task EnableWithOneCycleAsync(DmcAxis axis)
    {
        var enable = axis.EnableAsync();
        _poller.RefreshOnce();
        await enable;
    }

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
