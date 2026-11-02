using System.Diagnostics;
using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;
using StationKit.Alarms;
using StationKit.Demo;
using StationKit.Io.Simulation;
using StationKit.Machines;
using StationKit.Motion.Simulation;
using StationKit.Safety;
using StationKit.Stations;

// 演示：上下兩個工站各有一根 X 軸，共用一台相機（同一時刻只能給一個工站拍照），由 Machine 統籌成一台設備。
// 每個工站回到上料位後用一個氣缸上料；安全門關閉才允許軸運動（互鎖）。
// 加 --fault 參數：下工位拍照時 X 軸伺服報警，演示「整機停下 → 資源保持佔用 → 人工恢復 → 重新初始化」。
// 加 --door 參數：上工位拍照時打開安全門，演示「下一步運動被互鎖拒絕 → 整機停下 → 關門 → 人工恢復」。
// 加 --estop 參數：上工位拍照時按下急停，演示「運行監視發現違反 → 立即停止，鬆開也不自動繼續 → 人工恢復」。
var injectFault = args.Contains("--fault");
var openDoor = args.Contains("--door");
var pressEstop = args.Contains("--estop");
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

// 安全門：低電平有效（常閉觸點，斷線時等同開門）
using var io = new SimIoBoard("IO板");
var doorClosed = new IoPointConfig("安全門關閉", 0, ActiveHigh: false);
var door = io.CreateInput(doorClosed);
var doorInterlock = Interlock.RequireInput("安全門", door, kinds: [MotionKind.Move, MotionKind.Jog]);
// 急停：同樣低電平有效。這裡只是讓軟件知道，真正切斷動力的是硬件回路
var estopReleased = new IoPointConfig("急停未按下", 5, ActiveHigh: false);
var estop = io.CreateInput(estopReleased);
var motionGate = new MachineMotionGate();

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

var alarms = new AlarmCenter(new AlarmCatalog().AddStationAlarms().AddIoAlarms().AddMachineAlarms());
alarms.Raised += (_, a) => Log($"!! 報警 {a.Definition.Code} [{a.Source}] {a.Definition.Message}：{a.Detail}");
var arbiter = new ResourceArbiter();

var upper = new PhotoStation("上工位", new GuardedAxis(upperX, safety, [motionGate, doorInterlock]), upperLoader, arbiter, alarms, Log)
{
    // 第二個循環拍照時打開安全門
    OnCapture = cycle =>
    {
        if (openDoor && cycle == 2)
        {
            Log("!! 安全門被打開");
            io.SetInputLevel(doorClosed.Bit, true);
        }

        if (pressEstop && cycle == 2)
        {
            Log("!! 急停被按下");
            io.SetInputLevel(estopReleased.Bit, true);
        }
    },
};
var lower = new PhotoStation("下工位", new GuardedAxis(lowerX, safety, [motionGate, doorInterlock]), lowerLoader, arbiter, alarms,
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

using var machine = new Machine(new MachineConfig
{
    Name = "整機",
    Stations = [upper, lower],
    Axes = [new AxisSafetyBinding(upperX, upperX, safety), new AxisSafetyBinding(lowerX, lowerX, safety)],
    // 先縮回氣缸再回零：恢復時氣缸可能停在伸出位
    StartupSteps =
    [
        new("上料氣缸縮回", async ct =>
        {
            await upperLoader.RetractAsync(ct);
            await lowerLoader.RetractAsync(ct);
        }),
        new("兩軸回零", ct => MotionGroup.RunAllAsync([upperX.HomeAsync, lowerX.HomeAsync], ct)),
    ],
    Guards =
    [
        new MachineGuard("急停", estop, true, StopMode.Immediate)
        {
            Scope = MachineGuardScope.Initialize | MachineGuardScope.Run | MachineGuardScope.ManualMotion,
        },
    ],
    MotionGate = motionGate,
    OnStationFault = StationFaultPolicy.AbortAll,
    MonitorPeriod = TimeSpan.FromMilliseconds(20),
    StopTimeout = TimeSpan.FromSeconds(2),
    CycleStopTimeout = TimeSpan.FromSeconds(5),
    Resources = arbiter,
}, alarms);
machine.StateChanged += (_, state) => Log($"整機狀態 → {state}");

Log("== 初始化");
await machine.InitializeAsync();

Log("== 雙工位運行 3 個循環");
await machine.RunAsync(cycleLimit: 3);
Report();

if (machine.State == MachineState.Faulted)
{
    Log($"== 首出原因：[{machine.FirstFault?.Source}] {machine.FirstFault?.Description}");
    Log("== 人工恢復：確認現場 → 恢復信號 → 解除相機佔用 → 清除報警 → 復位 → 重新初始化");
    io.SetInputLevel(doorClosed.Bit, false);
    io.SetInputLevel(estopReleased.Bit, false);

    foreach (var resource in arbiter.Snapshot().Where(r => r.UncertainReason != null))
    {
        arbiter.ResetUncertain(resource.Name);
    }

    foreach (var alarm in alarms.Active)
    {
        alarms.Clear(alarm.Definition.Code, alarm.Source);
    }

    await machine.ResetAsync();
    await machine.InitializeAsync();

    Log("== 恢復後運行 1 個循環");
    await machine.RunAsync(cycleLimit: 1);
    Report();
}

void Report()
{
    Log($"== 狀態匯總：整機 {machine.State}");
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
