// Redmi Helper - 底层模块：硬件通信、系统设置、监控数据
// 只用 C# 5 语法，以便用 Windows 自带的 csc.exe 编译

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RedmiHelper
{
    // ===== 和 BIOS/EC 通信：WMI 接口 MICommonInterface.MiInterface =====
    // 命令表（实测 + 从官方 libXiaomiGCommandCenter.dll 反汇编确认）：
    //   命令 8  = 性能模式（读/写）：静谧=2 均衡=1 极速=3 狂派=4
    //   命令 9  = 风扇转速和温度（只读）：[4,5]风扇A [6,7]风扇B [10]温度1 [14]温度2
    //   命令 11 = 显卡模式（读/写）：0=混合 1=独显直连，重启生效；读到的是当前实际生效的模式
    //   命令 10 = 按键锁，[4]=子号：0=触控板 1=Fn锁 2=Win键；值在 [6]：1=锁定/开启
    static class Mi
    {
        static ManagementObject inst;
        static readonly object lk = new object();

        public const byte CMD_PERF = 8, CMD_SENSORS = 9, CMD_KEYS = 10, CMD_GPU = 11;
        public const byte KEY_TOUCHPAD = 0, KEY_FN = 1, KEY_WIN = 2;

        public static void Init()
        {
            ManagementClass mc = new ManagementClass(@"root\wmi", "MICommonInterface", null);
            foreach (ManagementObject o in mc.GetInstances()) { inst = o; break; }
            if (inst == null) throw new Exception("找不到 MICommonInterface 接口");
        }

        static byte[] Call(byte op, byte fn, byte b4, byte b6)
        {
            lock (lk)
            {
                byte[] b = new byte[32];
                b[1] = op;   // 0xFA 读取, 0xFB 写入
                b[3] = fn;   // 命令号
                b[4] = b4;   // 值 或 子号
                b[6] = b6;   // 命令 10 的值
                ManagementBaseObject ip = inst.GetMethodParameters("MiInterface");
                ip["InData"] = b;
                ManagementBaseObject outp = inst.InvokeMethod("MiInterface", ip, null);
                return (byte[])outp["OutData"];
            }
        }

        public static byte[] Get(byte fn) { return Call(0xFA, fn, 0, 0); }
        public static byte[] Set(byte fn, byte val) { return Call(0xFB, fn, val, 0); }
        public static byte[] GetKey(byte sub) { return Call(0xFA, CMD_KEYS, sub, 0); }
        public static byte[] SetKey(byte sub, bool on) { return Call(0xFB, CMD_KEYS, sub, (byte)(on ? 1 : 0)); }

        // 返回数据第 1 字节是 128 表示成功，第 3 字节回显命令号
        public static bool Ok(byte[] r, byte fn)
        {
            return r != null && r.Length >= 16 && r[1] == 128 && r[3] == fn;
        }

        public static bool Accepted(byte[] r)
        {
            return r != null && r.Length >= 2 && r[1] == 128;
        }
    }

    // ===== 设置（存在注册表 HKCU\Software\RedmiHelper） =====
    static class Settings
    {
        const string Key = @"Software\RedmiHelper";

        static object Read(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        public static int GetInt(string name, int def)
        {
            object v = Read(name);
            return v is int ? (int)v : def;
        }

        public static string GetStr(string name, string def)
        {
            object v = Read(name);
            return v is string ? (string)v : def;
        }

        public static void SetInt(string name, int val)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue(name, val, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static void SetStr(string name, string val)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue(name, val, RegistryValueKind.String);
            }
            catch { }
        }
    }

    // ===== 开机自启（任务计划程序，带管理员权限、不弹 UAC） =====
    static class AutoStart
    {
        const string TaskName = "RedmiHelper";

        static int Run(string file, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static bool IsEnabled()
        {
            try { return Run("schtasks.exe", "/query /tn " + TaskName) == 0; }
            catch { return false; }
        }

        public static bool Enable()
        {
            string exe = Application.ExecutablePath.Replace("'", "''");
            string script =
                "$a = New-ScheduledTaskAction -Execute '" + exe + "' -Argument '/tray'; " +
                "$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; " +
                "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero); " +
                "$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive; " +
                "Register-ScheduledTask -TaskName " + TaskName + " -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            try { Run("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + enc); } catch { }
            return IsEnabled();
        }

        public static void Disable()
        {
            try { Run("schtasks.exe", "/delete /tn " + TaskName + " /f"); } catch { }
        }
    }

    // ===== 屏幕刷新率（Windows 显示接口，不涉及固件） =====
    static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        const int ENUM_CURRENT_SETTINGS = -1;
        const int DM_DISPLAYFREQUENCY = 0x400000;
        const uint CDS_UPDATEREGISTRY = 0x1;

        static string Dev { get { return Screen.PrimaryScreen.DeviceName; } }

        static DEVMODE NewMode()
        {
            DEVMODE m = new DEVMODE();
            m.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return m;
        }

        public static int Current()
        {
            DEVMODE m = NewMode();
            return EnumDisplaySettings(Dev, ENUM_CURRENT_SETTINGS, ref m) ? m.dmDisplayFrequency : 0;
        }

        public static void Rates(out int max, out int low)
        {
            max = 0; low = 0;
            DEVMODE cur = NewMode();
            if (!EnumDisplaySettings(Dev, ENUM_CURRENT_SETTINGS, ref cur)) return;
            DEVMODE m = NewMode();
            for (int i = 0; EnumDisplaySettings(Dev, i, ref m); i++)
            {
                if (m.dmPelsWidth != cur.dmPelsWidth || m.dmPelsHeight != cur.dmPelsHeight) continue;
                int f = m.dmDisplayFrequency;
                if (f <= 1) continue;
                if (f > max) max = f;
                if (f >= 59 && (low == 0 || f < low)) low = f;
            }
            if (low == 0) low = max;
        }

        public static bool Set(int hz)
        {
            DEVMODE m = NewMode();
            if (!EnumDisplaySettings(Dev, ENUM_CURRENT_SETTINGS, ref m)) return false;
            if (m.dmDisplayFrequency == hz) return true;
            m.dmDisplayFrequency = hz;
            m.dmFields = DM_DISPLAYFREQUENCY;
            return ChangeDisplaySettingsEx(Dev, ref m, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
        }
    }

    // ===== Windows 电源模式（最佳能效 / 平衡 / 最佳性能） =====
    static class PowerMode
    {
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(ref Guid overlay);
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

        public static readonly string[] Names = { "最佳能效", "平衡", "最佳性能" };
        static readonly Guid[] Guids = {
            new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"),
            Guid.Empty,
            new Guid("ded574b5-45a0-4f42-8737-46345c09c238")
        };

        public static int Get()
        {
            try
            {
                Guid g;
                if (PowerGetEffectiveOverlayScheme(out g) != 0) return -1;
                for (int i = 0; i < 3; i++) if (g == Guids[i]) return i;
                return 1;
            }
            catch { return -1; }
        }

        public static bool Set(int idx)
        {
            try { Guid g = Guids[idx]; return PowerSetActiveOverlayScheme(ref g) == 0; }
            catch { return false; }
        }
    }

    // ===== CPU 睿频开关（Windows 电源设置里隐藏的"处理器性能提升模式"，不需要驱动） =====
    static class Turbo
    {
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

        static Guid SUB_PROCESSOR = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static Guid PERFBOOSTMODE = new Guid("be337238-0d82-4146-a960-4f3749d470c7");

        static bool Scheme(out Guid g)
        {
            g = Guid.Empty;
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return false;
            g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            return true;
        }

        // 返回 1 = 开启，0 = 关闭，-1 = 读取失败（按当前供电方式读）
        public static int Get()
        {
            try
            {
                Guid g; uint v;
                if (!Scheme(out g)) return -1;
                bool battery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
                uint r = battery ? PowerReadDCValueIndex(IntPtr.Zero, ref g, ref SUB_PROCESSOR, ref PERFBOOSTMODE, out v)
                                 : PowerReadACValueIndex(IntPtr.Zero, ref g, ref SUB_PROCESSOR, ref PERFBOOSTMODE, out v);
                if (r != 0) return -1;
                if (v != 0 && Settings.GetInt("TurboOnValue", -1) != (int)v) Settings.SetInt("TurboOnValue", (int)v);   // 记住原来的开启方式（通常是 2 = 积极）
                return v == 0 ? 0 : 1;
            }
            catch { return -1; }
        }

        // 插电和电池两套设置一起改，立刻生效
        public static bool Set(bool on)
        {
            try
            {
                Guid g;
                if (!Scheme(out g)) return false;
                uint v = on ? (uint)Math.Max(1, Settings.GetInt("TurboOnValue", 2)) : 0u;
                bool ok = PowerWriteACValueIndex(IntPtr.Zero, ref g, ref SUB_PROCESSOR, ref PERFBOOSTMODE, v) == 0 &
                          PowerWriteDCValueIndex(IntPtr.Zero, ref g, ref SUB_PROCESSOR, ref PERFBOOSTMODE, v) == 0;
                PowerSetActiveScheme(IntPtr.Zero, ref g);
                return ok;
            }
            catch { return false; }
        }
    }

    // ===== CPU 占用率（GetSystemTimes，不受系统语言影响） =====
    static class CpuLoad
    {
        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        static long lastIdle, lastKernel, lastUser;

        public static double Sample()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return 0;
            long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
            bool first = lastKernel == 0;
            lastIdle = idle; lastKernel = kernel; lastUser = user;
            long total = dk + du;   // kernel 时间里已经包含 idle
            if (first || total <= 0) return 0;
            double v = (1.0 - (double)di / total) * 100.0;
            return Math.Max(0, Math.Min(100, v));
        }
    }

    // ===== NVIDIA 独显状态（调用驱动自带的 nvidia-smi） =====
    static class Nvidia
    {
        static string Exe()
        {
            string p = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (File.Exists(p)) return p;
            p = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe";
            return File.Exists(p) ? p : null;
        }

        static string Run(string args)
        {
            string exe = Exe();
            if (exe == null) return null;
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return o;
            }
        }

        static string Watt(string s)
        {
            double v;
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v.ToString("0.0") + "W" : "--";
        }

        public static string Status()
        {
            try
            {
                string o = Run("--query-gpu=name,utilization.gpu,power.draw,clocks.gr,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits");
                if (o == null) return "未找到 nvidia-smi（需要安装 NVIDIA 驱动）";
                string line = o.Trim().Split('\n')[0];
                string[] f = line.Split(',');
                if (f.Length < 7) return line;
                for (int i = 0; i < f.Length; i++) f[i] = f[i].Trim();
                return f[0] + "\n占用 " + f[1] + "%  ·  功耗 " + Watt(f[2]) + "  ·  " + f[6] + "°C\n" +
                       "核心 " + f[3] + " MHz  ·  显存 " + f[4] + " / " + f[5] + " MB";
            }
            catch (Exception ex) { return "读取失败：" + ex.Message; }
        }

        // 解析 nvidia-smi 默认输出里的进程表
        public static string Apps()
        {
            try
            {
                string o = Run("");
                if (o == null) return "--";
                Regex re = new Regex(@"^\|\s*\d+\s+\S+\s+\S+\s+(\d+)\s+(C\+G|C|G)\s+(.*?)\s+\S+\s*\|\s*$");
                List<string> names = new List<string>();
                foreach (string raw in o.Split('\n'))
                {
                    Match m = re.Match(raw.TrimEnd('\r'));
                    if (!m.Success) continue;
                    string name;
                    try
                    {
                        using (Process p = Process.GetProcessById(int.Parse(m.Groups[1].Value)))
                            name = p.ProcessName;
                    }
                    catch { name = Path.GetFileNameWithoutExtension(m.Groups[3].Value.Trim().TrimStart('.')); }
                    if (name.Length > 0 && !names.Contains(name)) names.Add(name);
                }
                if (names.Count == 0) return "（没有，独显可能在休眠）";
                if (names.Count > 8) return string.Join("、", names.GetRange(0, 8).ToArray()) + " 等 " + names.Count + " 个";
                return string.Join("、", names.ToArray());
            }
            catch (Exception ex) { return "读取失败：" + ex.Message; }
        }
    }

    // ===== 电池健康度 =====
    static class BatteryHealth
    {
        static double Q(string cls, string prop)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT " + prop + " FROM " + cls))
                    foreach (ManagementObject o in s.Get())
                        return Convert.ToDouble(o[prop]);
            }
            catch { }
            return -1;
        }

        public static string Text()
        {
            double design = Q("BatteryStaticData", "DesignedCapacity");
            double full = Q("BatteryFullChargedCapacity", "FullChargedCapacity");
            double cyc = Q("BatteryCycleCount", "CycleCount");
            if (design <= 0 || full <= 0) return "读取不到电池信息";
            double health = full / design * 100.0;
            return "设计容量 " + (design / 1000).ToString("0.0") + "Wh  ·  当前满电 " + (full / 1000).ToString("0.0") + "Wh\n" +
                   "健康度 " + health.ToString("0.0") + "%（损耗 " + Math.Max(0, 100 - health).ToString("0.0") + "%）  ·  " +
                   "循环 " + (cyc > 0 ? cyc.ToString("0") + " 次" : "--");
        }
    }

    // ===== 主题（浅色 / 深色） =====
    static class Theme
    {
        public static bool Dark;
        public static int Choice;   // 0 跟随系统 1 浅色 2 深色
        public static Color Back, Fore, Gray, Btn, Border, GraphBack;
        public static readonly Color Accent = Color.FromArgb(31, 111, 235);
        public static readonly Color Red = Color.FromArgb(220, 60, 60);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        static bool SystemDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static void Load()
        {
            Choice = Settings.GetInt("Theme", 0);
            Dark = Choice == 2 || (Choice == 0 && SystemDark());
            if (Dark)
            {
                Back = Color.FromArgb(32, 32, 32); Fore = Color.FromArgb(235, 235, 235);
                Gray = Color.FromArgb(150, 150, 150); Btn = Color.FromArgb(55, 55, 55);
                Border = Color.FromArgb(85, 85, 85); GraphBack = Color.FromArgb(42, 42, 42);
            }
            else
            {
                Back = Color.FromArgb(243, 243, 243); Fore = Color.FromArgb(25, 25, 25);
                Gray = Color.FromArgb(110, 110, 110); Btn = Color.White;
                Border = Color.FromArgb(200, 200, 200); GraphBack = Color.White;
            }
        }

        public static void Apply(Control c)
        {
            Button b = c as Button;
            Label l = c as Label;
            CheckBox cb = c as CheckBox;
            if (c is Graph) c.Invalidate();
            else if (c is Form || c is Panel) c.BackColor = Back;

            if (b != null)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Border;
                b.BackColor = Btn;
                b.ForeColor = Fore;
            }
            else if (l != null)
            {
                string t = l.Tag as string;
                l.ForeColor = t == "gray" ? Gray : (t == "red" ? Red : Fore);
                l.BackColor = Color.Transparent;
            }
            else if (cb != null) cb.ForeColor = Fore;
            else if (c is ComboBox || c is NumericUpDown || c is ListBox)
            {
                c.BackColor = Btn;
                c.ForeColor = Fore;
            }
            foreach (Control ch in c.Controls) Apply(ch);
        }

        // Windows 10 20H1+ / Windows 11 的深色标题栏
        public static void TitleBar(Form f)
        {
            try { int v = Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); } catch { }
        }
    }

    // ===== 监控数据 + 曲线图 =====
    class Sample
    {
        public DateTime T;
        public int Cpu, Gpu, FanA, FanB, Mode;
        public double Load;
    }

    class Graph : Panel
    {
        public List<Sample> Data;
        public int Capacity = 300;   // 300 个点 × 2 秒 = 10 分钟

        public Graph() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.GraphBack);
            float sc = g.DpiX / 96F;
            int L = (int)(30 * sc), R = (int)(40 * sc), T = (int)(22 * sc), B = (int)(16 * sc);
            int w = Width - L - R, h = Height - T - B;
            if (w <= 10 || h <= 10) return;

            using (Pen grid = new Pen(Theme.Border))
            using (Brush tb = new SolidBrush(Theme.Gray))
            using (Font f = new Font("Microsoft YaHei UI", 7.5F))
            {
                // 左轴温度 30~100°C，右轴转速 0~7000 RPM
                for (int t = 30; t <= 100; t += 10)
                {
                    float y = T + h - (t - 30) / 70F * h;
                    g.DrawLine(grid, L, y, L + w, y);
                    if (t % 20 == 0 || t == 30)
                    {
                        g.DrawString(t + "°", f, tb, 2, y - 7 * sc);
                        int rpm = (int)((t - 30) / 70F * 7000);
                        g.DrawString(rpm.ToString(), f, tb, L + w + 3, y - 7 * sc);
                    }
                }
                g.DrawString("最近 10 分钟", f, tb, L, T + h + 1);

                // 图例
                float lx = L;
                lx = Legend(g, f, lx, 3, Color.FromArgb(230, 70, 60), "CPU 温度");
                lx = Legend(g, f, lx, 3, Color.FromArgb(160, 100, 230), "GPU 温度");
                Legend(g, f, lx, 3, Color.FromArgb(40, 140, 240), "风扇A 转速");

                if (Data == null || Data.Count < 2)
                {
                    g.DrawString("正在收集数据……", f, tb, L + w / 2 - 40 * sc, T + h / 2);
                    return;
                }

                float step = (float)w / (Capacity - 1);
                int start = Capacity - Data.Count;
                Line(g, Color.FromArgb(40, 140, 240), L, T, w, h, step, start, 2, delegate (Sample s) { return s.FanA / 7000F; });
                Line(g, Color.FromArgb(160, 100, 230), L, T, w, h, step, start, 2, delegate (Sample s) { return (s.Gpu - 30) / 70F; });
                Line(g, Color.FromArgb(230, 70, 60), L, T, w, h, step, start, 2, delegate (Sample s) { return (s.Cpu - 30) / 70F; });
            }
        }

        delegate float Pick(Sample s);

        void Line(Graphics g, Color c, int L, int T, int w, int h, float step, int start, float width, Pick pick)
        {
            PointF[] pts = new PointF[Data.Count];
            for (int i = 0; i < Data.Count; i++)
            {
                float v = Math.Max(0, Math.Min(1, pick(Data[i])));
                pts[i] = new PointF(L + (start + i) * step, T + h - v * h);
            }
            using (Pen p = new Pen(c, width)) g.DrawLines(p, pts);
        }

        float Legend(Graphics g, Font f, float x, float y, Color c, string text)
        {
            using (Brush b = new SolidBrush(c)) g.FillRectangle(b, x, y + 5, 10, 3);
            using (Brush tb = new SolidBrush(Theme.Fore)) g.DrawString(text, f, tb, x + 13, y);
            return x + 13 + g.MeasureString(text, f).Width + 8;
        }
    }
}
