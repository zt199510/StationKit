using System.Runtime.InteropServices;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// Windows 默認計時精度約 15.6 ms，輪詢週期 5 ms 時睡眠會被拉長到 15.6 ms。
/// 輪詢線程運行期間把系統計時精度提高到 1 ms，停止時恢復。其他系統不需要，直接跳過。
/// </summary>
internal static class TimerResolution
{
    private const uint PeriodMs = 1;

    /// <summary>提高計時精度。返回是否成功；失敗時輪詢照常運行，只是週期會變長（統計日誌裡能看到）。</summary>
    public static bool Begin() => !OperatingSystem.IsWindows() || NativeMethods.timeBeginPeriod(PeriodMs) == 0;

    /// <summary>恢復計時精度，必須和成功的 <see cref="Begin"/> 成對調用。</summary>
    public static void End()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = NativeMethods.timeEndPeriod(PeriodMs);
        }
    }

    private static class NativeMethods
    {
        [DllImport("winmm.dll")]
        internal static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        internal static extern uint timeEndPeriod(uint uPeriod);
    }
}
