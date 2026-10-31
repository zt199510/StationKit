using Microsoft.Extensions.Time.Testing;
using StationKit.Stations;

namespace StationKit.Tests;

public class ResourceArbiterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly FakeTimeProvider _time = new();
    private readonly ResourceArbiter _arbiter;

    public ResourceArbiterTests() => _arbiter = new ResourceArbiter(_time);

    [Fact]
    public async Task 資源互斥_釋放後下一個工站才拿到()
    {
        var upper = await _arbiter.AcquireAsync("相機", "上工位", Timeout);
        var lower = _arbiter.AcquireAsync("相機", "下工位", Timeout);

        Assert.False(lower.IsCompleted);
        Assert.Equal("上工位", _arbiter.Snapshot().Single().Owner);

        upper.Dispose();
        using var lease = await lower;

        Assert.Equal("下工位", _arbiter.Snapshot().Single().Owner);
    }

    [Fact]
    public async Task 等待超時報告當前佔用者()
    {
        using var upper = await _arbiter.AcquireAsync("相機", "上工位", Timeout);
        var lower = _arbiter.AcquireAsync("相機", "下工位", Timeout);

        _time.Advance(Timeout);

        var ex = await Assert.ThrowsAsync<ResourceTimeoutException>(() => lower);
        Assert.Contains("當前佔用者：上工位", ex.Message);
    }

    [Fact]
    public async Task 持有期間異常_資源保持佔用直到人工復位()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _arbiter.UseAsync("拍照區", "上工位", Timeout,
            _ => throw new InvalidOperationException("X 軸伺服報警")));

        var state = _arbiter.Snapshot().Single();
        Assert.Equal("上工位", state.Owner);
        Assert.Contains("X 軸伺服報警", state.UncertainReason);

        var lower = _arbiter.AcquireAsync("拍照區", "下工位", Timeout);
        _time.Advance(Timeout);
        var ex = await Assert.ThrowsAsync<ResourceTimeoutException>(() => lower);
        Assert.Contains("不確定狀態", ex.Message);

        Assert.True(_arbiter.ResetUncertain("拍照區"));
        using var lease = await _arbiter.AcquireAsync("拍照區", "下工位", Timeout);
        Assert.Equal("下工位", _arbiter.Snapshot().Single().Owner);
    }

    [Fact]
    public async Task 正常完成時資源歸還()
    {
        await _arbiter.UseAsync("相機", "上工位", Timeout, _ => Task.CompletedTask);

        Assert.Null(_arbiter.Snapshot().Single().Owner);
        Assert.False(_arbiter.ResetUncertain("相機"));
    }

    [Fact]
    public async Task 調用方取消與超時區分開()
    {
        using var upper = await _arbiter.AcquireAsync("相機", "上工位", Timeout);
        using var cts = new CancellationTokenSource();
        var lower = _arbiter.AcquireAsync("相機", "下工位", Timeout, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lower);
    }

    [Fact]
    public async Task 重複釋放租約是安全的()
    {
        var lease = await _arbiter.AcquireAsync("相機", "上工位", Timeout);
        lease.Dispose();
        lease.Dispose();

        using var again = await _arbiter.AcquireAsync("相機", "下工位", Timeout);
        Assert.Equal("下工位", _arbiter.Snapshot().Single().Owner);
    }
}
