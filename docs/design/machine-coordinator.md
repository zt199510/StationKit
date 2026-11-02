# 設計提案：整機協調層

> **狀態**：提案已確認，第 8 節按傾向決定，尚未實現。

## 1. 背景與問題

框架目前只到單個工站，整機的流程都由上層自己拼（見 `samples/StationKit.Demo/Program.cs`）：
安全初始化、回零、啟動工站、恢復時按順序調十幾個方法。`docs/architecture.md` 的已知限制裡也有兩項屬於這一層：

| 問題 | 現狀 | 後果 |
|---|---|---|
| 工站故障不連帶其他工站 | 其他工站要到需要共享資源、超時後才停 | 一個工站已報警，另一個工站繼續動 |
| 互鎖只在下發前檢查 | 運動中開門不會停軸，軟件沒有任何反應 | 只有硬件回路動作，界面上看不到原因 |
| 空閒時卡故障沒有報警 | 雷賽輪詢器只寫日誌，不依賴 `AlarmCenter`；要等下一次運動才報出 | 卡已斷線，界面還顯示就緒 |
| 操作員只有「取消」一種停止 | `Station.StopAsync` 直接取消循環；持有資源時資源進入不確定 | 每次正常下班都可能要人工解除資源 |
| 中途停止後可以直接再啟動 | 取消後工站回到 `Idle`，和正常結束沒有區別 | 機構停在循環中間，下一輪從頭開始走 |
| 沒有統一的恢復入口 | 恢復順序由調用方記住 | 漏掉一步（例如解除資源前復位工站）沒有任何提示 |

讀代碼時另外發現三個現有缺陷，整機層會放大它們，要先修（第 7 節第 1 步）：

| 位置 | 缺陷 | 後果 |
|---|---|---|
| `AlarmCenter.Raise`（`AlarmCenter.cs:65`）、`Clear` | 事件訂閱者拋異常會穿透到調用方，與「觸發報警永不拋異常」相反 | `Station.LoopAsync` 的 `Alarms.Raise` 被打斷，跳過 `Transition(Faulted)` 和 `OnFaultedAsync`：軸沒停，工站狀態停在 Running |
| `Station.cs:251` | 善後失敗的報警來源是 `工站名/善後`，`HasBlockingAlarm(Name)` 按來源全等比較 | 善後失敗（軸可能沒停）不阻止復位和啟動；`ClearSource(Name)` 也清不掉它 |
| `Station.cs:244-247` | 先進入 `Faulted`，再執行善後；`Reset` 不檢查善後是否結束 | 善後還在停軸時，復位並重新啟動，善後會停掉新一輪的運動 |

## 2. 目標結構

新項目 `StationKit.Machines`，依賴 `Stations`、`Safety`、`Alarms`、`Abstractions`。
類名 `Machine`，項目名用複數，避免命名空間和類同名（與 `StationKit.Stations.Station` 一致）。

```text
界面 / 具體設備
   │  InitializeAsync  StartAsync  PauseAsync  ResumeAsync  CycleStopAsync  AbortAsync  ResetAsync
   ▼
Machine ── 整機狀態機、首出原因、運行監視
   ├─ SafetyStartup      初始化時的安全參數和使能
   ├─ 啟動步驟           氣缸縮回 → Z 回零 → XY 回零 → 走到待機位（設備自己定義）
   ├─ Station[]          只用現有的公開接口
   ├─ IAxis[]            整機異常時直接停軸
   ├─ ResourceArbiter    恢復前檢查不確定資源
   ├─ AlarmCenter        訂閱故障，發佈整機報警
   └─ MachineMotionGate  IMotionInterlock，異常停止期間拒絕一切新運動（由 GuardedAxis 檢查）
```

整機層**不是安全功能**：它跑在 Windows 上的托管線程裡，響應時間沒有保證。
急停和安全門必須有硬件回路，運行監視只負責讓軟件跟上現場、報出原因、不再下發新運動。

## 3. 狀態與語義

### 3.1 狀態機

```text
Uninitialized ──InitializeAsync──▶ Initializing ──成功──▶ Ready
                                                          │ StartAsync
                                     CycleStopAsync       ▼
                 Ready ◀── CycleStopping ◀──────────── Running ◀─▶ Pausing ─▶ Paused
                                                          │ 所有工站跑完 cycleLimit → Ready

任何狀態出現故障 / AbortAsync ──▶ Aborting ──全部停下──▶ Faulted ──ResetAsync──▶ Uninitialized
```

| 狀態 | 含義 |
|---|---|
| `Uninitialized` | 軸的位置和工站的進度不可信，不能啟動 |
| `Initializing` | 正在安全初始化和執行啟動步驟 |
| `Ready` | 已初始化且機構在起點，可以啟動 |
| `Running` / `Pausing` / `Paused` | 所有未結束的工站都到達安全點才算 `Paused`；與工站一樣區分「暫停中」和「已暫停」 |
| `CycleStopping` | 工站運行到下一個安全點後結束，不產生不確定資源 |
| `Aborting` | 異常停止進行中，運動閘關閉 |
| `Faulted` | 已停下，等待人工處理 |

**`Faulted` 復位後一律回到 `Uninitialized`。** 異常停止時循環被中途取消，位置和工件都不可信，
不能因為軸的 `IsHomed` 還是 true 就直接回 `Ready`。

### 3.2 兩種停止

| | `CycleStopAsync`（操作員按停止） | `AbortAsync` / 故障（異常停止） |
|---|---|---|
| 工站 | `Pause()`，等全部到達安全點，再 `StopAsync()` | 立即 `StopAsync()`（取消循環） |
| 軸 | 不主動停，工站自己走到安全點 | 全部直接停 |
| 持有的資源 | 安全點不持有資源，不會變成不確定 | 持有中的進入不確定，需要人工解除 |
| 結束狀態 | `Ready`，可以直接再啟動 | `Faulted`，要人工恢復和重新初始化 |
| 超時 | `CycleStopTimeout` 內沒停下，升級為異常停止 | `StopTimeout` 內沒停下，報 `MCH-003` |

### 3.3 異常停止的順序

```text
1. 鎖內改狀態為 Aborting，記錄首出原因（只記第一次，之後的觸發只追加日誌）
2. 關閉運動閘
3. 同時取消所有工站（不等待）
4. 同時停止所有軸（每根軸各自包成異步、各自 try/catch、各自超時）
5. 等所有工站的 RunAsync 任務結束（含善後），上限 StopTimeout
6. 再停一次所有軸：補上第 3、4 步之間已通過檢查的運動
7. 開啟運動閘，進入 Faulted
```

不主動失能：垂直軸失能可能掉落，是否斷使能由工站的 `OnFaultedAsync` 或硬件回路決定。
也不動輸出：閥斷電後氣缸往哪走取決於機構，整機層不知道。

### 3.4 運行監視

一個定時循環（`PeriodicTimer` + 注入的 `TimeProvider`），只調非阻塞接口（`IDigitalInput.GetReading`、`IAxis.GetStatus`），
不碰 DLL，也不佔卡鎖。

**信號條件**（`MachineGuard`）：

```csharp
public sealed record MachineGuard(string Name, IDigitalInput Input, bool Expected, StopMode? StopMode)
{
    /// <summary>必填，不能為 None。門在暫停時能否打開、回零時是否檢查，每台設備不同，不猜。</summary>
    public MachineGuardScope Scope { get; init; }
}

[Flags]
public enum MachineGuardScope { None = 0, Initialize = 1, Run = 2, ManualMotion = 4 }
```

| 當前狀態 | Scope 包含 | 信號不滿足時 |
|---|---|---|
| `Initializing` | `Initialize` | 異常停止 |
| `Running` / `Pausing` / `CycleStopping` | `Run` | 異常停止 |
| `Ready` / `Paused` / `Uninitialized` / `Faulted`，且有軸在動 | `ManualMotion` | 按 `StopMode` 停軸，報 `MCH-001` |
| 同上，沒有軸在動 | 任意 | 不報警。門開著是合法狀態，只是啟動和恢復時會被拒絕 |

- 數據不可信（`GetReading` 的 `Value` 為 null）等同不滿足。
- 違反**鎖存**：信號恢復（門關上）不會自動繼續，也不會自動清除報警。
- `StopMode` 為 null 時拒絕創建。

**軸健康**：`Ready`、`Running`、`Pausing`、`Paused`、`CycleStopping` 時，任何一根軸出現 `Alarm`、`Disabled`
或 `IsHomed == false`，報 `MCH-002` 並異常停止。這補上了「空閒時卡故障沒有報警」：快照過期、整卡故障都會讓 `GetStatus()` 返回 `Alarm`。

**監視循環自身異常**：報 `MCH-005` 並異常停止。監視停了卻沒人知道，比沒有監視更危險。

### 3.5 恢復

`ResetAsync` 只在 `Faulted` 時可用，條件不滿足時**列出全部原因**後拒絕，不改變狀態：

- 所有工站的運行任務已結束，沒有軸在動
- 所有 `MachineGuard` 滿足且數據可信
- 整機沒有 `Error` 級報警（`HasBlockingAlarm()` 不帶來源，涵蓋所有來源）
- 沒有不確定資源

通過後依次調用 `Station.Reset()`，進入 `Uninitialized`。報警和資源由人確認後單獨清除，整機層**不代勞**：
自動清除就等於沒有人工確認。

`InitializeAsync` 按以下順序執行：

1. 配置的 `ResetHardware` 委託（例如雷賽的 `Poller.ResetFault()`，它不在 `IAxis` 上）
2. 對 `Alarm` 狀態的軸調 `ResetAlarmAsync`
3. `SafetyStartup.RunAsync`
4. 按順序執行 `MachineStartupStep`（名稱 + 委託）
5. 再次檢查信號條件和軸健康，通過才進入 `Ready`

任何一步失敗：停所有軸，報 `MCH-004`（詳情裡寫步驟名），進入 `Faulted`。

### 3.6 配置

```csharp
public sealed record MachineConfig
{
    public required string Name { get; init; }
    public required IReadOnlyList<Station> Stations { get; init; }
    public required IReadOnlyList<AxisSafetyBinding> Axes { get; init; }
    public required IReadOnlyList<MachineStartupStep> StartupSteps { get; init; }
    public required IReadOnlyList<MachineGuard> Guards { get; init; }
    public required MachineMotionGate MotionGate { get; init; }

    /// <summary>工站故障時其他工站怎麼辦。必填，沒有默認值。</summary>
    public required StationFaultPolicy? OnStationFault { get; init; }

    public required TimeSpan MonitorPeriod { get; init; }
    public required TimeSpan StopTimeout { get; init; }
    public required TimeSpan CycleStopTimeout { get; init; }

    public ResourceArbiter? Resources { get; init; }
    public Func<CancellationToken, Task>? ResetHardware { get; init; }
}

public enum StationFaultPolicy
{
    /// <summary>其他工站立即取消：風險低於繼續運動，代價是資源可能不確定。</summary>
    AbortAll,

    /// <summary>其他工站運行到安全點後停下，再進入 Faulted。超時升級為 AbortAll。</summary>
    OthersToSafePoint,
}
```

啟動時校驗並拒絕：策略未指定、`Guard` 的 `Scope` 或 `StopMode` 未指定、工站重名、軸重名、週期或超時不為正。
**信號條件和軸的違反永遠是異常停止，不受這個策略影響。**

`MachineMotionGate` 單獨成類：工站要拿 `GuardedAxis`，`GuardedAxis` 要拿互鎖，`Machine` 又要拿工站，閘先創建才能解開這個循環依賴。

### 3.7 報警代碼

| 代碼 | 含義 |
|---|---|
| `MCH-001` | 運行條件不滿足（詳情：信號名、當前值或不可信原因） |
| `MCH-002` | 軸狀態異常（詳情：軸名、狀態、`AlarmReason`） |
| `MCH-003` | 異常停止超時（詳情：哪個工站沒結束、哪根軸沒停） |
| `MCH-004` | 初始化失敗（詳情：步驟名、原因） |
| `MCH-005` | 運行監視異常 |

`Machine.FirstFault` 保存首出原因（時間、來源、描述）。連帶停止會引發次生報警，現場最需要知道的是第一個。

## 4. 關鍵陷阱

### 4.1 報警回調裡不能等工站

`Alarms.Raise` 同步調用訂閱者，而工站是在 `LoopAsync` 的 `catch` 裡調它的，這時工站還是 `Running`。
訂閱者如果等 `StopAsync`（它要等 `_runTask`，也就是正在執行這個回調的循環），就會死鎖。
所以回調裡只在鎖內改狀態，停機放到線程池執行（`Task.Run`），回調立即返回。

### 4.2 `Faulted` 不代表已經停下

`Station.StopAsync` 在 `Faulted` 時立即返回，而此時 `OnFaultedAsync` 可能還在停軸。
判斷「工站都結束了」只看 `RunAsync` 返回的 Task，不看 `State`，也不靠 `StopAsync` 的完成。

### 4.3 事件參數可能過時

`StateChanged` 在鎖外觸發，併發時順序可能和實際轉換相反。訂閱者重新讀 `State`，不用事件參數判斷。

### 4.4 只訂閱報警會漏掉故障

`Raise` 對相同代碼和來源去重，已有未清除的 `STN-001` 時不會再觸發 `Raised`。
所以同時訂閱 `Raised`（`Error` 級）和各工站的 `StateChanged`，觸發是冪等的。
另外，進入 `Running` 之後要再查一次 `HasBlockingAlarm()`，補上「檢查條件」和「進入狀態」之間新出現的報警。

### 4.5 先停軸會把取消變成假故障

被外部停止的軸拋 `MotionFaultException`，不是 `OperationCanceledException`，工站會當成真故障：
進入 `Faulted` 並報 `STN-001`，看起來像一次新的故障。所以先取消、再停軸（3.3 第 3、4 步）。
兩者併發時仍可能出現次生報警：它是真實發生的事，不隱藏，靠 `FirstFault` 區分原因。

### 4.6 取消是協作式的

工站的循環不響應令牌時，取消不會生效，軸停了之後它還可能下發下一步。這一層做三件事：

- 運動閘：`GuardedAxis` 拒絕新運動（`MotionRejectedException`，沒有動過軸）
- 第二次停軸：處理已通過檢查、正要下發的運動
- `StopTimeout`：到時間還沒結束就報 `MCH-003`。線程無法強行終止，此時必須提示人工處理

運動閘只對經過 `GuardedAxis` 的運動有效，文檔裡寫明工站只能拿包裝後的軸。

### 4.7 消抖會造成啟動時的假違反

信號從「無效」開始過濾（`digital-io.md` 11.3），安全門即使一直關著，也要保持 `ActivateDelay` 才算有效。
卡故障恢復後同樣從無效開始。所以檢查時機放在初始化結束和啟動前，不能在 `ResetHardware` 之後立刻斷言；
檢查不通過時拒絕並寫明原因，操作員重試即可，不報故障。

### 4.8 一根軸的停止不能影響其他軸

`IAxis.StopAsync` 可能同步拋異常，也可能卡在卡鎖上。逐根 `await`，或者直接 `Task.WhenAll(axes.Select(a => a.StopAsync()))`，
都可能讓後面的軸停不下來。每根軸單獨包成異步（與 `MotionGroup.InvokeAsync` 同一手法）、單獨超時，失敗報 `MCH-003`，不中斷其他軸。

## 5. 不能改變的行為

- `Station`、`ResourceArbiter`、`GuardedAxis` 的公開行為不變，不用 `Machine` 的代碼照常工作（第 1 步的三個修復除外）
- 不確定資源不會被自動解除，報警不會被自動清除，互鎖違反不會自動恢復
- 演示現有的 `--fault`、`--door` 輸出的關鍵行為不變
- `Abstractions` 不依賴 `Machines`；`Machines` 不依賴任何廠商適配器（`ResetHardware` 用委託）

## 6. 測試計劃

全部用 `SimAxis`、`SimIoBoard`、`FakeTimeProvider`。

前置修復：

- [ ] 報警訂閱者拋異常：`Raise` 不拋，其他訂閱者仍被調用；工站照常進入 `Faulted` 並執行善後（反證：舊實現軸不會停）
- [ ] 善後失敗的報警阻止工站復位和啟動；`ClearSource(工站名)` 能清除它
- [ ] 善後未結束時 `Reset` 被拒絕

初始化：

- [ ] 順序：硬件復位 → 報警復位 → 安全初始化 → 步驟按序執行
- [ ] 某一步失敗：後面的步驟不執行，軸全部停下，`MCH-004` 寫明步驟名，進入 `Faulted`
- [ ] 初始化中違反 `Initialize` 範圍的條件：中止；不在範圍內的條件：不中止
- [ ] 配置未指定策略、`Scope`、`StopMode`：拒絕創建

啟動與停止：

- [ ] `Uninitialized`、`Faulted` 時不能啟動；條件不滿足時拒絕並列出全部原因
- [ ] 任何工站拒絕啟動：其他工站都沒有啟動
- [ ] 循環停止：等工站到安全點，資源不進入不確定，回到 `Ready` 且可再次啟動
- [ ] 循環停止超時：升級為異常停止
- [ ] 所有工站到達安全點才算 `Paused`；恢復時條件不滿足拒絕
- [ ] 全部工站跑完 `cycleLimit`：回到 `Ready`

異常停止：

- [ ] `AbortAll`：一個工站故障，其他工站立即結束，所有軸停下，`FirstFault` 是最初的故障（反證：現狀要等資源超時）
- [ ] `OthersToSafePoint`：其他工站完成當前段後停下；超時升級
- [ ] 持有資源時被取消：資源不確定；整機 `ResetAsync` 拒絕並列出資源
- [ ] 多個故障同時觸發：只執行一次停機流程，`FirstFault` 不被覆蓋
- [ ] 一根軸停止時同步拋異常、一根卡住：其他軸都停下，報 `MCH-003`
- [ ] 工站不響應令牌：`StopTimeout` 後報 `MCH-003` 並進入 `Faulted`；這期間它的新運動被運動閘拒絕
- [ ] 在 `Raised` 回調線程上觸發不會死鎖

運行監視：

- [ ] 運行中開門：異常停止，`MCH-001`；關門後不自動恢復（反證：現狀軸不停）
- [ ] 信號數據不可信：按不滿足處理
- [ ] `Paused` 時開門且軸不動：不報警；恢復被拒絕
- [ ] `Ready` 時手動點動中開門：`ManualMotion` 範圍內停軸報警，範圍外不干預
- [ ] `Ready` 時軸報警或掉使能：報 `MCH-002` 並進入 `Faulted`（反證：現狀沒有任何報警）
- [ ] 監視循環內部異常：`MCH-005` 並異常停止

恢復：

- [ ] 每個條件單獨不滿足都被拒絕且狀態不變；多個不滿足時全部列出
- [ ] 通過後進入 `Uninitialized`，不是 `Ready`；不清除報警、不解除資源
- [ ] 完整流程：故障 → 清除報警、解除資源 → 復位 → 初始化 → 再運行

## 7. 實施步驟

每步單獨提交，每步全部測試通過：

1. **前置修復**：`AlarmCenter` 隔離訂閱者異常（失敗的訂閱者不影響其他訂閱者）；善後報警歸入本工站；善後未結束時拒絕 `Reset`
2. **項目與初始化**：`StationKit.Machines`、配置校驗、狀態機骨架、`MachineMotionGate`、`InitializeAsync`、報警代碼表
3. **運行與正常停止**：`StartAsync`、`PauseAsync`、`ResumeAsync`、`CycleStopAsync`
4. **異常停止**：`AbortAsync`、3.3 的順序、故障訂閱、兩種工站故障策略、`FirstFault`
5. **運行監視**：信號條件、軸健康、監視自身異常
6. **恢復**：`ResetAsync` 和條件清單
7. **演示**：Demo 改用 `Machine`，新增 `--estop`（運行中違反條件），CI 增加這一項

收尾：提案補實現記錄，更新 README、`architecture.md` 的已知限制。

## 8. 待決策

| 問題 | 選項 | 決定 |
|---|---|---|
| 工站故障的影響範圍 | 只有整機；再支持工站分組、單工站隔離 | **第一版只做整機**。互相獨立的工站組等有具體設備再加，否則接口是憑空設計 |
| 並發模型 | 鎖 + `Task`；單消費者 `Channel` 串行處理所有事件 | **鎖 + `Task`**，與 `Station` 一致。`Channel` 更整齊，但多一層，也要處理「停機排在長命令後面」的問題 |
| `Faulted` 復位後 | 一律重新初始化；`IsHomed` 仍為 true 就回 `Ready` | **一律重新初始化**。代價是恢復慢一點，位置和工件都不可信時不該省這一步 |
| 啟動步驟是否分工站 | 整機一個有序列表；每個工站自己定義 | **整機一個列表**。回零順序往往跨工站（先抬所有 Z 再走 XY），同時回零用現有的 `MotionGroup.RunAllAsync` |
| 運行監視的線程 | 線程池上的 `PeriodicTimer`；專用線程（與輪詢器一致） | **`PeriodicTimer`**：只讀快照，不調 DLL，也不是安全功能；能用假時鐘測試 |
| `MonitorPeriod` 默認值 | 必填；給默認值 | **必填**，示例寫 20 ms，上機再調 |
| 暫停時門打開 | 按 `Scope` 配置；一律允許 | **按 `Scope` 配置**，軸不動時本來就不報警 |
| 三色燈、蜂鳴器 | 納入整機層；上層訂閱 `StateChanged` | **上層訂閱**，映射關係每個客戶不同 |

## 9. 上機時要確認

- 運行中開門：硬件回路和軟件的先後順序、界面看到的首出原因是否正確
- 斷網：`MCH-002` 從斷開到報出的實際時間（輪詢 `staleTimeout` + `MonitorPeriod`）
- `StopTimeout`：最慢的軸從最高速減速到停，留足餘量
- 再次 `SafetyStartup` 時，已使能的軸調 `EnableAsync` 是否安全，雷賽兩個系列分別確認
- 異常停止後垂直軸的實際狀態（抱閘、是否掉落）
