using Microsoft.Extensions.Time.Testing;
using StationKit.Abstractions.Io;
using StationKit.Io.Simulation;

namespace StationKit.Tests;

public sealed class DigitalIoTests : IDisposable
{
    private static readonly TimeSpan Period = SimIoBoard.DefaultPeriod;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private readonly FakeTimeProvider _time = new();
    private readonly SimIoBoard _board;

    public DigitalIoTests() => _board = new SimIoBoard(timeProvider: _time);

    public void Dispose() => _board.Dispose();

    [Fact]
    public void 低電平有效_電平為0時邏輯值為有效()
    {
        var door = _board.CreateInput(new("安全門關閉", 3, ActiveHigh: false));

        Assert.True(door.Read());
        _board.SetInputLevel(3, true);
        Assert.False(door.Read());
    }

    [Fact]
    public void 有效電平未指定_拒絕創建()
    {
        var ex = Assert.Throws<ArgumentException>(() => _board.CreateInput(new("真空", 0, ActiveHigh: null)));
        Assert.Contains("有效電平未指定", ex.Message);
        Assert.Throws<ArgumentException>(() => _board.CreateOutput(new("吸嘴", 0, ActiveHigh: null)));
    }

    [Fact]
    public void 位號超出範圍_拒絕創建()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _board.CreateInput(new("真空", 32, ActiveHigh: true)));
    }

    [Fact]
    public void 同一輸出位_不能分配兩次()
    {
        _board.ReserveOutput(5, "X軸報警復位");

        var ex = Assert.Throws<InvalidOperationException>(() => _board.CreateOutput(new("吸嘴", 5, true)));
        Assert.Contains("X軸報警復位", ex.Message);
        _board.CreateOutput(new("夾爪", 6, true));
        Assert.Throws<InvalidOperationException>(() => _board.CreateOutput(new("三色燈紅", 6, true)));
    }

    [Fact]
    public void 卡故障時_讀取拋異常而不是返回無效_界面讀數帶原因()
    {
        var door = _board.CreateInput(new("安全門關閉", 0, ActiveHigh: true));
        _board.InjectFault("通訊中斷");

        var ex = Assert.Throws<IoFaultException>(() => door.Read());
        Assert.Equal("安全門關閉", ex.Signal);
        Assert.Contains("通訊中斷", ex.Message);

        var reading = door.GetReading();
        Assert.Null(reading.Value);
        Assert.Contains("通訊中斷", reading.FaultReason);
    }

    [Fact]
    public async Task 等待_舊數據裡已滿足的條件不算_要等下一輪()
    {
        var done = _board.CreateInput(new("下游完成", 0, ActiveHigh: true));
        _board.SetInputLevel(0, true);

        var wait = done.WaitAsync(true, Timeout);
        Assert.False(wait.IsCompleted);

        await AdvanceUntilDoneAsync(wait, Period);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 等待_信號變化後完成()
    {
        var vacuum = _board.CreateInput(new("真空", 1, ActiveHigh: true));

        var wait = vacuum.WaitAsync(true, Timeout);
        await AdvanceAsync(Period * 3);
        Assert.False(wait.IsCompleted);

        _board.SetInputLevel(1, true);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 等待_保持時間內抖動_重新計時()
    {
        var sensor = _board.CreateInput(new("到位", 2, ActiveHigh: true));
        var stable = TimeSpan.FromMilliseconds(20);

        var wait = sensor.WaitAsync(true, Timeout, stableTime: stable);
        _board.SetInputLevel(2, true);
        await AdvanceAsync(TimeSpan.FromMilliseconds(15));
        // 按週期取樣，短於一輪的毛刺本來就看不到（實機也一樣），所以抖動持續一輪
        _board.SetInputLevel(2, false);
        await AdvanceAsync(Period);
        _board.SetInputLevel(2, true);
        await AdvanceAsync(TimeSpan.FromMilliseconds(15));
        // 從抖動算起只保持了 15 ms，不夠 20 ms
        Assert.False(wait.IsCompleted);

        await AdvanceUntilDoneAsync(wait, TimeSpan.FromMilliseconds(30));
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 等待超時_異常帶期望值和當前值()
    {
        var sensor = _board.CreateInput(new("伸出到位", 4, ActiveHigh: true));

        var wait = sensor.WaitAsync(true, TimeSpan.FromMilliseconds(50));
        await AdvanceUntilDoneAsync(wait, TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<IoTimeoutException>(() => wait);
        Assert.True(ex.Expected);
        Assert.False(ex.Actual);
        Assert.Contains("伸出到位", ex.Message);
        Assert.Contains("50 ms 內未等到有效", ex.Message);
    }

    [Fact]
    public async Task 等待中卡故障_立即報故障不等超時()
    {
        var sensor = _board.CreateInput(new("伸出到位", 4, ActiveHigh: true));

        var wait = sensor.WaitAsync(true, TimeSpan.FromSeconds(10));
        _board.InjectFault("通訊中斷");

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotType<IoTimeoutException>(ex);
        Assert.Contains("通訊中斷", ex.Message);
    }

    [Fact]
    public async Task 寫輸出_要等下一輪回讀到新值才完成()
    {
        var valve = _board.CreateOutput(new("夾爪閥", 0, ActiveHigh: true));

        var set = valve.SetAsync(true);
        Assert.True(_board.GetOutputLevel(0));
        // 輸出口已經寫了，但快照還是寫入前的：沒有回讀核對就不能算完成
        Assert.False(valve.Read());
        Assert.False(set.IsCompleted);

        await AdvanceUntilDoneAsync(set, Period);
        await set;
        Assert.True(valve.Read());
    }

    [Fact]
    public async Task 低電平有效的輸出_寫有效時輸出低電平()
    {
        var lamp = _board.CreateOutput(new("蜂鳴器", 1, ActiveHigh: false));
        _board.Refresh();
        Assert.True(lamp.Read());

        var set = lamp.SetAsync(false);
        await AdvanceUntilDoneAsync(set, Period);
        await set;
        Assert.True(_board.GetOutputLevel(1));
    }

    [Fact]
    public async Task 輸出口卡死_回讀不一致報故障()
    {
        var valve = _board.CreateOutput(new("夾爪閥", 0, ActiveHigh: true));
        _board.StickOutput(0, false);

        var set = valve.SetAsync(true);
        await AdvanceUntilDoneAsync(set, IoBoard.DefaultOutputVerifyTimeout * 2);

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => set);
        Assert.Contains("回讀仍為無效", ex.Message);
    }

    [Fact]
    public async Task 卡故障時寫輸出_在寫入前拒絕()
    {
        var valve = _board.CreateOutput(new("夾爪閥", 0, ActiveHigh: true));
        _board.InjectFault("通訊中斷");

        var ex = await Assert.ThrowsAsync<IoFaultException>(() => valve.SetAsync(true));
        Assert.Contains("拒絕寫入", ex.Message);
        Assert.False(_board.GetOutputLevel(0));
    }

    [Fact]
    public async Task 寫入前已取消_不寫輸出()
    {
        var valve = _board.CreateOutput(new("夾爪閥", 0, ActiveHigh: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => valve.SetAsync(true, new(canceled: true)));
        Assert.False(_board.GetOutputLevel(0));
    }

    /// <summary>
    /// 等待的後續在線程池上執行：每推進一步給它一點真實時間重新掛上等待，
    /// 否則連續推進時它來不及觀察中間的週期。
    /// </summary>
    private async Task AdvanceAsync(TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Period)
        {
            _time.Advance(Period);
            await Task.Delay(2);
        }
    }

    /// <summary>逐週期推進，直到任務結束或推進了 <paramref name="max"/>。</summary>
    private async Task AdvanceUntilDoneAsync(Task task, TimeSpan max)
    {
        for (var elapsed = TimeSpan.Zero; !task.IsCompleted && elapsed <= max; elapsed += Period)
        {
            _time.Advance(Period);
            await Task.WhenAny(task, Task.Delay(20));
        }
    }
}
