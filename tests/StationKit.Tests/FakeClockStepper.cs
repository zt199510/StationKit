using Microsoft.Extensions.Time.Testing;

namespace StationKit.Tests;

/// <summary>
/// 按固定步長推進假時鐘。IO 等待的後續在線程池上執行：每推進一步給它一點真實時間重新掛上等待，
/// 否則連續推進時它來不及觀察中間的週期。
/// </summary>
internal sealed class FakeClockStepper(FakeTimeProvider time, TimeSpan step)
{
    public async Task AdvanceAsync(TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step)
        {
            time.Advance(step);
            await Task.Delay(2);
        }
    }

    /// <summary>逐步推進，直到任務結束或推進了 <paramref name="max"/>。</summary>
    public async Task AdvanceUntilDoneAsync(Task task, TimeSpan max)
    {
        for (var elapsed = TimeSpan.Zero; !task.IsCompleted && elapsed <= max; elapsed += step)
        {
            time.Advance(step);
            await Task.WhenAny(task, Task.Delay(2));
        }
    }
}
