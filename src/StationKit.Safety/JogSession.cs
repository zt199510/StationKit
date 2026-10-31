using StationKit.Abstractions.Motion;

namespace StationKit.Safety;

/// <summary>
/// 帶看門狗的點動會話，給界面的點動按鈕用：
/// <code>
/// // 按下
/// _jog = JogSession.Start(guardedX, JogDirection.Positive, profile, TimeSpan.FromMilliseconds(300));
/// // 按住期間，界面定時器每 100 ms
/// _jog.KeepAlive();
/// // 鬆開、窗口失焦、控件卸載
/// _jog.Release();
/// await _jog.Completion;   // 看門狗超時或運動故障時拋出 MotionFaultException
/// </code>
/// 為什麼需要看門狗：窗口失焦、彈出對話框、觸摸屏誤觸、UI 線程卡住時，鬆開事件可能根本不會到達，
/// 只靠 MouseUp 停軸，軸會一直走到限位。看門狗在超時內沒收到 <see cref="KeepAlive"/> 就自動停軸。
/// </summary>
public sealed class JogSession : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _release = new();
    private readonly ITimer _watchdog;
    private readonly TimeSpan _timeout;
    private bool _released;
    private bool _disposed;

    private JogSession(IAxis axis, JogDirection direction, MotionProfile profile, TimeSpan timeout,
        TimeProvider time)
    {
        AxisName = axis.Name;
        _timeout = timeout;
        _watchdog = time.CreateTimer(_ => Expire(), null, timeout, Timeout.InfiniteTimeSpan);
        Completion = RunAsync(axis.JogAsync(direction, profile, _release.Token));
    }

    public string AxisName { get; }

    /// <summary>看門狗是否超時（鬆開信號沒有按時到達）。</summary>
    public bool TimedOut { get; private set; }

    /// <summary>點動結束（停穩）時完成。看門狗超時、限位、報警時以 <see cref="MotionFaultException"/> 結束。</summary>
    public Task Completion { get; }

    public static JogSession Start(IAxis axis, JogDirection direction, MotionProfile profile,
        TimeSpan watchdogTimeout, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(profile);
        if (watchdogTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(watchdogTimeout), watchdogTimeout, "看門狗超時必須大於 0");
        }

        return new JogSession(axis, direction, profile, watchdogTimeout, timeProvider ?? TimeProvider.System);
    }

    /// <summary>按住期間定時調用，重置看門狗。鬆開或超時之後調用無效，不會讓軸重新動起來。</summary>
    public void KeepAlive()
    {
        lock (_sync)
        {
            if (!_released)
            {
                _watchdog.Change(_timeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>鬆開按鈕：減速停止。可重複調用。</summary>
    public void Release() => Stop(timedOut: false);

    /// <summary>釋放即鬆開：界面控件卸載時忘了調用 <see cref="Release"/>，軸也會停。</summary>
    public void Dispose()
    {
        Stop(timedOut: false);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _watchdog.Dispose();
        _release.Dispose();
    }

    private void Expire() => Stop(timedOut: true);

    private void Stop(bool timedOut)
    {
        lock (_sync)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            TimedOut = timedOut;
            _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // 在鎖內取消：保證 Dispose 不會在取消之前釋放令牌源
            _release.Cancel();
        }
    }

    private async Task RunAsync(Task jog)
    {
        await jog.ConfigureAwait(false);
        if (TimedOut)
        {
            // 軸已經停了，但必須讓人知道是看門狗停的：說明界面的鬆開信號丟了
            throw new MotionFaultException(AxisName,
                $"點動看門狗超時：{_timeout.TotalMilliseconds:0} ms 內未收到按住信號，已自動停止");
        }
    }
}
