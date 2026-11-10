// Redmi Helper - 主窗口
// 只用 C# 5 语法，以便用 Windows 自带的 csc.exe 编译

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RedmiHelper
{
    class MainForm : Form
    {
        public const string Version = "1.3";

        public static readonly string[] ModeNames = { L.T("静谧", "Silent"), L.T("均衡", "Balanced"), L.T("极速", "Turbo"), L.T("狂派", "Beast") };
        static readonly byte[] ModeValues = { 2, 1, 3, 4 };
        public static readonly Color[] ModeColors = {
            Color.FromArgb(46, 160, 67),   // 静谧 绿
            Color.FromArgb(31, 111, 235),  // 均衡 蓝
            Color.FromArgb(219, 109, 40),  // 极速 橙
            Color.FromArgb(207, 34, 46)    // 狂派 红
        };
        static readonly string[] GpuNames = { L.T("混合模式", "Hybrid"), L.T("独显直连", "dGPU only") };
        static readonly Color GpuColor = Color.FromArgb(110, 84, 200);
        static readonly Color PowerColor = Color.FromArgb(0, 135, 140);
        static readonly string[] TabNames = { L.T("主页", "Home"), L.T("监控", "Monitor"), L.T("自动", "Auto"), L.T("设置", "Settings") };

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;

        // ---- 界面 ----
        float sc = 1F;
        Font fBig, fHead;
        Label lblCpu, lblGpu, lblFanA, lblFanB, lblBat, lblStatus;
        Label lblGpuNote, lblAuto, lblHz, lblGames, lblCtrlKey, lblNv, lblNvApps, lblHealth;
        Button[] tabButtons = new Button[4];
        Panel[] pages = new Panel[4];
        int page = 0;
        Button[] modeButtons = new Button[4];
        Button[] gpuButtons = new Button[2];
        Button[] powerButtons = new Button[3];
        ToolStripMenuItem[] modeItems = new ToolStripMenuItem[4];
        CheckBox chkFn, chkTouchpad, chkWin, chkBattery, chkHz, chkLink, chkAuto, chkSmart, chkSched, chkCsv, chkTurboOff, chkTurboAuto;
        ComboBox cmbLow, cmbHigh, cmbSched, cmbTheme, cmbLang;
        NumericUpDown nSh, nSm, nEh, nEm;
        Graph graph;
        NotifyIcon tray;
        TrayPopup popup;
        System.Windows.Forms.Timer timer, nvTimer;

        // ---- 状态 ----
        IntPtr iconHandle = IntPtr.Zero;
        string lastIconKey = "";
        bool exiting = false, updatingUI = false;
        int currentMode = -1;   // ModeNames 的下标，-1 = 未知
        int gpuMode = -1;       // 0 混合 1 独显直连（设置值，可能还没重启生效）
        int bootGpuMode = -1;   // 当前实际生效的显卡模式
        int powerMode = -1;
        int cpuTemp = 0, gpuTemp = 0, fanA = 0, fanB = 0;
        double cpuLoad = 0;
        List<double> loadHist = new List<double>();
        List<Sample> history = new List<Sample>();
        bool learningKey = false;
        ManagementEventWatcher keyWatcher;
        ManagementObjectSearcher batSearcher;
        bool nvBusy = false, healthLoaded = false;
        public DateTime PopupHiddenAt = DateTime.MinValue;

        // ---- 自动化 ----
        bool smartHigh = false;
        string lastAutoKey = "";
        int baseMode = -1;
        bool manualPaused = false, autoFailed = false;

        public MainForm()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics dg = CreateGraphics()) sc = dg.DpiX / 96F;
            Font = new Font("Microsoft YaHei UI", 9F);
            fBig = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            fHead = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            Text = "Redmi Helper";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = Z(340, 596);

            // 顶部：温度 / 风扇 / 电池（所有页面都显示）
            lblCpu = AddLabel(this, "CPU  --°C", 16, 10, 150, 30, fBig, null);
            lblGpu = AddLabel(this, "GPU  --°C", 176, 10, 150, 30, fBig, null);
            lblFanA = AddLabel(this, L.T("风扇A  -- RPM", "Fan A  -- RPM"), 16, 42, 150, 22, null, null);
            lblFanB = AddLabel(this, L.T("风扇B  -- RPM", "Fan B  -- RPM"), 176, 42, 150, 22, null, null);
            lblBat = AddLabel(this, L.T("电池 --", "Battery --"), 16, 64, 310, 22, null, "gray");

            // 分页按钮
            for (int i = 0; i < 4; i++)
            {
                Button b = AddButton(this, TabNames[i], 12 + i * 80, 92, 76, 30, i);
                b.Click += OnTab;
                tabButtons[i] = b;
                Panel p = new Panel();
                p.Location = P(0, 130);
                p.Size = Z(340, 418);
                p.Visible = i == 0;
                Controls.Add(p);
                pages[i] = p;
            }

            BuildHome(pages[0]);
            BuildMonitor(pages[1]);
            BuildAuto(pages[2]);
            BuildSettings(pages[3]);

            lblStatus = AddLabel(this, "", 16, 550, 310, 44, null, "gray");
            ResumeLayout(false);

            BuildTray();
            popup = new TrayPopup(this, sc);

            LoadSettings();

            // 先创建窗口句柄：隐藏启动时也要靠它接收快捷键和电源事件
            IntPtr h = Handle;
            RegisterHotKey(h, 1, MOD_CONTROL | MOD_ALT, (uint)Keys.M);
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;

            ReadGpuAtStartup();
            ApplyTheme();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            nvTimer = new System.Windows.Forms.Timer();
            nvTimer.Interval = 3000;
            nvTimer.Tick += delegate { PollNvidia(); };
            nvTimer.Start();

            CpuLoad.Sample();
            RefreshData();
            if (chkHz.Checked) ApplyRefreshRate(false);
            if (chkTurboAuto.Checked) ApplyTurboAuto();
            StartKeyWatcher();
        }

        // ================= 界面搭建 =================
        Point P(int x, int y) { return new Point((int)(x * sc), (int)(y * sc)); }
        Size Z(int w, int h) { return new Size((int)(w * sc), (int)(h * sc)); }

        Label AddLabel(Control parent, string text, int x, int y, int w, int h, Font f, string tag)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = P(x, y);
            l.Size = Z(w, h);
            l.Tag = tag;
            if (f != null) l.Font = f;
            parent.Controls.Add(l);
            return l;
        }

        Button AddButton(Control parent, string text, int x, int y, int w, int h, int tag)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = P(x, y);
            b.Size = Z(w, h);
            b.Tag = tag;
            b.FlatStyle = FlatStyle.Flat;
            parent.Controls.Add(b);
            return b;
        }

        CheckBox AddCheck(Control parent, string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = P(x, y);
            c.AutoSize = true;
            parent.Controls.Add(c);
            return c;
        }

        ComboBox AddCombo(Control parent, string[] items, int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.Items.AddRange(items);
            c.Location = P(x, y);
            c.Width = (int)(w * sc);
            parent.Controls.Add(c);
            return c;
        }

        NumericUpDown AddNum(Control parent, int x, int y, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = 0; n.Maximum = max;
            n.Location = P(x, y);
            n.Width = (int)(46 * sc);
            n.BorderStyle = BorderStyle.FixedSingle;
            parent.Controls.Add(n);
            return n;
        }

        void BuildHome(Panel p)
        {
            AddLabel(p, L.T("性能模式　（快捷键 Ctrl+Alt+M 循环切换）", "Performance mode  (Ctrl+Alt+M to cycle)"), 16, 0, 310, 22, fHead, null);
            for (int i = 0; i < 4; i++)
            {
                Button b = AddButton(p, ModeNames[i], 12 + i * 80, 24, 76, 36, i);
                b.Click += delegate (object s, EventArgs e) { SetMode((int)((Button)s).Tag, true); };
                modeButtons[i] = b;
            }

            AddLabel(p, L.T("显卡模式", "GPU mode"), 16, 72, 200, 22, fHead, null);
            for (int i = 0; i < 2; i++)
            {
                Button b = AddButton(p, GpuNames[i], 12 + i * 160, 96, 156, 36, i);
                b.Click += OnGpuButton;
                gpuButtons[i] = b;
            }
            lblGpuNote = AddLabel(p, "", 16, 136, 310, 22, null, "red");

            AddLabel(p, L.T("电源计划", "Power plan"), 16, 162, 200, 22, fHead, null);
            for (int i = 0; i < 3; i++)
            {
                Button b = AddButton(p, PowerPlan.Names[i], 12 + i * 107, 186, 102, 36, i);
                b.Click += OnPowerButton;
                powerButtons[i] = b;
            }

            AddLabel(p, L.T("按键", "Keys"), 16, 234, 200, 22, fHead, null);
            chkFn = AddCheck(p, L.T("Fn 锁", "Fn lock"), 16, 258);
            chkTouchpad = AddCheck(p, L.T("锁触控板", "Lock touchpad"), 120, 258);
            chkWin = AddCheck(p, L.T("锁 Win 键", "Lock Win key"), 228, 258);
            chkFn.CheckedChanged += delegate { if (!updatingUI) SetKey(Mi.KEY_FN, chkFn.Checked, L.T("Fn 锁", "Fn lock")); };
            chkTouchpad.CheckedChanged += delegate { if (!updatingUI) SetKey(Mi.KEY_TOUCHPAD, chkTouchpad.Checked, L.T("触控板锁", "Touchpad lock")); };
            chkWin.CheckedChanged += delegate { if (!updatingUI) SetKey(Mi.KEY_WIN, chkWin.Checked, L.T("Win 键锁", "Win key lock")); };

            AddLabel(p, L.T("CPU 睿频", "CPU turbo boost"), 16, 292, 200, 22, fHead, null);
            chkTurboOff = AddCheck(p, L.T("关闭睿频（更凉、更安静、更省电，峰值性能下降）", "Disable turbo (cooler, quieter, less peak speed)"), 16, 314);
            chkTurboOff.CheckedChanged += delegate { if (!updatingUI) SetTurbo(!chkTurboOff.Checked, true); };

            AddLabel(p, L.T("自动切换", "Automation"), 16, 348, 80, 22, fHead, null);
            lblAuto = AddLabel(p, L.T("未启用", "Off"), 96, 348, 230, 22, null, "gray");
            lblHz = AddLabel(p, L.T("屏幕 --Hz", "Display --Hz"), 16, 372, 310, 22, null, "gray");
        }

        void BuildMonitor(Panel p)
        {
            graph = new Graph();
            graph.Location = P(12, 0);
            graph.Size = Z(316, 160);
            graph.Data = history;
            p.Controls.Add(graph);

            chkCsv = AddCheck(p, L.T("持续记录到 CSV（logs 文件夹）", "Log to CSV (logs folder)"), 16, 166);
            Button exp = AddButton(p, L.T("导出曲线…", "Export..."), 228, 164, 100, 26, -1);
            exp.Click += delegate { ExportCsv(); };

            AddLabel(p, L.T("独显", "dGPU"), 16, 198, 60, 22, fHead, null);
            AddLabel(p, L.T("（只在打开这一页时读取，避免唤醒独显）", "(read only on this page to avoid waking it)"), 60, 198, 268, 22, null, "gray");
            lblNv = AddLabel(p, L.T("读取中……", "Loading..."), 16, 220, 312, 58, null, null);
            AddLabel(p, L.T("正在使用独显的程序", "Apps using the dGPU"), 16, 282, 200, 22, fHead, null);
            lblNvApps = AddLabel(p, "--", 16, 304, 312, 40, null, null);

            AddLabel(p, L.T("电池健康", "Battery health"), 16, 350, 200, 22, fHead, null);
            lblHealth = AddLabel(p, "--", 16, 372, 312, 42, null, null);
        }

        void BuildAuto(Panel p)
        {
            AddLabel(p, L.T("智能模式", "Smart mode"), 16, 0, 200, 22, fHead, null);
            chkSmart = AddCheck(p, L.T("根据 CPU 负载和温度自动切换", "Switch by CPU load and temperature"), 16, 22);
            AddLabel(p, L.T("平时", "Normal"), 16, 52, 40, 22, null, null);
            cmbLow = AddCombo(p, ModeNames, 56, 49, 76);
            AddLabel(p, L.T("高负载时", "Heavy load"), 150, 52, 70, 22, null, null);
            cmbHigh = AddCombo(p, ModeNames, 222, 49, 76);

            AddLabel(p, L.T("定时规则", "Schedule"), 16, 88, 200, 22, fHead, null);
            chkSched = AddCheck(p, L.T("每天", "Daily"), 16, 112);
            nSh = AddNum(p, 74, 110, 23);
            AddLabel(p, ":", 122, 113, 10, 22, null, null);
            nSm = AddNum(p, 132, 110, 59);
            AddLabel(p, L.T("到", "to"), 184, 113, 20, 22, null, null);
            nEh = AddNum(p, 206, 110, 23);
            AddLabel(p, ":", 254, 113, 10, 22, null, null);
            nEm = AddNum(p, 264, 110, 59);
            AddLabel(p, L.T("切换到", "Use mode"), 16, 146, 50, 22, null, null);
            cmbSched = AddCombo(p, ModeNames, 70, 143, 90);

            AddLabel(p, L.T("游戏", "Games"), 16, 180, 200, 22, fHead, null);
            Button bg = AddButton(p, L.T("游戏列表…", "Game list..."), 12, 202, 120, 30, -1);
            bg.Click += delegate { OpenGames(); };
            lblGames = AddLabel(p, "", 140, 208, 190, 22, null, "gray");

            AddLabel(p, L.T("电源", "Power"), 16, 242, 200, 22, fHead, null);
            chkBattery = AddCheck(p, L.T("拔掉电源时切到静谧，插回恢复原模式", "On battery: Silent, restore when plugged in"), 16, 264);
            chkLink = AddCheck(p, L.T("电源计划跟随性能模式", "Power plan follows performance mode"), 16, 288);
            chkHz = AddCheck(p, L.T("插电用最高刷新率，用电池切到 60Hz", "Max refresh rate plugged in, 60Hz on battery"), 16, 312);
            chkTurboAuto = AddCheck(p, L.T("静谧模式或用电池时关闭睿频，其他时候开启", "Turbo off in Silent mode or on battery"), 16, 336);

            AddLabel(p, L.T("优先级：游戏 > 定时 > 智能。你手动切换模式后，自动切换会暂停，直到情况变化（比如游戏关掉、到了定时时间）。", "Priority: games > schedule > smart. After you switch modes manually, automation pauses until something changes (a game closes, the schedule starts or ends)."),
                16, 364, 312, 52, null, "gray");
        }

        void BuildSettings(Panel p)
        {
            AddLabel(p, L.T("外观", "Appearance"), 16, 0, 200, 22, fHead, null);
            AddLabel(p, L.T("主题", "Theme"), 16, 26, 46, 22, null, null);
            cmbTheme = AddCombo(p, new string[] { L.T("跟随系统", "System"), L.T("浅色", "Light"), L.T("深色", "Dark") }, 64, 23, 100);
            AddLabel(p, L.T("语言", "Language"), 180, 26, 66, 22, null, null);
            cmbLang = AddCombo(p, new string[] { L.T("跟随系统", "System"), "中文", "English" }, 248, 23, 80);

            AddLabel(p, L.T("启动", "Startup"), 16, 62, 200, 22, fHead, null);
            chkAuto = AddCheck(p, L.T("开机自动启动（后台托盘运行）", "Start with Windows (in tray)"), 16, 84);

            AddLabel(p, L.T("键盘", "Keyboard"), 16, 118, 200, 22, fHead, null);
            Button bl = AddButton(p, L.T("设置控制中心键", "Set Control Center key"), 12, 140, 140, 30, -1);
            bl.Click += delegate { learningKey = true; lblStatus.Text = L.T("请按一下键盘上的「控制中心」键……", "Press the Control Center key on your keyboard..."); };
            lblCtrlKey = AddLabel(p, "", 160, 146, 170, 22, null, "gray");

            AddLabel(p, L.T("数据", "Data"), 16, 182, 200, 22, fHead, null);
            Button bo = AddButton(p, L.T("打开日志文件夹", "Open logs folder"), 12, 204, 140, 30, -1);
            bo.Click += delegate
            {
                string dir = LogDir();
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            };

            AddLabel(p, L.T("关于", "About"), 16, 246, 200, 22, fHead, null);
            AddLabel(p, "Redmi Helper v" + Version + L.T("　适用于 Redmi G Pro 2022（Intel）\n", "  for Redmi G Pro 2022 (Intel)\n") +
                        L.T("通过 BIOS 的 WMI 接口控制硬件，命令来自实测和对官方软件的分析。\n", "Controls hardware through the BIOS WMI interface; commands come from testing and analysis of the official app.\n") +
                        L.T("左键托盘图标打开小窗，双击打开完整窗口。", "Click the tray icon for the mini panel, double-click for the full window."),
                16, 268, 312, 70, null, "gray");
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            for (int i = 0; i < 4; i++)
            {
                ToolStripMenuItem it = new ToolStripMenuItem(ModeNames[i]);
                it.Tag = i;
                it.Click += delegate (object s, EventArgs e) { SetMode((int)((ToolStripMenuItem)s).Tag, true); };
                menu.Items.Add(it);
                modeItems[i] = it;
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("打开完整窗口", "Open full window"), null, delegate { ShowMain(); });
            menu.Items.Add(L.T("退出", "Exit"), null, delegate { ExitApp(); });

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Text = "Redmi Helper";
            tray.Icon = SystemIcons.Application;
            tray.Visible = true;
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if ((DateTime.Now - PopupHiddenAt).TotalMilliseconds < 300) return;  // 刚因为失去焦点被收起
                if (popup.Visible) popup.Hide();
                else { UpdatePopup(); popup.ShowNear(); }
            };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) { popup.Hide(); ShowMain(); } };
        }

        void LoadSettings()
        {
            chkBattery.Checked = Settings.GetInt("AutoBattery", 0) == 1;
            chkLink.Checked = Settings.GetInt("LinkPower", 0) == 1;
            chkHz.Checked = Settings.GetInt("AutoHz", 0) == 1;
            chkSmart.Checked = Settings.GetInt("Smart", 0) == 1;
            chkSched.Checked = Settings.GetInt("Sched", 0) == 1;
            chkCsv.Checked = Settings.GetInt("Csv", 0) == 1;
            chkTurboAuto.Checked = Settings.GetInt("TurboAuto", 0) == 1;
            cmbLow.SelectedIndex = Clamp(Settings.GetInt("SmartLow", 1), 0, 3);
            cmbHigh.SelectedIndex = Clamp(Settings.GetInt("SmartHigh", 3), 0, 3);
            cmbSched.SelectedIndex = Clamp(Settings.GetInt("SchedMode", 0), 0, 3);
            nSh.Value = Clamp(Settings.GetInt("SchedSH", 23), 0, 23);
            nSm.Value = Clamp(Settings.GetInt("SchedSM", 0), 0, 59);
            nEh.Value = Clamp(Settings.GetInt("SchedEH", 7), 0, 23);
            nEm.Value = Clamp(Settings.GetInt("SchedEM", 0), 0, 59);
            cmbTheme.SelectedIndex = Clamp(Settings.GetInt("Theme", 0), 0, 2);
            cmbLang.SelectedIndex = Clamp(Settings.GetInt("Lang", 0), 0, 2);
            chkAuto.Checked = AutoStart.IsEnabled();
            UpdateGamesLabel();
            UpdateCtrlKeyLabel();

            chkBattery.CheckedChanged += delegate { Settings.SetInt("AutoBattery", chkBattery.Checked ? 1 : 0); };
            chkLink.CheckedChanged += delegate
            {
                Settings.SetInt("LinkPower", chkLink.Checked ? 1 : 0);
                if (chkLink.Checked && currentMode >= 0) ApplyLinkedPower();
            };
            chkHz.CheckedChanged += delegate
            {
                Settings.SetInt("AutoHz", chkHz.Checked ? 1 : 0);
                if (chkHz.Checked) ApplyRefreshRate(true);
            };
            chkSmart.CheckedChanged += delegate { Settings.SetInt("Smart", chkSmart.Checked ? 1 : 0); Automation(); };
            chkSched.CheckedChanged += delegate { Settings.SetInt("Sched", chkSched.Checked ? 1 : 0); Automation(); };
            chkCsv.CheckedChanged += delegate { Settings.SetInt("Csv", chkCsv.Checked ? 1 : 0); };
            chkTurboAuto.CheckedChanged += delegate
            {
                Settings.SetInt("TurboAuto", chkTurboAuto.Checked ? 1 : 0);
                if (chkTurboAuto.Checked) ApplyTurboAuto();
            };
            cmbLow.SelectedIndexChanged += delegate { Settings.SetInt("SmartLow", cmbLow.SelectedIndex); };
            cmbHigh.SelectedIndexChanged += delegate { Settings.SetInt("SmartHigh", cmbHigh.SelectedIndex); };
            cmbSched.SelectedIndexChanged += delegate { Settings.SetInt("SchedMode", cmbSched.SelectedIndex); };
            nSh.ValueChanged += delegate { Settings.SetInt("SchedSH", (int)nSh.Value); };
            nSm.ValueChanged += delegate { Settings.SetInt("SchedSM", (int)nSm.Value); };
            nEh.ValueChanged += delegate { Settings.SetInt("SchedEH", (int)nEh.Value); };
            nEm.ValueChanged += delegate { Settings.SetInt("SchedEM", (int)nEm.Value); };
            cmbTheme.SelectedIndexChanged += delegate { Settings.SetInt("Theme", cmbTheme.SelectedIndex); ApplyTheme(); };
            chkAuto.CheckedChanged += OnAutoChanged;
            cmbLang.SelectedIndexChanged += delegate
            {
                Settings.SetInt("Lang", cmbLang.SelectedIndex);
                lblStatus.Text = L.T("重启 Redmi Helper 后生效（托盘菜单 → 退出，再重新打开）",
                                     "Restart Redmi Helper to apply (tray menu → Exit, then reopen)");
            };
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ================= 主题 / 分页 =================
        void ApplyTheme()
        {
            Theme.Load();
            Theme.Apply(this);
            Theme.Apply(popup);
            Theme.TitleBar(this);
            UpdateTabs();
            UpdateModeUI();
            UpdateGpuUI();
            UpdatePowerUI();
            popup.Invalidate();
        }

        void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Theme.Choice != 0) return;
            try { BeginInvoke(new MethodInvoker(ApplyTheme)); } catch { }
        }

        void OnTab(object sender, EventArgs e)
        {
            page = (int)((Button)sender).Tag;
            UpdateTabs();
            if (page == 1)
            {
                graph.Invalidate();
                if (!healthLoaded) { healthLoaded = true; lblHealth.Text = BatteryHealth.Text(); }
                PollNvidia();
            }
        }

        void UpdateTabs()
        {
            for (int i = 0; i < 4; i++)
            {
                bool on = i == page;
                tabButtons[i].BackColor = on ? Theme.Accent : Theme.Btn;
                tabButtons[i].ForeColor = on ? Color.White : Theme.Fore;
                pages[i].Visible = on;
            }
        }

        void Highlight(Button[] bs, int sel, Color c)
        {
            for (int i = 0; i < bs.Length; i++)
            {
                bool on = i == sel;
                bs[i].BackColor = on ? c : Theme.Btn;
                bs[i].ForeColor = on ? Color.White : Theme.Fore;
            }
        }

        // ================= 定时刷新 =================
        void Tick()
        {
            RefreshData();
            AddSample();
            Automation();
        }

        void RefreshData()
        {
            try
            {
                byte[] r = Mi.Get(Mi.CMD_SENSORS);
                if (Mi.Ok(r, Mi.CMD_SENSORS))
                {
                    fanA = r[4] + 256 * r[5];
                    fanB = r[6] + 256 * r[7];
                    cpuTemp = r[10];
                    gpuTemp = r[14];
                    lblCpu.Text = "CPU  " + cpuTemp + "°C";
                    lblGpu.Text = "GPU  " + gpuTemp + "°C";
                    lblFanA.Text = L.T("风扇A  ", "Fan A  ") + fanA + " RPM";
                    lblFanB.Text = L.T("风扇B  ", "Fan B  ") + fanB + " RPM";
                }

                byte[] m = Mi.Get(Mi.CMD_PERF);
                if (Mi.Ok(m, Mi.CMD_PERF)) currentMode = Array.IndexOf(ModeValues, m[4]);

                updatingUI = true;
                try
                {
                    byte[] k;
                    k = Mi.GetKey(Mi.KEY_FN); if (Mi.Ok(k, Mi.CMD_KEYS)) chkFn.Checked = k[6] == 1;
                    k = Mi.GetKey(Mi.KEY_TOUCHPAD); if (Mi.Ok(k, Mi.CMD_KEYS)) chkTouchpad.Checked = k[6] == 1;
                    k = Mi.GetKey(Mi.KEY_WIN); if (Mi.Ok(k, Mi.CMD_KEYS)) chkWin.Checked = k[6] == 1;
                }
                finally { updatingUI = false; }

                int tb = Turbo.Get();
                if (tb >= 0) { updatingUI = true; chkTurboOff.Checked = tb == 0; updatingUI = false; }

                powerMode = PowerPlan.Get();
                int hz = Display.Current();
                lblHz.Text = hz > 0 ? L.T("屏幕刷新率 ", "Display ") + hz + "Hz" : "";

                UpdateModeUI();
                UpdateGpuUI();
                UpdatePowerUI();
                UpdateBattery();
                UpdateTray();
                if (popup.Visible) UpdatePopup();
            }
            catch (Exception ex)
            {
                lblStatus.Text = L.T("读取失败：", "Read failed: ") + ex.Message;
            }
        }

        void UpdateBattery()
        {
            try
            {
                if (batSearcher == null)
                    batSearcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryStatus");
                int pct = (int)(SystemInformation.PowerStatus.BatteryLifePercent * 100);
                foreach (ManagementObject o in batSearcher.Get())
                {
                    bool online = Convert.ToBoolean(o["PowerOnline"]);
                    double dis = Convert.ToDouble(o["DischargeRate"]) / 1000.0;
                    double chg = Convert.ToDouble(o["ChargeRate"]) / 1000.0;
                    double remain = Convert.ToDouble(o["RemainingCapacity"]) / 1000.0;
                    string t = L.T("电池 ", "Battery ") + pct + "%";
                    if (!online && dis > 0.5)
                        t += L.T("  ·  放电 ", "  ·  Draw ") + dis.ToString("0.0") + L.T("W  ·  约 ", "W  ·  ~") + (remain / dis).ToString("0.0") + L.T(" 小时", " h");
                    else if (online && chg > 0.5)
                        t += L.T("  ·  充电 ", "  ·  Charging ") + chg.ToString("0.0") + "W";
                    else if (online)
                        t += L.T("  ·  已接通电源", "  ·  Plugged in");
                    lblBat.Text = t;
                    break;
                }
            }
            catch { }
        }

        // ================= 监控：曲线 / CSV / 独显 =================
        void AddSample()
        {
            cpuLoad = CpuLoad.Sample();
            loadHist.Add(cpuLoad);
            if (loadHist.Count > 15) loadHist.RemoveAt(0);

            Sample s = new Sample();
            s.T = DateTime.Now; s.Cpu = cpuTemp; s.Gpu = gpuTemp; s.FanA = fanA; s.FanB = fanB;
            s.Load = cpuLoad; s.Mode = currentMode;
            history.Add(s);
            if (history.Count > graph.Capacity) history.RemoveAt(0);
            if (Visible && page == 1) graph.Invalidate();

            if (chkCsv.Checked)
            {
                try
                {
                    string dir = LogDir();
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, "temps-" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
                    bool isNew = !File.Exists(file);
                    using (StreamWriter w = new StreamWriter(file, true, new UTF8Encoding(true)))
                    {
                        if (isNew) w.WriteLine(CsvHeader);
                        w.WriteLine(CsvLine(s));
                    }
                }
                catch { }
            }
        }

        const string CsvHeader = "time,cpu_temp_c,gpu_temp_c,fan_a_rpm,fan_b_rpm,cpu_load_pct,mode";

        static string CsvLine(Sample s)
        {
            return s.T.ToString("yyyy-MM-dd HH:mm:ss") + "," + s.Cpu + "," + s.Gpu + "," + s.FanA + "," + s.FanB + "," +
                   s.Load.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   (s.Mode >= 0 ? ModeNames[s.Mode] : "");
        }

        static string LogDir() { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "logs"); }

        void ExportCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = L.T("CSV 文件 (*.csv)|*.csv", "CSV files (*.csv)|*.csv");
                d.FileName = "redmi-helper-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (StreamWriter w = new StreamWriter(d.FileName, false, new UTF8Encoding(true)))
                    {
                        w.WriteLine(CsvHeader);
                        foreach (Sample s in history) w.WriteLine(CsvLine(s));
                    }
                    lblStatus.Text = L.T("已导出 ", "Exported ") + history.Count + L.T(" 条数据", " rows");
                }
                catch (Exception ex) { lblStatus.Text = L.T("导出失败：", "Export failed: ") + ex.Message; }
            }
        }

        // nvidia-smi 会唤醒独显，所以只在窗口可见且停在「监控」页时才读取
        void PollNvidia()
        {
            if (!Visible || page != 1 || nvBusy) return;
            nvBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string a = Nvidia.Status();
                string b = Nvidia.Apps();
                if (bootGpuMode == 1) b = L.T("独显直连模式下所有程序都用独显\n", "In dGPU-only mode every app uses the dGPU\n") + b;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        lblNv.Text = a;
                        lblNvApps.Text = b;
                        nvBusy = false;
                    }));
                }
                catch { nvBusy = false; }
            });
        }

        // ================= 性能模式 =================
        // manual = 用户手动切换（会暂停自动切换）
        public bool SetMode(int idx, bool manual)
        {
            try
            {
                Mi.Set(Mi.CMD_PERF, ModeValues[idx]);
                Thread.Sleep(300);
                byte[] m = Mi.Get(Mi.CMD_PERF);
                bool ok = Mi.Ok(m, Mi.CMD_PERF) && m[4] == ModeValues[idx];
                if (ok)
                {
                    currentMode = idx;
                    lblStatus.Text = L.T("已切换到「", "Switched to \"") + ModeNames[idx] + L.T("」", "\"");
                    if (chkLink.Checked) ApplyLinkedPower();
                    if (chkTurboAuto.Checked) ApplyTurboAuto();
                    if (manual && lastAutoKey != "")
                    {
                        manualPaused = true;
                        lblStatus.Text += L.T("，自动切换暂停到情况变化为止", " (automation paused until something changes)");
                    }
                }
                else
                {
                    lblStatus.Text = L.T("「", "\"") + ModeNames[idx] + L.T("」切换没有生效（极速/狂派可能需要插原装圆口电源）", "\" did not apply (Turbo/Beast may need the original barrel charger)");
                    tray.ShowBalloonTip(3000, "Redmi Helper", lblStatus.Text, ToolTipIcon.Warning);
                }
                UpdateModeUI();
                UpdateTray();
                UpdateAutoLabel();
                if (popup.Visible) UpdatePopup();
                return ok;
            }
            catch (Exception ex)
            {
                lblStatus.Text = L.T("切换失败：", "Switch failed: ") + ex.Message;
                return false;
            }
        }

        void UpdateModeUI()
        {
            Highlight(modeButtons, currentMode, currentMode >= 0 ? ModeColors[currentMode] : Theme.Accent);
            for (int i = 0; i < 4; i++) modeItems[i].Checked = i == currentMode;
        }

        // ================= 显卡模式 =================
        void ReadGpuAtStartup()
        {
            try
            {
                byte[] g = Mi.Get(Mi.CMD_GPU);
                if (Mi.Ok(g, Mi.CMD_GPU) && g[4] <= 1)
                {
                    bootGpuMode = g[4];
                    gpuMode = g[4];
                    int pending = Settings.GetInt("PendingGpu", -1);
                    if (pending == 0 || pending == 1)
                    {
                        if (pending == bootGpuMode) Settings.SetInt("PendingGpu", -1);
                        else gpuMode = pending;
                    }
                }
            }
            catch { }
        }

        void OnGpuButton(object sender, EventArgs e)
        {
            int idx = (int)((Button)sender).Tag;
            if (idx == gpuMode) return;
            try
            {
                // 重启前读回的仍是当前实际模式，所以只看写入回应的状态字节
                byte[] r = Mi.Set(Mi.CMD_GPU, (byte)idx);
                if (!Mi.Accepted(r)) { lblStatus.Text = L.T("显卡模式切换没有生效", "GPU mode change did not apply"); return; }
                gpuMode = idx;
                Settings.SetInt("PendingGpu", idx == bootGpuMode ? -1 : idx);
                UpdateGpuUI();
                if (idx == bootGpuMode) { lblStatus.Text = L.T("已撤销显卡模式更改", "GPU mode change undone"); return; }
                lblStatus.Text = L.T("显卡模式已设为「", "GPU mode set to \"") + GpuNames[idx] + L.T("」，重启后生效", "\", applies after restart");
                DialogResult dr = MessageBox.Show(
                    L.T("显卡模式已设为「", "GPU mode set to \"") + GpuNames[idx] + L.T("」，需要重启电脑才会生效。\n\n现在重启吗？（请先保存好打开的文件）", "\". Restart the computer to apply it.\n\nRestart now? (Save your open files first.)"),
                    "Redmi Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    ProcessStartInfo psi = new ProcessStartInfo("shutdown.exe", "/r /t 0");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
            }
            catch (Exception ex) { lblStatus.Text = L.T("显卡切换失败：", "GPU switch failed: ") + ex.Message; }
        }

        void UpdateGpuUI()
        {
            Highlight(gpuButtons, gpuMode, GpuColor);
            lblGpuNote.Text = (gpuMode >= 0 && bootGpuMode >= 0 && gpuMode != bootGpuMode)
                ? L.T("已更改，重启后生效（当前实际仍是「", "Changed, applies after restart (currently still \"") + GpuNames[bootGpuMode] + L.T("」）", "\")") : "";
        }

        // ================= 电源计划 =================
        void OnPowerButton(object sender, EventArgs e)
        {
            SetPlan((int)((Button)sender).Tag, true);
        }

        void SetPlan(int idx, bool showStatus)
        {
            int r = PowerPlan.Set(idx);
            if (r >= 0)
            {
                powerMode = idx;
                if (showStatus)
                    lblStatus.Text = L.T("电源计划：", "Power plan: ") + PowerPlan.Names[idx] +
                        (r == 1 ? L.T("（系统里原本隐藏了这个计划，已自动恢复）", " (it was hidden by Windows and has been restored)") : "");
                if (chkTurboAuto.Checked) ApplyTurboAuto();   // 每个计划的睿频设置是分开的
            }
            else lblStatus.Text = L.T("电源计划切换失败", "Power plan change failed");
            UpdatePowerUI();
        }

        // 静谧 -> 节能，均衡 -> 平衡，极速/狂派 -> 高性能
        void ApplyLinkedPower()
        {
            int target = currentMode == 0 ? 0 : (currentMode == 1 ? 1 : 2);
            if (target != powerMode) SetPlan(target, false);
            UpdatePowerUI();
        }

        void UpdatePowerUI() { Highlight(powerButtons, powerMode, PowerColor); }

        // ================= CPU 睿频 =================
        void SetTurbo(bool on, bool showStatus)
        {
            bool ok = Turbo.Set(on);
            if (showStatus || !ok)
                lblStatus.Text = ok ? (on ? L.T("CPU 睿频已开启", "Turbo boost on") : L.T("CPU 睿频已关闭", "Turbo boost off")) : L.T("睿频设置失败", "Turbo setting failed");
            updatingUI = true;
            chkTurboOff.Checked = !on;
            updatingUI = false;
        }

        // 静谧模式或用电池 -> 关闭；其他 -> 开启
        void ApplyTurboAuto()
        {
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            bool off = currentMode == 0 || onBattery;
            if (Turbo.Get() != (off ? 0 : 1)) SetTurbo(!off, false);
        }

        // ================= 按键锁 =================
        void SetKey(byte sub, bool on, string name)
        {
            try
            {
                Mi.SetKey(sub, on);
                lblStatus.Text = name + (on ? L.T(" 已开启", " on") : L.T(" 已关闭", " off"));
            }
            catch (Exception ex) { lblStatus.Text = name + L.T(" 设置失败：", " failed: ") + ex.Message; }
        }

        // ================= 自动切换（游戏 > 定时 > 智能） =================
        static string[] GameList()
        {
            return Settings.GetStr("Games", "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        string RunningGame()
        {
            foreach (string g in GameList())
            {
                Process[] ps = Process.GetProcessesByName(g);
                bool found = ps.Length > 0;
                foreach (Process p in ps) p.Dispose();
                if (found) return g;
            }
            return null;
        }

        bool InSchedule()
        {
            int now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            int s = (int)nSh.Value * 60 + (int)nSm.Value;
            int e = (int)nEh.Value * 60 + (int)nEm.Value;
            if (s == e) return false;
            return s < e ? (now >= s && now < e) : (now >= s || now < e);   // 支持跨午夜，比如 23:00 到 7:00
        }

        void UpdateSmartState()
        {
            if (loadHist.Count == 0) return;
            double avg10 = 0; int n10 = Math.Min(5, loadHist.Count);
            for (int i = loadHist.Count - n10; i < loadHist.Count; i++) avg10 += loadHist[i];
            avg10 /= n10;
            double avg30 = 0;
            foreach (double v in loadHist) avg30 += v;
            avg30 /= loadHist.Count;

            if (!smartHigh && (avg10 >= 50 || cpuTemp >= 88)) smartHigh = true;            // 10 秒平均负载 ≥50% 或温度 ≥88°C
            else if (smartHigh && avg30 < 20 && cpuTemp < 75 && loadHist.Count >= 15) smartHigh = false;  // 30 秒平均 <20% 且 <75°C
        }

        string autoReason = "";

        void Automation()
        {
            UpdateSmartState();
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;

            string key = "";
            int desired = -1;
            string game = RunningGame();
            if (onBattery && chkBattery.Checked)
            {
                key = ""; autoReason = L.T("用电池中（由「拔电切静谧」接管）", "On battery (handled by battery rule)");
            }
            else if (game != null)
            {
                key = "game:" + game; desired = Clamp(Settings.GetInt("GameMode", 3), 0, 3);
                autoReason = L.T("游戏 ", "Game ") + game;
            }
            else if (chkSched.Checked && InSchedule())
            {
                key = "sched"; desired = cmbSched.SelectedIndex;
                autoReason = L.T("定时规则", "Schedule");
            }
            else if (chkSmart.Checked)
            {
                key = smartHigh ? "smart-hi" : "smart-lo";
                desired = smartHigh ? cmbHigh.SelectedIndex : cmbLow.SelectedIndex;
                autoReason = smartHigh ? L.T("智能：高负载", "Smart: heavy load") : L.T("智能：平时", "Smart: normal");
            }
            else autoReason = "";

            if (key != lastAutoKey)
            {
                if (lastAutoKey == "" && key != "") baseMode = currentMode;
                string oldKey = lastAutoKey;
                lastAutoKey = key;
                manualPaused = false;
                autoFailed = false;
                if (key == "")
                {
                    // 自动规则都结束了：恢复接管之前的模式（用电池的情况交给电源事件处理）
                    if (!(onBattery && chkBattery.Checked) && oldKey != "" && baseMode >= 0 && baseMode != currentMode)
                        SetMode(baseMode, false);
                    baseMode = -1;
                }
            }

            if (key != "" && !manualPaused && !autoFailed && desired >= 0 && desired != currentMode)
            {
                if (SetMode(desired, false))
                    tray.ShowBalloonTip(1500, "Redmi Helper", autoReason + " → " + ModeNames[desired], ToolTipIcon.None);
                else autoFailed = true;   // 别每 2 秒重试一次
            }
            UpdateAutoLabel();
        }

        void UpdateAutoLabel()
        {
            if (lblAuto == null) return;
            if (autoReason == "") lblAuto.Text = (chkSmart.Checked || chkSched.Checked || GameList().Length > 0) ? L.T("待命中", "Standing by") : L.T("未启用", "Off");
            else if (manualPaused) lblAuto.Text = autoReason + L.T("（已手动暂停）", " (paused manually)");
            else lblAuto.Text = autoReason;
        }

        void OpenGames()
        {
            using (GamesForm f = new GamesForm(GameList(), Settings.GetInt("GameMode", 3), ModeNames, sc))
            {
                Theme.Apply(f);
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    Settings.SetStr("Games", string.Join(";", f.Games));
                    Settings.SetInt("GameMode", f.Mode);
                    UpdateGamesLabel();
                    lblStatus.Text = L.T("游戏列表已保存", "Game list saved");
                }
            }
        }

        void UpdateGamesLabel()
        {
            int n = GameList().Length;
            lblGames.Text = n == 0 ? L.T("还没有添加游戏", "No games added") : L.T("已添加 ", "") + n + L.T(" 个，运行时切到「", " game(s) → \"") + ModeNames[Clamp(Settings.GetInt("GameMode", 3), 0, 3)] + L.T("」", "\"");
        }

        // ================= 插拔电源 =================
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.StatusChange) return;
            try { BeginInvoke(new MethodInvoker(HandlePowerChange)); } catch { }
        }

        void HandlePowerChange()
        {
            if (chkHz.Checked) ApplyRefreshRate(true);
            if (chkTurboAuto.Checked) ApplyTurboAuto();
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            if (onBattery)
            {
                if (chkBattery.Checked && currentMode >= 0 && currentMode != 0)
                {
                    Settings.SetInt("SavedAcMode", currentMode);
                    SetMode(0, false);
                }
                if (bootGpuMode == 1)
                    tray.ShowBalloonTip(6000, "Redmi Helper", L.T("现在是独显直连，用电池会很费电。可以切到「混合模式」后重启。", "dGPU-only mode drains the battery fast. Switch to Hybrid and restart."), ToolTipIcon.Info);
            }
            else if (chkBattery.Checked)
            {
                int saved = Settings.GetInt("SavedAcMode", -1);
                Settings.SetInt("SavedAcMode", -1);
                if (saved >= 0 && saved < 4 && saved != currentMode)
                {
                    Thread.Sleep(1500);   // 等适配器稳定再切高性能模式
                    SetMode(saved, false);
                }
            }
            lastAutoKey = "";   // 让自动规则在插拔电后重新评估
            Automation();
        }

        void ApplyRefreshRate(bool showStatus)
        {
            try
            {
                bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
                int max, low;
                Display.Rates(out max, out low);
                if (max <= 0) return;
                int target = onBattery ? low : max;
                bool ok = Display.Set(target);
                if (showStatus || !ok)
                    lblStatus.Text = ok ? L.T("屏幕刷新率已设为 ", "Refresh rate set to ") + target + "Hz" : L.T("刷新率切换失败", "Refresh rate change failed");
            }
            catch (Exception ex) { lblStatus.Text = L.T("刷新率切换失败：", "Refresh rate change failed: ") + ex.Message; }
        }

        // ================= 键盘特殊键（HID_EVENT20） =================
        void StartKeyWatcher()
        {
            try
            {
                keyWatcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"),
                    new WqlEventQuery("SELECT * FROM HID_EVENT20"));
                keyWatcher.EventArrived += (s, e) =>
                {
                    byte[] d = null;
                    try { d = (byte[])e.NewEvent["EventDetail"]; } catch { }
                    if (d == null || d.Length < 3) return;
                    try { BeginInvoke(new MethodInvoker(delegate { OnKeyEvent(d); })); } catch { }
                };
                keyWatcher.Start();
            }
            catch (Exception ex) { lblStatus.Text = L.T("无法监听特殊键：", "Cannot listen for special keys: ") + ex.Message; }
        }

        void OnKeyEvent(byte[] d)
        {
            int id = (d[0] << 8) | d[1];
            if (learningKey)
            {
                learningKey = false;
                Settings.SetInt("ControlKey", id);
                UpdateCtrlKeyLabel();
                lblStatus.Text = L.T("已设置！以后按这个键会打开 Redmi Helper", "Done! This key now opens Redmi Helper");
                return;
            }
            if (id == Settings.GetInt("ControlKey", -1)) { ToggleWindow(); return; }
            RefreshData();   // 其他特殊键按下后立刻刷新界面
        }

        void UpdateCtrlKeyLabel()
        {
            int id = Settings.GetInt("ControlKey", -1);
            lblCtrlKey.Text = id < 0 ? L.T("未设置", "Not set") : L.T("已设置（事件 ", "Set (event ") + (id >> 8) + "-" + (id & 0xFF) + L.T("）", ")");
        }

        // ================= 快捷键 =================
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == 1)
            {
                int next = currentMode < 0 ? 1 : (currentMode + 1) % 4;
                bool ok = SetMode(next, true);
                if (!ok && next >= 2) { next = 0; ok = SetMode(0, true); }
                if (ok) tray.ShowBalloonTip(1500, "Redmi Helper", L.T("性能模式：", "Mode: ") + ModeNames[next], ToolTipIcon.None);
            }
            base.WndProc(ref m);
        }

        // ================= 托盘 =================
        void UpdateTray()
        {
            Color c = currentMode >= 0 ? ModeColors[currentMode] : Color.Gray;
            string num = Math.Min(cpuTemp, 99).ToString();
            string key = currentMode + "|" + num;
            if (key != lastIconKey)
            {
                lastIconKey = key;
                Bitmap bmp = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(bmp))
                using (Font f = new Font("Arial", 19F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                {
                    g.Clear(c);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(num, f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                bmp.Dispose();
                tray.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
            string modeName = currentMode >= 0 ? ModeNames[currentMode] : L.T("未知", "Unknown");
            tray.Text = "Redmi Helper - " + modeName + " | CPU " + cpuTemp + "°C";
        }

        void UpdatePopup()
        {
            string info = (gpuMode >= 0 ? GpuNames[gpuMode] : "--") + "  ·  " +
                          (powerMode >= 0 ? PowerPlan.Names[powerMode] : "--") + "  ·  " + lblHz.Text.Replace(L.T("屏幕刷新率 ", "Display "), "");
            popup.UpdateView("CPU " + cpuTemp + "°C   GPU " + gpuTemp + "°C   " + fanA + " RPM", info, currentMode);
        }

        // ================= 窗口 =================
        void OnAutoChanged(object sender, EventArgs e)
        {
            if (chkAuto.Checked)
            {
                bool ok = AutoStart.Enable();
                lblStatus.Text = ok ? L.T("已设置开机自启", "Start with Windows enabled") : L.T("设置开机自启失败", "Could not enable start with Windows");
                if (!ok) { chkAuto.CheckedChanged -= OnAutoChanged; chkAuto.Checked = false; chkAuto.CheckedChanged += OnAutoChanged; }
            }
            else
            {
                AutoStart.Disable();
                lblStatus.Text = L.T("已取消开机自启", "Start with Windows disabled");
            }
        }

        public void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (page == 1) PollNvidia();
        }

        void ToggleWindow()
        {
            if (Visible) Hide(); else ShowMain();
        }

        void ExitApp()
        {
            exiting = true;
            timer.Stop();
            nvTimer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
            try { if (keyWatcher != null) keyWatcher.Stop(); } catch { }
            UnregisterHotKey(Handle, 1);
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        // 点 X 只是缩到托盘，真正退出用托盘菜单里的「退出」
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
