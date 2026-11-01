namespace StationKit.Motion.Leadshine.Smc;

/// <summary>
/// 一台以太網連接的 SMC 控制器：負責連接、斷開和整機急停。
/// </summary>
public sealed class SmcController
{
    private const ushort EthernetConnection = 2; // smc_board_init type：2 以太網

    private readonly ISmcNativeApi _rawApi;
    private readonly GatedSmcNativeApi _api;
    private readonly string _ipAddress;
    private readonly uint _baud;
    private readonly Action<string>? _log;
    private readonly LeadshineCallGuard _guard;
    private readonly TimeSpan _pollPeriod;
    private readonly List<(ushort Bit, string Owner)> _reservedOutputs = [];

    /// <summary>默認輪詢週期，上機後按日誌裡的實際耗時調整。</summary>
    public static readonly TimeSpan DefaultPollPeriod = TimeSpan.FromMilliseconds(5);

    /// <param name="pollPeriod">集中輪詢週期，默認 <see cref="DefaultPollPeriod"/>。</param>
    /// <param name="staleTimeout">狀態過期時間，默認 <see cref="LeadshineStatusPoller.DefaultStaleTimeout"/>，至少為輪詢週期的 3 倍。</param>
    public SmcController(ISmcNativeApi api, ushort connectNo, string ipAddress, uint baud = 0,
        Action<string>? log = null, TimeSpan? pollPeriod = null, TimeSpan? staleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _rawApi = api;
        Poller = new LeadshineStatusPoller(api, connectNo, "SMC", log: log, staleTimeout: staleTimeout);
        _pollPeriod = pollPeriod ?? DefaultPollPeriod;
        // 配置錯誤在構造時就報出來，不要等到 Open
        Poller.ValidatePeriod(_pollPeriod);
        _api = new GatedSmcNativeApi(api, Poller.Gate);
        ConnectNo = connectNo;
        _ipAddress = ipAddress;
        _baud = baud;
        _log = log;
        _guard = new LeadshineCallGuard("SMC", connectNo, 0, $"控制器{connectNo}", log);
    }

    public ushort ConnectNo { get; }

    /// <summary>這台控制器的集中輪詢器，控制器和它創建的所有軸共用（包括卡鎖）。<see cref="Open"/> 成功後啟動。</summary>
    public LeadshineStatusPoller Poller { get; }

    /// <summary>本卡的通用 IO；調用 <see cref="CreateIoBoard"/> 之前為 null。</summary>
    public LeadshineIoBoard? IoBoard { get; private set; }

    /// <summary>
    /// 創建本卡的通用 IO，每卡只能創建一次。端口數決定每輪輪詢讀取多少個端口（每個 32 位），
    /// 只配置實際用到的，以太網卡上每多一個端口每輪多一次調用。
    /// </summary>
    public LeadshineIoBoard CreateIoBoard(ushort inputPorts, ushort outputPorts, string? name = null)
    {
        if (IoBoard != null)
        {
            throw new InvalidOperationException($"SMC 控制器{ConnectNo} 的 IO 已經創建過");
        }

        IoBoard = new LeadshineIoBoard(_rawApi, Poller, inputPorts, outputPorts, name, _log);
        foreach (var (bit, owner) in _reservedOutputs)
        {
            IoBoard.ReserveOutput(bit, owner);
        }

        return IoBoard;
    }

    /// <summary>連接控制器。沿用現場驗證過的順序：先關閉同號連接，再初始化。</summary>
    public void Open()
    {
        _api.BoardClose(ConnectNo);
        _guard.EnsureSuccess("smc_board_init", _api.BoardInit(ConnectNo, EthernetConnection, _ipAddress, _baud),
            $"type={EthernetConnection}, ip={_ipAddress}", "Connect");
        Poller.Start(_pollPeriod);
    }

    /// <summary>先停輪詢再斷開：斷開後輪詢讀到的都是錯誤碼。</summary>
    public void Close()
    {
        Poller.Stop();
        _api.BoardClose(ConnectNo);
    }

    /// <summary>整機急停（smc_emg_stop）。調用失敗時拋出異常，由上層報警，不靜默吞掉。</summary>
    public void EmergencyStop() =>
        _guard.EnsureSuccess("smc_emg_stop", _api.EmergencyStop(ConnectNo), "all-axes-stopped", "EmergencyStop");

    public SmcAxis CreateAxis(SmcAxisOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CardNo != ConnectNo)
        {
            throw new ArgumentException($"軸 {options.Name} 的連接號 {options.CardNo} 與控制器 {ConnectNo} 不一致");
        }

        if (options.AlarmReset is { Source: SmcAlarmResetSource.OutputBit } reset)
        {
            // 報警復位佔用的輸出位登記到 IO，防止又被配置成閥之類的輸出（docs/design/digital-io.md 4.4）
            ReserveOutput(reset.OutputBit, $"{options.Name} 報警復位");
        }

        // 傳原始 SDK 和同一把卡鎖，由軸自己包裝，避免包兩層
        return new SmcAxis(_rawApi, options, Poller, timeProvider, _log);
    }

    /// <summary>IO 還沒創建時先記下，創建 IO 時一併登記；兩根軸用同一個復位位同樣拒絕。</summary>
    private void ReserveOutput(ushort bit, string owner)
    {
        if (IoBoard != null)
        {
            IoBoard.ReserveOutput(bit, owner);
            return;
        }

        if (_reservedOutputs.FirstOrDefault(r => r.Bit == bit) is { Owner: { } existing })
        {
            throw new InvalidOperationException($"控制器{ConnectNo} 輸出位 {bit} 已被「{existing}」使用，不能再分配給「{owner}」");
        }

        _reservedOutputs.Add((bit, owner));
    }
}
