# 雷赛 DMC 适配器

`StationKit.Motion.Dmc` 把雷赛 DMC 以太网控制卡上的 EtherCAT 总线轴（`nmc_*` 系列）实现为 `IAxis` 和 `IAxisSafetyPort`。

> **状态**：58 个单元测试基于假 `IDmcNativeApi` 通过，**尚未在实机上验证**。上机前请按文末清单逐项确认。

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
    PulseEquivalent = 1000,               // 脉冲/mm
    HomeProfile = new DmcHomeProfile(Mode: 17, LowVelocity: 5, HighVelocity: 20,
        AccelerationTime: 0.1, DecelerationTime: 0.1, Offset: 0),
});

x.ApplyBaseParameters();                  // 脉冲当量（写入后回读）、S 段时间
await SafetyStartup.RunAsync([new(x, x, xSafety)]);   // 急停、软限位：写入 → 回读 → 使能
await x.HomeAsync();

IAxis guardedX = new GuardedAxis(x, xSafety, interlocks);   // 业务代码只拿包装后的轴
```

`LTDMC.dll` 需要随程序部署到输出目录，本仓库不分发。

## 结构

| 文件 | 职责 |
|---|---|
| `Native/NativeMethods.cs` | P/Invoke 声明，只声明用到的函数 |
| `IDmcNativeApi.cs` / `DmcNativeApi.cs` | 原样返回雷赛错误码，不做判断；测试时可替换 |
| `DmcCallGuard.cs` | 统一校验返回码和回读值，按 `CardId / AxisIndex / ApiName / ReturnCode / Expected / Actual / Stage` 记录日志 |
| `DmcAxis.cs` | `IAxis` + `IAxisSafetyPort` 实现 |
| `DmcController.cs` | 连接、断开、整卡急停 |

## 实现要点

| 契约 | 实现 |
|---|---|
| 到位才完成 | `dmc_pmove_unit` 下发后轮询 `dmc_check_done`；停下后还要核对位置在容差内，否则视为被外部停止 |
| 取消不晚于下发 | 运动、回零、停止共用发令锁；在锁内、`dmc_pmove_unit` / `dmc_home_move` 之前最后检查取消 |
| 停止能随时插入 | 发令锁只在下发时持有，等待到位期间释放 |
| 已下发后取消 | 先 `dmc_stop` 减速停止，再抛出 `OperationCanceledException` |
| 故障必须抛出 | 返回码非零抛 `DmcCallException`（带 `ApiName`、`ReturnCode`）；运动中 ALM 立即急停并读出驱动器错误码 |
| 速度参数 | 雷赛用加减速**时间**：`Tacc = v / a`，`Tdec = v / d` |
| 回零完成 | `dmc_get_home_result == 1` 且 `dmc_check_done == 1`；原点信号不能代替回零结果 |
| 回零参数 | 下发后用 `nmc_get_home_profile` 回读核对，不一致则不下发回零；返回 16088 按兼容码处理，以回读为准 |
| 报警复位 | `nmc_clear_axis_errcode` 后轴回到未使能、未回零状态 |
| 急停 / 软限位 | 写入后从控制卡回读；最大速度、最大加速度没有对应卡参数，由 `GuardedAxis` 在软件层限制 |

## 上机前必须实机确认

- [ ] `dmc_set_emg_mode` 的 `emg_logic`：0 低电平有效、1 高电平有效，与所用卡型手册一致
- [ ] `dmc_set_softlimit_unit` 的 `source_sel`、`SL_action` 取值含义（`DmcAxisOptions.SoftLimitSource` / `SoftLimitAction`）
- [ ] 回零方式 `Mode` 与驱动器支持的 CiA402 回零方法一致，先低速试回零
- [ ] 16088 兼容处理在当前固件上确实是「参数已生效」
- [ ] 脉冲当量：命令走 100 mm，用尺子量实际距离
- [ ] 运动中拔掉急停、触发限位、断开 EtherCAT，确认都能报出故障

## 暂不支持

- 脉冲型轴（非总线）：使能用 `dmc_write_sevon_pin`，有效电平需要实机确认后再加
- 连续插补、点动、多轴联动
