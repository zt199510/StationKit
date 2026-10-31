using StationKit.Abstractions.Io;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 雷賽卡上的通用 IO。讀取只看輪詢器的 IO 快照，不調 DLL；寫輸出經過卡鎖，寫完按週期號回讀核對（由 <see cref="IoBoard"/> 實現）。
/// <list type="bullet">
/// <item>卡故障、快照過期、尚無數據時，讀取拋異常、寫入在調用 DLL 之前被拒絕，與本卡的軸規則一致。</item>
/// <item>卡故障或程序退出時，輸出口保持最後的狀態，軟件寫不進去：危險執行機構必須有硬件斷開（docs/design/digital-io.md 4.3）。</item>
/// </list>
/// 由 <c>DmcController.CreateIoBoard</c> / <c>SmcController.CreateIoBoard</c> 創建，與本卡的軸共用輪詢器和卡鎖。
/// </summary>
public sealed class LeadshineIoBoard : IoBoard
{
    private readonly ILeadshineMotionApi _api;
    private readonly LeadshineStatusPoller _poller;
    private readonly ushort _inputPorts;
    private readonly ushort _outputPorts;
    private readonly Action<string>? _log;
    private readonly string _family;

    internal LeadshineIoBoard(ILeadshineMotionApi api, LeadshineStatusPoller poller, string family, string name,
        ushort inputPorts, ushort outputPorts, Action<string>? log)
        : base(name, poller.Time)
    {
        if (inputPorts == 0 && outputPorts == 0)
        {
            throw new ArgumentException($"{name} 的輸入和輸出端口數不能都為 0");
        }

        _api = api;
        _poller = poller;
        _family = family;
        _inputPorts = inputPorts;
        _outputPorts = outputPorts;
        _log = log;
        poller.ConfigureIo(inputPorts, outputPorts);
    }

    protected override long CurrentCycle => _poller.CurrentCycle;

    protected override IoSample Sample(IoKind kind, ushort bit)
    {
        if (_poller.Fault is { } fault)
        {
            return new IoSample(0, false, $"{fault}，本卡 IO 禁止操作，排查後需復位輪詢器");
        }

        if (_poller.GetIoSnapshot() is not { } snapshot)
        {
            // 輪詢剛啟動還沒讀完第一輪：不算故障，等待方繼續等；輪詢沒啟動則永遠等不到，直接報出來
            return _poller.IsRunning ? IoSample.NoData : new IoSample(0, false, $"{Name} 尚無 IO 數據（輪詢未啟動）");
        }

        var age = _poller.Time.GetElapsedTime(snapshot.Timestamp);
        if (age > _poller.StaleTimeout)
        {
            return new IoSample(snapshot.Cycle, false,
                $"{Name} IO 數據過期：{age.TotalMilliseconds:0} ms 未更新（上限 {_poller.StaleTimeout.TotalMilliseconds:0} ms），輪詢可能已停止");
        }

        return new IoSample(snapshot.Cycle, snapshot.Level(kind, bit));
    }

    protected override async Task WaitForCycleAsync(long afterCycle, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await _poller.WaitForCycleAsync(afterCycle, timeout, cancellationToken).ConfigureAwait(false);

    protected override void WriteOutput(ushort bit, bool level)
    {
        var value = level ? (ushort)1 : (ushort)0;
        short result;
        lock (_poller.Gate.Sync)
        {
            // 基類已檢查過故障；持鎖後再查一次，關掉「檢查之後、下發之前卡進入故障」的窗口
            if (_poller.Fault is { } fault)
            {
                throw new IoFaultException($"{Name} 輸出位 {bit}", $"拒絕寫入：{fault}");
            }

            result = _api.WriteOutputBit(_poller.CardNo, bit, value);
        }

        if (result != 0)
        {
            _log?.Invoke($"[報警] {_family} IO：CardId={_poller.CardNo}, Bit={bit}, Stage=WriteOutput, " +
                         $"write_outbit 返回 {result}，期望 level={value}");
            throw new IoFaultException($"{Name} 輸出位 {bit}", $"write_outbit 返回錯誤碼 {result}");
        }
    }

    protected override void ValidateAddress(IoKind kind, ushort bit)
    {
        var ports = kind == IoKind.Input ? _inputPorts : _outputPorts;
        if (bit / LeadshineIoSnapshot.BitsPerPort >= ports)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit,
                $"{Name} 只輪詢了 {ports} 個{(kind == IoKind.Input ? "輸入" : "輸出")}端口（每個 {LeadshineIoSnapshot.BitsPerPort} 位）");
        }
    }
}
