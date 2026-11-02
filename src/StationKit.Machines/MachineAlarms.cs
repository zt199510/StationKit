using StationKit.Alarms;

namespace StationKit.Machines;

public static class MachineAlarms
{
    public const string GuardViolated = "MCH-001";
    public const string AxisUnhealthy = "MCH-002";
    public const string StopTimeout = "MCH-003";
    public const string InitializationFailed = "MCH-004";
    public const string MonitorFault = "MCH-005";

    public static AlarmCatalog AddMachineAlarms(this AlarmCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog
            .Add(new(GuardViolated, AlarmSeverity.Error, "運行條件不滿足",
                "查看詳情中的信號；恢復條件、確認現場後清除報警，再復位並重新初始化。信號恢復不會自動繼續"))
            .Add(new(AxisUnhealthy, AlarmSeverity.Error, "軸狀態異常",
                "查看詳情中的軸和原因；卡故障先檢查通訊和接線，排除後清除報警，再復位並重新初始化"))
            .Add(new(StopTimeout, AlarmSeverity.Error, "停止超時",
                "查看詳情中沒有停下的工站或軸，確認設備實際狀態；必要時按急停。軟件無法強行結束它"))
            .Add(new(InitializationFailed, AlarmSeverity.Error, "初始化失敗",
                "查看詳情中失敗的步驟並排除；清除報警，再復位並重新初始化"))
            .Add(new(MonitorFault, AlarmSeverity.Error, "運行監視異常",
                "聯繫軟件工程師查看詳情；監視失效期間軟件不會發現條件變化，先確認現場安全"));
    }
}
