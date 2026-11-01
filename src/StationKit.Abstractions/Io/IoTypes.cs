namespace StationKit.Abstractions.Io;

public enum IoKind
{
    Input,
    Output,
}

/// <summary>
/// 一個 IO 點的配置。<see cref="ActiveHigh"/>：true 高電平有效，false 低電平有效，null 未指定（拒絕創建）。
/// 業務代碼只看邏輯值，取反只在這裡做一次：換一台接線不同的機台，只改配置。
/// <para>
/// 消抖（只用於輸入）：<see cref="ActivateDelay"/> / <see cref="DeactivateDelay"/> 分別是變成有效、變成無效前
/// 必須連續保持的時間，默認 0 立即生效。安全信號只能讓「變安全」的方向延時：
/// 安全門關閉用 <see cref="ActivateDelay"/>（關穩了才算關），<see cref="DeactivateDelay"/> 保持 0（打開立即生效）。
/// 啟動時從無效開始過濾；精度一個輪詢週期。見 docs/design/digital-io.md 第 11 節。
/// </para>
/// </summary>
public sealed record IoPointConfig(string Name, ushort Bit, bool? ActiveHigh)
{
    public TimeSpan ActivateDelay { get; init; }

    public TimeSpan DeactivateDelay { get; init; }

    public bool HasDebounce => ActivateDelay > TimeSpan.Zero || DeactivateDelay > TimeSpan.Zero;
}

/// <summary>
/// 一個 IO 點在某一輪讀取中的電平。<see cref="Cycle"/> 是讀取<b>開始時</b>的週期號，用來排除下發前讀的數據；
/// <see cref="Fault"/> 不為 null 時數據不可信，<see cref="Level"/> 沒有意義。
/// <see cref="Cycle"/> 為 0 表示還沒讀到過數據（例如輪詢剛啟動）：讀取和寫入拒絕，等待繼續等到超時。
/// </summary>
public readonly record struct IoSample(long Cycle, bool Level, string? Fault = null)
{
    public static IoSample NoData => new(0, false);

    public bool HasData => Cycle > 0;
}

/// <summary>給界面用的讀數：數據不可信時 <see cref="Value"/> 為 null，<see cref="FaultReason"/> 寫明原因。</summary>
public readonly record struct IoReading(bool? Value, string? FaultReason)
{
    public bool IsValid => Value.HasValue;
}

/// <summary>IO 數據不可信（卡故障、數據過期、尚無數據）或寫入失敗。不能當作「信號無效」處理。</summary>
public class IoFaultException(string signal, string reason) : Exception($"信號 {signal}：{reason}")
{
    public string Signal { get; } = signal;

    public string Reason { get; } = reason;
}

/// <summary>等待信號超時。<see cref="Actual"/> 為 null 表示超時前沒有讀到等待開始之後的數據。</summary>
public sealed class IoTimeoutException(string signal, bool expected, bool? actual, TimeSpan timeout)
    : IoFaultException(signal,
        $"{timeout.TotalMilliseconds:0} ms 內未等到{Describe(expected)}，當前{(actual is { } a ? Describe(a) : "沒有新數據")}")
{
    public bool Expected { get; } = expected;

    public bool? Actual { get; } = actual;

    public TimeSpan Timeout { get; } = timeout;

    private static string Describe(bool value) => value ? "有效" : "無效";
}

/// <summary>氣缸動作失敗：超時、兩個到位同時有效。<see cref="IoFaultException.Signal"/> 是氣缸名。</summary>
public sealed class CylinderFaultException(string cylinder, string reason) : IoFaultException(cylinder, reason);

/// <summary>與上下游的握手失敗。<see cref="IoFaultException.Signal"/> 是握手名。</summary>
public sealed class HandshakeFaultException(string handshake, string reason) : IoFaultException(handshake, reason);
