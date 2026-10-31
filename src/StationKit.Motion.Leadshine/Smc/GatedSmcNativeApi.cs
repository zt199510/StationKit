namespace StationKit.Motion.Leadshine.Smc;

/// <summary>SMC 的加鎖包裝，見 <see cref="GatedLeadshineMotionApi{TApi}"/>。</summary>
internal sealed class GatedSmcNativeApi(ISmcNativeApi inner, LeadshineCardGate gate)
    : GatedLeadshineMotionApi<ISmcNativeApi>(inner, gate), ISmcNativeApi
{
    public short BoardInit(ushort connectNo, ushort connectType, string connectString, uint baud) =>
        Locked(() => Inner.BoardInit(connectNo, connectType, connectString, baud));

    public short BoardClose(ushort connectNo) => Locked(() => Inner.BoardClose(connectNo));

    public short WriteServoOn(ushort connectNo, ushort axisNo, ushort level) =>
        Locked(() => Inner.WriteServoOn(connectNo, axisNo, level));

    public short ReadServoOn(ushort connectNo, ushort axisNo) => Locked(() => Inner.ReadServoOn(connectNo, axisNo));

    public short SetAlarmMode(ushort connectNo, ushort axisNo, SmcSignalMode mode) =>
        Locked(() => Inner.SetAlarmMode(connectNo, axisNo, mode));

    public short GetAlarmMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode)
    {
        lock (Sync)
        {
            return Inner.GetAlarmMode(connectNo, axisNo, out mode);
        }
    }

    public short SetLimitMode(ushort connectNo, ushort axisNo, SmcSignalMode mode) =>
        Locked(() => Inner.SetLimitMode(connectNo, axisNo, mode));

    public short GetLimitMode(ushort connectNo, ushort axisNo, out SmcSignalMode mode)
    {
        lock (Sync)
        {
            return Inner.GetLimitMode(connectNo, axisNo, out mode);
        }
    }

    public short SetHomeMode(ushort connectNo, ushort axisNo, SmcHomeMode mode) =>
        Locked(() => Inner.SetHomeMode(connectNo, axisNo, mode));

    public short GetHomeMode(ushort connectNo, ushort axisNo, out SmcHomeMode mode)
    {
        lock (Sync)
        {
            return Inner.GetHomeMode(connectNo, axisNo, out mode);
        }
    }

    public short SetHomeSpeed(ushort connectNo, ushort axisNo, SmcHomeSpeed speed) =>
        Locked(() => Inner.SetHomeSpeed(connectNo, axisNo, speed));

    public short GetHomeSpeed(ushort connectNo, ushort axisNo, out SmcHomeSpeed speed)
    {
        lock (Sync)
        {
            return Inner.GetHomeSpeed(connectNo, axisNo, out speed);
        }
    }

    public short SetHomePinLogic(ushort connectNo, ushort axisNo, SmcHomePinLogic logic) =>
        Locked(() => Inner.SetHomePinLogic(connectNo, axisNo, logic));

    public short GetHomePinLogic(ushort connectNo, ushort axisNo, out SmcHomePinLogic logic)
    {
        lock (Sync)
        {
            return Inner.GetHomePinLogic(connectNo, axisNo, out logic);
        }
    }

    public short SetHomePosition(ushort connectNo, ushort axisNo, SmcHomePosition position) =>
        Locked(() => Inner.SetHomePosition(connectNo, axisNo, position));

    public short GetHomePosition(ushort connectNo, ushort axisNo, out SmcHomePosition position)
    {
        lock (Sync)
        {
            return Inner.GetHomePosition(connectNo, axisNo, out position);
        }
    }

    public short HomeMove(ushort connectNo, ushort axisNo) => Locked(() => Inner.HomeMove(connectNo, axisNo));

    public short GetHomeResult(ushort connectNo, ushort axisNo, out ushort state)
    {
        lock (Sync)
        {
            return Inner.GetHomeResult(connectNo, axisNo, out state);
        }
    }

    public short WriteErcPin(ushort connectNo, ushort axisNo, ushort level) =>
        Locked(() => Inner.WriteErcPin(connectNo, axisNo, level));
}
