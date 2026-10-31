# 設計提案：數字 IO 抽象和模擬 IO

> **狀態**：提案，未實現。第 8 節的問題確定後按第 7 節分步實現。

## 1. 背景與問題

目前框架裡只有軸，沒有通用 IO：

- 軸自己的專用信號（ALM、EL、ORG、INP）已經在 `axis_io_status` 裡，由集中輪詢讀取，**不在本提案範圍內**
- 通用輸入輸出（氣缸到位、真空、安全門、光柵、電磁閥、三色燈）沒有抽象。SMC 只有 `WriteOutputBit` 給報警復位用
- `IMotionInterlock` 已經有了，但「安全門關閉」「Z 軸氣缸縮回」這類條件要讀 IO，現在只能在委託裡自己寫
- 演示和測試沒有 IO，工站流程只能用 `Task.Delay` 代替氣缸動作

IO 和運動一樣容易出事故，而且常見問題都很具體：

| 問題 | 後果 |
|---|---|
| 讀 IO 失敗時返回 0 | 「安全門打開」被當成「沒有信號」，或者「縮回到位」讀不到被當成「沒到位」，取決於接線，屬於偶然安全 |
| 輸出寫完立即讀感應器 | 讀到的是寫之前的狀態（和 central-polling.md 4.1 是同一個陷阱） |
| 氣缸只看伸出到位 | 感應器粘連時，閥還沒動就「到位」了 |
| 低電平有效的信號，業務代碼自己取反 | 換一台接線不同的機台，取反漏改一處就是事故 |
| 輸出位被兩處使用 | 報警復位用的輸出位又被配置成電磁閥 |

## 2. 目標結構

```text
業務 / 工站 / 互鎖
   │  IDigitalInput.Read() / WaitAsync()      IDigitalOutput.Write() / SetAsync()
   │  Cylinder.ExtendAsync()                  Interlock.RequireInput(...)
   ▼
IIoBoard（每張卡一個）
   ├─ SimIoBoard        模擬：輸入可由測試設置，輸出可掛模擬設備（例如氣缸延時給到位）
   └─ LeadshineIoBoard  雷賽：輸入輸出由集中輪詢每週期按端口讀取，寫輸出經過卡鎖
                         └─ 與軸共用 LeadshineStatusPoller、卡鎖、卡故障、過期檢測
```

業務代碼只拿到具名的 `IDigitalInput` / `IDigitalOutput`，不接觸卡號和位號。

## 3. 接口語義（寫死在接口文檔裡）

### 3.1 邏輯值，不是電平

每個信號在配置裡聲明位號和有效電平（`ActiveLevel`），接口上只有**邏輯值**：`true` 表示「信號有效」
（門已關、氣缸到位、閥打開）。取反只在配置裡做一次，業務代碼裡不出現電平。
有效電平未指定時拒絕創建，不默認（與急停電平同一原則）。

### 3.2 讀取：故障必須被看見

```csharp
public interface IDigitalInput
{
    string Name { get; }

    /// <summary>邏輯值。數據不可信（卡故障、快照過期、尚無數據）時拋 IoFaultException，絕不返回 false。</summary>
    bool Read();

    /// <summary>給界面用：不拋異常，數據不可信時 Value 為 null 並帶原因。</summary>
    IoReading GetReading();

    /// <summary>
    /// 等到邏輯值等於 expected 並保持 stableTime。超時拋 IoTimeoutException（帶信號名、期望值、當前值）；
    /// 數據不可信時拋 IoFaultException；只接受調用之後讀到的數據（見 4.1）。
    /// </summary>
    Task WaitAsync(bool expected, TimeSpan timeout, TimeSpan stableTime = default,
        CancellationToken cancellationToken = default);
}
```

### 3.3 輸出：寫完要核對

```csharp
public interface IDigitalOutput : IDigitalInput  // Read() 讀回的是卡上輸出口的實際狀態
{
    /// <summary>
    /// 寫入並回讀核對：等到下發之後的輪詢數據顯示輸出口已是 value 才完成；
    /// 返回碼錯誤、回讀不一致、卡故障時拋 IoFaultException。
    /// </summary>
    Task SetAsync(bool value, CancellationToken cancellationToken = default);
}
```

不提供「只寫不核對」的同步方法。三色燈這類不需要核對的輸出也走 `SetAsync`，代價只是等一個輪詢週期。

### 3.4 氣缸

氣缸是工站裡最常用的 IO 組合，單獨封裝，不讓每個工站各自寫一遍：

```csharp
public sealed class Cylinder
{
    // 單電控：一個閥輸出；雙電控：伸出閥 + 縮回閥（寫一個前先關另一個）
    // 感應器：伸出到位、縮回到位，各自可選（沒有就按時間判定，必須顯式配置 NoSensorDelay）
    Task ExtendAsync(CancellationToken ct);
    Task RetractAsync(CancellationToken ct);
    CylinderState GetState();   // Extended / Retracted / Moving / Fault（兩個到位同時有效）
}
```

`ExtendAsync` 的完成條件：**伸出到位有效並且縮回到位無效**，保持 `StableTime`。兩個感應器同時有效直接報故障。
超時異常帶上「哪個感應器沒到」，報警建議可以直接寫「檢查氣壓 / 感應器 / 節流閥」。

### 3.5 互鎖

```csharp
Interlock.RequireInput("安全門", doorClosed, expected: true, kinds: [MotionKind.Move, MotionKind.Home]);
```

讀取拋 `IoFaultException` 時視為**阻止**，原因寫明「安全門 狀態不可信：卡故障…」。
同時修改 `GuardedAxis`：任何互鎖拋出異常都轉為 `MotionRejectedException`（目前會原樣拋出，軸雖然不動，但異常類型不對，
工站會把它當成運動故障而不是拒絕）。

## 4. 關鍵陷阱

### 4.1 寫輸出後讀到舊的感應器狀態

和軸一樣：寫輸出時，本輪的輸入可能已經讀過了。典型是和上下游的握手：
寫「請求取料」→ 等「對方完成」。上一次握手的「完成」信號還沒撤掉，舊快照裡它是有效的，
等待立即成立，本次請求對方還沒處理就被當成完成。

**對策**：沿用輪詢的週期號圍欄。`WaitAsync` 和 `SetAsync` 開始時記錄 `CurrentCycle`，只接受週期號更大的數據。
`SimIoBoard` 也有週期號（每次設置輸入或推進時間都算一輪），保證模擬和實機行為一致。

圍欄只排除「下發前讀的數據」。對方確實還沒撤掉信號時，新數據裡它仍然有效，這要靠握手協議本身：
先等「完成」無效，再發請求。第一版提供 `Handshake` 輔助類把這個順序寫死，見步驟 3。

### 4.2 讀取函數直接返回電平，沒有錯誤碼

雷賽的 `dmc_read_inbit` / `smc_read_inbit` 直接返回電平，通訊失敗時返回什麼沒有保證，可能就是 0。
和 `axis_io_status` 一樣，**無法區分「低電平」和「讀取失敗」**。

**對策**：

- 優先使用帶錯誤碼的版本（`*_read_inport_ex` 等，需查手冊確認所用卡型和 SDK 版本是否提供）
- 沒有帶錯誤碼的版本時，在同一次持卡鎖內加讀一個有錯誤碼的函數（例如軸的 `get_position_unit`）作為通訊探針，
  探針失敗就視為本輪 IO 數據不可信
- 按**端口**讀（一次 32 位），不按位讀：一輪只多一兩次調用，以太網卡上很重要

### 4.3 卡故障時輸出保持原狀

卡故障或程序退出時，卡上的輸出口保持最後的狀態，軟件**寫不進去**。閥會一直開著。

**對策**：軟件做不到，只能寫進文檔和上機清單：

- 危險的執行機構（夾爪、刀具、加熱）必須在安全回路裡有硬件斷開
- 卡故障時，`IoBoard` 把所有輸出的狀態標為「未知」，`Cylinder.GetState()` 返回 `Fault`，工站不會繼續
- 恢復時（`ResetFault` 之後）不自動重寫輸出，由人工確認後執行復位流程

### 4.4 輸出位被重複使用

同一張卡上，一個輸出位只能有一個所有者。`IoBoard` 創建輸出時登記位號，重複登記拋異常；
SMC 軸報警復位使用 `OutputBit` 時也到同一張表登記。

## 5. 不能改變的行為

- 軸的所有行為、測試保持不變；軸的輪詢讀取不受 IO 讀取影響（IO 讀取失敗仍按 central-polling.md 4.3 整卡故障）
- 卡鎖順序仍是「發令鎖 → 卡鎖」，卡鎖不跨 `await`
- `GetReading()` 和界面刷新不調 DLL，只讀快照

## 6. 測試計劃

用 `SimIoBoard` + 假時鐘，結果確定：

- [ ] 有效電平：低電平有效的信號，電平 0 時 `Read()` 為 `true`；有效電平未配置時拒絕創建
- [ ] 卡故障 / 快照過期 / 尚無數據時，`Read()` 拋異常，`GetReading()` 返回 null 和原因
- [ ] `WaitAsync` 只接受調用之後的數據：舊數據裡已滿足的條件不算（4.1）
- [ ] `WaitAsync` 的 `stableTime`：抖動期間不完成
- [ ] `SetAsync` 回讀不一致時拋異常（模擬注入：輸出口卡死）
- [ ] 氣缸：正常伸縮；超時；兩個到位同時有效報故障；伸出到位粘連時不會「立即完成」
- [ ] 互鎖：輸入不滿足時拒絕；讀取故障時拒絕並寫明原因；互鎖拋異常時 `GuardedAxis` 轉為拒絕
- [ ] 輸出位重複登記拋異常（含 SMC 報警復位位）
- [ ] 握手：上一次的「完成」未撤銷時不發新請求；舊數據裡的「完成」不算本次完成

雷賽部分用假 SDK：

- [ ] 一輪輪詢中每個登記的端口讀一次，軸的讀取次數不變
- [ ] IO 讀取失敗（返回碼錯誤 / 通訊探針失敗）整卡故障，本卡軸禁止操作
- [ ] 寫輸出經過卡鎖；卡故障時寫輸出在調用 DLL 之前被拒絕

## 7. 實施步驟

每步單獨提交，每步全部測試通過：

1. **接口與模擬**：`StationKit.Abstractions/Io`（接口、`IoReading`、異常）+ `StationKit.Io.Simulation`（`SimIoBoard`、週期號、故障注入）
2. **氣缸**：`Cylinder` 和模擬氣缸（閥動作後按配置延時翻轉感應器，可注入「不到位」「粘連」）
3. **互鎖與握手**：`Interlock.RequireInput`；`Handshake`（先等對方撤銷信號，再發請求，再等完成，最後撤銷請求）；`GuardedAxis` 把互鎖異常轉為拒絕；報警代碼表加 IO 類報警
4. **演示**：Demo 加上料氣缸和安全門，`--door` 參數演示運動中開門被拒絕
5. **雷賽 IO 讀取**：原生接口補 IO 函數；輪詢器每週期讀登記的輸入 / 輸出端口，存入卡級 IO 快照
6. **雷賽 IO 寫入**：`SetAsync` 經過卡鎖下發，按週期號回讀核對；SMC 報警復位位改為到 IO 登記表登記

1～4 不依賴硬件，做完就能在 Demo 裡完整演示；5～6 和軸一樣，**待實機驗證**。

## 8. 待決策

| 問題 | 選項 | 傾向 |
|---|---|---|
| `Read()` 遇到不可信數據 | 拋異常；返回 `bool?` | **拋異常**（與「運動故障拋異常」一致；界面用 `GetReading()`） |
| 項目劃分 | 接口放 Abstractions、模擬放新項目 `StationKit.Io.Simulation`；或模擬並入 `Motion.Simulation` | 新項目：IO 不屬於運動，Demo 以外的使用者可以只引用 IO |
| `Cylinder` 放哪裡 | Abstractions（與 `MotionGroup` 同層）；新項目 `StationKit.Devices` | Abstractions：只依賴 IO 接口，沒有其他依賴 |
| 輸入去抖 | 全部在軟件做（`stableTime`）；用卡上的硬件濾波 | 軟件 `stableTime` 由調用方指定，默認 0；卡上濾波按現場配置，不在本提案 |
| EtherCAT / CANopen 擴展 IO 模塊 | 本次一起做；先只做卡上本地 IO | **先只做本地 IO**，擴展模塊的函數和錯誤處理不同，單獨評估 |
| IO 輪詢週期 | 和軸同一週期；IO 單獨較慢的週期 | 同一週期：按端口讀只多一兩次調用，上機看統計日誌再定 |

## 9. 上機時要確認

- 所用卡型的 IO 讀取函數是否有帶錯誤碼的版本；沒有時，斷開網線看 `read_inport` 返回什麼
- 每個輸入的有效電平：手動觸發，看邏輯值是否正確
- 輸出寫入後多久能回讀到（決定 `SetAsync` 一般等幾個週期）
- 卡故障、程序退出時，閥的實際狀態，以及安全回路能否切斷危險動作
