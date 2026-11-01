using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Io;
using StationKit.Motion.Leadshine;

namespace StationKit.Tests.Leadshine;

/// <summary>
/// 雷賽 IO 的消抖跟著輪詢走：每輪發佈 IO 快照後更新過濾，進入卡故障的那一輪也更新。
/// 假時鐘 + RefreshOnce 手動推進，結果確定。
/// </summary>
public sealed class LeadshineDebounceTests : IDisposable
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(5);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeDmcNativeApi _api = new();
    private readonly LeadshineStatusPoller _poller;
    private readonly LeadshineIoBoard _io;

    public LeadshineDebounceTests()
    {
        _poller = new LeadshineStatusPoller(_api, 0, "DMC", _time, staleTimeout: TimeSpan.FromSeconds(5));
        _io = new LeadshineIoBoard(_api, _poller, inputPorts: 1, outputPorts: 1);
    }

    public void Dispose() => _poller.Dispose();

    [Fact]
    public void 每輪輪詢更新過濾_保持延時後才有效()
    {
        var door = _io.CreateInput(new("安全門關閉", 2, ActiveHigh: true)
        {
            ActivateDelay = TimeSpan.FromMilliseconds(20),
        });
        _api.InputPorts[0] = 1u << 2;

        Refresh(cycles: 4);
        Assert.False(door.Read());

        Refresh(cycles: 1);
        Assert.True(door.Read());

        // 打開立即生效
        _api.InputPorts[0] = 0;
        Refresh(cycles: 1);
        Assert.False(door.Read());
    }

    [Fact]
    public void 卡故障後復位_過濾從無效重新開始_不沿用故障前的數據()
    {
        var door = _io.CreateInput(new("安全門關閉", 2, ActiveHigh: true)
        {
            ActivateDelay = TimeSpan.FromMilliseconds(20),
        });
        _api.InputPorts[0] = 1u << 2;
        Refresh(cycles: 6);
        Assert.True(door.Read());

        _api.ThrowOnIoRead = true;
        _poller.RefreshOnce();
        Assert.Throws<IoFaultException>(() => door.Read());

        _api.ThrowOnIoRead = false;
        _poller.ResetFault();
        // 復位後、下一輪讀完之前：故障前的數據不算
        Assert.Contains("尚無 IO 數據", Assert.Throws<IoFaultException>(() => door.Read()).Message);
        Refresh(cycles: 1);
        Assert.False(door.Read());
        Refresh(cycles: 4);
        Assert.True(door.Read());
    }

    [Fact]
    public void 同一張卡_不能創建第二個IO板()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new LeadshineIoBoard(_api, _poller, inputPorts: 1, outputPorts: 1));
        Assert.Contains("已經有 IO 板", ex.Message);
    }

    /// <summary>每輪先推進一個週期再讀：第一輪的時間點作為起點。</summary>
    private void Refresh(int cycles)
    {
        for (var i = 0; i < cycles; i++)
        {
            _poller.RefreshOnce();
            _time.Advance(Period);
        }
    }
}
