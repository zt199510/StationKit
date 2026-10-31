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
    InPositionSignal = LeadshineInpMode.Disabled,   // 必填：是否用 INP 判定到位
    HomeProfile = new DmcHomeProfile(Mode: 17, LowVelocity: 5, HighVelocity: 20,
        AccelerationTime: 0.1, DecelerationTime: 0.1, Offset: 0),
});

x.ApplyBaseParameters();                  // 脈衝當量（寫入後回讀）、S 段時間
await SafetyStartup.RunAsync([new(x, x, xSafety)]);   // 急停、軟限位：寫入 → 回讀 → 使能
await x.HomeAsync();

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

## 結構

| 文件 | 職責 |
|---|---|
| `Common/ILeadshineMotionApi.cs` | 兩個系列共有的 SDK 調用（當量、速度、點位、停止、IO 狀態、急停、軟限位） |
| `Common/LeadshineAxisBase.cs` | 公共軸實現：發令鎖、到位判定、取消、超時、報警、安全參數 |
| `Common/LeadshineCallGuard.cs` | 統一校驗返回碼和回讀值，按 `CardId / AxisIndex / AxisName / ApiName / ReturnCode / Expected / Actual / Stage` 記錄日誌 |
| `Common/LeadshineAxisIo.cs` | `axis_io_status` 狀態字解析（兩個系列位定義相同） |
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
| 到位判定 | `check_done == 1` 且指令位置在容差內（發現被提前停止）；`InPositionSignal` 啟用時，再等 INP 有效，超過 `InPositionTimeout` 報故障。INP 配置不管啟不啟用都寫入並回讀，防止卡裡固化著舊設置 |
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
| 報警復位 | 未配置 `AlarmReset` 時直接報故障，不假裝復位成功；配置了則先斷使能，再在 ERC 或通用輸出口上打一個有效電平脈衝（取消也保證撤銷），之後 ALM 必須在 `ClearTimeout` 內消失 |

## 上機前必須實機確認

通用：

- [ ] `set_emg_mode` 的 `emg_logic`：0 低電平有效、1 高電平有效，與所用卡型手冊一致
- [ ] `set_softlimit_unit` 的 `source_sel`、`SL_action` 取值含義（`SoftLimitSource` / `SoftLimitAction`）
- [ ] 脈衝當量：命令走 100 mm，用尺子量實際距離
- [ ] 運動中拍急停、觸發限位、斷開通訊，確認都能報出故障
- [ ] INP：驅動器的 INP 輸出是否接到卡上、有效電平；啟用後手動推軸偏離，確認 `axis_io_status` 第 8 位會變。DMC 總線軸的 INP 是否同樣反映在第 8 位，需要實機確認
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

## 暫不支持

- 連續插補、點動、多軸聯動
