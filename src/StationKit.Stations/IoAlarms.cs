using StationKit.Abstractions.Io;
using StationKit.Alarms;

namespace StationKit.Stations;

public static class IoAlarms
{
    public const string IoFault = "IO-001";
    public const string SignalTimeout = "IO-002";
    public const string CylinderFault = "CYL-001";
    public const string HandshakeFault = "HSK-001";

    public static AlarmCatalog AddIoAlarms(this AlarmCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog
            .Add(new(IoFault, AlarmSeverity.Error, "IO 數據不可信或寫入失敗",
                "檢查控制卡通訊和接線；卡故障排除後手動復位，確認輸出口實際狀態後再繼續"))
            .Add(new(SignalTimeout, AlarmSeverity.Error, "等待信號超時",
                "查看詳情中的信號名，檢查對應感應器、設備動作和接線"))
            .Add(new(CylinderFault, AlarmSeverity.Error, "氣缸動作異常",
                "檢查氣壓、節流閥、感應器位置和接線；兩個到位同時有效時先排查感應器"))
            .Add(new(HandshakeFault, AlarmSeverity.Error, "上下游握手失敗",
                "檢查對方設備狀態和握手信號；請求撤銷失敗時需人工確認對方是否已開始動作"));
    }

    /// <summary>IO 類異常對應的報警代碼；不是 IO 異常時返回 null。</summary>
    public static string? Classify(Exception exception) => exception switch
    {
        CylinderFaultException => CylinderFault,
        HandshakeFaultException => HandshakeFault,
        IoTimeoutException => SignalTimeout,
        IoFaultException => IoFault,
        _ => null,
    };
}
