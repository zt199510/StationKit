# 設計提案：按行程計算運動超時

> **狀態**：提案，待確認，尚未實現。

## 1. 背景與問題

雷賽適配器的點位運動用一個固定的 `MotionTimeout`（默認 30 秒，`LeadshineAxisOptions.cs:81`）作為等待規劃走完的上限。
同一根軸的行程和速度差別很大，固定值怎麼填都有一頭不合適：

| 場景 | 現狀 | 後果 |
|---|---|---|
| 短行程異常 | 只需 0.2 秒的運動，要等滿 30 秒才報超時 | 故障發現太慢 |
| 長行程或低速 | 300 mm、5 mm/s 正常要走 60 秒，30 秒就超時 | 正常運動被停軸並報故障 |
| 調整速度或行程 | 參數改了，超時不會跟著變 | 要記得手動改，否則回到上面兩種情況 |

讀代碼時另外發現兩個相關問題：

| 位置 | 問題 | 後果 |
|---|---|---|
| `GuardedAxis.JogAsync`（`GuardedAxis.cs:103`） | 已回零的點動是點位運動到軟限位邊界，同樣受 `MotionTimeout` 限制 | 低速長按超過 30 秒，點動會被當作超時故障停下 |
| `WaitStoppedAsync`（`LeadshineAxisBase.cs:783`） | 點動鬆開後的停止確認也借用 `MotionTimeout` | 一個配置管兩件事，改了運動超時會影響停止確認 |

## 2. 目標

超時按這一次運動計算：

```text
超時 = 預計規劃時間 + 餘量（默認 3 秒）
```

例子（`a` 為加速度，`d` 為減速度，餘量 3 秒）：

| 距離 | 速度 | a、d | 預計規劃時間 | 超時 | 原來 |
|---|---|---|---|---:|---:|
| 10 mm | 100 mm/s | 1000 mm/s² | 0.2 s | 3.2 s | 30 s |
| 1000 mm | 500 mm/s | 1000 mm/s² | 2.5 s | 5.5 s | 30 s |
| 300 mm | 5 mm/s | 100 mm/s² | 60.05 s | 63.05 s | 30 s（誤報） |

## 3. 語義

### 3.1 預計規劃時間

只用 `距離 / 速度` 會少算加減速，所以按實際的速度規劃計算。公式由模擬軸的 `TrapezoidProfile` 驗證過：

```text
加減速所需距離 = v²/(2a) + v²/(2d)

D ≥ 加減速所需距離（梯形）：T = D/v + v/(2a) + v/(2d)
D < 加減速所需距離（三角形）：vp = √(2·D·a·d / (a+d))，T = vp/a + vp/d
```

- `D` 取下發前新鮮快照的指令位置到目標的距離，和規劃器看到的一致。
- `D = 0` 時 `T = 0`，超時等於餘量。
- `v`、`a`、`d` 取這一次傳入的 `MotionProfile`，也就是實際下發給控制卡的參數。

`MinVelocity`、`StopVelocity` 不參與計算：它們只會讓實際時間變短，超時偏大一點，方向是安全的。
S 段會讓實際時間變長，由餘量覆蓋，上機時確認（第 10 節）。

### 3.2 超時只管第一段

運動完成的順序不變，新的超時只替換第一段：

```text
下發運動（圍欄之後的快照才算數）
  ↓
等軸空閒（check_done）        ← 預計規劃時間 + 餘量；超時就減速停止並報故障
  ↓
核對指令位置和目標            ← 超差就報故障（可能被外部停止）
  ↓
有 INP：等 INP 有效           ← InPositionTimeout，單獨計時
沒有 INP：等 SettleTime（可為 0）
  ↓
運動完成
```

### 3.3 這個超時能發現什麼

`check_done == 1` 只代表規劃走完，不代表電機到了。所以這個超時**不負責發現電機堵轉**：

| 異常 | 誰發現 |
|---|---|
| 規劃遲遲不結束（`check_done` 一直不是 1） | **這個超時** |
| 卡故障、輪詢停止 | 快照過期（`staleTimeout`），已有 |
| 伺服過載、跟隨誤差過大 | 驅動器 ALM，已有 |
| 規劃走完，電機沒跟上 | INP 超時，已有 |
| 規劃走完，電機沒跟上，而且沒接 INP | **目前沒有檢查**，本提案也不解決（見第 5 節） |

### 3.4 配置

`LeadshineAxisOptions` 刪除 `MotionTimeout`，新增兩項：

```csharp
/// <summary>點位運動超時的餘量：超時 = 按行程和速度算出的預計規劃時間 + 這個值。必須大於 0。</summary>
public TimeSpan MoveTimeoutMargin { get; init; } = TimeSpan.FromSeconds(3);

/// <summary>發出停止後，等軸確認停下的最長時間。</summary>
public TimeSpan StopConfirmTimeout { get; init; } = TimeSpan.FromSeconds(30);
```

- 直接刪除 `MotionTimeout`，不留空殼：舊配置編譯報錯，不會讓人以為它還有效。
- `StopConfirmTimeout` 默認 30 秒，和現在的停止確認行為一致。
- `MoveTimeoutMargin` 小於等於 0 時，創建軸直接拒絕。

計算放在 `Abstractions`，因為適配器不能依賴模擬項目：

```csharp
public sealed record MotionProfile(...)
{
    /// <summary>走完指定距離的預計規劃時間（秒），不含 S 段和通信延遲。</summary>
    public double EstimateMoveSeconds(double distance);
}
```

`TrapezoidProfile` 還要計算每個時刻的位置，那是模擬才需要的，留在原處。兩邊的結果由測試保證一致。

## 4. 關鍵陷阱

### 4.1 `TimeSpan` 會溢出

速度極低、距離很大時，`TimeSpan.FromSeconds` 會拋 `OverflowException`。這個異常如果出現在下發之後，軸已經在走，卻沒有人等它。
所以超時要在 `ApplyProfile` 和 `pmove_unit` **之前**算好；超出 `TimeSpan` 範圍時拋 `MotionRejectedException`，軸不動。

### 4.2 起點要在發令鎖內取

現在 `EnsureReadyForMotionAsync` 在發令鎖內取得起點，超時要用同一個起點。不能另外調用 `GetStatus()`：那是另一個時刻的快照，鎖外可能已經過時。

### 4.3 倍率

代碼裡目前沒有全局速度倍率，傳入的 `MotionProfile` 就是實際速度。
以後如果加倍率，必須在這之前換算成實際速度，否則調到 10% 時超時只有應有的十分之一，正常運動會誤報。

## 5. 不解決

- **沒接 INP 的軸發現不了堵轉**：可以用編碼器反饋和目標比較，但要另外定義容差，開環步進也沒有反饋。這是獨立的問題，以後單獨提案。
- **回零和速度模式點動**：事先不知道實際距離，繼續用 `HomeTimeout`、`JogTimeout`。絕對值回零走的是 `MoveAbsoluteAsync`，距離已知，自動用新超時。
- **模擬軸**：沒有等待超時，不受影響。

## 6. 不能改變的行為

- 超時後仍然是先減速停止，再拋 `MotionFaultException`。
- 超時的計時從下發之後開始，判定順序不變：報警、限位優先於超時。
- 圍欄之前的快照不算數，「剛下發時讀到的空閒」不能當作走完。
- `InPositionTimeout`、`SettleTime` 的語義和「兩者不能同時配置」的校驗不變。
- 取消時先停軸，再拋 `OperationCanceledException`。

## 7. 測試計劃

**預計時間（`MotionProfile.EstimateMoveSeconds`）**

- 梯形、三角形、恰好在臨界距離，分別等於手算值
- 加速度和減速度不對稱時正確
- 距離為 0 時為 0
- 距離為負數、NaN、無窮大時拋異常
- 多組參數下與 `TrapezoidProfile.Plan(...).Duration` 一致

**雷賽軸（DMC、SMC 共用邏輯）**

- 規劃不結束：超過「預計時間 + 餘量」後減速停止並報故障
- 反證：預計時間比餘量長、運動在預計時間內正常結束，不誤報。這條用舊的固定超時會失敗
- 距離為 0：超時等於餘量
- 餘量為 0 或負數：拒絕創建
- 超時溢出：拋 `MotionRejectedException`，沒有調用 `set_profile_unit` 和 `pmove_unit`
- INP 的等待單獨計時：第一段用掉大部分超時後，INP 仍有完整的 `InPositionTimeout`
- 點動鬆開後的停止確認使用 `StopConfirmTimeout`，改 `MoveTimeoutMargin` 不影響它
- 經 `GuardedAxis` 低速點動到軟限位：點動時間比餘量長時不誤報，鬆開後正常結束

## 8. 實施步驟

每步單獨提交，每步全部測試通過：

1. **預計時間**：`MotionProfile.EstimateMoveSeconds`，以及和 `TrapezoidProfile` 的一致性測試
2. **雷賽軸改用計算超時**：刪除 `MotionTimeout`，新增 `MoveTimeoutMargin`、`StopConfirmTimeout`，改現有測試的配置

收尾：提案補實現記錄，更新 `docs/hardware-adapter.md` 的配置示例和上機清單。

## 9. 待決策

| 問題 | 選項 | 傾向 |
|---|---|---|
| 計算方式 | `距離/速度 + 餘量`；含加減速的完整規劃時間 + 餘量 | **完整規劃時間**。對使用者仍然是「大概時間 + 3 秒」，只多幾行公式；加速很慢的重載軸也不用另外加大餘量 |
| 餘量形式 | 只有固定餘量；比例係數 + 固定餘量 | **只有固定餘量**。配置簡單，第一版夠用；上機發現長行程不夠再加比例 |
| 餘量默認值 | 默認 3 秒；必填 | **默認 3 秒**。與其他超時一致；填錯的結果是停軸報故障或報得晚一點，不是「猜錯就不安全」，和急停電平這類必填項不同 |
| 超時上限 | 不設；可選的絕對上限 | **不設**。規劃本來就要走這麼久，加上限又會回到長行程誤報；速度填得過低由工程師調參解決 |

## 10. 上機時要確認

- 規劃不結束的情況：能否實際觸發，例如運動中斷開總線，看先報快照過期還是先報超時
- 堵轉時 `check_done` 的行為：脈衝軸預期脈衝發完就是 1；DMC 總線軸要實測
- S 段：`STime` 調大後，實際運動時間比預計多多少，3 秒是否足夠
- `StopConfirmTimeout`：最慢的軸從最高速減速到停，30 秒是否合適
