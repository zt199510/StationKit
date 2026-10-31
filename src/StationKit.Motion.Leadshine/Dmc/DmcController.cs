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

    public DmcController(IDmcNativeApi api, ushort cardNo, string ipAddress, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _rawApi = api;
        _api = new GatedDmcNativeApi(api, Gate);
        CardNo = cardNo;
        _ipAddress = ipAddress;
        _log = log;
        _guard = new LeadshineCallGuard("DMC", cardNo, 0, $"卡{cardNo}", log);
    }

    public ushort CardNo { get; }

    /// <summary>這張卡的 DLL 調用鎖，控制器和它創建的所有軸共用。</summary>
    public LeadshineCardGate Gate { get; } = new();

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
        return totalAxes;
    }

    public void Close() => _api.BoardClose();

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
        return new DmcAxis(_rawApi, options, Gate, timeProvider, _log);
    }
}
