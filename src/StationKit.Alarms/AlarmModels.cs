namespace StationKit.Alarms;

public enum AlarmSeverity
{
    /// <summary>提示，不阻止生產。</summary>
    Warning,

    /// <summary>故障，工站不允許開始新的循環，直到報警被清除。</summary>
    Error,
}

/// <summary>報警定義：代碼、等級、現象、處理建議。現場最需要的是「怎麼處理」，不是異常堆棧。</summary>
public sealed record AlarmDefinition(string Code, AlarmSeverity Severity, string Message, string Remedy);

public sealed record ActiveAlarm(
    AlarmDefinition Definition,
    string Source,
    string Detail,
    DateTimeOffset RaisedAt,
    bool Acknowledged);

public sealed record AlarmRecord(
    AlarmDefinition Definition,
    string Source,
    string Detail,
    DateTimeOffset RaisedAt,
    DateTimeOffset ClearedAt);

public sealed class AlarmCatalog
{
    public const string UnknownCode = "SYS-000";

    private readonly Dictionary<string, AlarmDefinition> _definitions = new(StringComparer.Ordinal)
    {
        [UnknownCode] = new(UnknownCode, AlarmSeverity.Error, "未登記的報警代碼", "聯繫軟件工程師補充報警表"),
    };

    public AlarmCatalog Add(AlarmDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_definitions.TryAdd(definition.Code, definition))
        {
            throw new ArgumentException($"報警代碼重複：{definition.Code}", nameof(definition));
        }

        return this;
    }

    public AlarmDefinition Resolve(string code) =>
        _definitions.TryGetValue(code, out var definition)
            ? definition
            : _definitions[UnknownCode] with { Message = $"未登記的報警代碼 {code}" };

    public IReadOnlyCollection<AlarmDefinition> All => _definitions.Values;
}
