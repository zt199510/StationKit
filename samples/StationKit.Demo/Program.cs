using System.Diagnostics;
using StationKit.Abstractions.Safety;
using StationKit.Alarms;
using StationKit.Demo;
using StationKit.Motion.Simulation;
using StationKit.Safety;
using StationKit.Stations;

// 演示：上下兩個工站各有一根 X 軸，共用一台相機（同一時刻只能給一個工站拍照）。
// 加 --fault 參數：下工位拍照時 X 軸伺服報警，演示「資源保持佔用 → 另一工站超時報警 → 人工恢復」。
var injectFault = args.Contains("--fault");
var clock = Stopwatch.StartNew();
void Log(string message) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.000}s] {message}");

var safety = new AxisSafetyConfig
{
    SoftLimitMin = -1,
    SoftLimitMax = 250,
    MaxVelocity = 800,
    MaxAcceleration = 8000,
    EmergencyStopEnabled = true,
    EmergencyStopActiveHigh = false,
    ConfirmedOnMachine = true,
};
var upperX = new SimAxis("上工位X") { PositiveLimitSwitch = 260, NegativeLimitSwitch = -5 };
var lowerX = new SimAxis("下工位X") { PositiveLimitSwitch = 260, NegativeLimitSwitch = -5 };

Log("== 安全初始化");
foreach (var line in await SafetyStartup.RunAsync([new(upperX, upperX, safety), new(lowerX, lowerX, safety)]))
{
    Log(line);
}

await Task.WhenAll(upperX.HomeAsync(), lowerX.HomeAsync());
Log("兩軸回零完成");

var alarms = new AlarmCenter(new AlarmCatalog().AddStationAlarms());
alarms.Raised += (_, a) => Log($"!! 報警 {a.Definition.Code} [{a.Source}] {a.Definition.Message}：{a.Detail}");
var arbiter = new ResourceArbiter();

var upper = new PhotoStation("上工位", new GuardedAxis(upperX, safety), arbiter, alarms, Log);
var lower = new PhotoStation("下工位", new GuardedAxis(lowerX, safety), arbiter, alarms, Log)
{
    // 第二個循環拍照時注入伺服報警
    OnCapture = cycle =>
    {
        if (injectFault && cycle == 2)
        {
            lowerX.InjectAlarm("Err 16.0 伺服過載");
        }
    },
};

Log("== 雙工位運行 3 個循環");
await Task.WhenAll(upper.RunAsync(cycleLimit: 3), lower.RunAsync(cycleLimit: 3));
Report();

if (injectFault)
{
    Log("== 人工恢復：確認現場 → 復位軸報警並重新使能回零 → 解除相機佔用 → 清除報警 → 復位工站");
    await lowerX.ResetAlarmAsync();
    await lowerX.EnableAsync();
    await lowerX.HomeAsync();
    arbiter.ResetUncertain(PhotoStation.Camera);
    alarms.ClearSource(upper.Name);
    alarms.ClearSource(lower.Name);
    upper.Reset();
    lower.Reset();

    Log("== 恢復後各再運行 1 個循環");
    await Task.WhenAll(upper.RunAsync(cycleLimit: 1), lower.RunAsync(cycleLimit: 1));
    Report();
}

void Report()
{
    Log("== 狀態匯總");
    foreach (var station in new[] { upper, lower })
    {
        var ct = station.Statistics.Snapshot();
        Log($"{station.Name}：{station.State}，完成 {ct.Count} 個循環，平均 CT {ct.Average.TotalSeconds:0.000}s");
    }

    foreach (var resource in arbiter.Snapshot())
    {
        Log($"資源「{resource.Name}」：佔用者 {resource.Owner ?? "無"}" +
            (resource.UncertainReason is { } reason ? $"，不確定：{reason}" : ""));
    }

    foreach (var alarm in alarms.Active)
    {
        Log($"未清除報警 {alarm.Definition.Code} [{alarm.Source}] → 處理建議：{alarm.Definition.Remedy}");
    }
}
