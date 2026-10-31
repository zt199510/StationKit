namespace StationKit.Motion.Dmc;

/// <summary>
/// 一張以太網連接的 DMC 控制卡：負責連接、斷開和整卡急停。
/// </summary>
public sealed class DmcController(IDmcNativeApi api, ushort cardNo, string ipAddress, Action<string>? log = null)
{
    private readonly DmcCallGuard _guard = new(cardNo, 0, $"卡{cardNo}", log);

    public ushort CardNo { get; } = cardNo;

    /// <summary>連接控制卡，返回總軸數。初始化順序沿用現場驗證過的：先關閉，再初始化，再建立以太網連接。</summary>
    public uint Open()
    {
        const string stage = "Connect";
        api.BoardClose();
        _guard.EnsureSuccess("dmc_board_init", api.BoardInit(), "0", stage);
        _guard.EnsureSuccess("dmc_board_init_eth", api.BoardInitEthernet(CardNo, ipAddress), $"ip={ipAddress}", stage);
        var result = api.GetTotalAxes(CardNo, out var totalAxes);
        _guard.EnsureSuccess("nmc_get_total_axes", result, "read", stage, $"totalAxes={totalAxes}");
        return totalAxes;
    }

    public void Close() => api.BoardClose();

    /// <summary>整卡急停（dmc_emg_stop）。調用失敗時拋出異常，由上層報警，不靜默吞掉。</summary>
    public void EmergencyStop() =>
        _guard.EnsureSuccess("dmc_emg_stop", api.EmergencyStop(CardNo), "all-axes-stopped", "EmergencyStop");

    public DmcAxis CreateAxis(DmcAxisOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CardNo != CardNo)
        {
            throw new ArgumentException($"軸 {options.Name} 的卡號 {options.CardNo} 與控制卡 {CardNo} 不一致");
        }

        return new DmcAxis(api, options, timeProvider, log);
    }
}
