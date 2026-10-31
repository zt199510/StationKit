using StationKit.Abstractions.Motion;
using StationKit.Motion.Leadshine;

namespace StationKit.Tests.Leadshine;

/// <summary>
/// 假雷賽卡的公共部分：記錄調用順序，可注入返回碼、IO 狀態、回讀偏差；
/// 運動在若干次 check_done 輪詢後到位，被 stop 打斷時停在原地。
/// 調用名帶系列前綴（dmc_ / smc_），和真實函數名一致。
/// </summary>
internal abstract class FakeLeadshineMotionApi(string prefix) : ILeadshineMotionApi
{
    private readonly object _sync = new();
    private readonly List<string> _calls = [];
    private int _pollsUntilDone;
    private double? _arrival;
    private bool _homing;
    private double _equiv;
    private bool _emgEnabled;
    private ushort _emgLogic;
    private LeadshineSoftLimit _softLimit;
    private LeadshineInpMode _inp;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _reads = new();
    private int _concurrent;
    private int _maxConcurrent;

    /// <summary>每次運動需要多少次 check_done 輪詢才到位。</summary>
    public int PollsPerMove { get; set; } = 3;

    /// <summary>stop 之後還要幾次 check_done 輪詢才真正停下（模擬減速段）。</summary>
    public int PollsAfterStop { get; set; }

    public JogDirection? LastVmoveDirection { get; private set; }

    public double Position { get; set; }

    public uint IoStatus { get; set; }

    public Dictionary<string, short> ReturnCodes { get; } = [];

    public Func<ushort, ushort>? EmergencyLogicReadback { get; set; }

    public double? EquivReadback { get; set; }

    public Func<LeadshineInpMode, LeadshineInpMode>? InpReadback { get; set; }

    /// <summary>編碼器 = 指令位置 + 偏差。上電時編碼器計數和指令位置不一致，用它模擬。</summary>
    public double EncoderOffset { get; set; }

    /// <summary>模擬 set_encoder_unit 調用成功但沒有生效。</summary>
    public bool EncoderWriteIgnored { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_sync)
            {
                return [.. _calls];
            }
        }
    }

    public (double Min, double Max, double Tacc, double Tdec, double Stop) LastProfile { get; private set; }

    public LeadshineStopMode? LastStopMode { get; private set; }

    public short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value)
    {
        _equiv = value;
        return Record($"{prefix}_set_equiv");
    }

    public short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value)
    {
        value = EquivReadback ?? _equiv;
        return Record($"{prefix}_get_equiv");
    }

    public short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity)
    {
        LastProfile = (minVelocity, maxVelocity, accelerationTime, decelerationTime, stopVelocity);
        return Record($"{prefix}_set_profile_unit");
    }

    public short SetSProfile(ushort cardNo, ushort axisNo, double seconds) => Record($"{prefix}_set_s_profile");

    public short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic)
    {
        (_emgEnabled, _emgLogic) = (enabled, logic);
        return Record($"{prefix}_set_emg_mode");
    }

    public short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic)
    {
        enabled = _emgEnabled;
        logic = EmergencyLogicReadback?.Invoke(_emgLogic) ?? _emgLogic;
        return Record($"{prefix}_get_emg_mode");
    }

    public short SetSoftLimit(ushort cardNo, ushort axisNo, LeadshineSoftLimit softLimit)
    {
        _softLimit = softLimit;
        return Record($"{prefix}_set_softlimit_unit");
    }

    public short GetSoftLimit(ushort cardNo, ushort axisNo, out LeadshineSoftLimit softLimit)
    {
        softLimit = _softLimit;
        return Record($"{prefix}_get_softlimit_unit");
    }

    public short SetInpMode(ushort cardNo, ushort axisNo, LeadshineInpMode mode)
    {
        _inp = mode;
        return Record($"{prefix}_set_inp_mode");
    }

    public short GetInpMode(ushort cardNo, ushort axisNo, out LeadshineInpMode mode)
    {
        mode = InpReadback?.Invoke(_inp) ?? _inp;
        return Record($"{prefix}_get_inp_mode");
    }

    public short MoveAbsolute(ushort cardNo, ushort axisNo, double position)
    {
        var result = Record($"{prefix}_pmove_unit");
        if (result == 0)
        {
            lock (_sync)
            {
                _arrival = position;
                _pollsUntilDone = PollsPerMove;
            }
        }

        return result;
    }

    public short MoveContinuous(ushort cardNo, ushort axisNo, JogDirection direction)
    {
        var result = Record($"{prefix}_vmove");
        if (result == 0)
        {
            lock (_sync)
            {
                // 速度模式沒有終點，一直走到 stop
                LastVmoveDirection = direction;
                _arrival = null;
                _pollsUntilDone = int.MaxValue;
            }
        }

        return result;
    }

    public short CheckDone(ushort cardNo, ushort axisNo)
    {
        CountRead($"{prefix}_check_done", axisNo);
        lock (_sync)
        {
            if (_pollsUntilDone > 0)
            {
                _pollsUntilDone--;
                if (_pollsUntilDone == 0 && _arrival is { } arrival)
                {
                    Position = arrival;
                }

                return 0;
            }

            return 1;
        }
    }

    public short StopAxis(ushort cardNo, ushort axisNo, LeadshineStopMode mode)
    {
        lock (_sync)
        {
            // 被停下的運動不會到達原目標
            _arrival = null;
            _pollsUntilDone = Math.Min(_pollsUntilDone, PollsAfterStop);
            LastStopMode = mode;
        }

        return Record($"{prefix}_stop");
    }

    public short EmergencyStop(ushort cardNo) => Record($"{prefix}_emg_stop");

    public short ReadPosition(ushort cardNo, ushort axisNo, out double position)
    {
        CountRead($"{prefix}_get_position_unit", axisNo);
        lock (_sync)
        {
            position = Position;
        }

        return 0;
    }

    /// <summary>模擬 set_position_unit 調用成功但沒有生效。</summary>
    public bool PositionWriteIgnored { get; set; }

    /// <summary>改寫指令位置計數，編碼器讀數不變（只是兩個計數器之一被重設）。</summary>
    public short SetPosition(ushort cardNo, ushort axisNo, double position)
    {
        if (!PositionWriteIgnored)
        {
            lock (_sync)
            {
                EncoderOffset -= position - Position;
                Position = position;
            }
        }

        return Record($"{prefix}_set_position_unit");
    }

    public short ReadFeedbackPosition(ushort cardNo, ushort axisNo, out double position)
    {
        lock (_sync)
        {
            position = Position + EncoderOffset;
        }

        return Record($"{prefix}_get_encoder_unit");
    }

    public short SetFeedbackPosition(ushort cardNo, ushort axisNo, double position)
    {
        if (!EncoderWriteIgnored)
        {
            lock (_sync)
            {
                EncoderOffset = position - Position;
            }
        }

        return Record($"{prefix}_set_encoder_unit");
    }

    public short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status)
    {
        CountRead($"{prefix}_axis_io_status", axisNo);
        status = IoStatus;
        return 0;
    }

    private void CountRead(string apiName, ushort axisNo)
    {
        if (ThrowOnRead?.Invoke(axisNo) == true)
        {
            throw new InvalidOperationException($"模擬通訊中斷：{apiName} axis={axisNo}");
        }

        _reads.AddOrUpdate(apiName, 1, (_, count) => count + 1);
    }

    /// <summary>開始模擬回零：若干次輪詢後位置歸零，回零結果變為 1。</summary>
    protected void StartHoming()
    {
        lock (_sync)
        {
            _homing = true;
            _arrival = 0;
            _pollsUntilDone = PollsPerMove;
        }
    }

    protected ushort TakeHomeResult()
    {
        lock (_sync)
        {
            if (_homing && _pollsUntilDone == 0)
            {
                _homing = false;
                return 1;
            }

            return 0;
        }
    }

    /// <summary>讀取類函數（狀態輪詢用到的）的調用次數，不計入 <see cref="Calls"/>，避免干擾順序斷言。</summary>
    public int ReadCount(string apiName) => _reads.TryGetValue(apiName, out var count) ? count : 0;

    public int TotalReads => _reads.Values.Sum();

    /// <summary>模擬某根軸讀取時 DLL 拋異常（例如通訊中斷）。</summary>
    public Func<ushort, bool>? ThrowOnRead { get; set; }

    /// <summary>同時在 SDK 內部的調用數的最大值，用於驗證卡鎖：大於 1 說明有併發調用。</summary>
    public int MaxConcurrentCalls => _maxConcurrent;

    /// <summary>每次記錄調用時在「SDK 內部」停留的時間，用來放大併發窗口。</summary>
    public TimeSpan CallDuration { get; set; }

    protected short Record(string apiName)
    {
        var inside = Interlocked.Increment(ref _concurrent);
        int max;
        while (inside > (max = _maxConcurrent) && Interlocked.CompareExchange(ref _maxConcurrent, inside, max) != max)
        {
        }

        if (CallDuration > TimeSpan.Zero)
        {
            Thread.Sleep(CallDuration);
        }

        Interlocked.Decrement(ref _concurrent);
        lock (_sync)
        {
            _calls.Add(apiName);
        }

        return ReturnCodes.TryGetValue(apiName, out var code) ? code : (short)0;
    }
}
