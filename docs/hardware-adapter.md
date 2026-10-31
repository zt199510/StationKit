# 雷賽 DMC 適配器

`StationKit.Motion.Dmc` 把雷賽 DMC 以太網控制卡上的 EtherCAT 總線軸（`nmc_*` 系列）實現為 `IAxis` 和 `IAxisSafetyPort`。

> **狀態**：58 個單元測試基於假 `IDmcNativeApi` 通過，**尚未在實機上驗證**。上機前請按文末清單逐項確認。

## 使用

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
    HomeProfile = new DmcHomeProfile(Mode: 17, LowVelocity: 5, HighVelocity: 20,
        AccelerationTime: 0.1, DecelerationTime: 0.1, Offset: 0),
});

x.ApplyBaseParameters();                  // 脈衝當量（寫入後回讀）、S 段時間
await SafetyStartup.RunAsync([new(x, x, xSafety)]);   // 急停、軟限位：寫入 → 回讀 → 使能
await x.HomeAsync();

IAxis guardedX = new GuardedAxis(x, xSafety, interlocks);   // 業務代碼只拿包裝後的軸
```

`LTDMC.dll` 需要隨程序部署到輸出目錄，本倉庫不分發。

## 結構

| 文件 | 職責 |
|---|---|
| `Native/NativeMethods.cs` | P/Invoke 聲明，只聲明用到的函數 |
| `IDmcNativeApi.cs` / `DmcNativeApi.cs` | 原樣返回雷賽錯誤碼，不做判斷；測試時可替換 |
| `DmcCallGuard.cs` | 統一校驗返回碼和回讀值，按 `CardId / AxisIndex / ApiName / ReturnCode / Expected / Actual / Stage` 記錄日誌 |
| `DmcAxis.cs` | `IAxis` + `IAxisSafetyPort` 實現 |
| `DmcController.cs` | 連接、斷開、整卡急停 |

## 實現要點

| 契約 | 實現 |
|---|---|
| 到位才完成 | `dmc_pmove_unit` 下發後輪詢 `dmc_check_done`；停下後還要核對位置在容差內，否則視為被外部停止 |
| 取消不晚於下發 | 運動、回零、停止共用發令鎖；在鎖內、`dmc_pmove_unit` / `dmc_home_move` 之前最後檢查取消 |
| 停止能隨時插入 | 發令鎖只在下發時持有，等待到位期間釋放 |
| 已下發後取消 | 先 `dmc_stop` 減速停止，再拋出 `OperationCanceledException` |
| 故障必須拋出 | 返回碼非零拋 `DmcCallException`（帶 `ApiName`、`ReturnCode`）；運動中 ALM 立即急停並讀出驅動器錯誤碼 |
| 速度參數 | 雷賽用加減速**時間**：`Tacc = v / a`，`Tdec = v / d` |
| 回零完成 | `dmc_get_home_result == 1` 且 `dmc_check_done == 1`；原點信號不能代替回零結果 |
| 回零參數 | 下發後用 `nmc_get_home_profile` 回讀核對，不一致則不下發回零；返回 16088 按兼容碼處理，以回讀為準 |
| 報警復位 | `nmc_clear_axis_errcode` 後軸回到未使能、未回零狀態 |
| 急停 / 軟限位 | 寫入後從控制卡回讀；最大速度、最大加速度沒有對應卡參數，由 `GuardedAxis` 在軟件層限制 |

## 上機前必須實機確認

- [ ] `dmc_set_emg_mode` 的 `emg_logic`：0 低電平有效、1 高電平有效，與所用卡型手冊一致
- [ ] `dmc_set_softlimit_unit` 的 `source_sel`、`SL_action` 取值含義（`DmcAxisOptions.SoftLimitSource` / `SoftLimitAction`）
- [ ] 回零方式 `Mode` 與驅動器支持的 CiA402 回零方法一致，先低速試回零
- [ ] 16088 兼容處理在當前固件上確實是「參數已生效」
- [ ] 脈衝當量：命令走 100 mm，用尺子量實際距離
- [ ] 運動中拔掉急停、觸發限位、斷開 EtherCAT，確認都能報出故障

## 暫不支持

- 脈衝型軸（非總線）：使能用 `dmc_write_sevon_pin`，有效電平需要實機確認後再加
- 連續插補、點動、多軸聯動
