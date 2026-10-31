using System.Runtime.InteropServices;

namespace StationKit.Motion.Dmc.Native;

/// <summary>
/// 雷赛 LTDMC.dll 的 P/Invoke 声明，只声明本适配器用到的函数。
/// 签名与雷赛官方 LTDMC.cs 一致；LTDMC.dll 需随程序部署（不随本仓库分发）。
/// </summary>
internal static class NativeMethods
{
    private const string Dll = "LTDMC.dll";

    //--------------------- 板卡初始化 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_board_init();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_board_close();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    internal static extern short dmc_board_init_eth(ushort CardNo, string pconnectstring);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_get_total_axes(ushort CardNo, ref uint TotalAxis);

    //--------------------- 基础参数 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_set_equiv(ushort CardNo, ushort axis, double equiv);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_get_equiv(ushort CardNo, ushort axis, ref double equiv);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_set_profile_unit(ushort CardNo, ushort Axis, double Min_Vel, double Max_Vel,
        double Tacc, double Tdec, double Stop_Vel);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_set_s_profile(ushort CardNo, ushort axis, ushort s_mode, double s_para);

    //--------------------- 安全参数 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_set_emg_mode(ushort CardNo, ushort axis, ushort enable, ushort emg_logic);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_get_emg_mode(ushort CardNo, ushort axis, ref ushort enbale, ref ushort emg_logic);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_set_softlimit_unit(ushort CardNo, ushort axis, ushort enable, ushort source_sel,
        ushort SL_action, double N_limit, double P_limit);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_get_softlimit_unit(ushort CardNo, ushort axis, ref ushort enable,
        ref ushort source_sel, ref ushort SL_action, ref double N_limit, ref double P_limit);

    //--------------------- 使能与报警 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_set_axis_enable(ushort CardNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_set_axis_disable(ushort CardNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_clear_axis_errcode(ushort CardNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_get_axis_errcode(ushort CardNo, ushort axis, ref ushort Errcode);

    //--------------------- 运动 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_pmove_unit(ushort CardNo, ushort axis, double Dist, ushort posi_mode);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_check_done(ushort CardNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_stop(ushort CardNo, ushort axis, ushort stop_mode);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_emg_stop(ushort CardNo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_get_position_unit(ushort CardNo, ushort axis, ref double pos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint dmc_axis_io_status(ushort CardNo, ushort axis);

    //--------------------- 回零 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_set_home_profile(ushort CardNo, ushort axis, ushort home_mode, double Low_Vel,
        double High_Vel, double Tacc, double Tdec, double offsetpos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short nmc_get_home_profile(ushort CardNo, ushort axis, ref ushort home_mode,
        ref double Low_Vel, ref double High_Vel, ref double Tacc, ref double Tdec, ref double offsetpos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_home_move(ushort CardNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short dmc_get_home_result(ushort CardNo, ushort axis, ref ushort state);
}
