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
    [Fact]
    public void 訂閱者拋異常_Raise不拋_其他訂閱者仍收到_並留下提示()
    {
        var received = new List<string>();
        _alarms.Raised += (_, a) =>
        {
            if (a.Definition.Code == "AX-001")
            {
                throw new InvalidOperationException("界面已關閉");
            }
        };
        _alarms.Raised += (_, a) => received.Add(a.Definition.Code);

        _alarms.Raise("AX-001", "上工位");

        Assert.Equal(["AX-001", AlarmCatalog.SubscriberFaultCode], received);
        var hint = _alarms.Active.Single(a => a.Definition.Code == AlarmCatalog.SubscriberFaultCode);
        Assert.Equal("界面已關閉", hint.Detail);
        Assert.Equal(AlarmSeverity.Warning, hint.Definition.Severity);
    }

    [Fact]
    public void 訂閱者對任何報警都拋異常_不會無限遞歸()
    {
        var calls = 0;
        _alarms.Raised += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("一直失敗");
        };

        _alarms.Raise("AX-001", "上工位");

        Assert.Equal(2, calls);
        Assert.Equal(2, _alarms.Active.Count);
    }

    [Fact]
    public void 清除時訂閱者拋異常_Clear不拋且報警已清除()
    {
        _alarms.Cleared += (_, _) => throw new InvalidOperationException("日誌寫入失敗");
        _alarms.Raise("AX-001", "上工位");

        Assert.True(_alarms.Clear("AX-001", "上工位"));

        Assert.DoesNotContain(_alarms.Active, a => a.Definition.Code == "AX-001");
        Assert.Contains(_alarms.Active, a => a.Definition.Code == AlarmCatalog.SubscriberFaultCode);
    }

    [Fact]
    public void 按來源查詢和清除_包含下一層_不包含名稱相近的來源()
    {
        _alarms.Raise("AX-001", "上工位/善後");
        _alarms.Raise("AX-001", "上工位2");

        Assert.True(_alarms.HasBlockingAlarm("上工位"));

        _alarms.ClearSource("上工位");

        Assert.False(_alarms.HasBlockingAlarm("上工位"));
        Assert.Equal("上工位2", Assert.Single(_alarms.Active).Source);
    }
}
