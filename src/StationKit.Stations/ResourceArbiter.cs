namespace StationKit.Stations;

public sealed record ResourceState(string Name, string? Owner, string? UncertainReason);

public sealed class ResourceTimeoutException(string resource, string requester, TimeSpan timeout, ResourceState state)
    : TimeoutException(state.UncertainReason is { } uncertain
        ? $"{requester} 等待資源「{resource}」超時（{timeout.TotalSeconds:0.#} 秒）：資源處於不確定狀態（{uncertain}），需人工確認後復位"
        : $"{requester} 等待資源「{resource}」超時（{timeout.TotalSeconds:0.#} 秒），當前佔用者：{state.Owner ?? "無"}")
{
    public string Resource { get; } = resource;

    public ResourceState State { get; } = state;
}

/// <summary>
/// 工站之間的共享資源仲裁，例如共用相機、共用拍照區、共用上下料機械手。
/// <para>
/// 核心規則：持有資源期間出現任何異常（包括停止），資源進入「不確定」狀態並保持佔用，
/// 其他工站不能進入，直到人工確認現場安全後調用 <see cref="ResetUncertain"/>。
/// 樂觀地釋放一個狀態未知的區域，就是撞機的開始。
/// </para>
/// </summary>
public sealed class ResourceArbiter(TimeProvider? timeProvider = null)
{
    private readonly object _sync = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    public event EventHandler<ResourceState>? Changed;

    public IReadOnlyList<ResourceState> Snapshot()
    {
        lock (_sync)
        {
            return [.. _slots.Select(pair => pair.Value.ToState(pair.Key))];
        }
    }

    public async Task<ResourceLease> AcquireAsync(string resource, string owner, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var slot = GetSlot(resource);
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await slot.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ResourceState state;
            lock (_sync)
            {
                state = slot.ToState(resource);
            }

            throw new ResourceTimeoutException(resource, owner, timeout, state);
        }

        ResourceState acquired;
        lock (_sync)
        {
            slot.Owner = owner;
            acquired = slot.ToState(resource);
        }

        Changed?.Invoke(this, acquired);
        return new ResourceLease(this, resource, slot, owner);
    }

    /// <summary>持有資源執行一段動作。動作拋出任何異常（包括取消），資源都進入不確定狀態。</summary>
    public async Task UseAsync(string resource, string owner, TimeSpan timeout,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var lease = await AcquireAsync(resource, owner, timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lease.MarkUncertain($"{owner} 持有期間異常：{ex.Message}");
            throw;
        }
    }

    /// <summary>人工確認現場安全後，解除不確定狀態。返回 false 表示該資源不處於不確定狀態。</summary>
    public bool ResetUncertain(string resource)
    {
        Slot slot;
        ResourceState state;
        lock (_sync)
        {
            if (!_slots.TryGetValue(resource, out slot!) || slot.UncertainReason is null)
            {
                return false;
            }

            slot.UncertainReason = null;
            slot.Owner = null;
            state = slot.ToState(resource);
        }

        slot.Gate.Release();
        Changed?.Invoke(this, state);
        return true;
    }

    internal void Release(string resource, Slot slot, string? uncertainReason)
    {
        ResourceState state;
        lock (_sync)
        {
            if (uncertainReason is not null)
            {
                // 不釋放信號量：保持佔用，直到人工復位
                slot.UncertainReason = uncertainReason;
            }
            else
            {
                slot.Owner = null;
            }

            state = slot.ToState(resource);
        }

        if (uncertainReason is null)
        {
            slot.Gate.Release();
        }

        Changed?.Invoke(this, state);
    }

    private Slot GetSlot(string resource)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue(resource, out var slot))
            {
                slot = new Slot();
                _slots.Add(resource, slot);
            }

            return slot;
        }
    }

    internal sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string? Owner { get; set; }

        public string? UncertainReason { get; set; }

        public ResourceState ToState(string name) => new(name, Owner, UncertainReason);
    }
}

public sealed class ResourceLease : IDisposable
{
    private readonly ResourceArbiter _arbiter;
    private readonly ResourceArbiter.Slot _slot;
    private string? _uncertainReason;
    private int _disposed;

    internal ResourceLease(ResourceArbiter arbiter, string resource, ResourceArbiter.Slot slot, string owner)
    {
        _arbiter = arbiter;
        _slot = slot;
        Resource = resource;
        Owner = owner;
    }

    public string Resource { get; }

    public string Owner { get; }

    /// <summary>標記為不確定：釋放租約時資源不會歸還，需要人工復位。</summary>
    public void MarkUncertain(string reason) => _uncertainReason = reason;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _arbiter.Release(Resource, _slot, _uncertainReason);
        }
    }
}
