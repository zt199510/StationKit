# 設計提案：雷賽軸集中輪詢

> **狀態**：已按第 7 節分 5 步實現，單元測試通過，**尚未在實機上驗證**。第 8 節已確定 DLL 調用全部串行。
> 實現與提案的差異見第 9 節；使用方法見 [hardware-adapter.md](../hardware-adapter.md#狀態輪詢)。

## 1. 背景與問題

目前每根軸各自讀取狀態，直接調用 DLL 的地方有：

- `GetStatus()`：每次讀 3～4 個函數（`get_position_unit`、`get_encoder_unit`、`axis_io_status`、`check_done`）
- `WaitUntilAsync`：每輪調用一次 `GetStatus()`
- `IsHomeFinished`、`WaitStoppedAsync`、`WaitInPositionSignalAsync`：各自直接調用
- 每根軸各自 `Task.Delay(PollInterval)`

由此帶來三個問題：

| 問題 | 影響 |
|---|---|
| 多軸同時運動時，DLL 被多個線程併發調用 | 雷賽 DLL 的線程安全性沒有文檔保證 |
| SMC 走以太網，每次調用都是一次網絡往返 | 調用次數隨軸數線性增長，延遲被放大 |
| `Task.Delay(10)` 在 Windows 上默認約 15.6 ms | 實際輪詢週期比配置值長，直接影響節拍 |

## 2. 目標結構

```text
            ┌────────────── 每張卡一個 ──────────────┐
            │  LeadshineStatusPoller（後台線程）       │
            │  每週期：逐軸讀 位置/編碼器/IO/check_done │
            │  → 生成不可變快照，原子替換              │
            └──────────────┬──────────────────────────┘
                           │ 讀快照（不調 DLL）
     ┌─────────────┬───────┴─────┬─────────────┐
   AxisX         AxisY         AxisZ        界面刷新
GetStatus()   WaitUntil…    GetStatus()
     │ 下發指令（pmove / stop …）
     └────────► CardGate（每卡一把鎖，所有 DLL 調用串行）
```

## 3. 改動清單

| 部分 | 內容 |
|---|---|
| `LeadshineCardGate`（新） | 每卡一把鎖，所有 DLL 調用經過它。輪詢只在**讀單根軸**時持有，不是整個週期：`stop` 最多等一根軸讀完就能插入 |
| `LeadshineAxisSnapshot`（新） | 不可變 record：`Cycle`、`Command`、`Feedback`、`Io`、`Done`、各讀取的返回碼、`Timestamp` |
| `LeadshineStatusPoller`（新） | 每卡一個。`RefreshOnce()`（同步讀一輪，測試直接調用）、`Start/Stop`、`GetSnapshot(axis)`、`WaitForCycleAsync(afterCycle, timeout, ct)`。後台循環只是反覆調用 `RefreshOnce()` |
| `LeadshineAxisBase` | 構造時注入 poller；`GetStatus()` 讀快照，**不再調 DLL**；等待循環改為等下一週期，不再 `Task.Delay` |
| `DmcController` / `SmcController` | `Open` 後啟動 poller、`Close` 前停止；`CreateAxis` 時註冊軸 |
| `LeadshineAxisOptions.PollInterval` | 移到 poller，變成每卡一個的週期 |

`home_result` 只在回零時對單根軸讀取，頻率低，繼續直接調用，但必須經過 `CardGate`。

## 4. 兩個關鍵陷阱

### 4.1 下發前的舊快照被當成到位

時序：輪詢第 N 輪開始，讀到 `check_done == 1` → 業務下發 `pmove` → 第 N 輪快照發佈。
等待循環拿到這個快照，看到「已停止」，判定到位，接著核對位置不一致而報「被外部停止」，
或者在原地判定為完成。

**對策**：快照記錄**開始讀取時**的週期號。下發後記錄當前週期號 `issuedAt`，
只接受 `snapshot.Cycle > issuedAt` 的快照。判斷依據是「開始讀取時」，不是「發佈時」。

### 4.2 輪詢線程悄悄停止

後台循環因異常退出後，快照凍結在最後一刻：界面一直顯示「靜止、正常」，等待循環永遠等不到新週期。

**對策**：

- 快照帶時間戳。`GetStatus()` 發現快照超過 `StaleTimeout`（例如 100 ms）未更新，返回 `Alarm`，
  原因「狀態數據過期，輪詢可能已停止」；等待循環同樣報故障。
- 輪詢循環捕獲所有異常並記錄日誌，單次讀取失敗不能結束線程。

## 5. 不能改變的行為

現有測試覆蓋的行為全部保持，特別是：

- 取消不晚於下發；發令鎖只在下發時持有；停止隨時能插入
- 到位判定：`check_done`、位置容差、INP、`SettleTime`
- 只盯運動方向上的限位；點動鬆開後等停穩才返回
- 一根軸讀取失敗只影響這根軸（`Alarm`），其他軸照常輪詢

## 6. 測試計劃

**現有測試**：測試中運行真實的後台 poller（週期 1 ms），基本不用修改。
假 SDK 的 `PollsPerMove` 按 `check_done` 調用次數倒數，由輪詢線程調用同樣成立。

**新增測試**（用 `RefreshOnce()` 手動推進，結果確定）：

- [x] 下發後只接受新週期的快照：舊快照中的 `check_done == 1` 不算到位（4.1）
- [x] 輪詢停止超過 `StaleTimeout`：狀態為 `Alarm`，等待中的運動報故障（4.2）
- [x] `GetStatus()` 不產生 DLL 調用：調用前後 `Calls.Count` 不變
- [x] 一輪輪詢中，每根軸每個函數各讀一次
- [x] 一根軸讀取失敗，不影響其他軸的快照
- [x] 輪詢正在讀其他軸時，`stop` 不用等整輪結束

## 7. 實施步驟

每步單獨提交，每步全部測試通過：

1. **`CardGate`**：所有 DLL 調用經過同一把鎖。行為不變，只做串行化
2. **poller 與快照**：`GetStatus()` 讀快照，等待循環暫時仍用 `Delay`
3. **週期號隔離**：等待循環改為等新週期，解決 4.1 並補測試
4. **過期檢測**：解決 4.2 並補測試
5. **專用線程**：後台循環用專用線程加高精度計時，日誌記錄每輪耗時，上機時據此確定週期

## 8. 待決策

| 問題 | 選項 | 傾向 |
|---|---|---|
| 雷賽 DLL 是否線程安全 | 確定安全：`CardGate` 只保證單軸讀取的一致性，下發不與輪詢互斥；不確定：全部串行 | **已決定：全部串行**（線程安全性未經確認，以穩妥為先；實機驗證後可再放寬） |
| 輪詢週期 | — | SMC 以太網先定 5 ms，上機後按日誌實測調整 |
| 高精度計時 | `timeBeginPeriod(1)` + `Thread.Sleep(1)`（影響整個進程）；專用線程自旋加短睡眠（CPU 佔用更高） | 前者，視工控機上的其他程序而定 |

## 9. 實現記錄

| 步驟 | 提交 | 與提案的差異 |
|---|---|---|
| 1 卡鎖 | `0a2258f` | 用 `System.Threading.Lock`；鎖順序固定為「發令鎖 → 卡鎖」，卡鎖不跨 `await` |
| 2 輪詢器與快照 | `f1ea681` | 無快照時 `GetStatus()` 返回 `Alarm`「尚無狀態數據」 |
| 3 週期號隔離 | `acbc33c` | 除運動等待外，運動前檢查、使能前報警檢查也要求快照晚於上一條指令（復位後立即使能不會被舊報警擋住）；ALM 有效時輪詢順帶讀取驅動器錯誤碼存入快照，`GetStatus()` 完全不調 DLL |
| 4 過期檢測 | `b32febe` | 等待先判斷過期再判斷圍欄。順帶修正：某軸一直讀取失敗時週期照常完成但快照不更新，原先的等待會卡死，現在按過期報故障；輪詢週期必須不超過過期時間的 1/3，構造控制器時檢查 |
| 5 專用線程 | `1b57f8e` | 線程優先級 AboveNormal；`Stop` 最多等 1 s，DLL 卡死時 `Close` 不跟著卡死；統計增加 `MaxGapMs`（相鄰兩輪開始的間隔），用來發現計時精度不足 |

上機時要確認：

- 統計日誌裡 `AvgMs` / `MaxMs`（一輪讀取耗時）和 `MaxGapMs`，據此確定週期；`Overruns` 應為 0
- `timeBeginPeriod` 是否生效（`MaxGapMs` 接近週期而不是 15.6 ms）。CI 只在 Linux 上運行，Windows 分支未經測試
- 卡鎖全部串行後，`stop` 的實際延遲（最多等一根軸讀完）
