namespace StationKit.Abstractions.Io;

/// <summary>
/// 一張卡（或一個模擬板）上的通用 IO。負責創建具名的輸入輸出、登記輸出位，並統一實現
/// 邏輯值換算、等待、寫入後回讀核對：模擬和實機共用這一套，行為不會分叉。
/// <para>
/// 派生類只提供原始數據：按週期讀取的電平（<see cref="Sample"/>）、週期號、等待新週期、寫輸出。
/// 週期號規則與軸的集中輪詢相同（docs/design/central-polling.md 4.1）：數據帶讀取<b>開始時</b>的週期號，
/// 寫入前記錄 <see cref="CurrentCycle"/>，只有週期號更大的數據才是寫入之後讀的。
/// </para>
/// 設計說明見 docs/design/digital-io.md。
/// </summary>
public abstract class IoBoard
{
    /// <summary>寫輸出後等待回讀的默認時間。</summary>
    public static readonly TimeSpan DefaultOutputVerifyTimeout = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly Dictionary<ushort, string> _outputOwners = [];

    protected IoBoard(string name, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Time = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    /// <summary>寫輸出後，這段時間內回讀不到新值就報故障。</summary>
    public TimeSpan OutputVerifyTimeout { get; init; } = DefaultOutputVerifyTimeout;

    protected TimeProvider Time { get; }

    /// <summary>最近一輪讀取開始時的週期號。</summary>
    protected abstract long CurrentCycle { get; }

    public IDigitalInput CreateInput(IoPointConfig config)
    {
        var activeHigh = Validate(config, IoKind.Input);
        return new DigitalInput(this, config.Name, IoKind.Input, config.Bit, activeHigh);
    }

    /// <summary>創建輸出並登記位號：同一位只能有一個所有者。</summary>
    public IDigitalOutput CreateOutput(IoPointConfig config)
    {
        var activeHigh = Validate(config, IoKind.Output);
        ReserveOutput(config.Bit, config.Name);
        return new DigitalOutput(this, config.Name, config.Bit, activeHigh);
    }

    /// <summary>
    /// 登記一個不經過 <see cref="CreateOutput"/> 使用的輸出位（例如 SMC 驅動器報警復位），
    /// 防止它又被配置成別的輸出。已被佔用時拋 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void ReserveOutput(ushort bit, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateAddress(IoKind.Output, bit);
        lock (_sync)
        {
            if (!_outputOwners.TryAdd(bit, owner))
            {
                throw new InvalidOperationException(
                    $"{Name} 輸出位 {bit} 已被「{_outputOwners[bit]}」使用，不能再分配給「{owner}」");
            }
        }
    }

    /// <summary>讀一個點最新的電平。必須非阻塞、不調用 DLL；數據不可信時返回帶 <see cref="IoSample.Fault"/> 的結果。</summary>
    protected abstract IoSample Sample(IoKind kind, ushort bit);

    /// <summary>
    /// 等到週期號大於 <paramref name="afterCycle"/> 的一輪讀完；數據變為不可信時也立即返回，由調用方重新取樣。
    /// <paramref name="timeout"/> 內沒有等到拋 <see cref="TimeoutException"/>。
    /// </summary>
    protected abstract Task WaitForCycleAsync(long afterCycle, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>下發輸出電平。失敗時拋 <see cref="IoFaultException"/>，不返回 false。</summary>
    protected abstract void WriteOutput(ushort bit, bool level);

    /// <summary>檢查位號是否在範圍內，超出時拋 <see cref="ArgumentOutOfRangeException"/>。</summary>
    protected virtual void ValidateAddress(IoKind kind, ushort bit)
    {
    }

    private bool Validate(IoPointConfig config, IoKind kind)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Name);
        if (config.ActiveHigh is not { } activeHigh)
        {
            // 電平猜錯，「門已關」和「門打開」就反了
            throw new ArgumentException($"信號 {config.Name} 的有效電平未指定", nameof(config));
        }

        ValidateAddress(kind, config.Bit);
        return activeHigh;
    }

    private class DigitalInput(IoBoard board, string name, IoKind kind, ushort bit, bool activeHigh) : IDigitalInput
    {
        public string Name { get; } = name;

        protected IoBoard Board { get; } = board;

        protected ushort Bit { get; } = bit;

        protected bool ActiveHigh { get; } = activeHigh;

        public bool Read()
        {
            var sample = Board.Sample(kind, Bit);
            return sample.Fault is { } fault ? throw new IoFaultException(Name, fault) : ToValue(sample.Level);
        }

        public IoReading GetReading()
        {
            var sample = Board.Sample(kind, Bit);
            return sample.Fault is { } fault ? new(null, fault) : new(ToValue(sample.Level), null);
        }

        public Task WaitAsync(bool expected, TimeSpan timeout, TimeSpan stableTime = default,
            CancellationToken cancellationToken = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "等待時間必須大於 0");
            }

            if (stableTime < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(stableTime), stableTime, "保持時間不能為負");
            }

            return WaitForAsync(Board.CurrentCycle, expected, timeout, stableTime, cancellationToken);
        }

        protected bool ToValue(bool level) => level == ActiveHigh;

        /// <summary>等到 <paramref name="fence"/> 之後讀到的邏輯值等於 expected 並保持 stableTime。</summary>
        protected async Task WaitForAsync(long fence, bool expected, TimeSpan timeout, TimeSpan stableTime,
            CancellationToken cancellationToken)
        {
            var time = Board.Time;
            var start = time.GetTimestamp();
            long? stableSince = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = Board.Sample(kind, Bit);
                if (sample.Fault is { } fault)
                {
                    throw new IoFaultException(Name, fault);
                }

                // 圍欄之前讀的數據不參與判斷：舊數據裡已經滿足的條件不算（docs/design/digital-io.md 4.1）
                bool? actual = sample.Cycle > fence ? ToValue(sample.Level) : null;
                if (actual == expected)
                {
                    stableSince ??= time.GetTimestamp();
                    if (time.GetElapsedTime(stableSince.Value) >= stableTime)
                    {
                        return;
                    }
                }
                else
                {
                    // 抖動：中間只要有一次不滿足，保持時間重新計算
                    stableSince = null;
                }

                var remaining = timeout - time.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new IoTimeoutException(Name, expected, actual, timeout);
                }

                try
                {
                    await Board.WaitForCycleAsync(Math.Max(sample.Cycle, fence), remaining, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 回到循環頂部再取樣一次：最後一輪可能恰好在超時前讀完，由統一的超時判斷拋出
                }
            }
        }
    }

    private sealed class DigitalOutput(IoBoard board, string name, ushort bit, bool activeHigh)
        : DigitalInput(board, name, IoKind.Output, bit, activeHigh), IDigitalOutput
    {
        public async Task SetAsync(bool value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Board.Sample(IoKind.Output, Bit).Fault is { } fault)
            {
                // 卡的狀態未知時不下發，與軸「故障時除停止外一律拒絕」一致
                throw new IoFaultException(Name, $"拒絕寫入：{fault}");
            }

            // 圍欄必須在寫入前記錄：寫入之後才開始的一輪，週期號一定更大
            var fence = Board.CurrentCycle;
            Board.WriteOutput(Bit, value == ActiveHigh);
            try
            {
                await WaitForAsync(fence, value, Board.OutputVerifyTimeout, TimeSpan.Zero, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IoTimeoutException ex)
            {
                throw new IoFaultException(Name,
                    $"寫入{(value ? "有效" : "無效")}後 {ex.Timeout.TotalMilliseconds:0} ms 內回讀" +
                    $"{(ex.Actual is { } actual ? $"仍為{(actual ? "有效" : "無效")}" : "沒有新數據")}，" +
                    "輸出口可能損壞或被其他程序改寫");
            }
        }
    }
}
