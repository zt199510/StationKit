using StationKit.Abstractions.Motion;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 給每個 SDK 調用加卡鎖的包裝。軸、控制器、輪詢器在構造時把收到的 SDK 包一層，之後只用包裝後的對象，
/// 結構上不存在「忘了加鎖」的調用路徑，不需要靠人逐個檢查。
/// </summary>
internal abstract class GatedLeadshineMotionApi<TApi>(TApi inner, LeadshineCardGate gate) : ILeadshineMotionApi
    where TApi : ILeadshineMotionApi
{
    protected TApi Inner { get; } = inner;

    protected Lock Sync { get; } = gate.Sync;

    public short SetPulseEquivalent(ushort cardNo, ushort axisNo, double value) =>
        Locked(() => Inner.SetPulseEquivalent(cardNo, axisNo, value));

    public short GetPulseEquivalent(ushort cardNo, ushort axisNo, out double value)
    {
        lock (Sync)
        {
            return Inner.GetPulseEquivalent(cardNo, axisNo, out value);
        }
    }

    public short SetProfile(ushort cardNo, ushort axisNo, double minVelocity, double maxVelocity,
        double accelerationTime, double decelerationTime, double stopVelocity) =>
        Locked(() => Inner.SetProfile(cardNo, axisNo, minVelocity, maxVelocity, accelerationTime, decelerationTime,
            stopVelocity));

    public short SetSProfile(ushort cardNo, ushort axisNo, double seconds) =>
        Locked(() => Inner.SetSProfile(cardNo, axisNo, seconds));

    public short SetEmergencyStopMode(ushort cardNo, ushort axisNo, bool enabled, ushort logic) =>
        Locked(() => Inner.SetEmergencyStopMode(cardNo, axisNo, enabled, logic));

    public short GetEmergencyStopMode(ushort cardNo, ushort axisNo, out bool enabled, out ushort logic)
    {
        lock (Sync)
        {
            return Inner.GetEmergencyStopMode(cardNo, axisNo, out enabled, out logic);
        }
    }

    public short SetSoftLimit(ushort cardNo, ushort axisNo, LeadshineSoftLimit softLimit) =>
        Locked(() => Inner.SetSoftLimit(cardNo, axisNo, softLimit));

    public short GetSoftLimit(ushort cardNo, ushort axisNo, out LeadshineSoftLimit softLimit)
    {
        lock (Sync)
        {
            return Inner.GetSoftLimit(cardNo, axisNo, out softLimit);
        }
    }

    public short SetInpMode(ushort cardNo, ushort axisNo, LeadshineInpMode mode) =>
        Locked(() => Inner.SetInpMode(cardNo, axisNo, mode));

    public short GetInpMode(ushort cardNo, ushort axisNo, out LeadshineInpMode mode)
    {
        lock (Sync)
        {
            return Inner.GetInpMode(cardNo, axisNo, out mode);
        }
    }

    public short MoveAbsolute(ushort cardNo, ushort axisNo, double position) =>
        Locked(() => Inner.MoveAbsolute(cardNo, axisNo, position));

    public short MoveContinuous(ushort cardNo, ushort axisNo, JogDirection direction) =>
        Locked(() => Inner.MoveContinuous(cardNo, axisNo, direction));

    public short CheckDone(ushort cardNo, ushort axisNo) => Locked(() => Inner.CheckDone(cardNo, axisNo));

    public short StopAxis(ushort cardNo, ushort axisNo, LeadshineStopMode mode) =>
        Locked(() => Inner.StopAxis(cardNo, axisNo, mode));

    public short EmergencyStop(ushort cardNo) => Locked(() => Inner.EmergencyStop(cardNo));

    public short ReadPosition(ushort cardNo, ushort axisNo, out double position)
    {
        lock (Sync)
        {
            return Inner.ReadPosition(cardNo, axisNo, out position);
        }
    }

    public short SetPosition(ushort cardNo, ushort axisNo, double position) =>
        Locked(() => Inner.SetPosition(cardNo, axisNo, position));

    public short ReadFeedbackPosition(ushort cardNo, ushort axisNo, out double position)
    {
        lock (Sync)
        {
            return Inner.ReadFeedbackPosition(cardNo, axisNo, out position);
        }
    }

    public short SetFeedbackPosition(ushort cardNo, ushort axisNo, double position) =>
        Locked(() => Inner.SetFeedbackPosition(cardNo, axisNo, position));

    public short ReadAxisIoStatus(ushort cardNo, ushort axisNo, out uint status)
    {
        lock (Sync)
        {
            return Inner.ReadAxisIoStatus(cardNo, axisNo, out status);
        }
    }

    protected short Locked(Func<short> call)
    {
        lock (Sync)
        {
            return call();
        }
    }
}

/// <summary>只用公共函數的地方（輪詢器）使用的包裝。</summary>
internal sealed class GatedLeadshineMotionApi(ILeadshineMotionApi inner, LeadshineCardGate gate)
    : GatedLeadshineMotionApi<ILeadshineMotionApi>(inner, gate);
