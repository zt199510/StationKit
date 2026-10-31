namespace StationKit.Abstractions.Io;

public enum CylinderState
{
    /// <summary>沒有感應器，無法判斷位置。</summary>
    Unknown,
    Retracted,
    Extended,

    /// <summary>兩個到位都無效：在行程中間，或者感應器沒有反應。</summary>
    Moving,

    /// <summary>兩個到位同時有效，或感應器數據不可信。</summary>
    Fault,
}

public readonly record struct CylinderStatus(CylinderState State, string? FaultReason = null);

/// <summary>
/// 氣缸配置。<see cref="RetractValve"/> 為 null 表示單電控（斷電彈簧復位）；感應器可以缺省，
/// 但缺了哪個方向的感應器，就必須顯式配置 <see cref="NoSensorDelay"/>，按時間判定到位。
/// </summary>
public sealed record CylinderConfig
{
    public required string Name { get; init; }

    public required IDigitalOutput ExtendValve { get; init; }

    public IDigitalOutput? RetractValve { get; init; }

    public IDigitalInput? ExtendedSensor { get; init; }

    public IDigitalInput? RetractedSensor { get; init; }

    /// <summary>一次動作從下發到到位的最長時間，未配置（零）時拒絕創建。</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>到位信號需連續保持的時間，用於濾掉到位瞬間的回彈。</summary>
    public TimeSpan StableTime { get; init; }

    /// <summary>沒有感應器的方向，下發後等這麼久就算到位。只在缺感應器時使用，必須顯式配置。</summary>
    public TimeSpan? NoSensorDelay { get; init; }
}

/// <summary>
/// 氣缸：寫閥、等到位，並檢查兩個感應器的一致性。
/// <list type="bullet">
/// <item>到位條件是「目標到位有效<b>並且</b>另一端到位無效」：只看一端，感應器粘連時閥還沒動就「到位」了。</item>
/// <item>動作前兩個到位同時有效，直接報故障不動作：至少有一個感應器壞了，位置不可知。</item>
/// <item>雙電控先關反方向的閥，再開目標方向的閥，不會兩個閥同時打開。</item>
/// <item>取消時閥保持當前狀態（氣缸沒有「停下」這個動作），只中止等待。</item>
/// </list>
/// 同一個氣缸的動作由調用方保證不並發。
/// </summary>
public sealed class Cylinder
{
    private const string Remedy = "檢查氣壓、節流閥、感應器位置和接線";

    private readonly CylinderConfig _config;
    private readonly TimeProvider _time;

    public Cylinder(CylinderConfig config, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Name);
        ArgumentNullException.ThrowIfNull(config.ExtendValve);
        if (config.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"氣缸 {config.Name} 的動作超時未配置", nameof(config));
        }

        if (config.StableTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"氣缸 {config.Name} 的保持時間不能為負", nameof(config));
        }

        if ((config.ExtendedSensor == null || config.RetractedSensor == null) && config.NoSensorDelay is null)
        {
            // 缺感應器又沒配延時，默認「下發即到位」等於不等
            throw new ArgumentException($"氣缸 {config.Name} 缺少到位感應器，必須配置 NoSensorDelay", nameof(config));
        }

        if (config.NoSensorDelay is { } d && (d <= TimeSpan.Zero || d >= config.Timeout))
        {
            throw new ArgumentException($"氣缸 {config.Name} 的 NoSensorDelay 必須大於 0 並小於動作超時", nameof(config));
        }

        _config = config;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name => _config.Name;

    public Task ExtendAsync(CancellationToken cancellationToken = default) =>
        ActAsync(extend: true, cancellationToken);

    public Task RetractAsync(CancellationToken cancellationToken = default) =>
        ActAsync(extend: false, cancellationToken);

    /// <summary>按感應器判斷的狀態，不拋異常，給界面和互鎖用。</summary>
    public CylinderStatus GetStatus()
    {
        var extended = _config.ExtendedSensor?.GetReading();
        var retracted = _config.RetractedSensor?.GetReading();
        if ((extended?.FaultReason ?? retracted?.FaultReason) is { } fault)
        {
            return new(CylinderState.Fault, fault);
        }

        return (extended?.Value, retracted?.Value) switch
        {
            (true, true) => new(CylinderState.Fault, "伸出到位和縮回到位同時有效"),
            (true, _) => new(CylinderState.Extended),
            (_, true) => new(CylinderState.Retracted),
            (false, false) => new(CylinderState.Moving),
            _ => new(CylinderState.Unknown),
        };
    }

    private async Task ActAsync(bool extend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var direction = extend ? "伸出" : "縮回";
        var target = extend ? _config.ExtendedSensor : _config.RetractedSensor;
        var opposite = extend ? _config.RetractedSensor : _config.ExtendedSensor;
        if (_config.ExtendedSensor != null && _config.RetractedSensor != null &&
            _config.ExtendedSensor.Read() && _config.RetractedSensor.Read())
        {
            throw new CylinderFaultException(Name, $"伸出到位和縮回到位同時有效，位置不可知，拒絕{direction}；{Remedy}");
        }

        var start = _time.GetTimestamp();
        await WriteValvesAsync(extend, cancellationToken).ConfigureAwait(false);

        // 先等另一端到位消失（氣缸確實動了），再等目標到位：目標感應器粘連時不會在原地「到位」
        if (opposite != null)
        {
            await WaitSensorAsync(opposite, false, start, TimeSpan.Zero, direction, cancellationToken)
                .ConfigureAwait(false);
        }

        if (target == null)
        {
            var remaining = _config.NoSensorDelay!.Value - _time.GetElapsedTime(start);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await WaitSensorAsync(target, true, start, _config.StableTime, direction, cancellationToken)
            .ConfigureAwait(false);
        if (opposite?.Read() == true)
        {
            throw new CylinderFaultException(Name, $"{direction}到位後另一端到位又有效，兩個到位同時有效；{Remedy}");
        }
    }

    private async Task WriteValvesAsync(bool extend, CancellationToken cancellationToken)
    {
        if (_config.RetractValve is not { } retractValve)
        {
            await _config.ExtendValve.SetAsync(extend, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 先關反方向的閥：雙電控兩個閥同時打開，氣缸停在中間位置，行為取決於閥型
        var (close, open) = extend ? (retractValve, _config.ExtendValve) : (_config.ExtendValve, retractValve);
        await close.SetAsync(false, cancellationToken).ConfigureAwait(false);
        await open.SetAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitSensorAsync(IDigitalInput sensor, bool expected, long start, TimeSpan stableTime,
        string direction, CancellationToken cancellationToken)
    {
        var remaining = _config.Timeout - _time.GetElapsedTime(start);
        try
        {
            if (remaining <= TimeSpan.Zero)
            {
                throw new IoTimeoutException(sensor.Name, expected, null, _config.Timeout);
            }

            await sensor.WaitAsync(expected, remaining, stableTime, cancellationToken).ConfigureAwait(false);
        }
        catch (IoTimeoutException ex)
        {
            var what = expected ? $"{sensor.Name} 未有效" : $"{sensor.Name} 未消失（氣缸沒有離開原位）";
            throw new CylinderFaultException(Name,
                $"{direction}超時：{_config.Timeout.TotalMilliseconds:0} ms 內 {what}（{ex.Reason}）；{Remedy}");
        }
    }
}
