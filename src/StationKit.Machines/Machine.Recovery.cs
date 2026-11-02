using StationKit.Abstractions.Motion;

namespace StationKit.Machines;

/// <summary>人工恢復。</summary>
public sealed partial class Machine
{
    /// <summary>
    /// 故障復位：硬件復位 → 檢查條件 → 復位工站 → Uninitialized，之後必須重新初始化。
    /// 條件不滿足時拋 <see cref="MachineRejectedException"/> 列出全部原因，保持 Faulted，排除後重試即可。
    /// <para>
    /// 回到 Uninitialized 而不是 Ready：異常停止時循環被中途取消，位置和工件都不可信，
    /// 不能因為軸的 IsHomed 還是 true 就省掉重新初始化。
    /// </para>
    /// <para>
    /// 報警和不確定資源由人確認後單獨清除，這裡<b>不代勞</b>：自動清除就等於沒有人工確認。
    /// </para>
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        EnsureFaulted();

        // 必須在檢查條件之前：卡故障時 IO 不可信，條件永遠不通過，硬件復位就永遠輪不到
        if (_config.ResetHardware is { } resetHardware)
        {
            try
            {
                await resetHardware(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MachineRejectedException("復位", [$"硬件復位失敗：{ex.Message}"]);
            }
        }

        lock (_sync)
        {
            EnsureFaulted();
            var reasons = ResetBlockers();
            if (reasons.Count > 0)
            {
                throw new MachineRejectedException("復位", reasons);
            }

            foreach (var station in _config.Stations)
            {
                station.Reset();
            }

            // 運動閘在異常停止時可能因工站沒結束而沒有打開；走到這裡工站都結束了
            _config.MotionGate.Open();
            _firstFault = null;
            _state = MachineState.Uninitialized;
        }

        NotifyState(MachineState.Uninitialized);
    }

    private void EnsureFaulted()
    {
        lock (_sync)
        {
            if (_state != MachineState.Faulted)
            {
                throw new MachineRejectedException("復位", [$"當前狀態為 {_state}，只有故障時才能復位"]);
            }
        }
    }

    private List<string> ResetBlockers()
    {
        var reasons = new List<string>();
        if (_activeWork.Exists(t => !t.IsCompleted))
        {
            reasons.Add("還沒有結束：" + DescribeUnfinishedWork());
        }

        reasons.AddRange(_config.Axes.Select(a => a.Axis)
            .Where(a => a.GetStatus().State is AxisState.Moving or AxisState.Homing)
            .Select(a => $"軸 {a.Name} 還在運動"));
        reasons.AddRange(_config.Guards.Select(DescribeViolation).OfType<string>());

        // 不帶來源：涵蓋所有來源，包括工站的善後失敗
        if (_alarms.HasBlockingAlarm())
        {
            reasons.Add("存在未清除的故障報警");
        }

        if (_config.Resources is { } resources)
        {
            reasons.AddRange(resources.Snapshot().Where(r => r.UncertainReason != null)
                .Select(r => $"資源 {r.Name} 處於不確定狀態：{r.UncertainReason}"));
        }

        return reasons;
    }
}
