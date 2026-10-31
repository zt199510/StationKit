# 接入真實運動控制卡

以雷賽 DMC 為例。新建項目 `StationKit.Motion.Dmc`，只依賴 `StationKit.Abstractions` 和廠商 SDK。

## 1. 把廠商 SDK 包在一個可替換的接口後面

```csharp
public interface IDmcNativeApi
{
    short PMove(ushort card, ushort axis, int distance, ushort absolute);
    short CheckDone(ushort card, ushort axis);
    short SetEmgMode(ushort card, ushort axis, ushort enable, ushort activeLevel, double filter);
    short GetEmgMode(ushort card, ushort axis, out ushort enable, out ushort activeLevel, out double filter);
    // ...
}
```

真實實現直接調用 `LTDMC.dmc_*`；測試裡用假實現記錄調用順序、注入返回碼。這樣「安全參數必須在使能之前下發」「設置失敗後不能調用使能」這類規則都能寫成單元測試。

## 2. 實現 IAxis 時遵守接口語義

| 語義 | DMC 實現要點 |
|---|---|
| 到位才完成 | 下發 `dmc_pmove` 後輪詢 `dmc_check_done`，同時檢查報警和限位 |
| 取消不晚於下發 | 在持鎖狀態下先檢查令牌，再調用 `dmc_pmove` |
| 已下發後取消 | 先 `dmc_stop`，等待軸停穩，再拋出 `OperationCanceledException` |
| 故障必須拋出 | 任何非 0 返回碼都轉成 `MotionFaultException`，並帶上錯誤碼 |
| 單位換算 | 脈沖當量放在適配器裡，對外只暴露 mm |

## 3. 實現 IAxisSafetyPort

`WriteSafetyConfigAsync` 依次調用急停、軟限位、速度上限的設置函數；`ReadSafetyConfigAsync` 調用對應的 `get` 函數回讀。不要在 `Read` 裡直接返回緩存的配置，否則回讀校驗就形同虛設。

## 4. 用同一套測試驗證

模擬軸的行為測試（`SimAxisTests`）描述的就是 `IAxis` 的契約。新適配器應該能在接上假 `IDmcNativeApi` 之後通過同樣的測試。
