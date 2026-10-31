using StationKit.Alarms;

namespace StationKit.Stations;

public static class StationAlarms
{
    public const string CycleFault = "STN-001";
    public const string ResourceTimeout = "RES-001";

    public static AlarmCatalog AddStationAlarms(this AlarmCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog
            .Add(new(CycleFault, AlarmSeverity.Error, "工站循環異常",
                "查看報警詳情排除故障；確認共享區域無干涉後，復位不確定的資源，再清除報警並復位工站"))
            .Add(new(ResourceTimeout, AlarmSeverity.Error, "等待共享資源超時",
                "查看佔用者是否卡住；若資源處於不確定狀態，需人工確認現場後復位該資源"));
    }
}
