// Redmi Helper - 托盘小窗、游戏列表窗口、程序入口
// 只用 C# 5 语法，以便用 Windows 自带的 csc.exe 编译

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace RedmiHelper
{
    // ===== G-Helper 风格的托盘小窗：左键托盘图标弹出，点别处自动收起 =====
    class TrayPopup : Form
    {
        MainForm main;
        float sc;
        Label lblTop, lblInfo;
        Button[] btns = new Button[4];
        int mode = -1;

        Point P(int x, int y) { return new Point((int)(x * sc), (int)(y * sc)); }
        Size Z(int w, int h) { return new Size((int)(w * sc), (int)(h * sc)); }

        public TrayPopup(MainForm m, float scale)
        {
            main = m;
            sc = scale;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = Z(300, 150);

            lblTop = new Label();
            lblTop.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            lblTop.Location = P(12, 10);
            lblTop.Size = Z(276, 26);
            Controls.Add(lblTop);

            for (int i = 0; i < 4; i++)
            {
                Button b = new Button();
                b.Text = MainForm.ModeNames[i];
                b.Tag = i;
                b.FlatStyle = FlatStyle.Flat;
                b.Location = P(12 + i * 70, 40);
                b.Size = Z(66, 36);
                b.Click += delegate (object s, EventArgs e) { main.SetMode((int)((Button)s).Tag, true); };
                Controls.Add(b);
                btns[i] = b;
            }

            lblInfo = new Label();
            lblInfo.Tag = "gray";
            lblInfo.Location = P(12, 84);
            lblInfo.Size = Z(276, 22);
            Controls.Add(lblInfo);

            Button open = new Button();
            open.Text = L.T("打开完整窗口", "Open full window");
            open.FlatStyle = FlatStyle.Flat;
            open.Location = P(12, 110);
            open.Size = Z(276, 30);
            open.Click += delegate { Hide(); main.ShowMain(); };
            Controls.Add(open);

            Deactivate += delegate { Hide(); main.PopupHiddenAt = DateTime.Now; };
        }

        public void ShowNear()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - (int)(12 * sc), wa.Bottom - Height - (int)(12 * sc));
            Show();
            Activate();
        }

        public void UpdateView(string top, string info, int currentMode)
        {
            lblTop.Text = top;
            lblInfo.Text = info;
            mode = currentMode;
            for (int i = 0; i < 4; i++)
            {
                bool on = i == mode;
                btns[i].BackColor = on ? MainForm.ModeColors[i] : Theme.Btn;
                btns[i].ForeColor = on ? Color.White : Theme.Fore;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ===== 游戏列表窗口 =====
    class GamesForm : Form
    {
        ListBox list;
        ComboBox combo;
        public string[] Games = new string[0];
        public int Mode;

        public GamesForm(string[] games, int mode, string[] modeNames, float sc)
        {
            Text = L.T("游戏列表", "Game list");
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size((int)(320 * sc), (int)(330 * sc));

            Label tip = new Label();
            tip.Text = L.T("下面这些程序运行时，自动切换到：", "When any of these run, switch to:");
            tip.Location = new Point((int)(12 * sc), (int)(14 * sc));
            tip.Size = new Size((int)(200 * sc), (int)(22 * sc));
            Controls.Add(tip);

            combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Items.AddRange(modeNames);
            combo.SelectedIndex = (mode >= 0 && mode < modeNames.Length) ? mode : 3;
            combo.Location = new Point((int)(214 * sc), (int)(10 * sc));
            combo.Width = (int)(94 * sc);
            Controls.Add(combo);

            list = new ListBox();
            list.Location = new Point((int)(12 * sc), (int)(44 * sc));
            list.Size = new Size((int)(296 * sc), (int)(220 * sc));
            foreach (string g in games) list.Items.Add(g);
            Controls.Add(list);

            Button add = new Button();
            add.Text = L.T("添加游戏…", "Add game...");
            add.FlatStyle = FlatStyle.Flat;
            add.Location = new Point((int)(12 * sc), (int)(276 * sc));
            add.Size = new Size((int)(96 * sc), (int)(32 * sc));
            add.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = L.T("程序 (*.exe)|*.exe", "Programs (*.exe)|*.exe");
                    d.Title = L.T("选择游戏的 exe 文件", "Choose the game .exe");
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        string n = System.IO.Path.GetFileNameWithoutExtension(d.FileName);
                        if (!list.Items.Contains(n)) list.Items.Add(n);
                    }
                }
            };
            Controls.Add(add);

            Button del = new Button();
            del.Text = L.T("删除", "Remove");
            del.FlatStyle = FlatStyle.Flat;
            del.Location = new Point((int)(114 * sc), (int)(276 * sc));
            del.Size = new Size((int)(70 * sc), (int)(32 * sc));
            del.Click += delegate { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };
            Controls.Add(del);

            Button ok = new Button();
            ok.Text = L.T("保存", "Save");
            ok.FlatStyle = FlatStyle.Flat;
            ok.Location = new Point((int)(228 * sc), (int)(276 * sc));
            ok.Size = new Size((int)(80 * sc), (int)(32 * sc));
            ok.Click += delegate
            {
                Games = new string[list.Items.Count];
                for (int i = 0; i < Games.Length; i++) Games[i] = (string)list.Items[i];
                Mode = combo.SelectedIndex;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            AcceptButton = ok;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex mx = new Mutex(true, "RedmiHelper_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show(L.T("Redmi Helper 已经在运行了，看看右下角托盘。", "Redmi Helper is already running (check the tray)."));
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Mi.Init(); }
                catch (Exception ex)
                {
                    MessageBox.Show(L.T("初始化失败：", "Initialization failed: ") + ex.Message + L.T("\n请确认是以管理员身份运行的，并且这台电脑是 Redmi G Pro 2022。", "\nMake sure it runs as administrator on a Redmi G Pro 2022."));
                    return;
                }
                bool hidden = args.Length > 0 && args[0] == "/tray";
                MainForm form = new MainForm();
                if (!hidden) form.Show();
                Application.Run();
                GC.KeepAlive(form);
            }
        }
    }
}
