namespace StationKit.Motion.Leadshine;

/// <summary>
/// 一張卡的 DLL 調用鎖。雷賽 DLL 的線程安全性沒有文檔保證，同一張卡上的所有調用
/// （輪詢、運動指令、參數讀寫）都經過這把鎖串行執行。
/// <para>
/// 和軸的發令鎖的分工：發令鎖每軸一把，保證這根軸的指令順序，可以跨 await；
/// 卡鎖每卡一把，只包住同步的 DLL 調用。<see cref="Lock"/> 用在 <c>lock</c> 語句裡時，
/// 塊內出現 await 會編譯報錯，所以卡鎖在結構上不可能跨 await 持有。
/// </para>
/// <para>
/// 加鎖順序固定為「先發令鎖、後卡鎖」：持有卡鎖時只做同步的 DLL 調用，不會去等發令鎖，因此不會死鎖。
/// </para>
/// </summary>
public sealed class LeadshineCardGate
{
    /// <summary>可重入：同一線程在持有時再次進入（例如輪詢一根軸時連續讀幾個值）不會死鎖。</summary>
    internal Lock Sync { get; } = new();
}
