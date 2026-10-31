using System.Diagnostics;
using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Alarms;
using StationKit.Demo;
using StationKit.Io.Simulation;
using StationKit.Motion.Simulation;
using StationKit.Safety;
using StationKit.Stations;

// 演示：上下兩個工站各有一根 X 軸，共用一台相機（同一時刻只能給一個工站拍照）。
// 每個工站回到上料位後用一個氣缸上料；安全門關閉才允許軸運動（互鎖）。
// 加 --fault 參數：下工位拍照時 X 軸伺服報警，演示「資源保持佔用 → 另一工站超時報警 → 人工恢復」。
// 加 --door 參數：上工位拍照時打開安全門，演示「下一步運動被互鎖拒絕 → 關門 → 人工恢復」。
var injectFault = args.Contains("--fault");
var openDoor = args.Contains("--door");
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

// 安全門：低電平有效（常閉觸點，斷線時等同開門）
using var io = new SimIoBoard("IO板");
var doorClosed = new IoPointConfig("安全門關閉", 0, ActiveHigh: false);
var door = io.CreateInput(doorClosed);
var doorInterlock = Interlock.RequireInput("安全門", door, kinds: [MotionKind.Move, MotionKind.Jog]);

Cylinder CreateLoader(string name, ushort valve, ushort extended, ushort retracted, List<IDisposable> sims)
{
    IoPointConfig valveConfig = new($"{name}閥", valve, ActiveHigh: true);
    IoPointConfig extendedConfig = new($"{name}伸出到位", extended, ActiveHigh: true);
    IoPointConfig retractedConfig = new($"{name}縮回到位", retracted, ActiveHigh: true);
    sims.Add(new SimCylinder(io, valveConfig, null, extendedConfig, retractedConfig, TimeSpan.FromMilliseconds(80)));
    return new Cylinder(new CylinderConfig
    {
        Name = name,
        ExtendValve = io.CreateOutput(valveConfig),
        ExtendedSensor = io.CreateInput(extendedConfig),
        RetractedSensor = io.CreateInput(retractedConfig),
        Timeout = TimeSpan.FromMilliseconds(500),
    });
}

var simCylinders = new List<IDisposable>();
var upperLoader = CreateLoader("上工位上料氣缸", 0, 1, 2, simCylinders);
var lowerLoader = CreateLoader("下工位上料氣缸", 1, 3, 4, simCylinders);

var alarms = new AlarmCenter(new AlarmCatalog().AddStationAlarms().AddIoAlarms());
alarms.Raised += (_, a) => Log($"!! 報警 {a.Definition.Code} [{a.Source}] {a.Definition.Message}：{a.Detail}");
var arbiter = new ResourceArbiter();

var upper = new PhotoStation("上工位", new GuardedAxis(upperX, safety, [doorInterlock]), upperLoader, arbiter, alarms, Log)
{
    // 第二個循環拍照時打開安全門
    OnCapture = cycle =>
    {
        if (openDoor && cycle == 2)
        {
            Log("!! 安全門被打開");
            io.SetInputLevel(doorClosed.Bit, true);
        }
    },
};
var lower = new PhotoStation("下工位", new GuardedAxis(lowerX, safety, [doorInterlock]), lowerLoader, arbiter, alarms,
    Log)
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

if (injectFault || openDoor)
{
    if (injectFault)
    {
        Log("== 人工恢復：確認現場 → 復位軸報警並重新使能回零 → 解除相機佔用 → 清除報警 → 復位工站");
        await lowerX.ResetAlarmAsync();
        await lowerX.EnableAsync();
        await lowerX.HomeAsync();
    }
    else
    {
        Log("== 人工恢復：確認現場 → 關閉安全門 → 解除相機佔用 → 清除報警 → 復位工站");
        io.SetInputLevel(doorClosed.Bit, false);
    }

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

simCylinders.ForEach(c => c.Dispose());
