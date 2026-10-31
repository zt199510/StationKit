using StationKit.Abstractions.Io;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 一張卡的通用 IO 在一輪輪詢中的讀數，不可變。按端口存放，每個端口 32 位，位號 n 在第 n / 32 個端口的第 n % 32 位。
/// <see cref="Cycle"/> 與軸快照相同，是本輪讀取<b>開始時</b>的週期號。
/// </summary>
public sealed record LeadshineIoSnapshot(long Cycle, long Timestamp, IReadOnlyList<uint> Inputs,
    IReadOnlyList<uint> Outputs)
{
    public const int BitsPerPort = 32;

    /// <summary>位號對應的電平。位號超出已輪詢的端口時拋 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public bool Level(IoKind kind, ushort bit)
    {
        var ports = kind == IoKind.Input ? Inputs : Outputs;
        var port = bit / BitsPerPort;
        if (port >= ports.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit, $"位號 {bit} 超出輪詢的 {ports.Count} 個端口");
        }

        return (ports[port] >> (bit % BitsPerPort) & 1) == 1;
    }
}
