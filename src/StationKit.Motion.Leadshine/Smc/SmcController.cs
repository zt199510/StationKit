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

    /// <summary>默認輪詢週期，上機後按日誌裡的實際耗時調整。</summary>
    public static readonly TimeSpan DefaultPollPeriod = TimeSpan.FromMilliseconds(5);

    /// <param name="pollPeriod">集中輪詢週期，默認 <see cref="DefaultPollPeriod"/>。</param>
    public SmcController(ISmcNativeApi api, ushort connectNo, string ipAddress, uint baud = 0,
        Action<string>? log = null, TimeSpan? pollPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _rawApi = api;
        Poller = new LeadshineStatusPoller(api, connectNo, "SMC", log: log);
        _pollPeriod = pollPeriod ?? DefaultPollPeriod;
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

        // 傳原始 SDK 和同一把卡鎖，由軸自己包裝，避免包兩層
        return new SmcAxis(_rawApi, options, Poller, timeProvider, _log);
    }
}
