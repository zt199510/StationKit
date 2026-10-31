namespace StationKit.Abstractions.Io;

/// <summary>
/// 與上下游設備的電平握手（本機發「請求」，對方回「完成」），順序寫死：
/// <list type="number">
/// <item>等對方撤銷上一次的「完成」：沒撤銷就發請求，舊的「完成」會被當成本次完成。</item>
/// <item>發出請求（回讀核對）。</item>
/// <item>等「完成」有效：只接受發出請求之後讀到的數據。</item>
/// <item>撤銷請求。</item>
/// </list>
/// 第 2 步之後失敗或取消，盡力撤銷請求再拋出，避免對方在本機已放棄後才動作。
/// 設計說明見 docs/design/digital-io.md 4.1。
/// </summary>
public sealed class Handshake
{
    private readonly IDigitalOutput _request;
    private readonly IDigitalInput _done;

    /// <param name="doneTimeout">等對方完成的最長時間，包含對方的處理時間。</param>
    /// <param name="releaseTimeout">等對方撤銷上一次「完成」的最長時間。</param>
    public Handshake(string name, IDigitalOutput request, IDigitalInput done, TimeSpan doneTimeout,
        TimeSpan releaseTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(doneTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(releaseTimeout, TimeSpan.Zero);
        Name = name;
        _request = request;
        _done = done;
        DoneTimeout = doneTimeout;
        ReleaseTimeout = releaseTimeout;
    }

    public string Name { get; }

    public TimeSpan DoneTimeout { get; }

    public TimeSpan ReleaseTimeout { get; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _done.WaitAsync(false, ReleaseTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (IoTimeoutException)
        {
            throw new HandshakeFaultException(Name,
                $"上一次握手的 {_done.Name} 在 {ReleaseTimeout.TotalMilliseconds:0} ms 內未撤銷，未發出 {_request.Name}；" +
                "檢查對方設備是否卡在上一次握手");
        }

        try
        {
            await _request.SetAsync(true, cancellationToken).ConfigureAwait(false);
            await _done.WaitAsync(true, DoneTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var withdrawn = await TryWithdrawAsync().ConfigureAwait(false);
            if (ex is IoTimeoutException)
            {
                throw new HandshakeFaultException(Name,
                    $"發出 {_request.Name} 後 {DoneTimeout.TotalMilliseconds:0} ms 內 {_done.Name} 未有效；" +
                    (withdrawn ? "請求已撤銷" : "請求撤銷失敗，需人工確認") + "；檢查對方設備狀態");
            }

            throw;
        }

        await _request.SetAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> TryWithdrawAsync()
    {
        try
        {
            // 不用調用方的令牌：調用方取消後更要撤銷
            await _request.SetAsync(false, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (IoFaultException)
        {
            // 撤銷失敗不能掩蓋原始故障，由異常信息提示人工確認
            return false;
        }
    }
}
