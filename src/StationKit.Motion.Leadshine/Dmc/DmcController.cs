namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>
/// 一張以太網連接的 DMC 控制卡：負責連接、斷開和整卡急停。
/// </summary>
public sealed class DmcController
{
    private readonly IDmcNativeApi _rawApi;
    private readonly GatedDmcNativeApi _api;
    private readonly string _ipAddress;
    private readonly Action<string>? _log;
    private readonly LeadshineCallGuard _guard;
    private readonly TimeSpan _pollPeriod;

    /// <summary>默認輪詢週期，上機後按日誌裡的實際耗時調整。</summary>
    public static readonly TimeSpan DefaultPollPeriod = TimeSpan.FromMilliseconds(5);

    /// <param name="pollPeriod">集中輪詢週期，默認 <see cref="DefaultPollPeriod"/>。</param>
    /// <param name="staleTimeout">狀態過期時間，默認 <see cref="LeadshineStatusPoller.DefaultStaleTimeout"/>，至少為輪詢週期的 3 倍。</param>
    public DmcController(IDmcNativeApi api, ushort cardNo, string ipAddress, Action<string>? log = null,
        TimeSpan? pollPeriod = null, TimeSpan? staleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _rawApi = api;
        Poller = new LeadshineStatusPoller(api, cardNo, "DMC", log: log, staleTimeout: staleTimeout);
        _pollPeriod = pollPeriod ?? DefaultPollPeriod;
        // 配置錯誤在構造時就報出來，不要等到 Open
        Poller.ValidatePeriod(_pollPeriod);
        _api = new GatedDmcNativeApi(api, Poller.Gate);
        CardNo = cardNo;
        _ipAddress = ipAddress;
        _log = log;
        _guard = new LeadshineCallGuard("DMC", cardNo, 0, $"卡{cardNo}", log);
    }

    public ushort CardNo { get; }

    /// <summary>這張卡的集中輪詢器，控制器和它創建的所有軸共用（包括卡鎖）。<see cref="Open"/> 成功後啟動。</summary>
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
            throw new InvalidOperationException($"DMC 卡{CardNo} 的 IO 已經創建過");
        }

        IoBoard = new LeadshineIoBoard(_rawApi, Poller, inputPorts, outputPorts, name, _log);
        return IoBoard;
    }

    /// <summary>連接控制卡，返回總軸數。初始化順序沿用現場驗證過的：先關閉，再初始化，再建立以太網連接。</summary>
    public uint Open()
    {
        const string stage = "Connect";
        _api.BoardClose();
        _guard.EnsureSuccess("dmc_board_init", _api.BoardInit(), "0", stage);
        _guard.EnsureSuccess("dmc_board_init_eth", _api.BoardInitEthernet(CardNo, _ipAddress), $"ip={_ipAddress}",
            stage);
        var result = _api.GetTotalAxes(CardNo, out var totalAxes);
        _guard.EnsureSuccess("nmc_get_total_axes", result, "read", stage, $"totalAxes={totalAxes}");
        Poller.Start(_pollPeriod);
        return totalAxes;
    }

    /// <summary>先停輪詢再斷開：斷開後輪詢讀到的都是錯誤碼。</summary>
    public void Close()
    {
        Poller.Stop();
        _api.BoardClose();
    }

    /// <summary>整卡急停（dmc_emg_stop）。調用失敗時拋出異常，由上層報警，不靜默吞掉。</summary>
    public void EmergencyStop() =>
        _guard.EnsureSuccess("dmc_emg_stop", _api.EmergencyStop(CardNo), "all-axes-stopped", "EmergencyStop");

    public DmcAxis CreateAxis(DmcAxisOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CardNo != CardNo)
        {
            throw new ArgumentException($"軸 {options.Name} 的卡號 {options.CardNo} 與控制卡 {CardNo} 不一致");
        }

        // 傳原始 SDK 和同一把卡鎖，由軸自己包裝，避免包兩層
        return new DmcAxis(_rawApi, options, Poller, timeProvider, _log);
    }
}
