using System.Runtime.InteropServices;

namespace StationKit.Motion.Leadshine.Smc.Native;

/// <summary>
/// 雷賽 LTSMC.dll 的 P/Invoke 聲明，只聲明本適配器用到的函數。
/// 簽名與雷賽官方 LTSMC.cs 一致；LTSMC.dll 需隨程序部署（不隨本倉庫分發）。
/// SMC 系列以 ConnectNo 區分控制器，對應這裡的 cardNo。
/// </summary>
internal static class NativeMethods
{
    private const string Dll = "LTSMC.dll";

    //--------------------- 控制器連接 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    internal static extern short smc_board_init(ushort ConnectNo, ushort type, string pconnectstring, uint baud);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_board_close(ushort ConnectNo);

    //--------------------- 基礎參數 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_equiv(ushort ConnectNo, ushort axis, double equiv);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_equiv(ushort ConnectNo, ushort axis, ref double equiv);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_profile_unit(ushort ConnectNo, ushort axis, double Min_Vel, double Max_Vel,
        double Tacc, double Tdec, double Stop_Vel);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_s_profile(ushort ConnectNo, ushort axis, ushort s_mode, double s_para);

    //--------------------- 專用信號 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_emg_mode(ushort ConnectNo, ushort axis, ushort enable, ushort emg_logic);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_emg_mode(ushort ConnectNo, ushort axis, ref ushort enable,
        ref ushort emg_logic);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_inp_mode(ushort ConnectNo, ushort axis, ushort enable, ushort inp_logic);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_inp_mode(ushort ConnectNo, ushort axis, ref ushort enable, ref ushort inp_logic);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_softlimit_unit(ushort ConnectNo, ushort axis, ushort enable,
        ushort source_sel, ushort SL_action, double N_limit, double P_limit);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_softlimit_unit(ushort ConnectNo, ushort axis, ref ushort enable,
        ref ushort source_sel, ref ushort SL_action, ref double N_limit, ref double P_limit);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_alm_mode(ushort ConnectNo, ushort axis, ushort enable, ushort alm_logic,
        ushort alm_action);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_alm_mode(ushort ConnectNo, ushort axis, ref ushort enable,
        ref ushort alm_logic, ref ushort alm_action);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_el_mode(ushort ConnectNo, ushort axis, ushort enable, ushort el_logic,
        ushort el_mode);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_el_mode(ushort ConnectNo, ushort axis, ref ushort enable,
        ref ushort el_logic, ref ushort el_mode);

    //--------------------- 伺服使能、報警復位 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_write_sevon_pin(ushort ConnectNo, ushort axis, ushort on_off);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_read_sevon_pin(ushort ConnectNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_write_erc_pin(ushort ConnectNo, ushort axis, ushort on_off);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_write_outbit(ushort ConnectNo, ushort bitno, ushort on_off);

    //--------------------- 點位運動 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_pmove_unit(ushort ConnectNo, ushort axis, double Dist, ushort posi_mode);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_vmove(ushort ConnectNo, ushort axis, ushort dir);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_check_done(ushort ConnectNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_stop(ushort ConnectNo, ushort axis, ushort stop_mode);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_emg_stop(ushort ConnectNo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_position_unit(ushort ConnectNo, ushort axis, ref double pos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_position_unit(ushort ConnectNo, ushort axis, double pos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_encoder_unit(ushort ConnectNo, ushort axis, ref double pos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_encoder_unit(ushort ConnectNo, ushort axis, double pos);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint smc_axis_io_status(ushort ConnectNo, ushort axis);

    //--------------------- 通用 IO ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint smc_read_inport(ushort ConnectNo, ushort portno);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint smc_read_outport(ushort ConnectNo, ushort portno);

    //--------------------- 回零 ---------------------
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_homemode(ushort ConnectNo, ushort axis, ushort home_dir, double vel_mode,
        ushort mode, ushort pos_source);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_homemode(ushort ConnectNo, ushort axis, ref ushort home_dir,
        ref double vel_mode, ref ushort home_mode, ref ushort pos_source);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_home_profile_unit(ushort ConnectNo, ushort axis, double Low_Vel,
        double High_Vel, double Tacc, double Tdec);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_home_profile_unit(ushort ConnectNo, ushort axis, ref double Low_Vel,
        ref double High_Vel, ref double Tacc, ref double Tdec);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_home_pin_logic(ushort ConnectNo, ushort axis, ushort org_logic,
        double filter);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_home_pin_logic(ushort ConnectNo, ushort axis, ref ushort org_logic,
        ref double filter);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_set_home_position_unit(ushort ConnectNo, ushort axis, ushort enable,
        double position);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_home_position_unit(ushort ConnectNo, ushort axis, ref ushort enable,
        ref double position);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_home_move(ushort ConnectNo, ushort axis);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern short smc_get_home_result(ushort ConnectNo, ushort axis, ref ushort state);
}
