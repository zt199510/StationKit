using System.Runtime.ExceptionServices;

namespace StationKit.Abstractions.Motion;

/// <summary>多軸同時運動中的一根軸。</summary>
public sealed record AxisMove(IAxis Axis, double Position, MotionProfile Profile);

/// <summary>
/// 多軸同時運動，全部到位才完成。和直接 <c>Task.WhenAll</c> 的區別：
/// <list type="bullet">
/// <item>任何一軸故障（報警、限位、被安全檢查拒絕），其餘軸<b>立即</b>取消並減速停止，
/// 而不是等它們各自走到目標再報故障。</item>
/// <item>下發前就被拒絕的軸，排在它後面的軸不再下發；排在前面已經下發的軸被停下。</item>
/// <item>拋出的是引發停止的那個故障，被連帶停下的軸不重複報告。</item>
/// </list>
/// </summary>
public static class MotionGroup
{
    public static Task MoveAllAsync(IEnumerable<AxisMove> moves, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var list = moves.ToList();
        // 同一根軸出現兩次，第二條指令會因「軸正在運動」被拒絕，然後把整組停掉；不如下發前就拒絕。
        // 按名稱判斷：GuardedAxis 包裝後是不同的對象，但仍是同一根物理軸
        var duplicate = list.GroupBy(m => m.Axis.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new ArgumentException($"軸 {duplicate.Key} 在同一組運動中出現了 {duplicate.Count()} 次", nameof(moves));
        }

        return RunAllAsync(list.Select(m => (Func<CancellationToken, Task>)(token =>
            m.Axis.MoveAbsoluteAsync(m.Position, m.Profile, token))), cancellationToken);
    }

    /// <summary>
    /// 同時執行任意一組運動（例如多軸同時回零），規則同 <see cref="MoveAllAsync"/>。
    /// 每個委託都必須使用傳入的令牌，組內停止靠它實現。
    /// </summary>
    public static async Task RunAllAsync(IEnumerable<Func<CancellationToken, Task>> motions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(motions);
        using var group = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new List<Task>();
        foreach (var motion in motions)
        {
            var task = InvokeAsync(motion, group.Token);
            started.Add(task);
            if (task.IsFaulted)
            {
                // 下發前就失敗：已下發的停下，後面的不再下發（下發前會檢查到取消）
                await group.CancelAsync().ConfigureAwait(false);
            }
        }

        var faults = new List<Exception>();
        var pending = new List<Task>(started);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.IsFaulted)
            {
                faults.AddRange(done.Exception!.InnerExceptions);
                await group.CancelAsync().ConfigureAwait(false);
            }
        }

        // 被連帶停下的軸以取消結束，屬於後果不是原因，不報告
        switch (faults.Count)
        {
            case 1:
                ExceptionDispatchInfo.Throw(faults[0]);
                break;
            case > 1:
                throw new MotionGroupFaultException(faults);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (started.FirstOrDefault(t => t.IsCanceled) is { } canceled)
        {
            // 調用方沒有取消，卻有運動以取消結束：原樣拋出，不能當作全部到位
            await canceled.ConfigureAwait(false);
        }
    }

    /// <summary>包成異步方法：同步拋出的異常（例如 GuardedAxis 的拒絕）也變成 Task 的故障，不會打斷後續處理。</summary>
    private static async Task InvokeAsync(Func<CancellationToken, Task> motion, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(motion);
        await motion(token).ConfigureAwait(false);
    }
}

/// <summary>同一組運動裡有多根軸各自故障（例如共用電源跌落）。<see cref="Faults"/> 按發生順序排列。</summary>
public sealed class MotionGroupFaultException(IReadOnlyList<Exception> faults)
    : MotionFaultException(
        string.Join(",", faults.Select(f => f is MotionFaultException m ? m.Axis : "?")),
        "多軸故障：" + string.Join("；", faults.Select(f => f.Message)))
{
    public IReadOnlyList<Exception> Faults { get; } = faults;
}
