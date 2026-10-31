namespace StationKit.Motion.Dmc;

/// <summary>
/// 一张以太网连接的 DMC 控制卡：负责连接、断开和整卡急停。
/// </summary>
public sealed class DmcController(IDmcNativeApi api, ushort cardNo, string ipAddress, Action<string>? log = null)
{
    private readonly DmcCallGuard _guard = new(cardNo, 0, $"卡{cardNo}", log);

    public ushort CardNo { get; } = cardNo;

    /// <summary>连接控制卡，返回总轴数。初始化顺序沿用现场验证过的：先关闭，再初始化，再建立以太网连接。</summary>
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

    /// <summary>整卡急停（dmc_emg_stop）。调用失败时抛出异常，由上层报警，不静默吞掉。</summary>
    public void EmergencyStop() =>
        _guard.EnsureSuccess("dmc_emg_stop", api.EmergencyStop(CardNo), "all-axes-stopped", "EmergencyStop");

    public DmcAxis CreateAxis(DmcAxisOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CardNo != CardNo)
        {
            throw new ArgumentException($"轴 {options.Name} 的卡号 {options.CardNo} 与控制卡 {CardNo} 不一致");
        }

        return new DmcAxis(api, options, timeProvider, log);
    }
}
