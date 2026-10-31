namespace StationKit.Motion.Leadshine.Smc;

/// <summary>
/// 一台以太網連接的 SMC 控制器：負責連接、斷開和整機急停。
/// </summary>
public sealed class SmcController(ISmcNativeApi api, ushort connectNo, string ipAddress, uint baud = 0,
    Action<string>? log = null)
{
    private const ushort EthernetConnection = 2; // smc_board_init type：2 以太網

    private readonly LeadshineCallGuard _guard = new("SMC", connectNo, 0, $"控制器{connectNo}", log);

    public ushort ConnectNo { get; } = connectNo;

    /// <summary>連接控制器。沿用現場驗證過的順序：先關閉同號連接，再初始化。</summary>
    public void Open()
    {
        api.BoardClose(ConnectNo);
        _guard.EnsureSuccess("smc_board_init", api.BoardInit(ConnectNo, EthernetConnection, ipAddress, baud),
            $"type={EthernetConnection}, ip={ipAddress}", "Connect");
    }

    public void Close() => api.BoardClose(ConnectNo);

    /// <summary>整機急停（smc_emg_stop）。調用失敗時拋出異常，由上層報警，不靜默吞掉。</summary>
    public void EmergencyStop() =>
        _guard.EnsureSuccess("smc_emg_stop", api.EmergencyStop(ConnectNo), "all-axes-stopped", "EmergencyStop");

    public SmcAxis CreateAxis(SmcAxisOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CardNo != ConnectNo)
        {
            throw new ArgumentException($"軸 {options.Name} 的連接號 {options.CardNo} 與控制器 {ConnectNo} 不一致");
        }

        return new SmcAxis(api, options, timeProvider, log);
    }
}
