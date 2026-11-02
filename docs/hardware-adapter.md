# 雷賽適配器

`StationKit.Motion.Leadshine` 把雷賽兩類控制器上的軸實現為 `IAxis` 和 `IAxisSafetyPort`：

| 系列 | 軸類型 | 動態庫 | 使能方式 |
|---|---|---|---|
| DMC | EtherCAT 總線軸（`nmc_*`） | `LTDMC.dll` | `nmc_set_axis_enable` |
| SMC | 脈衝軸（脈衝 + 方向，專用 IO 接伺服） | `LTSMC.dll` | 寫 SEVON 電平並回讀 |

兩個系列的運動、停止、回零等待、急停 / 軟限位、狀態讀取完全一樣，安全相關的邏輯只在 `LeadshineAxisBase` 裡寫一份；子類只實現使能、回零參數和報警復位。

> **狀態**：雷賽部分的單元測試基於假 SDK 通過，**尚未在實機上驗證**。上機前請按文末清單逐項確認。

## 使用：DMC 總線軸

```csharp
var api = new DmcNativeApi();
var controller = new DmcController(api, cardNo: 0, ipAddress: "192.168.5.11", log: logger.Info);
controller.Open();

var x = controller.CreateAxis(new DmcAxisOptions
{
    CardNo = 0,
    AxisNo = 0,
    Name = "X",
    PulseEquivalent = 1000,               // 脈衝/mm
    Feedback = LeadshineFeedback.Encoder,           // 必填：反饋位置來源（None / Encoder）
    InPositionSignal = LeadshineInpMode.Disabled,   // 必填：是否用 INP 判定到位
    SettleTime = TimeSpan.FromMilliseconds(50),     // 沒接 INP 時的停穩延時（可選，默認 0）
    HomeProfile = new DmcHomeProfile(Mode: 17, LowVelocity: 5, HighVelocity: 20,
        AccelerationTime: 0.1, DecelerationTime: 0.1, Offset: 0),
});

x.ApplyBaseParameters();                  // 脈衝當量（寫入後回讀）、S 段時間
await SafetyStartup.RunAsync([new(x, x, xSafety)]);   // 急停、軟限位：寫入 → 回讀 → 使能
await x.HomeAsync();

// 絕對值編碼器的總線軸：不觸碰感應器，讀絕對位置後直接走到零點
var r = controller.CreateAxis(new DmcAxisOptions
{
    CardNo = 0,
    AxisNo = 3,
    Name = "R",
    PulseEquivalent = 1000,
    Feedback = LeadshineFeedback.Encoder,                  // 絕對值回零必須有編碼器反饋
    InPositionSignal = LeadshineInpMode.Disabled,
    HomeMethod = LeadshineHomeMethod.AbsoluteEncoder,
    AbsoluteHomeProfile = MotionProfile.Symmetric(50, 500),
    AbsoluteHomeTarget = 0,
});

IAxis guardedX = new GuardedAxis(x, xSafety, interlocks);   // 業務代碼只拿包裝後的軸
```

## 使用：SMC 脈衝軸

```csharp
var api = new SmcNativeApi();
var controller = new SmcController(api, connectNo: 0, ipAddress: "192.168.5.11", log: logger.Info);
controller.Open();                        // 先 smc_board_close，再 smc_board_init（以太網）

var z = controller.CreateAxis(new SmcAxisOptions
{
    CardNo = 0,                           // SMC 上即 ConnectNo
    AxisNo = 2,
    Name = "Z",
    PulseEquivalent = 1000,
    Feedback = LeadshineFeedback.Encoder,           // 回零後自動把編碼器對齊到指令位置
    InPositionSignal = new LeadshineInpMode(Enabled: true, Logic: 0),   // 啟用 INP：脈衝發完後等 INP 有效
    ServoOnLevel = 0,                     // SEVON 為 0 時使能 —— 按接線填，沒有默認值
    AlarmSignal = new SmcSignalMode(Enabled: true, Logic: 0, Action: 0),
    LimitSignal = new SmcSignalMode(Enabled: true, Logic: 0, Action: 0),
    HomeMode = new SmcHomeMode(Direction: 0, VelocityMode: 1, Mode: 2, PositionSource: 0),
    HomeSpeed = new SmcHomeSpeed(LowVelocity: 5, HighVelocity: 20, AccelerationTime: 0.1, DecelerationTime: 0.1),
    HomePinLogic = new SmcHomePinLogic(Logic: 0, Filter: 0),
    AlarmReset = new SmcAlarmReset { Source = SmcAlarmResetSource.ErcPin, ActiveLevel = 0 },   // 可選
});

z.ApplyBaseParameters();                  // 脈衝當量、S 段、INP，以及 ALM / EL 電平（全部寫入後回讀）
await SafetyStartup.RunAsync([new(z, z, zSafety)]);
await z.HomeAsync();                      // homemode / 回零速度 / 原點電平 / 回零位置 四組回讀一致才回零
```

`LTDMC.dll`、`LTSMC.dll` 需要隨程序部署到輸出目錄，本倉庫不分發。

## 狀態輪詢

每張卡一個 `LeadshineStatusPoller`，由控制器創建：`Open()` 成功後啟動，`Close()` 時先停止再斷開。
一個專用線程逐軸讀取 指令位置 / 編碼器 / IO / `check_done`，生成快照；軸的 `GetStatus()` 只讀快照，**不調 DLL**。
同一張卡的所有 DLL 調用（輪詢、運動指令、參數讀寫）經過同一把卡鎖串行。設計說明見 [design/central-polling.md](design/central-polling.md)。

```csharp
var controller = new DmcController(api, cardNo: 0, ipAddress: "192.168.5.11", log: logger.Info,
    pollPeriod: TimeSpan.FromMilliseconds(5),        // 默認 5 ms，上機後按統計日誌調整
    staleTimeout: TimeSpan.FromMilliseconds(100));   // 默認 100 ms，至少為週期的 3 倍
controller.Poller.StatsInterval = TimeSpan.FromSeconds(10);   // 週期統計日誌間隔，Open 之前設置
```

| 行為 | 說明 |
|---|---|
| 下發後的等待 | 只接受下發之後才開始讀取的快照（週期號大於圍欄），舊的「已停止」不會被當成到位 |
| 快照過期 | 超過 `staleTimeout` 未更新：`GetStatus()` 返回 `Alarm`（位置保留最後讀到的值）；等待中的運動先停軸再報故障；不會下發新運動 |
| 讀取失敗 | 任何一根軸讀取拋異常或返回錯誤碼：整卡故障，停止讀取，記一次 `[報警]`；本卡所有軸 `Alarm`，除停止、急停外所有操作被拒絕，等待中的運動立即停軸報故障。排查後調用 `controller.Poller.ResetFault()` 恢復，本卡所有軸需重新使能並回零 |
| 週期統計 | `Cycles / AvgMs / MaxMs / MaxGapMs / Overruns`；有讀取超週期時以 `[警告]` 輸出 |
| 計時精度 | Windows 下運行期間 `timeBeginPeriod(1)`，停止時恢復 |

直接構造軸時（不經過控制器）需要傳入同一張卡的輪詢器：`new DmcAxis(api, options, controller.Poller)`。
原來每根軸的 `PollInterval` 已移除，週期由輪詢器統一決定。

## 通用 IO

卡上的通用輸入輸出併入同一個輪詢器：每輪在軸之後按端口讀取（每個端口 32 位），寫輸出經過同一把卡鎖。
接口語義見 [design/digital-io.md](design/digital-io.md)。

```csharp
var io = controller.CreateIoBoard(inputPorts: 1, outputPorts: 1);     // 只配置用到的端口數，每卡一次
// 消抖：關穩 50 ms 才算關閉，打開立即生效（安全信號只讓「變安全」的方向延時）
var door = io.CreateInput(new IoPointConfig("安全門關閉", Bit: 3, ActiveHigh: false)
{
    ActivateDelay = TimeSpan.FromMilliseconds(50),
});
var clamp = io.CreateOutput(new IoPointConfig("夾爪閥", Bit: 5, ActiveHigh: true));

await clamp.SetAsync(true);                       // 下發後等到回讀一致才返回
await door.WaitAsync(true, TimeSpan.FromSeconds(3));
var guardedX = new GuardedAxis(x, safety, [Interlock.RequireInput("安全門", door)]);
```

| 行為 | 說明 |
|---|---|
| 讀取 | 只讀 IO 快照，不調 DLL；卡故障、過期、輪詢未啟動時拋 `IoFaultException`，不返回 false |
| 寫入 | 持卡鎖調 `write_outbit`，返回碼錯誤拋異常並記 `[報警]`；之後等到下發之後讀到的輸出端口與寫入一致才完成 |
| 讀取失敗 | `read_inport` 沒有錯誤碼，同一次持鎖內讀軸 0 指令位置作探針；拋異常或探針失敗與軸一樣整卡故障 |
| 消抖 | 每輪發佈 IO 快照後在輪詢線程上更新過濾，精度一個輪詢週期；卡故障時立即報出，復位後從無效重新開始。短於一個週期的毛刺看不到，要靠卡上的硬件濾波 |
| 輸出位登記 | 同一位只能有一個所有者；SMC 軸用作報警復位的輸出位自動登記，不能再配置成其他輸出 |
| 卡故障時的輸出 | 輸出口保持最後的狀態，軟件寫不進去。危險執行機構必須在安全回路裡有硬件斷開 |

## 接入整機協調

卡故障的復位不在 `IAxis` 上，通過 `ResetHardware` 委託交給 `Machine`，復位時最先調用（可以重複調用，沒有故障時 `ResetFault` 直接返回）。
卡故障和快照過期會讓 `GetStatus()` 返回 `Alarm`，就緒和運行時由運行監視報 `MCH-002`，不用等到下一次運動。

```csharp
var config = new MachineConfig
{
    // ...
    ResetHardware = _ =>
    {
        controller.Poller.ResetFault();
        return Task.CompletedTask;
    },
};
```

## 結構

| 文件 | 職責 |
|---|---|
| `Common/ILeadshineMotionApi.cs` | 兩個系列共有的 SDK 調用（當量、速度、點位、停止、IO 狀態、急停、軟限位） |
| `Common/LeadshineAxisBase.cs` | 公共軸實現：發令鎖、到位判定、取消、超時、報警、安全參數 |
| `Common/LeadshineCallGuard.cs` | 統一校驗返回碼和回讀值，按 `CardId / AxisIndex / AxisName / ApiName / ReturnCode / Expected / Actual / Stage` 記錄日誌 |
| `Common/LeadshineAxisIo.cs` | `axis_io_status` 狀態字解析（兩個系列位定義相同） |
| `Common/LeadshineIoBoard.cs`、`LeadshineIoSnapshot.cs` | 通用 IO：讀 IO 快照，經卡鎖寫輸出 |
| `Dmc/*`、`Smc/*` | 各自的 P/Invoke、SDK 封裝、軸、控制器 |

`*NativeApi` 只原樣返回雷賽錯誤碼，不做判斷；測試時替換成假實現。

## 實現要點（兩個系列共用）

| 契約 | 實現 |
|---|---|
| 到位才完成 | `pmove_unit` 下發後輪詢 `check_done`；停下後還要核對位置在容差內，否則視為被外部停止 |
| 取消不晚於下發 | 運動、回零、停止共用發令鎖；在鎖內、`pmove_unit` / `home_move` 之前最後檢查取消 |
| 停止能隨時插入 | 發令鎖只在下發時持有，等待到位期間釋放 |
| 已下發後取消 | 先 `stop` 減速停止，再拋出 `OperationCanceledException` |
| 故障必須拋出 | 返回碼非零或回讀不一致拋 `LeadshineCallException`（帶 `ApiName`、`ReturnCode`）；運動中 ALM / EMG 立即停止 |
| 速度參數 | 雷賽用加減速**時間**：`Tacc = v / a`，`Tdec = v / d` |
| 指令 / 反饋位置 | `AxisStatus.Position` 是指令位置（`get_position_unit`），運動判定和相對運動起點都用它；`FeedbackPosition` 是編碼器位置（`get_encoder_unit`），`Feedback = None` 時為 NaN，不拿指令位置冒充；`FollowingError` = 指令 − 反饋 |
| 到位判定 | `check_done == 1` 且指令位置在容差內（發現被提前停止）；`InPositionSignal` 啟用時，再等 INP 有效，超過 `InPositionTimeout` 報故障。未啟用 INP 時按 `SettleTime` 固定延時（默認 0），兩者不能同時配置。INP 配置不管啟不啟用都寫入並回讀，防止卡裡固化著舊設置 |
| 限位判定 | 只盯運動方向上的限位（EL 和卡上軟限位）：停在負限位上往正方向走出來，不會誤報負限位 |
| 點動 | `vmove` 速度模式；已在點動方向的限位上時拒絕，反方向允許（用於移出限位）；鬆開後 `stop` 減速，等 `check_done == 1` 停穩才返回；沒鬆開卻停了報故障；`JogTimeout` 兜底 |
| 回零完成 | `get_home_result == 1` 且 `check_done == 1`；原點信號不能代替回零結果 |
| 急停 / 軟限位 | 寫入後從控制卡回讀；最大速度、最大加速度沒有對應卡參數，由 `GuardedAxis` 在軟件層限制 |

## SMC 脈衝軸的差異

| 項目 | 實現 |
|---|---|
| 使能 | `smc_write_sevon_pin` 寫 `ServoOnLevel`，等 `ServoSettleTime` 後 `smc_read_sevon_pin` 回讀，不一致即使能失敗；失能寫相反電平並回讀 |
| 信號配置 | `smc_set_alm_mode`、`smc_set_el_mode` 每次啟動重新下發並回讀，不依賴控制卡裡固化（burn）的參數 |
| 回零參數 | `homemode`、`home_profile_unit`、`home_pin_logic`、`home_position_unit` 四組逐一寫入回讀，任一不一致不回零 |
| 回零方式 | `HomeMethod = Sensor`（默認，增量編碼器）：觸碰感應器回零。`AbsoluteEncoder`（僅總線軸）：讀編碼器絕對位置 → 必須落在軟限位內才可信（防電池掉電、多圈數據丟失）→ 指令位置同步過去並回讀 → 用普通絕對運動走到 `AbsoluteHomeTarget`，到位後才算回零完成。脈衝軸配置絕對值方式時直接拒絕 |
| 編碼器對齊 | `Feedback = Encoder` 時，回零完成後把編碼器設為指令位置並回讀（容差一個脈衝），失敗則回零不算完成。DMC 總線軸由驅動器維護實際位置，不改寫 |
| 報警復位 | 未配置 `AlarmReset` 時直接報故障，不假裝復位成功；配置了則先斷使能，再在 ERC 或通用輸出口上打一個有效電平脈衝（取消也保證撤銷），之後 ALM 必須在 `ClearTimeout` 內消失 |

## 上機前必須實機確認

通用：

- [ ] `set_emg_mode` 的 `emg_logic`：0 低電平有效、1 高電平有效，與所用卡型手冊一致
- [ ] `set_softlimit_unit` 的 `source_sel`、`SL_action` 取值含義（`SoftLimitSource` / `SoftLimitAction`）
- [ ] 脈衝當量：命令走 100 mm，用尺子量實際距離
- [ ] 運動中拍急停、觸發限位、斷開通訊，確認都能報出故障
- [ ] INP：驅動器的 INP 輸出是否接到卡上、有效電平；啟用後手動推軸偏離，確認 `axis_io_status` 第 8 位會變。DMC 總線軸的 INP 是否同樣反映在第 8 位，需要實機確認
- [ ] 反饋位置比例：命令走 100 mm，看反饋是否也是 100 mm。不一致說明驅動器編碼器輸出分頻和電子齒輪不匹配，`encoder_unit` 和指令用的是同一個脈衝當量
- [ ] 反饋方向：正向運動時反饋位置也要增大，反了要改驅動器的編碼器輸出方向
- [ ] 絕對值編碼器：驅動器的絕對原點已在機械零點設定；斷電重上電後讀到的位置與斷電前一致；拔掉電池確認驅動器會報警（而不是給一個錯誤值）
- [ ] 絕對值回零走零點的速度先設低，第一次手放急停上試
- [ ] DMC 總線軸：回零後 `get_encoder_unit` 是否和指令位置一致（現在不改寫，依賴驅動器）
- [ ] 沒接 INP 的軸：需要停穩的（拍照、加工）按實測配置 `SettleTime`，不需要的保持 0
- [ ] `InPositionTimeout`：按驅動器整定後的實際停穩時間設置，太短會誤報，太長會掩蓋增益問題
- [ ] `vmove` 的 `dir`：0 負方向、1 正方向，與電機實際轉向、坐標正方向一致（先低速點一下）
- [ ] 未回零時卡上軟限位仍然生效，坐標是上電時的隨機值，可能擋住未回零點動：確認現場的處理方式
- [ ] 回零過程中觸發限位：現在仍按故障處理；用限位作原點的回零方式需要按實機情況調整

DMC：

- [ ] 回零方式 `Mode` 與驅動器支持的 CiA402 回零方法一致，先低速試回零
- [ ] 16088 兼容處理在當前固件上確實是「參數已生效」

SMC：

- [ ] **SEVON 使能電平**：斷開電機動力線，分別寫 0 和 1，看驅動器面板哪個是使能。同一套舊代碼裡見過兩種寫法，必須逐台確認
- [ ] ALM、EL、ORG 的有效電平：手動觸發每個信號，看 `axis_io_status` 對應位是否跟著變
- [ ] 回零方式：先把 `HomeSpeed` 調低，手放急停上試回零，確認方向和停止位置
- [ ] 報警復位：驅動器報警清除輸入的最小脈寬，以及使能狀態下是否響應清除

通用 IO：

- [ ] 每個輸入的有效電平：手動觸發，看邏輯值是否正確
- [ ] 不穩的感應器：看界面上信號是否閃爍，按實測抖動時間配置 `ActivateDelay` / `DeactivateDelay`；觸點彈跳查卡上是否有輸入濾波參數
- [ ] 斷開網線：`read_inport` 返回什麼，通訊探針是否報出整卡故障
- [ ] 輸出寫入後幾個週期能回讀到；`IoBoard.OutputVerifyTimeout`（默認 100 ms）是否足夠
- [ ] 卡故障、程序退出時閥的實際狀態，安全回路能否切斷危險動作
- [ ] 加入 IO 後的週期統計：`AvgMs` 增加多少，`Overruns` 仍為 0

整機協調：

- [ ] 運行中開門、按急停：硬件回路和軟件的先後順序，界面看到的首出原因是否正確
- [ ] 斷網：`MCH-002` 從斷開到報出的實際時間（`staleTimeout` + `MonitorPeriod`）
- [ ] `StopTimeout`：最慢的軸從最高速減速到停，留足餘量
- [ ] 重新初始化時，已使能的軸再調 `EnableAsync` 是否安全，兩個系列分別確認
- [ ] 異常停止後垂直軸的實際狀態（抱閘、是否掉落）：整機層不主動失能

## 暫不支持

- 連續插補
