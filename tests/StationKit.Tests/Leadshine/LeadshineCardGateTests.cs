using StationKit.Motion.Leadshine;
using StationKit.Motion.Leadshine.Dmc;
using StationKit.Motion.Leadshine.Smc;

namespace StationKit.Tests.Leadshine;

public class LeadshineCardGateTests
{
    private static readonly TimeSpan CallDuration = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task 同一控制器的多根軸並行下發參數_DLL調用不會重疊()
    {
        var api = new FakeDmcNativeApi { CallDuration = CallDuration };
        var controller = new DmcController(api, 0, "192.168.5.11");
        using var x = controller.CreateAxis(DmcOptions(0));
        using var y = controller.CreateAxis(DmcOptions(1));
        using var z = controller.CreateAxis(DmcOptions(2));

        await RunInParallelAsync(x.ApplyBaseParameters, y.ApplyBaseParameters, z.ApplyBaseParameters,
            () => controller.EmergencyStop());

        Assert.Equal(1, api.MaxConcurrentCalls);
    }

    [Fact]
    public async Task SMC控制器同樣串行()
    {
        var api = new FakeSmcNativeApi { CallDuration = CallDuration };
        var controller = new SmcController(api, 0, "192.168.5.11");
        using var x = controller.CreateAxis(SmcOptions(0));
        using var y = controller.CreateAxis(SmcOptions(1));

        await RunInParallelAsync(x.ApplyBaseParameters, y.ApplyBaseParameters, () => controller.EmergencyStop());

        Assert.Equal(1, api.MaxConcurrentCalls);
    }

    [Fact]
    public async Task 探針本身有效_各用各的鎖時能檢測到重疊()
    {
        // 反證：不共用卡鎖時探針能看到併發，說明上面兩個測試的「1」不是探針失效造成的
        var api = new FakeDmcNativeApi { CallDuration = CallDuration };
        using var x = new DmcAxis(api, DmcOptions(0), new LeadshineCardGate());
        using var y = new DmcAxis(api, DmcOptions(1), new LeadshineCardGate());

        for (var attempt = 0; attempt < 20 && api.MaxConcurrentCalls < 2; attempt++)
        {
            await RunInParallelAsync(x.ApplyBaseParameters, y.ApplyBaseParameters);
        }

        Assert.True(api.MaxConcurrentCalls > 1);
    }

    private static async Task RunInParallelAsync(params Action[] actions)
    {
        // 每個動作重複多次，並且同時起跑，盡量製造重疊
        using var start = new ManualResetEventSlim();
        var tasks = actions.Select(action => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 10; i++)
            {
                action();
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks);
    }

    private static DmcAxisOptions DmcOptions(ushort axisNo) => new()
    {
        CardNo = 0,
        AxisNo = axisNo,
        Name = $"X{axisNo}",
        PulseEquivalent = 1000,
        InPositionSignal = LeadshineInpMode.Disabled,
        Feedback = LeadshineFeedback.None,
        HomeProfile = new DmcHomeProfile(17, 5, 20, 0.1, 0.1, 0),
    };

    private static SmcAxisOptions SmcOptions(ushort axisNo) => new()
    {
        CardNo = 0,
        AxisNo = axisNo,
        Name = $"Y{axisNo}",
        PulseEquivalent = 1000,
        InPositionSignal = LeadshineInpMode.Disabled,
        Feedback = LeadshineFeedback.None,
        ServoOnLevel = 0,
        AlarmSignal = new SmcSignalMode(true, 0, 0),
        LimitSignal = new SmcSignalMode(true, 0, 0),
        HomeMode = new SmcHomeMode(0, 1, 2, 0),
        HomeSpeed = new SmcHomeSpeed(5, 20, 0.1, 0.1),
        HomePinLogic = new SmcHomePinLogic(0, 0),
    };
}
