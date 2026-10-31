namespace StationKit.Motion.Leadshine.Dmc;

/// <summary>DMC 的加鎖包裝，見 <see cref="GatedLeadshineMotionApi{TApi}"/>。</summary>
internal sealed class GatedDmcNativeApi(IDmcNativeApi inner, LeadshineCardGate gate)
    : GatedLeadshineMotionApi<IDmcNativeApi>(inner, gate), IDmcNativeApi
{
    public short BoardClose() => Locked(Inner.BoardClose);

    public short BoardInit() => Locked(Inner.BoardInit);

    public short BoardInitEthernet(ushort cardNo, string ipAddress) =>
        Locked(() => Inner.BoardInitEthernet(cardNo, ipAddress));

    public short GetTotalAxes(ushort cardNo, out uint totalAxes)
    {
        lock (Sync)
        {
            return Inner.GetTotalAxes(cardNo, out totalAxes);
        }
    }

    public short EnableAxis(ushort cardNo, ushort axisNo) => Locked(() => Inner.EnableAxis(cardNo, axisNo));

    public short DisableAxis(ushort cardNo, ushort axisNo) => Locked(() => Inner.DisableAxis(cardNo, axisNo));

    public short ClearAxisErrorCode(ushort cardNo, ushort axisNo) =>
        Locked(() => Inner.ClearAxisErrorCode(cardNo, axisNo));

    public short GetAxisErrorCode(ushort cardNo, ushort axisNo, out ushort errorCode)
    {
        lock (Sync)
        {
            return Inner.GetAxisErrorCode(cardNo, axisNo, out errorCode);
        }
    }

    public short SetHomeProfile(ushort cardNo, ushort axisNo, DmcHomeProfile profile) =>
        Locked(() => Inner.SetHomeProfile(cardNo, axisNo, profile));

    public short GetHomeProfile(ushort cardNo, ushort axisNo, out DmcHomeProfile profile)
    {
        lock (Sync)
        {
            return Inner.GetHomeProfile(cardNo, axisNo, out profile);
        }
    }

    public short HomeMove(ushort cardNo, ushort axisNo) => Locked(() => Inner.HomeMove(cardNo, axisNo));

    public short GetHomeResult(ushort cardNo, ushort axisNo, out ushort state)
    {
        lock (Sync)
        {
            return Inner.GetHomeResult(cardNo, axisNo, out state);
        }
    }
}
