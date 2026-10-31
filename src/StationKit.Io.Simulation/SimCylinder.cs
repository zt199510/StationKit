using StationKit.Abstractions.Io;

namespace StationKit.Io.Simulation;

/// <summary>
/// 模擬氣缸：掛在 <see cref="SimIoBoard"/> 上，閥輸出變化時開始動作——原位感應器立即消失，
/// 經過 <see cref="TravelTime"/> 後目標感應器有效。初始位置為縮回。
/// <para>
/// 配置與真實氣缸用同一組 <see cref="IoPointConfig"/>，有效電平的換算只有一處。
/// 雙電控兩個閥都關或都開時保持當前動作（二位五通雙電控閥有記憶）。
/// </para>
/// 故障注入：<see cref="Jammed"/> 卡住不到位；<see cref="StickSensor"/> 感應器粘連。
/// </summary>
public sealed class SimCylinder : IDisposable
{
    private readonly object _sync = new();
    private readonly SimIoBoard _board;
    private readonly IoPointConfig _extendValve;
    private readonly IoPointConfig? _retractValve;
    private readonly IoPointConfig? _extendedSensor;
    private readonly IoPointConfig? _retractedSensor;
    private readonly Dictionary<IoPointConfig, bool> _stuckSensors = [];
    private readonly ITimer _timer;
    private bool _extending;

    public SimCylinder(SimIoBoard board, IoPointConfig extendValve, IoPointConfig? retractValve,
        IoPointConfig? extendedSensor, IoPointConfig? retractedSensor, TimeSpan travelTime,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(extendValve);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(travelTime, TimeSpan.Zero);
        _board = board;
        _extendValve = extendValve;
        _retractValve = retractValve;
        _extendedSensor = extendedSensor;
        _retractedSensor = retractedSensor;
        TravelTime = travelTime;
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => Arrive(), null, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        lock (_sync)
        {
            SetSensor(_extendedSensor, false);
            SetSensor(_retractedSensor, true);
        }

        board.OutputWritten += OnOutputWritten;
    }

    public TimeSpan TravelTime { get; }

    /// <summary>故障注入：氣缸卡住（氣壓不足、機構卡死），動作開始後永遠到不了位。</summary>
    public bool Jammed { get; set; }

    /// <summary>故障注入：感應器粘連在 <paramref name="active"/>，不再跟隨氣缸位置。</summary>
    public void StickSensor(IoPointConfig sensor, bool active)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        lock (_sync)
        {
            _stuckSensors[sensor] = active;
            SetSensor(sensor, active);
        }
    }

    public void Dispose()
    {
        _board.OutputWritten -= OnOutputWritten;
        _timer.Dispose();
    }

    private void OnOutputWritten(ushort bit, bool level)
    {
        if (bit != _extendValve.Bit && bit != _retractValve?.Bit)
        {
            return;
        }

        var extendOn = IsOn(_extendValve);
        bool extend;
        if (_retractValve == null)
        {
            // 單電控：閥斷電彈簧復位
            extend = extendOn;
        }
        else
        {
            var retractOn = IsOn(_retractValve);
            if (extendOn == retractOn)
            {
                return;
            }

            extend = extendOn;
        }

        lock (_sync)
        {
            if (extend == _extending)
            {
                return;
            }

            _extending = extend;
            // 離開原位：兩個感應器都無效，走完行程後目標感應器有效
            SetSensor(_extendedSensor, false);
            SetSensor(_retractedSensor, false);
            _timer.Change(TravelTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void Arrive()
    {
        lock (_sync)
        {
            if (!Jammed)
            {
                SetSensor(_extending ? _extendedSensor : _retractedSensor, true);
            }
        }
    }

    private bool IsOn(IoPointConfig valve) => _board.GetOutputLevel(valve.Bit) == valve.ActiveHigh;

    private void SetSensor(IoPointConfig? sensor, bool active)
    {
        if (sensor == null)
        {
            return;
        }

        if (_stuckSensors.TryGetValue(sensor, out var stuck))
        {
            active = stuck;
        }

        _board.SetInputLevel(sensor.Bit, active == sensor.ActiveHigh);
    }
}
