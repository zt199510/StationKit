using Microsoft.Extensions.Time.Testing;
using StationKit.Alarms;

namespace StationKit.Tests;

public class AlarmCenterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly AlarmCenter _alarms;

    public AlarmCenterTests()
    {
        var catalog = new AlarmCatalog()
            .Add(new("AX-001", AlarmSeverity.Error, "伺服報警", "檢查驅動器報警碼"))
            .Add(new("IO-001", AlarmSeverity.Warning, "氣壓偏低", "檢查氣源"));
        _alarms = new AlarmCenter(catalog, _time, historyCapacity: 2);
    }

    [Fact]
    public void 同一來源同一代碼只保留一條()
    {
        _alarms.Raise("AX-001", "上工位", "第一次");
        _alarms.Raise("AX-001", "上工位", "輪詢再次觸發");
        _alarms.Raise("AX-001", "下工位");

        Assert.Equal(2, _alarms.Active.Count);
        Assert.Equal("第一次", _alarms.Active[0].Detail);
    }

    [Fact]
    public void 未登記的代碼不拋異常且按故障處理()
    {
        _alarms.Raise("XX-999", "上工位");

        var alarm = Assert.Single(_alarms.Active);
        Assert.Equal(AlarmCatalog.UnknownCode, alarm.Definition.Code);
        Assert.Contains("XX-999", alarm.Definition.Message);
        Assert.True(_alarms.HasBlockingAlarm());
    }

    [Fact]
    public void 只有故障級報警會阻止生產()
    {
        _alarms.Raise("IO-001", "上工位");
        Assert.False(_alarms.HasBlockingAlarm());

        _alarms.Raise("AX-001", "下工位");
        Assert.True(_alarms.HasBlockingAlarm("下工位"));
        Assert.False(_alarms.HasBlockingAlarm("上工位"));
    }

    [Fact]
    public void 清除後進入歷史並記錄持續時間且歷史有上限()
    {
        _alarms.Raise("AX-001", "A");
        _time.Advance(TimeSpan.FromMinutes(3));
        _alarms.Acknowledge("AX-001", "A");
        Assert.True(_alarms.Active[0].Acknowledged);

        Assert.True(_alarms.Clear("AX-001", "A"));
        Assert.False(_alarms.Clear("AX-001", "A"));

        var record = Assert.Single(_alarms.History);
        Assert.Equal(TimeSpan.FromMinutes(3), record.ClearedAt - record.RaisedAt);

        foreach (var source in new[] { "B", "C" })
        {
            _alarms.Raise("AX-001", source);
            _alarms.Clear("AX-001", source);
        }

        Assert.Equal(["C", "B"], _alarms.History.Select(h => h.Source));
    }
}
