using StationKit.Abstractions.Motion;
using StationKit.Alarms;
using StationKit.Stations;

namespace StationKit.Demo;

/// <summary>
/// 一個典型的「拍照定位 → 加工 → 回上料位」工站。拍照段需要獨佔共用相機。
/// </summary>
internal sealed class PhotoStation(string name, IAxis x, ResourceArbiter arbiter, AlarmCenter alarms, Action<string> log)
    : Station(name, alarms)
{
    public const string Camera = "相機";

    private const double PhotoPosition = 120;
    private const double WorkPosition = 220;
    private const double LoadPosition = 0;

    private static readonly MotionProfile Fast = MotionProfile.Symmetric(800, 8000);
    private static readonly TimeSpan CameraTimeout = TimeSpan.FromSeconds(2);

    private int _cycle;

    public Action<int>? OnCapture { get; init; }

    protected override async Task RunCycleAsync(StationContext context)
    {
        var cycle = ++_cycle;
        var token = context.Token;

        log($"{Name} 循環 {cycle}：等待相機");
        await arbiter.UseAsync(Camera, Name, CameraTimeout, async ct =>
        {
            log($"{Name} 取得相機，移動到拍照位");
            await x.MoveAbsoluteAsync(PhotoPosition, Fast, ct);
            OnCapture?.Invoke(cycle);
            await Task.Delay(150, ct);
            // 拍照完成後先離開拍照區，再釋放相機
            await x.MoveAbsoluteAsync(WorkPosition, Fast, ct);
            log($"{Name} 拍照完成，釋放相機");
        }, token);

        await Task.Delay(300, token);
        await x.MoveAbsoluteAsync(LoadPosition, Fast, token);
        log($"{Name} 循環 {cycle} 完成");
    }

    protected override async Task OnFaultedAsync(Exception exception)
    {
        await x.StopAsync(StopMode.Immediate);
        log($"{Name} 故障，軸已停止：{exception.Message}");
    }
}
