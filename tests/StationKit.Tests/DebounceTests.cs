using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Io;
using StationKit.Abstractions.Safety;
using StationKit.Io.Simulation;

namespace StationKit.Tests;

/// <summary>
/// 信號級消抖（docs/design/digital-io.md 第 11 節）。輸入電平變化和假時鐘推進都會同步刷新一輪並更新過濾，
/// 所以讀取類斷言是確定的；只有等待需要逐步推進。
/// </summary>
public sealed class DebounceTests : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(20);

    private readonly FakeTimeProvider _time = new();
    private readonly SimIoBoard _board;
    private readonly FakeClockStepper _clock;

    public DebounceTests()
    {
        _board = new SimIoBoard(timeProvider: _time);
        _clock = new FakeClockStepper(_time, SimIoBoard.DefaultPeriod);
    }

    public void Dispose() => _board.Dispose();

    [Fact]
    public void 有效延時_抖動時不變有效_連續保持足夠後才有效()
    {
        var sensor = _board.CreateInput(new("料盤到位", 0, ActiveHigh: true) { ActivateDelay = Delay });

        _board.SetInputLevel(0, true);
        Assert.False(sensor.Read());
        Advance(10);
        _board.SetInputLevel(0, false);
        _board.SetInputLevel(0, true);
        Advance(15);
        // 從抖動之後算只保持了 15 ms
        Assert.False(sensor.Read());

        Advance(5);
        Assert.True(sensor.Read());
    }

    [Fact]
    public void 安全門_關閉要關穩才算_打開立即生效()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true) { ActivateDelay = Delay });
        _board.SetInputLevel(0, true);
        Advance(20);
        Assert.True(door.Read());

        _board.SetInputLevel(0, false);

        Assert.False(door.Read());
    }

    [Fact]
    public void 無效延時_短暫斷開不變無效()
    {
        var vacuum = _board.CreateInput(new("真空", 0, ActiveHigh: true) { DeactivateDelay = Delay });
        _board.SetInputLevel(0, true);
        Assert.True(vacuum.Read());

        _board.SetInputLevel(0, false);
        Advance(10);
        Assert.True(vacuum.Read());
        _board.SetInputLevel(0, true);
        _board.SetInputLevel(0, false);
        Advance(15);
        Assert.True(vacuum.Read());

        Advance(5);
        Assert.False(vacuum.Read());
    }

    [Fact]
    public void 啟動時從無效開始_一直有效的信號也要保持延時()
    {
        // 低電平有效，模擬板上電電平為 0：信號一開始就是有效的
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: false) { ActivateDelay = Delay });

        Assert.False(door.Read());
        Advance(15);
        Assert.False(door.Read());
        Advance(5);
        Assert.True(door.Read());
    }

    [Fact]
    public void 卡故障立即報出_恢復後從無效重新開始()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true) { ActivateDelay = Delay });
        _board.SetInputLevel(0, true);
        Advance(20);
        Assert.True(door.Read());

        _board.InjectFault("通訊中斷");
        Assert.Contains("通訊中斷", Assert.Throws<IoFaultException>(() => door.Read()).Message);

        _board.ClearFault();
        Assert.False(door.Read());
        Advance(20);
        Assert.True(door.Read());
    }

    [Fact]
    public void 輸出配置延時或延時為負_拒絕創建()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _board.CreateOutput(new("夾爪閥", 0, ActiveHigh: true) { ActivateDelay = Delay }));
        Assert.Contains("不支持消抖", ex.Message);

        Assert.Throws<ArgumentException>(() =>
            _board.CreateInput(new("真空", 0, ActiveHigh: true) { DeactivateDelay = TimeSpan.FromMilliseconds(-1) }));
    }

    [Fact]
    public async Task 等待作用在過濾後的值上()
    {
        var sensor = _board.CreateInput(new("料盤到位", 0, ActiveHigh: true) { ActivateDelay = Delay });

        var wait = sensor.WaitAsync(true, TimeSpan.FromMilliseconds(500));
        _board.SetInputLevel(0, true);
        await _clock.AdvanceAsync(TimeSpan.FromMilliseconds(15));
        Assert.False(wait.IsCompleted);

        await _clock.AdvanceUntilDoneAsync(wait, TimeSpan.FromMilliseconds(50));
        await wait;
    }

    [Fact]
    public void 互鎖讀到過濾後的值()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true) { ActivateDelay = Delay });
        var interlock = Interlock.RequireInput("安全門", door);
        var request = new MotionRequest("X", MotionKind.Move, 50);

        _board.SetInputLevel(0, true);
        Assert.Contains("當前無效", interlock.CheckBlocked(request));

        Advance(20);
        Assert.Null(interlock.CheckBlocked(request));
    }

    [Fact]
    public void 未配置延時_行為與原來一樣立即生效()
    {
        var sensor = _board.CreateInput(new("料盤到位", 0, ActiveHigh: true));

        _board.SetInputLevel(0, true);

        Assert.True(sensor.Read());
    }

    /// <summary>推進假時鐘：每個週期模擬板刷新一輪，過濾同步更新。</summary>
    private void Advance(int milliseconds)
    {
        for (var elapsed = 0; elapsed < milliseconds; elapsed += (int)SimIoBoard.DefaultPeriod.TotalMilliseconds)
        {
            _time.Advance(SimIoBoard.DefaultPeriod);
        }
    }
}
