using System.Globalization;
using StationKit.Abstractions.Motion;
using StationKit.Abstractions.Safety;

namespace StationKit.Motion.Leadshine;

/// <summary>
/// 雷賽軸的公共實現（DMC 總線軸、SMC 脈衝軸共用）。安全相關的邏輯只在這裡寫一份：
/// <list type="bullet">
/// <item>運動、回零、停止共用一把發令鎖：取消後不會有晚到的指令覆蓋停止。</item>
/// <item>發令鎖只在下發指令時持有，等待到位時釋放，停止隨時能插進來。</item>
/// <item>到位判定：check_done 返回 1 且位置在容差內；停了但沒到位視為被外部停止，報故障。</item>
/// <item>回零完成以 get_home_result == 1 為準，原點信號不能代替回零結果。</item>
/// <item>運動中 ALM / EMG 有效立即急停並報故障；超時減速停止並報故障。</item>
/// </list>
/// 子類只實現各系列不同的部分：使能、回零參數、報警復位。
/// </summary>
public abstract class LeadshineAxisBase : IAxis, IAxisSafetyPort, IDisposable
{
    protected const double ReadbackTolerance = 1e-6;
    private const ushort EmergencyLogicLow = 0;  // emg_logic：0 低電平有效
    private const ushort EmergencyLogicHigh = 1; // 1 高電平有效

    private readonly ILeadshineMotionApi _api;
    private readonly LeadshineStatusPoller _poller;
    private readonly LeadshineAxisOptions _options;
    private readonly SemaphoreSlim _motionCommandGate = new(1, 1);
    private readonly object _sync = new();
    private bool _enabled;
    private bool _homed;
    private bool _homing;
    private AxisSafetyConfig _softwareSafety = new();

    /// <param name="api">已經用 <paramref name="poller"/> 的卡鎖包裝過的 SDK。</param>
    protected LeadshineAxisBase(ILeadshineMotionApi api, LeadshineAxisOptions options, LeadshineStatusPoller poller,
        string apiPrefix, TimeProvider? timeProvider, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(poller);
        if (poller.CardNo != options.CardNo)
        {
            throw new ArgumentException($"軸 {options.Name} 的卡號 {options.CardNo} 與輪詢器的卡號 {poller.CardNo} 不一致");
        }

        _api = api;
        _poller = poller;
        _options = options;
        ApiPrefix = apiPrefix;
        Time = timeProvider ?? TimeProvider.System;
        Guard = new LeadshineCallGuard(apiPrefix.ToUpperInvariant(), options.CardNo, options.AxisNo, options.Name, log);
        if (options.InPositionSignal.Logic > 1)
        {
            throw new ArgumentException($"{options.Name}：INP 有效電平只能是 0 或 1，實際 {options.InPositionSignal.Logic}");
        }

        if (options.SettleTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"{options.Name}：停穩延時不能為負數（{options.SettleTime}）");
        }

        if (options.InPositionSignal.Enabled && options.SettleTime > TimeSpan.Zero)
        {
            // 兩個都配，出了問題分不清是哪個起作用；有 INP 就以 INP 為準
            throw new ArgumentException($"{options.Name}：已啟用 INP，不能再配置停穩延時");
        }

        if (options.HomeMethod == LeadshineHomeMethod.AbsoluteEncoder)
        {
            if (options.Feedback != LeadshineFeedback.Encoder)
            {
                throw new ArgumentException($"{options.Name}：絕對值回零需要編碼器反饋（Feedback = Encoder）");
            }

            if (options.AbsoluteHomeProfile is null)
            {
                throw new ArgumentException($"{options.Name}：絕對值回零未配置走到零點的速度（AbsoluteHomeProfile）");
            }

            options.AbsoluteHomeProfile.Validate();
            if (!double.IsFinite(options.AbsoluteHomeTarget))
            {
                throw new ArgumentException($"{options.Name}：絕對值回零的零點位置非法（{options.AbsoluteHomeTarget}）");
            }
        }

        // 所有校驗通過後才註冊，避免構造失敗的軸留在輪詢列表裡
        poller.Register(options.AxisNo, options.Feedback == LeadshineFeedback.Encoder);
    }

    public string Name => _options.Name;

    /// <summary>函數名前綴：dmc 或 smc，用於日誌裡的 ApiName。</summary>
    protected string ApiPrefix { get; }

    protected TimeProvider Time { get; }

    internal LeadshineCallGuard Guard { get; }

    protected ushort Card => _options.CardNo;

    protected ushort Axis => _options.AxisNo;

    /// <summary>
    /// 下發基礎參數（脈衝當量、S 段時間，以及各系列自己的信號配置）。必須在安全初始化和使能之前調用。
    /// 脈衝當量錯了，所有距離都會按比例走錯，所以寫入後必須回讀。
    /// </summary>
    public void ApplyBaseParameters()
    {
        const string stage = "BaseParameters";
        var equiv = _options.PulseEquivalent;
        if (!double.IsFinite(equiv) || equiv <= 0)
        {
            throw new ArgumentException($"{Name}：脈衝當量非法 {equiv}");
        }

        Guard.EnsureSuccess(Api("set_equiv"), _api.SetPulseEquivalent(Card, Axis, equiv), $"equiv={equiv:R}", stage);
        var result = _api.GetPulseEquivalent(Card, Axis, out var actual);
        Guard.EnsureSuccess(Api("get_equiv"), result, $"equiv={equiv:R}", stage, $"equiv={actual:R}");
        Guard.EnsureReadback(Api("get_equiv"), Math.Abs(actual - equiv) <= ReadbackTolerance,
            $"equiv={equiv:R}", $"equiv={actual:R}", stage);

        Guard.EnsureSuccess(Api("set_s_profile"), _api.SetSProfile(Card, Axis, _options.STime),
            $"sTime={_options.STime:R}", stage);

        // INP 不啟用時也要寫：卡裡可能固化著以前的設置
        var inp = _options.InPositionSignal;
        Guard.EnsureSuccess(Api("set_inp_mode"), _api.SetInpMode(Card, Axis, inp), Describe(inp), stage);
        result = _api.GetInpMode(Card, Axis, out var actualInp);
        Guard.EnsureSuccess(Api("get_inp_mode"), result, Describe(inp), stage, Describe(actualInp));
        Guard.EnsureReadback(Api("get_inp_mode"), actualInp == inp, Describe(inp), Describe(actualInp), stage);

        ApplySignalParameters();
    }

    /// <summary>從輪詢快照生成狀態，不調用 DLL。</summary>
    public AxisStatus GetStatus()
    {
        var snapshot = _poller.GetSnapshot(Axis);
        if (snapshot == null)
        {
            bool homed;
            lock (_sync)
            {
                homed = _homed;
            }

            return new AxisStatus(AxisState.Alarm, double.NaN, homed, false, false,
                "尚無狀態數據（輪詢未啟動或尚未完成第一輪）");
        }

        return BuildStatus(snapshot);
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatusDirect();
            if (status.State == AxisState.Alarm)
            {
                throw new MotionFaultException(Name, $"報警中，需先復位：{status.AlarmReason}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await EnableCoreAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _enabled = true;
            }
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        // 先停再失能：直接斷使能，垂直軸可能下墜
        await StopAsync().ConfigureAwait(false);
        await _motionCommandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _enabled = false;
            }

            await DisableCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public async Task MoveAbsoluteAsync(double position, MotionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (!double.IsFinite(position))
        {
            throw new ArgumentException($"目標位置非法：{position}", nameof(position));
        }

        LimitWatch limits;
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = EnsureReadyForMotion().Position;
            // 只盯運動方向上的限位：停在負限位上往正方向走出來，不能誤報負限位
            limits = new LimitWatch(Positive: position > start, Negative: position < start);
            ApplyProfile(profile, "Move");

            // 取消檢查放在下發前最後一步：取消完成後不能再有運動指令下發
            cancellationToken.ThrowIfCancellationRequested();
            Guard.EnsureSuccess(Api("pmove_unit"), _api.MoveAbsolute(Card, Axis, position),
                $"target={position:R}, posi_mode=1", "Move");
        }
        finally
        {
            _motionCommandGate.Release();
        }

        await WaitUntilAsync("Move", _options.MotionTimeout, status => status.State == AxisState.Idle, limits,
            cancellationToken).ConfigureAwait(false);

        var actual = ReadStatusDirect().Position;
        if (Math.Abs(actual - position) > _options.InPositionTolerance)
        {
            throw new MotionFaultException(Name,
                $"運動已停止但未到位：目標 {position:R}，實際 {actual:R}（可能被外部停止）");
        }

        if (_options.InPositionSignal.Enabled)
        {
            await WaitInPositionSignalAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_options.SettleTime > TimeSpan.Zero)
        {
            // 沒接 INP 時的兜底：固定延時不知道電機是否真的停穩，能接 INP 的機台應該接上
            await Task.Delay(_options.SettleTime, Time, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(StopMode mode = StopMode.Decelerate)
    {
        var leadshineMode = mode == StopMode.Immediate ? LeadshineStopMode.Immediate : LeadshineStopMode.Decelerated;
        // 停止不可取消；與發令共用鎖，保證停止排在已下發的指令之後
        await _motionCommandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Guard.EnsureSuccess(Api("stop"), _api.StopAxis(Card, Axis, leadshineMode),
                $"stop_mode={(ushort)leadshineMode}", "Stop");
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    /// <summary>
    /// 速度模式點動（vmove）。鬆開（取消）時減速停止，等 check_done 確認停穩後正常完成。
    /// 已在點動方向的限位上時拒絕；反方向可以點動，用於把軸從限位上移出來。
    /// </summary>
    public async Task JogAsync(JogDirection direction, MotionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var positive = direction == JogDirection.Positive;
        var side = positive ? "正" : "負";
        try
        {
            await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var status = EnsureReadyForMotion();
                if (positive ? status.PositiveLimit : status.NegativeLimit)
                {
                    throw new MotionFaultException(Name, $"已在{side}限位上，不能繼續{side}向點動");
                }

                ApplyProfile(profile, "Jog");
                // 同運動一樣，下發前最後檢查：已經鬆開就不再下發
                cancellationToken.ThrowIfCancellationRequested();
                Guard.EnsureSuccess(Api("vmove"), _api.MoveContinuous(Card, Axis, direction),
                    $"dir={(positive ? 1 : 0)}, v={profile.Velocity:R}", "Jog");
            }
            finally
            {
                _motionCommandGate.Release();
            }

            await WaitUntilAsync("Jog", _options.JogTimeout, status => status.State == AxisState.Idle,
                new LimitWatch(positive, !positive), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 鬆開按鈕：已下發的話 WaitUntilAsync 已發出減速停止，這裡等軸真正停下來
            await WaitStoppedAsync("Jog").ConfigureAwait(false);
            return;
        }

        // 沒鬆開卻停了：被外部停止或控制卡自己停的，不能當作正常結束
        throw new MotionFaultException(Name, "點動中軸被停止（未鬆開按鈕）");
    }

    public async Task HomeAsync(CancellationToken cancellationToken = default)
    {
        if (_options.HomeMethod == LeadshineHomeMethod.AbsoluteEncoder)
        {
            await HomeByAbsoluteEncoderAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadyForMotion();
            lock (_sync)
            {
                _homed = false;
            }

            // 回零參數下發並回讀核對，不一致則不回零
            ConfigureAndVerifyHome();

            cancellationToken.ThrowIfCancellationRequested();
            Guard.EnsureSuccess(Api("home_move"), StartHome(), "home=start", "Home");
            lock (_sync)
            {
                _homing = true;
            }
        }
        finally
        {
            _motionCommandGate.Release();
        }

        try
        {
            await WaitUntilAsync("Home", _options.HomeTimeout, _ => IsHomeFinished(), LimitWatch.Both,
                cancellationToken).ConfigureAwait(false);
            // 回零後處理（例如脈衝軸對齊編碼器），失敗則不算回零完成
            OnHomeCompleted();
            lock (_sync)
            {
                _homed = true;
            }
        }
        finally
        {
            lock (_sync)
            {
                _homing = false;
            }
        }
    }

    /// <summary>
    /// 絕對值回零：讀編碼器絕對位置 → 校驗可信 → 把指令位置同步過去並回讀 → 絕對運動走到零點。
    /// 走到零點後才標記為已回零，和感應器回零一致。
    /// </summary>
    private async Task HomeByAbsoluteEncoderAsync(CancellationToken cancellationToken)
    {
        const string stage = "AbsoluteHome";
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadyForMotion();
            AxisSafetyConfig safety;
            lock (_sync)
            {
                _homed = false;
                safety = _softwareSafety;
            }

            var result = _api.ReadFeedbackPosition(Card, Axis, out var absolute);
            Guard.EnsureSuccess(Api("get_encoder_unit"), result, "read", stage, $"encoder={absolute:R}");

            // 絕對位置必須落在軟限位內才可信：編碼器電池沒電、多圈數據丟失、驅動器原點被改過，
            // 讀到的都是一個「看起來正常」的錯誤值，直接拿去走零點就是撞機
            if (double.IsNaN(safety.SoftLimitMin) || double.IsNaN(safety.SoftLimitMax))
            {
                throw new MotionFaultException(Name, "未寫入安全配置，無法校驗絕對位置是否可信，拒絕絕對值回零");
            }

            if (!(absolute >= safety.SoftLimitMin && absolute <= safety.SoftLimitMax))
            {
                throw new MotionFaultException(Name,
                    $"絕對位置 {absolute:R} 不在軟限位 [{safety.SoftLimitMin:R}, {safety.SoftLimitMax:R}] 內，不可信：" +
                    "檢查編碼器電池、多圈數據和驅動器原點設置；確認位置正確後低速點動回到範圍內");
            }

            // 上電時指令位置是 0，不同步的話「走到零點」這條運動根本不會動
            Guard.EnsureSuccess(Api("set_position_unit"), _api.SetPosition(Card, Axis, absolute),
                $"pos={absolute:R}", stage);
            result = _api.ReadPosition(Card, Axis, out var command);
            Guard.EnsureSuccess(Api("get_position_unit"), result, $"pos={absolute:R}", stage, $"pos={command:R}");
            Guard.EnsureReadback(Api("get_position_unit"), Math.Abs(command - absolute) <= 1 / _options.PulseEquivalent,
                $"pos={absolute:R}", $"pos={command:R}", stage);
        }
        finally
        {
            _motionCommandGate.Release();
        }

        // 坐標已經可信，走零點就是普通的點位運動：到位、INP、取消、報警處理全部複用
        await MoveAbsoluteAsync(_options.AbsoluteHomeTarget, _options.AbsoluteHomeProfile!, cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _homed = true;
        }
    }

    public async Task ResetAlarmAsync(CancellationToken cancellationToken = default)
    {
        await _motionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 報警後原點不再可信；復位後需要重新使能並回零
            lock (_sync)
            {
                _enabled = false;
                _homed = false;
            }

            await ResetAlarmCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _motionCommandGate.Release();
        }
    }

    public Task WriteSafetyConfigAsync(AxisSafetyConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        if (config.EmergencyStopEnabled && config.EmergencyStopActiveHigh is null)
        {
            // 急停電平不猜默認值
            throw new ArgumentException($"{Name}：已啟用急停，但未指定急停有效電平");
        }

        const string stage = "Safety";
        var logic = config.EmergencyStopActiveHigh == true ? EmergencyLogicHigh : EmergencyLogicLow;
        Guard.EnsureSuccess(Api("set_emg_mode"),
            _api.SetEmergencyStopMode(Card, Axis, config.EmergencyStopEnabled, logic),
            $"enable={config.EmergencyStopEnabled}, logic={logic}", stage);

        var softLimit = ExpectedSoftLimit(config);
        Guard.EnsureSuccess(Api("set_softlimit_unit"), _api.SetSoftLimit(Card, Axis, softLimit), Describe(softLimit),
            stage);

        lock (_sync)
        {
            _softwareSafety = config;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 急停和軟限位從控制卡回讀。最大速度、最大加速度在雷賽卡上沒有對應參數，
    /// 由 GuardedAxis 在軟件層限制，這裡返回寫入值。
    /// </summary>
    public Task<AxisSafetyConfig> ReadSafetyConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string stage = "SafetyReadback";
        AxisSafetyConfig written;
        lock (_sync)
        {
            written = _softwareSafety;
        }

        var result = _api.GetEmergencyStopMode(Card, Axis, out var emgEnabled, out var logic);
        Guard.EnsureSuccess(Api("get_emg_mode"), result, "read", stage, $"enable={emgEnabled}, logic={logic}");

        result = _api.GetSoftLimit(Card, Axis, out var softLimit);
        Guard.EnsureSuccess(Api("get_softlimit_unit"), result, "read", stage, Describe(softLimit));
        // 計數源和觸發動作不在通用安全配置裡，在這裡單獨核對
        var expectedSoftLimit = ExpectedSoftLimit(written);
        Guard.EnsureReadback(Api("get_softlimit_unit"),
            softLimit.Source == expectedSoftLimit.Source && softLimit.Action == expectedSoftLimit.Action,
            Describe(expectedSoftLimit), Describe(softLimit), stage);

        return Task.FromResult(written with
        {
            SoftLimitMin = softLimit.Enabled ? softLimit.Negative : double.NaN,
            SoftLimitMax = softLimit.Enabled ? softLimit.Positive : double.NaN,
            EmergencyStopEnabled = emgEnabled,
            EmergencyStopActiveHigh = logic switch
            {
                EmergencyLogicLow => false,
                EmergencyLogicHigh => true,
                _ => null,
            },
        });
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poller.Unregister(Axis);
            _motionCommandGate.Dispose();
        }
    }

    /// <summary>使能伺服。失敗必須拋出異常。在發令鎖內調用。</summary>
    protected abstract Task EnableCoreAsync();

    /// <summary>斷開伺服使能。在發令鎖內調用，調用前軸已停止。</summary>
    protected abstract Task DisableCoreAsync();

    /// <summary>下發回零參數並回讀核對，不一致必須拋出異常。在發令鎖內調用。</summary>
    protected abstract void ConfigureAndVerifyHome();

    /// <summary>啟動回零，返回雷賽錯誤碼。在發令鎖內調用。</summary>
    protected abstract short StartHome();

    /// <summary>讀取回零結果（1 表示本次回零完成），返回雷賽錯誤碼。</summary>
    protected abstract short ReadHomeResult(out ushort state);

    /// <summary>回零完成、標記為已回零之前調用。拋出異常則本次回零不算完成。</summary>
    protected virtual void OnHomeCompleted()
    {
    }

    /// <summary>
    /// 把反饋位置設為當前指令位置並回讀核對（容差一個脈衝）。
    /// 用於回零後：脈衝軸的編碼器計數是上電時的隨機值，不對齊的話反饋位置和跟隨誤差都沒有意義。
    /// </summary>
    protected void AlignFeedbackToCommand(string stage)
    {
        var result = _api.ReadPosition(Card, Axis, out var command);
        Guard.EnsureSuccess(Api("get_position_unit"), result, "read", stage, $"pos={command:R}");
        Guard.EnsureSuccess(Api("set_encoder_unit"), _api.SetFeedbackPosition(Card, Axis, command),
            $"encoder={command:R}", stage);
        result = _api.ReadFeedbackPosition(Card, Axis, out var feedback);
        Guard.EnsureSuccess(Api("get_encoder_unit"), result, $"encoder={command:R}", stage, $"encoder={feedback:R}");
        Guard.EnsureReadback(Api("get_encoder_unit"), Math.Abs(feedback - command) <= 1 / _options.PulseEquivalent,
            $"encoder={command:R}", $"encoder={feedback:R}", stage);
    }

    /// <summary>清除伺服報警。在發令鎖內調用。</summary>
    protected abstract Task ResetAlarmCoreAsync(CancellationToken cancellationToken);

    /// <summary>各系列自己的信號配置（例如脈衝軸的 ALM、EL 電平），在 <see cref="ApplyBaseParameters"/> 末尾調用。</summary>
    protected virtual void ApplySignalParameters()
    {
    }

    /// <summary>ALM 有效時的報警描述。總線軸可以讀出驅動器錯誤碼。</summary>
    protected virtual string DescribeServoAlarm() => "伺服報警 ALM";

    protected string Api(string function) => $"{ApiPrefix}_{function}";

    private static string Describe(LeadshineInpMode m) => $"enable={m.Enabled}, logic={m.Logic}";

    protected static string Describe(LeadshineSoftLimit s) =>
        $"enable={s.Enabled}, source={s.Source}, action={s.Action}, n={s.Negative:R}, p={s.Positive:R}";

    /// <summary>直接讀卡（持卡鎖）生成狀態。內部等待在改為按輪詢週期之前暫時使用。</summary>
    private AxisStatus ReadStatusDirect()
    {
        LeadshineAxisSnapshot snapshot;
        lock (_poller.Gate.Sync)
        {
            snapshot = LeadshineAxisSnapshot.Read(_api, Card, Axis, _options.Feedback == LeadshineFeedback.Encoder,
                cycle: -1, Time.GetTimestamp());
        }

        return BuildStatus(snapshot);
    }

    private AxisStatus BuildStatus(LeadshineAxisSnapshot snapshot)
    {
        bool enabled, homed, homing;
        lock (_sync)
        {
            (enabled, homed, homing) = (_enabled, _homed, _homing);
        }

        if (snapshot.ReadFailed)
        {
            return new AxisStatus(AxisState.Alarm, snapshot.Command, homed, false, false,
                $"狀態讀取失敗：{Api("get_position_unit")}={snapshot.PositionResult}, " +
                $"{Api("check_done")}={snapshot.Done}, {Api("get_encoder_unit")}={snapshot.FeedbackResult}");
        }

        var io = LeadshineAxisIo.Parse(snapshot.Io);
        var alarm = io.Alarm ? DescribeServoAlarm() : io.EmergencyStop ? "急停 EMG 有效" : null;
        var state = alarm != null ? AxisState.Alarm
            : !enabled ? AxisState.Disabled
            : homing ? AxisState.Homing
            : snapshot.Done == 0 ? AxisState.Moving
            : AxisState.Idle;
        return new AxisStatus(state, snapshot.Command, homed, io.PositiveLimit, io.NegativeLimit, alarm,
            snapshot.Feedback);
    }

    private AxisStatus EnsureReadyForMotion()
    {
        var status = ReadStatusDirect();
        return status.State switch
        {
            AxisState.Alarm => throw new MotionFaultException(Name, $"報警中：{status.AlarmReason}"),
            AxisState.Disabled => throw new MotionFaultException(Name, "未使能"),
            AxisState.Moving or AxisState.Homing => throw new MotionFaultException(Name, "軸正在運動"),
            _ => status,
        };
    }

    /// <summary>下發速度參數。雷賽用加減速時間，不是加速度。在發令鎖內調用。</summary>
    private void ApplyProfile(MotionProfile profile, string stage)
    {
        var tacc = profile.Velocity / profile.Acceleration;
        var tdec = profile.Velocity / profile.Deceleration;
        var minVelocity = Math.Min(_options.MinVelocity, profile.Velocity);
        var stopVelocity = Math.Min(_options.StopVelocity, profile.Velocity);
        Guard.EnsureSuccess(Api("set_profile_unit"),
            _api.SetProfile(Card, Axis, minVelocity, profile.Velocity, tacc, tdec, stopVelocity),
            $"min={minVelocity:R}, max={profile.Velocity:R}, tacc={tacc:R}, tdec={tdec:R}, stop={stopVelocity:R}",
            stage);
    }

    /// <summary>
    /// 等 INP 有效。check_done == 1 只代表規劃走完（脈衝軸是脈衝發完），伺服還在追跟隨誤差，
    /// 馬上拍照或加工會偏。這裡等驅動器自己報告到位，代替「運動完 Sleep 幾十毫秒」。
    /// </summary>
    private async Task WaitInPositionSignalAsync(CancellationToken cancellationToken)
    {
        var start = Time.GetTimestamp();
        while (true)
        {
            // 每次輪詢都記日誌會刷屏，這裡只在失敗時拋出
            var result = _api.ReadAxisIoStatus(Card, Axis, out var raw);
            if (result != 0)
            {
                throw new LeadshineCallException(Name, Api("axis_io_status"), result,
                    $"等待 INP 時讀取 IO 狀態失敗：錯誤碼 {result}");
            }

            var io = LeadshineAxisIo.Parse(raw);
            if (io.Alarm || io.EmergencyStop)
            {
                throw new MotionFaultException(Name, $"等待 INP 時{(io.Alarm ? DescribeServoAlarm() : "急停 EMG 有效")}");
            }

            if (io.InPosition)
            {
                return;
            }

            if (Time.GetElapsedTime(start) > _options.InPositionTimeout)
            {
                throw new MotionFaultException(Name,
                    $"規劃已走完，{_options.InPositionTimeout.TotalMilliseconds:0} ms 內 INP 未有效" +
                    "（跟隨誤差過大、增益不足，或 INP 未接線 / 電平配錯）");
            }

            // 指令已經走完，這裡取消不需要停軸
            await Task.Delay(_options.PollInterval, Time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>停止指令發出後，等 check_done 確認軸真正停下；減速段還在走時不能當作已停止。</summary>
    private async Task WaitStoppedAsync(string stage)
    {
        var start = Time.GetTimestamp();
        while (true)
        {
            var done = _api.CheckDone(Card, Axis);
            if (done == 1)
            {
                return;
            }

            if (done != 0)
            {
                throw new LeadshineCallException(Name, Api("check_done"), done, $"{stage} 停止確認失敗：check_done={done}");
            }

            if (Time.GetElapsedTime(start) > _options.MotionTimeout)
            {
                throw new MotionFaultException(Name, $"{stage} 已發出停止，但軸在超時內沒有停下");
            }

            await Task.Delay(_options.PollInterval, Time).ConfigureAwait(false);
        }
    }

    /// <summary>運動過程中要盯哪一側的限位。</summary>
    private readonly record struct LimitWatch(bool Positive, bool Negative)
    {
        public static LimitWatch Both => new(true, true);
    }

    /// <summary>
    /// 輪詢直到完成。期間出現報警立即急停並報故障；超時減速停止並報故障；
    /// 調用方取消時先停軸，再拋出取消。
    /// </summary>
    private async Task WaitUntilAsync(string stage, TimeSpan timeout, Func<AxisStatus, bool> finished,
        LimitWatch limits, CancellationToken cancellationToken)
    {
        var start = Time.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(_options.PollInterval, Time, cancellationToken).ConfigureAwait(false);
                var status = ReadStatusDirect();
                if (status.State == AxisState.Alarm)
                {
                    await StopAsync(StopMode.Immediate).ConfigureAwait(false);
                    throw new MotionFaultException(Name, $"{stage} 過程中報警：{status.AlarmReason}");
                }

                var positiveHit = limits.Positive && status.PositiveLimit;
                if (positiveHit || (limits.Negative && status.NegativeLimit))
                {
                    // 限位由控制卡自行停軸，這裡只負責報出來
                    throw new MotionFaultException(Name, $"{stage} 過程中觸發{(positiveHit ? "正" : "負")}限位");
                }

                if (finished(status))
                {
                    return;
                }

                if (Time.GetElapsedTime(start) > timeout)
                {
                    await StopAsync().ConfigureAwait(false);
                    throw new MotionFaultException(Name,
                        $"{stage} 超時（{timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒），已減速停止");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 已下發的運動被取消：先停軸，再報告取消
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool IsHomeFinished()
    {
        var result = ReadHomeResult(out var state);
        if (result != 0 || state is not (0 or 1))
        {
            throw new LeadshineCallException(Name, Api("get_home_result"), result,
                $"回零結果讀取失敗：錯誤碼 {result}，狀態 {state}");
        }

        // 1 才表示本次回零完成，並且要等軸真正停下來
        return state == 1 && _api.CheckDone(Card, Axis) == 1;
    }

    private LeadshineSoftLimit ExpectedSoftLimit(AxisSafetyConfig config) =>
        new(true, _options.SoftLimitSource, _options.SoftLimitAction, config.SoftLimitMin, config.SoftLimitMax);
}
