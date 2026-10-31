using StationKit.Abstractions.Motion;

namespace StationKit.Motion.Dmc;

/// <summary>DMC 調用失敗：返回碼非零，或回讀值與期望不一致。</summary>
public sealed class DmcCallException(string axis, string apiName, short returnCode, string detail)
    : MotionFaultException(axis, detail)
{
    public string ApiName { get; } = apiName;

    public short ReturnCode { get; } = returnCode;
}

/// <summary>
/// 統一檢查 DMC 返回碼和回讀值，並按固定格式記錄日誌：
/// CardId、AxisIndex、AxisName、ApiName、ReturnCode、Expected、Actual、Stage。
/// 出問題時日誌裡直接能看到是哪張卡、哪根軸、哪個函數、期望什麼、實際是什麼。
/// </summary>
internal sealed class DmcCallGuard(ushort cardNo, ushort axisNo, string axisName, Action<string>? log)
{
    public void EnsureSuccess(string apiName, short returnCode, string expected, string stage, string actual = "not-read")
    {
        var detail = BuildDetail(apiName, returnCode, expected, actual, stage);
        if (returnCode != 0)
        {
            log?.Invoke("[報警] " + detail);
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

        var detail = BuildDetail(apiName, 0, expected, actual, stage) + "，回讀不一致";
        log?.Invoke("[報警] " + detail);
        throw new DmcCallException(axisName, apiName, 0, detail);
    }

    public void Warn(string apiName, short returnCode, string expected, string stage, string note) =>
        log?.Invoke("[警告] " + BuildDetail(apiName, returnCode, expected, "not-read", stage) + "，" + note);

    private string BuildDetail(string apiName, short returnCode, string expected, string actual, string stage) =>
        $"DMC 調用：CardId={cardNo}, AxisIndex={axisNo}, AxisName={axisName}, ApiName={apiName}, " +
        $"ReturnCode={returnCode}, Expected={expected}, Actual={actual}, Stage={stage}";
}
