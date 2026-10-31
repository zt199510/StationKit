namespace StationKit.Motion.Leadshine;

/// <summary>
/// 一根軸的一次狀態讀取結果，不可變。
/// <see cref="Cycle"/> 是讀取<b>開始時</b>的週期號：判斷快照是否晚於某條指令，必須按開始時間，
/// 不能按發佈時間（見 docs/design/central-polling.md 4.1）。
/// </summary>
public sealed record LeadshineAxisSnapshot(
    long Cycle,
    long Timestamp,
    double Command,
    double Feedback,
    uint Io,
    short Done,
    short PositionResult,
    short FeedbackResult)
{
    public bool ReadFailed => PositionResult != 0 || FeedbackResult != 0 || Done is not (0 or 1);

    /// <summary>
    /// 讀一根軸的狀態。調用方必須持有卡鎖，保證這幾個值是同一時刻的（中間不會插進其他調用）。
    /// axis_io_status 直接返回狀態字，沒有錯誤碼。
    /// </summary>
    internal static LeadshineAxisSnapshot Read(ILeadshineMotionApi api, ushort cardNo, ushort axisNo,
        bool readFeedback, long cycle, long timestamp)
    {
        var positionResult = api.ReadPosition(cardNo, axisNo, out var command);
        api.ReadAxisIoStatus(cardNo, axisNo, out var io);
        var done = api.CheckDone(cardNo, axisNo);
        var feedback = double.NaN;
        short feedbackResult = 0;
        if (readFeedback)
        {
            feedbackResult = api.ReadFeedbackPosition(cardNo, axisNo, out feedback);
        }

        return new LeadshineAxisSnapshot(cycle, timestamp, command, feedback, io, done, positionResult,
            feedbackResult);
    }
}
