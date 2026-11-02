namespace StationKit.Alarms;

/// <summary>
/// 報警中心：觸發、確認、清除、歷史。線程安全。
/// 同一來源的同一代碼在未清除前只保留一條，避免輪詢觸發時報警列表被刷屏。
/// 觸發報警永遠不拋異常：報警路徑本身出錯會掩蓋原始故障。
/// 來源可以分層，「上工位/善後」屬於「上工位」：按來源查詢和清除時包含下一層。
/// </summary>
public sealed class AlarmCenter(AlarmCatalog catalog, TimeProvider? timeProvider = null, int historyCapacity = 1000)
{
    private const string SubscriberFaultSource = "報警中心";

    private readonly object _sync = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly List<ActiveAlarm> _active = [];
    private readonly LinkedList<AlarmRecord> _history = new();

    public event EventHandler<ActiveAlarm>? Raised;

    public event EventHandler<AlarmRecord>? Cleared;

    public IReadOnlyList<ActiveAlarm> Active
    {
        get
        {
            lock (_sync)
            {
                return [.. _active];
            }
        }
    }

    public IReadOnlyList<AlarmRecord> History
    {
        get
        {
            lock (_sync)
            {
                return [.. _history];
            }
        }
    }

    public bool HasBlockingAlarm(string? source = null)
    {
        lock (_sync)
        {
            return _active.Exists(a => a.Definition.Severity == AlarmSeverity.Error &&
                                       (source is null || BelongsTo(a.Source, source)));
        }
    }

    public void Raise(string code, string source, string detail = "")
    {
        ActiveAlarm alarm;
        lock (_sync)
        {
            if (_active.Exists(a => a.Definition.Code == code && a.Source == source))
            {
                return;
            }

            var definition = catalog.Resolve(code);
            alarm = new ActiveAlarm(definition, source, detail, _time.GetUtcNow(), Acknowledged: false);
            _active.Add(alarm);
        }

        Notify(Raised, alarm);
    }

    public void Acknowledge(string code, string source)
    {
        lock (_sync)
        {
            var index = _active.FindIndex(a => a.Definition.Code == code && a.Source == source);
            if (index >= 0)
            {
                _active[index] = _active[index] with { Acknowledged = true };
            }
        }
    }

    /// <summary>清除一條報警。返回 false 表示該報警不存在。</summary>
    public bool Clear(string code, string source)
    {
        AlarmRecord record;
        lock (_sync)
        {
            var index = _active.FindIndex(a => a.Definition.Code == code && a.Source == source);
            if (index < 0)
            {
                return false;
            }

            var alarm = _active[index];
            _active.RemoveAt(index);
            record = new AlarmRecord(alarm.Definition, alarm.Source, alarm.Detail, alarm.RaisedAt, _time.GetUtcNow());
            _history.AddFirst(record);
            if (_history.Count > historyCapacity)
            {
                _history.RemoveLast();
            }
        }

        Notify(Cleared, record);
        return true;
    }

    public void ClearSource(string source)
    {
        foreach (var alarm in Active.Where(a => BelongsTo(a.Source, source)))
        {
            Clear(alarm.Definition.Code, alarm.Source);
        }
    }

    private static bool BelongsTo(string alarmSource, string source) =>
        alarmSource == source || alarmSource.StartsWith(source + "/", StringComparison.Ordinal);

    /// <summary>
    /// 逐個調用訂閱者並隔離異常：不能讓界面的一個錯誤打斷調用方的故障處理，也不能讓後面的訂閱者收不到。
    /// 失敗記為提示級報警；訂閱者對這條報警再次失敗時，去重讓遞歸到此為止。
    /// </summary>
    private void Notify<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        Exception? failure = null;
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        if (failure != null)
        {
            Raise(AlarmCatalog.SubscriberFaultCode, SubscriberFaultSource, failure.Message);
        }
    }
}
