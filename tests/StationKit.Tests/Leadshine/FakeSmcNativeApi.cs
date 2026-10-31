using StationKit.Motion.Leadshine.Smc;

namespace StationKit.Tests.Leadshine;

/// <summary>假 SMC：公共部分之外，模擬控制器連接、SEVON 輸出、ALM/EL 配置、回零參數和報警復位輸出。</summary>
internal sealed class FakeSmcNativeApi() : FakeLeadshineMotionApi("smc"), ISmcNativeApi
{
    private readonly Dictionary<string, object> _settings = [];
    private ushort _sevon;

    /// <summary>SEVON 回讀值改寫，模擬輸出口沒有翻轉。</summary>
    public Func<ushort, ushort>? ServoOnReadback { get; set; }

    /// <summary>某項參數的回讀值改寫，鍵為 get 函數名，例如 smc_get_alm_mode。</summary>
    public Dictionary<string, Func<object, object>> Readback { get; } = [];

    /// <summary>報警復位脈衝結束（撤銷有效電平）時是否清除 ALM，模擬驅動器響應復位。</summary>
    public bool ResetClearsAlarm { get; set; } = true;

    public List<(string Api, ushort Index, ushort Level)> ResetWrites { get; } = [];

    public ushort ServoOn => _sevon;

    public (ushort Type, string Ip, uint Baud) LastInit { get; private set; }

    public short BoardInit(ushort connectNo, ushort connectType, string connectString, uint baud)
    {
        LastInit = (connectType, connectString, baud);
        return Record("smc_board_init");
    }

    public short BoardClose(ushort connectNo) => Record("smc_board_close");

    public short WriteServoOn(ushort connectNo, ushort axisNo, ushort level)
    {
        _sevon = level;
        return Record("smc_write_sevon_pin");
    }

    public short ReadServoOn(ushort connectNo, ushort axisNo)
    {
        Record("smc_read_sevon_pin");
        return (short)(ServoOnReadback?.Invoke(_sevon) ?? _sevon);
    }

    public short SetAlarmMode(ushort connectNo, ushort axisNo, SmcSignalMode mode) => Set("alm_mode", mode);

    public short GetAlarmMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode) => Get("alm_mode", out mode);

    public short SetLimitMode(ushort connectNo, ushort axisNo, SmcSignalMode mode) => Set("el_mode", mode);

    public short GetLimitMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode) => Get("el_mode", out mode);

    public short SetHomeMode(ushort connectNo, ushort axisNo, SmcHomeMode mode) => Set("homemode", mode);

    public short GetHomeMode(ushort connectNo, ushort axisNo, out SmcHomeMode mode) => Get("homemode", out mode);

    public short SetHomeSpeed(ushort connectNo, ushort axisNo, SmcHomeSpeed speed) => Set("home_profile_unit", speed);

    public short GetHomeSpeed(ushort connectNo, ushort axisNo, out SmcHomeSpeed speed) =>
        Get("home_profile_unit", out speed);

    public short SetHomePinLogic(ushort connectNo, ushort axisNo, SmcHomePinLogic logic) =>
        Set("home_pin_logic", logic);

    public short GetHomePinLogic(ushort connectNo, ushort axisNo, out SmcHomePinLogic logic) =>
        Get("home_pin_logic", out logic);

    public short SetHomePosition(ushort connectNo, ushort axisNo, SmcHomePosition position) =>
        Set("home_position_unit", position);

    public short GetHomePosition(ushort connectNo, ushort axisNo, out SmcHomePosition position) =>
        Get("home_position_unit", out position);

    public short HomeMove(ushort connectNo, ushort axisNo)
    {
        var result = Record("smc_home_move");
        if (result == 0)
        {
            StartHoming();
        }

        return result;
    }

    public short GetHomeResult(ushort connectNo, ushort axisNo, out ushort state)
    {
        state = CurrentHomeResult();
        return 0;
    }

    public short WriteErcPin(ushort connectNo, ushort axisNo, ushort level) =>
        WriteReset("smc_write_erc_pin", axisNo, level);

    public short WriteOutputBit(ushort connectNo, ushort bitNo, ushort level) =>
        WriteReset("smc_write_outbit", bitNo, level);

    private short WriteReset(string apiName, ushort index, ushort level)
    {
        // 有效電平之後再寫一次（撤銷）即視為一個完整的復位脈衝
        if (ResetWrites.Count > 0 && ResetClearsAlarm)
        {
            IoStatus &= ~1u;
        }

        ResetWrites.Add((apiName, index, level));
        return Record(apiName);
    }

    private short Set<T>(string function, T value)
        where T : notnull
    {
        _settings[function] = value;
        return Record("smc_set_" + function);
    }

    private short Get<T>(string function, out T value)
    {
        var apiName = "smc_get_" + function;
        var stored = _settings.TryGetValue(function, out var raw) ? raw : default(T)!;
        value = (T)(Readback.TryGetValue(apiName, out var change) ? change(stored) : stored);
        return Record(apiName);
    }
}
