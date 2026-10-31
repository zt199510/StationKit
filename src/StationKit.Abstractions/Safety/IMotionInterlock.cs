namespace StationKit.Abstractions.Safety;

public enum MotionKind
{
    Move,
    Home,
}

public sealed record MotionRequest(string Axis, MotionKind Kind, double? Target = null);

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

    private sealed class DelegateInterlock(string name, Func<MotionRequest, string?> check) : IMotionInterlock
    {
        public string Name { get; } = name;

        public string? CheckBlocked(MotionRequest request) => check(request);
    }
}
