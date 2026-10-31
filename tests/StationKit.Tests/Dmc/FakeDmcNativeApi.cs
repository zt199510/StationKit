using StationKit.Motion.Dmc;

namespace StationKit.Tests.Dmc;

/// <summary>
/// 假 DMC：記錄調用順序，可注入返回碼、IO 狀態、回讀偏差；
/// 運動在若干次 dmc_check_done 輪詢後到位，被 dmc_stop 打斷時停在原地。
/// </summary>
internal sealed class FakeDmcNativeApi : IDmcNativeApi
{
    private readonly object _sync = new();
    private readonly List<string> _calls = [];
    private int _pollsUntilDone;
    private double _target;
    private bool _homing;
    private double _equiv;
    private bool _emgEnabled;
    private ushort _emgLogic;
    private DmcSoftLimit _softLimit;
    private DmcHomeProfile _homeProfile;

    /// <summary>每次運動需要多少次 dmc_check_done 輪詢才到位。</summary>
    public int PollsPerMove { get; set; } = 3;

    public double Position { get; set; }

    public uint IoStatus { get; set; }

    public ushort AxisErrorCode { get; set; }

    public Dictionary<string, short> ReturnCodes { get; } = [];

    public Func<DmcHomeProfile, DmcHomeProfile>? HomeProfileReadback { get; set; }

    public Func<ushort, ushort>? EmergencyLogicReadback { get; set; }

    public double? EquivReadback { get; set; }

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

    public DmcStopMode? LastStopMode { get; private set; }

    public short BoardClose() => Record("dmc_board_close");

    public short BoardInit() => Record("dmc_board_init");

    public short BoardInitEthernet(ushort cardNo, string ipAddress) => Record("dmc_board_init_eth");

    public short GetTotalAxes(ushort cardNo, out uint totalAxes)
    {
        totalAxes = 4;
        return Record("nmc_get_total_axes");
    }

    public short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value)
    {
        _equiv = value;
        return Record("dmc_set_equiv");
    }

    public short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value)
    {
        value = EquivReadback ?? _equiv;
        return Record("dmc_get_equiv");
    }

    public short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity)
    {
        LastProfile = (minVelocity, maxVelocity, accelerationTime, decelerationTime, stopVelocity);
        return Record("dmc_set_profile_unit");
    }

    public short SetSProfile(ushort cardNo, ushort axisNo, double seconds) => Record("dmc_set_s_profile");

    public short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic)
    {
        (_emgEnabled, _emgLogic) = (enabled, logic);
        return Record("dmc_set_emg_mode");
    }

    public short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic)
    {
        enabled = _emgEnabled;
        logic = EmergencyLogicReadback?.Invoke(_emgLogic) ?? _emgLogic;
        return Record("dmc_get_emg_mode");
    }

    public short SetSoftLimit(ushort cardNo, ushort axisNo, DmcSoftLimit softLimit)
    {
        _softLimit = softLimit;
        return Record("dmc_set_softlimit_unit");
    }

    public short GetSoftLimit(ushort cardNo, ushort axisNo, out DmcSoftLimit softLimit)
    {
        softLimit = _softLimit;
        return Record("dmc_get_softlimit_unit");
    }

    public short EnableAxis(ushort cardNo, ushort axisNo) => Record("nmc_set_axis_enable");

    public short DisableAxis(ushort cardNo, ushort axisNo) => Record("nmc_set_axis_disable");

    public short ClearAxisErrorCode(ushort cardNo, ushort axisNo)
    {
        IoStatus &= ~1u;
        return Record("nmc_clear_axis_errcode");
    }

    public short GetAxisErrorCode(ushort cardNo, ushort axisNo, out ushort errorCode)
    {
        errorCode = AxisErrorCode;
        return 0;
    }

    public short MoveAbsolute(ushort cardNo, ushort axisNo, double position)
    {
        var result = Record("dmc_pmove_unit");
        if (result == 0)
        {
            lock (_sync)
            {
                _target = position;
                _pollsUntilDone = PollsPerMove;
            }
        }

        return result;
    }

    public short CheckDone(ushort cardNo, ushort axisNo)
    {
        lock (_sync)
        {
            if (_pollsUntilDone > 0)
            {
                _pollsUntilDone--;
                if (_pollsUntilDone == 0)
                {
                    Position = _homing ? 0 : _target;
                }

                return 0;
            }

            return 1;
        }
    }

    public short StopAxis(ushort cardNo, ushort axisNo, DmcStopMode mode)
    {
        lock (_sync)
        {
            _pollsUntilDone = 0;
            LastStopMode = mode;
        }

        return Record("dmc_stop");
    }

    public short EmergencyStop(ushort cardNo) => Record("dmc_emg_stop");

    public short ReadPosition(ushort cardNo, ushort axisNo, out double position)
    {
        lock (_sync)
        {
            position = Position;
        }

        return 0;
    }

    public short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status)
    {
        status = IoStatus;
        return 0;
    }

    public short SetHomeProfile(ushort cardNo, ushort axisNo, DmcHomeProfile profile)
    {
        _homeProfile = profile;
        return Record("nmc_set_home_profile");
    }

    public short GetHomeProfile(ushort cardNo, ushort axisNo, out DmcHomeProfile profile)
    {
        profile = HomeProfileReadback?.Invoke(_homeProfile) ?? _homeProfile;
        return Record("nmc_get_home_profile");
    }

    public short HomeMove(ushort cardNo, ushort axisNo)
    {
        var result = Record("dmc_home_move");
        if (result == 0)
        {
            lock (_sync)
            {
                _homing = true;
                _pollsUntilDone = PollsPerMove;
            }
        }

        return result;
    }

    public short GetHomeResult(ushort cardNo, ushort axisNo, out ushort state)
    {
        lock (_sync)
        {
            state = _homing && _pollsUntilDone == 0 ? (ushort)1 : (ushort)0;
            if (state == 1)
            {
                _homing = false;
            }
        }

        return 0;
    }

    private short Record(string apiName)
    {
        lock (_sync)
        {
            _calls.Add(apiName);
        }

        return ReturnCodes.TryGetValue(apiName, out var code) ? code : (short)0;
    }
}
