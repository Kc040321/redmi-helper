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
        public const string Version = "1.4";

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
        Font fBig, fHead, fSmall;
        ToolTip tips = new ToolTip();
        Label lblCpu, lblGpu, lblFanA, lblFanB, lblBat, lblStatus;
        Label lblGpuNote, lblAuto, lblHz, lblGames, lblCtrlKey, lblNv, lblNvApps, lblHealth;
        Button[] tabButtons = new Button[4];
        Panel[] pages = new Panel[4];
        int page = 0;
        Button[] modeButtons = new Button[4];
        Button[] gpuButtons = new Button[2];
        Panel planRow;
        List<Button> planButtons = new List<Button>();
        List<PlanInfo> plans = new List<PlanInfo>();
        string planKey = "";
        Guid activePlan = Guid.Empty;
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
            fBig = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold);
            fHead = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            fSmall = new Font("Microsoft YaHei UI", 8.5F);
            Icon = Draw.AppIcon();
            Text = "Redmi Helper";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = Z(360, 652);

            // 顶部卡片：温度 / 风扇 / 电池（所有页面都显示）
            Card head = AddCard(this, 12, 12, 336, 104);
            AddLabel(head, "CPU", 16, 10, 80, 18, fSmall, "gray");
            lblCpu = AddLabel(head, "--°C", 12, 26, 100, 46, fBig, null);
            AddLabel(head, "GPU", 114, 10, 80, 18, fSmall, "gray");
            lblGpu = AddLabel(head, "--°C", 110, 26, 100, 46, fBig, null);
            AddLabel(head, L.T("风扇", "Fans"), 216, 10, 100, 18, fSmall, "gray");
            lblFanA = AddLabel(head, "A  -- RPM", 216, 32, 108, 20, null, null);
            lblFanB = AddLabel(head, "B  -- RPM", 216, 54, 108, 20, null, null);
            lblBat = AddLabel(head, L.T("电池 --", "Battery --"), 16, 78, 308, 20, fSmall, "gray");

            // 分页：分段式按钮
            Card tabs = AddCard(this, 12, 124, 336, 40);
            for (int i = 0; i < 4; i++)
            {
                Button b = AddButton(tabs, TabNames[i], 4 + i * 82, 4, 80, 32, i);
                b.Click += OnTab;
                tabButtons[i] = b;
                Panel p = new Panel();
                p.Location = P(0, 172);
                p.Size = Z(360, 440);
                p.Visible = i == 0;
                Controls.Add(p);
                pages[i] = p;
            }

            BuildHome(pages[0]);
            BuildMonitor(pages[1]);
            BuildAuto(pages[2]);
            BuildSettings(pages[3]);

            lblStatus = AddLabel(this, "", 18, 616, 324, 34, fSmall, "gray");
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
            l.BackColor = Color.Transparent;
            if (f != null) l.Font = f;
            parent.Controls.Add(l);
            return l;
        }

        Label Head(Control card, string text)
        {
            return AddLabel(card, text, 14, 10, 200, 22, fHead, null);
        }

        Label Hint(Control card, string text, int x, int y, int w)
        {
            Label l = AddLabel(card, text, x, y, w, 20, fSmall, "gray");
            l.TextAlign = ContentAlignment.MiddleRight;
            return l;
        }

        Card AddCard(Control parent, int x, int y, int w, int h)
        {
            Card c = new Card();
            c.Location = P(x, y);
            c.Size = Z(w, h);
            parent.Controls.Add(c);
            return c;
        }

        Button AddButton(Control parent, string text, int x, int y, int w, int h, int tag)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Location = P(x, y);
            b.Size = Z(w, h);
            b.Tag = tag;
            parent.Controls.Add(b);
            return b;
        }

        CheckBox AddCheck(Control parent, string text, int x, int y)
        {
            return AddCheck(parent, text, x, y, 322 - x);
        }

        CheckBox AddCheck(Control parent, string text, int x, int y, int w)
        {
            ToggleSwitch c = new ToggleSwitch();
            c.Text = text;
            c.Location = P(x, y);
            c.Size = Z(w, 26);
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
            n.Width = (int)(42 * sc);
            n.BorderStyle = BorderStyle.FixedSingle;
            parent.Controls.Add(n);
            return n;
        }

        void BuildHome(Panel p)
        {
            Card c1 = AddCard(p, 12, 0, 336, 88);
            Head(c1, L.T("性能模式", "Performance mode"));
            Hint(c1, "Ctrl+Alt+M", 200, 11, 122);
            for (int i = 0; i < 4; i++)
            {
                Button b = AddButton(c1, ModeNames[i], 14 + i * 78, 38, 74, 38, i);
                b.Click += delegate (object s, EventArgs e) { SetMode((int)((Button)s).Tag, true); };
                modeButtons[i] = b;
            }

            Card c2 = AddCard(p, 12, 96, 336, 104);
            Head(c2, L.T("显卡模式", "GPU mode"));
            for (int i = 0; i < 2; i++)
            {
                Button b = AddButton(c2, GpuNames[i], 14 + i * 157, 38, 151, 38, i);
                b.Click += OnGpuButton;
                gpuButtons[i] = b;
            }
            lblGpuNote = AddLabel(c2, "", 14, 80, 308, 20, fSmall, "red");

            Card c3 = AddCard(p, 12, 208, 336, 88);
            Head(c3, L.T("电源计划", "Power plan"));
            planRow = new Panel();
            planRow.Location = P(14, 38);
            planRow.Size = Z(308, 38);
            c3.Controls.Add(planRow);

            Card c4 = AddCard(p, 12, 304, 336, 128);
            Head(c4, L.T("快捷开关", "Quick toggles"));
            chkFn = AddCheck(c4, L.T("Fn 锁", "Fn lock"), 14, 36, 150);
            chkTouchpad = AddCheck(c4, L.T("锁触控板", "Lock touchpad"), 172, 36, 150);
            chkWin = AddCheck(c4, L.T("锁 Win 键", "Lock Win key"), 14, 64, 150);
            chkTurboOff = AddCheck(c4, L.T("关闭睿频", "Turbo off"), 172, 64, 150);
            tips.SetToolTip(chkTurboOff, L.T("更凉、更安静、更省电，峰值性能下降", "Cooler, quieter, longer battery life; lower peak speed"));
            chkFn.CheckedChanged += delegate { if (!updatingUI) SetKey(Mi.KEY_FN, chkFn.Checked, L.T("Fn 锁", "Fn lock")); };
            chkTouchpad.CheckedChanged += delegate { if (!updatingUI) SetKey(Mi.KEY_TOUCHPAD, chkTouchpad.Checked, L.T("触控板锁", "Touchpad lock")); };
            chkWin.CheckedChanged += delegate { if (!updatingUI) SetKey(Mi.KEY_WIN, chkWin.Checked, L.T("Win 键锁", "Win key lock")); };
            chkTurboOff.CheckedChanged += delegate { if (!updatingUI) SetTurbo(!chkTurboOff.Checked, true); };

            lblAuto = AddLabel(c4, "", 14, 100, 196, 20, fSmall, "gray");
            lblHz = Hint(c4, "", 204, 100, 118);
        }

        void BuildMonitor(Panel p)
        {
            Card g1 = AddCard(p, 12, 0, 336, 236);
            Head(g1, L.T("温度与风扇", "Temps & fans"));
            Button exp = AddButton(g1, L.T("导出…", "Export..."), 238, 8, 84, 26, -1);
            exp.Click += delegate { ExportCsv(); };
            graph = new Graph();
            graph.Location = P(8, 38);
            graph.Size = Z(320, 160);
            graph.Data = history;
            g1.Controls.Add(graph);
            chkCsv = AddCheck(g1, L.T("持续记录到 CSV（logs 文件夹）", "Log to CSV (logs folder)"), 14, 202);

            Card g2 = AddCard(p, 12, 244, 336, 120);
            Head(g2, L.T("独显", "dGPU"));
            Hint(g2, L.T("仅在本页读取，避免唤醒独显", "Read only on this page"), 100, 11, 222);
            lblNv = AddLabel(g2, L.T("读取中……", "Loading..."), 14, 34, 308, 56, null, null);
            AddLabel(g2, L.T("使用中", "In use"), 14, 93, 56, 20, fSmall, "gray");
            lblNvApps = AddLabel(g2, "--", 70, 93, 252, 20, fSmall, null);

            Card g3 = AddCard(p, 12, 372, 336, 68);
            Head(g3, L.T("电池健康", "Battery health"));
            lblHealth = AddLabel(g3, "--", 14, 32, 308, 34, fSmall, null);
        }

        void BuildAuto(Panel p)
        {
            Card a1 = AddCard(p, 12, 0, 336, 100);
            Head(a1, L.T("智能模式", "Smart mode"));
            chkSmart = AddCheck(a1, L.T("根据负载和温度自动切换", "Switch by CPU load and temperature"), 14, 34);
            AddLabel(a1, L.T("平时", "Normal"), 14, 70, 44, 22, null, null);
            cmbLow = AddCombo(a1, ModeNames, 60, 66, 90);
            AddLabel(a1, L.T("高负载", "Heavy"), 168, 70, 56, 22, null, null);
            cmbHigh = AddCombo(a1, ModeNames, 228, 66, 94);

            Card a2 = AddCard(p, 12, 108, 336, 100);
            Head(a2, L.T("定时规则", "Schedule"));
            Hint(a2, L.T("切换到", "Use"), 150, 11, 74);
            cmbSched = AddCombo(a2, ModeNames, 228, 8, 94);
            chkSched = AddCheck(a2, L.T("每天按时间切换", "Switch at set times daily"), 14, 36);
            nSh = AddNum(a2, 14, 66, 23);
            AddLabel(a2, ":", 58, 69, 8, 22, null, null);
            nSm = AddNum(a2, 66, 66, 59);
            AddLabel(a2, L.T("到", "to"), 114, 69, 24, 22, null, null);
            nEh = AddNum(a2, 140, 66, 23);
            AddLabel(a2, ":", 184, 69, 8, 22, null, null);
            nEm = AddNum(a2, 192, 66, 59);

            Card a3 = AddCard(p, 12, 216, 336, 64);
            Head(a3, L.T("游戏", "Games"));
            lblGames = AddLabel(a3, "", 14, 34, 190, 22, fSmall, "gray");
            Button bg = AddButton(a3, L.T("游戏列表…", "Game list..."), 206, 14, 116, 36, -1);
            bg.Click += delegate { OpenGames(); };

            Card a4 = AddCard(p, 12, 288, 336, 152);
            Head(a4, L.T("电源", "Power"));
            chkBattery = AddCheck(a4, L.T("拔掉电源时切到静谧，插回恢复原模式", "On battery: Silent, restore when plugged in"), 14, 34);
            chkLink = AddCheck(a4, L.T("电源计划跟随性能模式", "Power plan follows performance mode"), 14, 62);
            chkHz = AddCheck(a4, L.T("插电用最高刷新率，用电池切到 60Hz", "Max refresh rate plugged in, 60Hz on battery"), 14, 90);
            chkTurboAuto = AddCheck(a4, L.T("静谧模式或用电池时关闭睿频，其他时候开启", "Turbo off in Silent mode or on battery"), 14, 118);
        }

        void BuildSettings(Panel p)
        {
            Card s1 = AddCard(p, 12, 0, 336, 80);
            Head(s1, L.T("外观", "Appearance"));
            AddLabel(s1, L.T("主题", "Theme"), 14, 44, 46, 22, null, null);
            cmbTheme = AddCombo(s1, new string[] { L.T("跟随系统", "System"), L.T("浅色", "Light"), L.T("深色", "Dark") }, 62, 40, 100);
            AddLabel(s1, L.T("语言", "Language"), 174, 44, 58, 22, null, null);
            cmbLang = AddCombo(s1, new string[] { L.T("跟随系统", "System"), "中文", "English" }, 232, 40, 90);

            Card s2 = AddCard(p, 12, 88, 336, 116);
            Head(s2, L.T("启动与按键", "Startup & keys"));
            chkAuto = AddCheck(s2, L.T("开机自动启动（后台托盘运行）", "Start with Windows (in tray)"), 14, 36);
            Button bl = AddButton(s2, L.T("设置控制中心键", "Set Control Center key"), 14, 70, 160, 32, -1);
            bl.Click += delegate { learningKey = true; lblStatus.Text = L.T("请按一下键盘上的「控制中心」键……", "Press the Control Center key on your keyboard..."); };
            lblCtrlKey = AddLabel(s2, "", 182, 76, 140, 22, fSmall, "gray");

            Card s3 = AddCard(p, 12, 212, 336, 60);
            AddLabel(s3, L.T("数据", "Data"), 14, 19, 100, 22, fHead, null);
            Button bo = AddButton(s3, L.T("打开日志文件夹", "Open logs folder"), 172, 12, 150, 36, -1);
            bo.Click += delegate
            {
                string dir = LogDir();
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            };

            Card s4 = AddCard(p, 12, 280, 336, 160);
            Head(s4, L.T("关于", "About"));
            AddLabel(s4, "Redmi Helper v" + Version + L.T("　适用于 Redmi G Pro 2022（Intel）\n", "  for Redmi G Pro 2022 (Intel)\n") +
                        L.T("通过 BIOS 的 WMI 接口控制硬件，命令来自实测和对官方软件的分析。\n", "Controls hardware through the BIOS WMI interface; commands come from testing and analysis of the official app.\n") +
                        L.T("左键托盘图标打开小窗，双击打开完整窗口。\n", "Click the tray icon for the mini panel, double-click for the full window.\n") +
                        L.T("自动切换优先级：游戏 > 定时 > 智能；手动切换后会暂停，直到情况变化。", "Automation priority: games > schedule > smart; a manual switch pauses it until something changes."),
                14, 34, 308, 120, fSmall, "gray");
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
            popup.BackColor = Theme.CardBack;   // 小窗整体用卡片色，看起来更像浮层
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
                tabButtons[i].BackColor = on ? Theme.Accent : Theme.CardBack;
                tabButtons[i].ForeColor = on ? Color.White : Theme.Gray;
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
                    lblCpu.Text = cpuTemp + "°C";
                    lblGpu.Text = gpuTemp + "°C";
                    lblFanA.Text = "A   " + fanA + " RPM";
                    lblFanB.Text = "B   " + fanB + " RPM";
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

                RefreshPlans();
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
                if (bootGpuMode == 1) b = L.T("独显直连：所有程序", "dGPU only: all apps");
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
        // 读取电脑上实际有哪些计划；计划列表变了就重建按钮
        void RefreshPlans()
        {
            List<PlanInfo> list = PowerPlan.List();
            bool hasSaver = false;
            string key = "";
            foreach (PlanInfo pl in list) { key += pl.Id + pl.Name + ";"; if (pl.Rank == 0) hasSaver = true; }
            activePlan = PowerPlan.Active();
            if (key != planKey)
            {
                planKey = key;
                plans = list;
                planRow.SuspendLayout();
                planRow.Controls.Clear();
                planButtons.Clear();
                int n = Math.Min(plans.Count, hasSaver ? 4 : 3);
                int slots = n + (hasSaver ? 0 : 1);
                int gap = 5;
                int w = (308 - gap * (slots - 1)) / Math.Max(1, slots);
                for (int i = 0; i < n; i++)
                {
                    Button b = AddButton(planRow, plans[i].Name, i * (w + gap), 0, w, 38, i);
                    b.Click += OnPlanButton;
                    planButtons.Add(b);
                }
                if (!hasSaver)
                {
                    Button add = AddButton(planRow, L.T("+ 节能", "+ Saver"), n * (w + gap), 0, w, 38, -1);
                    add.Click += delegate
                    {
                        lblStatus.Text = PowerPlan.CreateSaver()
                            ? L.T("已创建「节能」计划", "Power saver plan created")
                            : L.T("创建「节能」计划失败", "Could not create a power saver plan");
                        RefreshPlans();
                    };
                }
                planRow.ResumeLayout();
                Theme.Apply(planRow);
            }
            UpdatePowerUI();
        }

        void OnPlanButton(object sender, EventArgs e)
        {
            int i = (int)((Button)sender).Tag;
            if (i >= 0 && i < plans.Count) SetPlan(plans[i], true);
        }

        void SetPlan(PlanInfo plan, bool showStatus)
        {
            if (PowerPlan.Activate(plan.Id))
            {
                activePlan = plan.Id;
                if (showStatus) lblStatus.Text = L.T("电源计划：", "Power plan: ") + plan.Name;
                if (chkTurboAuto.Checked) ApplyTurboAuto();   // 每个计划的睿频设置是分开的
            }
            else lblStatus.Text = L.T("电源计划切换失败", "Power plan change failed");
            UpdatePowerUI();
        }

        // 静谧 -> 节能，均衡 -> 平衡，极速 -> 高性能，狂派 -> 卓越性能（没有就找最接近的）
        void ApplyLinkedPower()
        {
            PlanInfo target = PowerPlan.ForMode(plans, currentMode);
            if (target != null && target.Id != activePlan) SetPlan(target, false);
            UpdatePowerUI();
        }

        string ActivePlanName()
        {
            foreach (PlanInfo pl in plans) if (pl.Id == activePlan) return pl.Name;
            return "--";
        }

        void UpdatePowerUI()
        {
            int sel = -1;
            for (int i = 0; i < planButtons.Count; i++) if (plans[i].Id == activePlan) sel = i;
            Highlight(planButtons.ToArray(), sel, PowerColor);
        }

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
            string t;
            if (autoReason == "") t = (chkSmart.Checked || chkSched.Checked || GameList().Length > 0) ? L.T("待命中", "Standing by") : L.T("未启用", "Off");
            else if (manualPaused) t = autoReason + L.T("（已手动暂停）", " (paused manually)");
            else t = autoReason;
            lblAuto.Text = L.T("自动切换：", "Automation: ") + t;
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
                          ActivePlanName() + "  ·  " + lblHz.Text.Replace(L.T("屏幕刷新率 ", "Display "), "");
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
