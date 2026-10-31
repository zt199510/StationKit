using StationKit.Abstractions.Io;
using StationKit.Abstractions.Motion;

namespace StationKit.Abstractions.Safety;

public enum MotionKind
{
    Move,
    Home,
    Jog,
}

/// <summary>
/// 待檢查的運動。<see cref="Target"/>：點位運動為目標位置；點動為本次最遠可能到達的位置
/// （已回零時是軟限位邊界，未回零時為 null）。
/// </summary>
public sealed record MotionRequest(string Axis, MotionKind Kind, double? Target = null)
{
    /// <summary>點動方向，僅 <see cref="MotionKind.Jog"/> 時有值。</summary>
    public JogDirection? Direction { get; init; }
}

/// <summary>
/// 運動互鎖規則，例如「Z 軸不在安全高度時禁止 XY 移動」。
/// 這是軟件層的第一道防線，不能替代硬件互鎖和安全回路。
/// </summary>
public interface IMotionInterlock
{
    string Name { get; }

    /// <summary>允許時返回 null，否則返回阻止原因。</summary>
    string? CheckBlocked(MotionRequest request);
}

public static class Interlock
{
    public static IMotionInterlock Create(string name, Func<MotionRequest, string?> check) =>
        new DelegateInterlock(name, check);

    /// <summary>
    /// 要求輸入信號為 <paramref name="expected"/> 才允許運動，例如「安全門關閉」。
    /// 數據不可信（卡故障、過期）時<b>阻止</b>，不當作滿足。
    /// <paramref name="kinds"/> 為 null 時對所有運動生效。
    /// 只在下發前檢查一次：運動中信號變化（例如開門）不會停軸，必須由硬件安全回路處理。
    /// </summary>
    public static IMotionInterlock RequireInput(string name, IDigitalInput input, bool expected = true,
        IReadOnlyCollection<MotionKind>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new DelegateInterlock(name, request =>
        {
            if (kinds != null && !kinds.Contains(request.Kind))
            {
                return null;
            }

            var reading = input.GetReading();
            if (reading.Value is not { } value)
            {
                return $"{input.Name} 狀態不可信：{reading.FaultReason}";
            }

            return value == expected ? null : $"需要 {input.Name} {Describe(expected)}，當前{Describe(value)}";
        });
    }

    private static string Describe(bool value) => value ? "有效" : "無效";

    private sealed class DelegateInterlock(string name, Func<MotionRequest, string?> check) : IMotionInterlock
    {
        public string Name { get; } = name;

        public string? CheckBlocked(MotionRequest request) => check(request);
    }
}
