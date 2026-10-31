using StationKit.Abstractions.Motion;

namespace StationKit.Motion.Dmc;

/// <summary>DMC 调用失败：返回码非零，或回读值与期望不一致。</summary>
public sealed class DmcCallException(string axis, string apiName, short returnCode, string detail)
    : MotionFaultException(axis, detail)
{
    public string ApiName { get; } = apiName;

    public short ReturnCode { get; } = returnCode;
}

/// <summary>
/// 统一检查 DMC 返回码和回读值，并按固定格式记录日志：
/// CardId、AxisIndex、AxisName、ApiName、ReturnCode、Expected、Actual、Stage。
/// 出问题时日志里直接能看到是哪张卡、哪根轴、哪个函数、期望什么、实际是什么。
/// </summary>
internal sealed class DmcCallGuard(ushort cardNo, ushort axisNo, string axisName, Action<string>? log)
{
    public void EnsureSuccess(string apiName, short returnCode, string expected, string stage, string actual = "not-read")
    {
        var detail = BuildDetail(apiName, returnCode, expected, actual, stage);
        if (returnCode != 0)
        {
            log?.Invoke("[报警] " + detail);
            throw new DmcCallException(axisName, apiName, returnCode, detail);
        }

        log?.Invoke(detail);
    }

    public void EnsureReadback(string apiName, bool matches, string expected, string actual, string stage)
    {
        if (matches)
        {
            return;
        }

        var detail = BuildDetail(apiName, 0, expected, actual, stage) + "，回读不一致";
        log?.Invoke("[报警] " + detail);
        throw new DmcCallException(axisName, apiName, 0, detail);
    }

    public void Warn(string apiName, short returnCode, string expected, string stage, string note) =>
        log?.Invoke("[警告] " + BuildDetail(apiName, returnCode, expected, "not-read", stage) + "，" + note);

    private string BuildDetail(string apiName, short returnCode, string expected, string actual, string stage) =>
        $"DMC 调用：CardId={cardNo}, AxisIndex={axisNo}, AxisName={axisName}, ApiName={apiName}, " +
        $"ReturnCode={returnCode}, Expected={expected}, Actual={actual}, Stage={stage}";
}
