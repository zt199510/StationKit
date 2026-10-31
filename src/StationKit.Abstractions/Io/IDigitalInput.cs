namespace StationKit.Abstractions.Io;

/// <summary>
/// 數字輸入。所有實現都必須遵守下列語義：
/// <list type="bullet">
/// <item>值是<b>邏輯值</b>：true 表示信號有效，有效電平在 <see cref="IoPointConfig"/> 裡聲明。</item>
/// <item>數據不可信時拋 <see cref="IoFaultException"/>，絕不返回 false：讀失敗當成「沒有信號」屬於偶然安全。</item>
/// <item>等待只接受調用之後讀到的數據，舊數據裡已經滿足的條件不算。</item>
/// </list>
/// </summary>
public interface IDigitalInput
{
    string Name { get; }

    /// <summary>讀取最新數據，非阻塞、線程安全、不調用 DLL。數據不可信時拋 <see cref="IoFaultException"/>。</summary>
    bool Read();

    /// <summary>給界面用：不拋異常，數據不可信時 <see cref="IoReading.Value"/> 為 null 並帶原因。</summary>
    IoReading GetReading();

    /// <summary>
    /// 等到邏輯值等於 <paramref name="expected"/> 並連續保持 <paramref name="stableTime"/>。
    /// 超時拋 <see cref="IoTimeoutException"/>，數據不可信時拋 <see cref="IoFaultException"/>。
    /// </summary>
    Task WaitAsync(bool expected, TimeSpan timeout, TimeSpan stableTime = default,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 數字輸出。<see cref="IDigitalInput.Read"/> 讀回的是卡上輸出口的實際狀態，不是最後一次寫入的值。
/// </summary>
public interface IDigitalOutput : IDigitalInput
{
    /// <summary>
    /// 寫入並回讀核對：等到寫入之後讀到的數據顯示輸出口已是 <paramref name="value"/> 才完成。
    /// 數據不可信時在寫入之前拒絕；寫入失敗、回讀不一致時拋 <see cref="IoFaultException"/>。
    /// 取消只在寫入前生效；已寫入後取消，輸出保持新值，等待核對的過程被中止。
    /// </summary>
    Task SetAsync(bool value, CancellationToken cancellationToken = default);
}
