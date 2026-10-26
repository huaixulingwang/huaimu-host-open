using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LingWangRemote
{
    /// <summary>
    /// OpenRemoteDesk 电脑端托盘 + 状态窗口。
    /// 只用 Windows 自带的 .NET Framework，不装任何运行库，不弹命令行黑框。
    /// 主窗口负责：显示服务/组网/连接状态、看日志、一键体检修复。
    /// </summary>
    static class TrayApp
    {
        const string AppName = "OpenRemoteDesk";
        const string UiVersion = "0.3.0";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "LingWangTray";
        const string WebUrl = "https://localhost:47990";
        const string DataDir = @"C:\ProgramData\LingWangRemote";
        const string VirtualIp = "10.126.126.1";
        const int StreamPort = 47984;      // Sunshine 串流端口
        const int WebPort = 47990;         // Sunshine 管理网页端口

        static NotifyIcon tray;
        static Timer uiTimer;
        static Form mainForm;

        // 托盘菜单项
        static ToolStripMenuItem statusItem;
        static ToolStripMenuItem connItem;
        static ToolStripMenuItem autoItem;
        static ToolStripMenuItem accountItem;
        static ToolStripMenuItem codeItem;

        // 主窗口控件
        static Label lblSunshine;
        static Label lblEasyTier;
        static Label lblWatchdog;
        static Label lblConnTitle;
        static Label lblConnDetail;
        static Label lblLocalInfo;
        static TextBox txtDiag;

        // 账号 / 控制码
        static Label lblAccount;
        static Label lblAccountDetail;
        static Button btnAccount;
        static Label lblCode;
        static Label lblCodeDetail;
        static Button btnCode;
        static Button btnCopyCode;

        static Icon iconOk;
        static Icon iconWarn;

        // ── 日志增量读取状态 ──────────────────────────────
        static long logPos = 0;
        static bool sessionActive = false;
        static DateTime sessionStart = DateTime.MinValue;
        static string clientUuid = "";
        static string streamRes = "";
        static string streamFps = "";
        static string streamBitrate = "";
        static string displayName = "";
        static int activeSessions = 0;
        static string lastDiag = "";

        static string ErrorLogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LingWangRemote", "tray-error.log");
            }
        }

        static string RepairLogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LingWangRemote", "last-repair.log");
            }
        }

        /// <summary>安装根目录（托盘在 {AppDir}\tray\ 下）</summary>
        static string AppDir
        {
            get
            {
                string trayDir = Path.GetDirectoryName(Application.ExecutablePath);
                string parent = Path.GetDirectoryName(trayDir);
                return string.IsNullOrEmpty(parent) ? trayDir : parent;
            }
        }

        static string SunshineLogPath
        {
            get
            {
                string p = Path.Combine(AppDir, "sunshine", "config", "sunshine.log");
                if (File.Exists(p)) return p;
                string alt = Path.Combine(DataDir, "logs", "sunshine.log");
                if (File.Exists(alt)) return alt;
                return p;
            }
        }

        static void Boom(Exception ex)
        {
            try
            {
                string dir = Path.GetDirectoryName(ErrorLogPath);
                Directory.CreateDirectory(dir);
                File.AppendAllText(ErrorLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // 体检修复模式：由主程序以管理员身份再次拉起，跑完弹结果
            if (args.Length > 0 && args[0] == "--repair")
            {
                string result = RunRepair();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RepairLogPath));
                    File.WriteAllText(RepairLogPath, result, Encoding.UTF8);
                }
                catch { }
                MessageBox.Show(result, AppName + " · 体检修复结果",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                bool createdNew;
                using (var mutex = new System.Threading.Mutex(true, "Local\\LingWangTrayMutex", out createdNew))
                {
                    if (!createdNew) return;

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    iconOk = MakeIcon(Color.FromArgb(38, 166, 124));
                    iconWarn = MakeIcon(Color.FromArgb(210, 90, 60));

                    // 恢复上次登录的账号；启动本地控制校验口（虚拟网卡可能还没起来，定时器会重试）
                    try { AccountClient.Restore(); } catch (Exception ex) { Boom(ex); }
                    AuthGate.EnsureStarted();

                    tray = new NotifyIcon();
                    tray.Icon = iconOk;
                    tray.Text = AppName;
                    tray.Visible = true;
                    tray.DoubleClick += delegate { ShowMainWindow(); };

                    BuildMenu();
                    BuildMainForm();

                    uiTimer = new Timer();
                    uiTimer.Interval = 2000;
                    uiTimer.Tick += delegate
                    {
                        try
                        {
                            AuthGate.EnsureStarted();
                            PollLog();
                            RefreshStatus();
                        }
                        catch (Exception ex) { Boom(ex); }
                    };
                    uiTimer.Start();

                    PollLog();
                    RefreshStatus();

                    Application.ApplicationExit += delegate
                    {
                        tray.Visible = false;
                        AuthGate.Stop();
                    };
                    Application.Run();
                }
            }
            catch (Exception ex)
            {
                Boom(ex);
            }
        }

        // ── 托盘菜单 ──────────────────────────────────────

        static void BuildMenu()
        {
            var menu = new ContextMenuStrip();

            statusItem = new ToolStripMenuItem("正在检查服务状态…");
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);

            connItem = new ToolStripMenuItem("当前没有设备连接");
            connItem.Enabled = false;
            menu.Items.Add(connItem);
            menu.Items.Add(new ToolStripSeparator());

            accountItem = new ToolStripMenuItem("登录统一账号…");
            accountItem.Click += delegate { ShowAccountAction(); };
            menu.Items.Add(accountItem);

            codeItem = new ToolStripMenuItem("生成临时控制码");
            codeItem.Click += delegate { GenerateAndCopyCode(); };
            menu.Items.Add(codeItem);

            menu.Items.Add(new ToolStripSeparator());

            var openItem = new ToolStripMenuItem("打开主界面…");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += delegate { ShowMainWindow(); };
            menu.Items.Add(openItem);

            var repairItem = new ToolStripMenuItem("一键体检修复…");
            repairItem.Click += delegate { StartRepair(); };
            menu.Items.Add(repairItem);

            var webItem = new ToolStripMenuItem("打开电脑管理网页…");
            webItem.Click += delegate { OpenWeb(); };
            menu.Items.Add(webItem);

            var logItem = new ToolStripMenuItem("打开日志文件夹…");
            logItem.Click += delegate { OpenFolder(Path.Combine(DataDir, "logs")); };
            menu.Items.Add(logItem);

            autoItem = new ToolStripMenuItem("开机自动启动");
            autoItem.CheckOnClick = true;
            autoItem.Checked = IsAutoStart();
            autoItem.CheckedChanged += delegate { SetAutoStart(autoItem.Checked); };
            menu.Items.Add(autoItem);

            menu.Items.Add(new ToolStripSeparator());

            var aboutItem = new ToolStripMenuItem("关于");
            aboutItem.Click += delegate
            {
                MessageBox.Show(
                    AppName + " 电脑端（界面 " + UiVersion + "）\n\n" +
                    "右下角小图标查看远控服务状态：绿色＝正常，红色＝有异常。\n" +
                    "双击图标打开主界面。\n" +
                    "关闭主窗口不会退出程序，服务仍在后台运行。",
                    "关于 " + AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            };
            menu.Items.Add(aboutItem);

            var exitItem = new ToolStripMenuItem("退出托盘（不影响远控服务）");
            exitItem.Click += delegate { Application.Exit(); };
            menu.Items.Add(exitItem);

            tray.ContextMenuStrip = menu;
        }

        // ── 主窗口 ────────────────────────────────────────

        static void BuildMainForm()
        {
            var f = new Form();
            f.Text = AppName + " · 电脑端";
            f.ClientSize = new Size(560, 860);
            f.AutoScroll = true;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.FixedSingle;
            f.MaximizeBox = false;
            f.BackColor = Color.FromArgb(245, 246, 248);
            f.Font = new Font("Microsoft YaHei UI", 9f);
            f.Icon = iconOk;
            f.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                // 点叉只隐藏，程序继续在托盘运行
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    f.Hide();
                }
            };

            var title = new Label();
            title.Text = AppName + " · 电脑端";
            title.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(32, 36, 40);
            title.AutoSize = true;
            title.Location = new Point(22, 16);
            f.Controls.Add(title);

            var subtitle = new Label();
            subtitle.Text = "这台电脑作为被控端运行，下面是它的实时状态";
            subtitle.ForeColor = Color.FromArgb(110, 116, 124);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(24, 52);
            f.Controls.Add(subtitle);

            // 账号卡
            Panel cardAccount = MakeCard(20, 80, 520, 96);
            cardAccount.Controls.Add(MakeCardTitle("账号", 16, 10));
            lblAccount = new Label();
            lblAccount.Text = AccountClient.LoggedIn ? AccountClient.DisplayName : "未登录";
            lblAccount.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            lblAccount.ForeColor = AccountClient.LoggedIn
                ? Color.FromArgb(32, 36, 40) : Color.FromArgb(90, 96, 104);
            lblAccount.AutoSize = true;
            lblAccount.Location = new Point(16, 36);
            cardAccount.Controls.Add(lblAccount);

            lblAccountDetail = new Label();
            lblAccountDetail.Text = "登录后，同一账号的平板可直接连接这台电脑";
            lblAccountDetail.ForeColor = Color.FromArgb(120, 126, 134);
            lblAccountDetail.AutoSize = true;
            lblAccountDetail.Location = new Point(16, 64);
            cardAccount.Controls.Add(lblAccountDetail);

            btnAccount = MakeCardButton("登录", 396, 22, 108);
            btnAccount.Click += delegate { ShowAccountAction(); };
            cardAccount.Controls.Add(btnAccount);
            f.Controls.Add(cardAccount);

            // 临时控制码卡
            Panel cardCode = MakeCard(20, 186, 520, 112);
            cardCode.Controls.Add(MakeCardTitle("临时控制码", 16, 10));

            lblCode = new Label();
            lblCode.Text = "— — — — — —";
            lblCode.Font = new Font("Consolas", 22f, FontStyle.Bold);
            lblCode.ForeColor = Color.FromArgb(38, 166, 124);
            lblCode.AutoSize = true;
            lblCode.Location = new Point(14, 32);
            cardCode.Controls.Add(lblCode);

            lblCodeDetail = new Label();
            lblCodeDetail.Text = "给非同一账号的人临时用：在平板上输入这串数字即可连接";
            lblCodeDetail.ForeColor = Color.FromArgb(120, 126, 134);
            lblCodeDetail.AutoSize = true;
            lblCodeDetail.Location = new Point(16, 80);
            cardCode.Controls.Add(lblCodeDetail);

            btnCode = MakeCardButton("生成", 288, 40, 96);
            btnCode.Click += delegate { GenerateCode(); };
            cardCode.Controls.Add(btnCode);

            btnCopyCode = MakeCardButton("复制", 396, 40, 108);
            btnCopyCode.Click += delegate { CopyCode(); };
            cardCode.Controls.Add(btnCopyCode);
            f.Controls.Add(cardCode);

            // 服务状态卡
            Panel card1 = MakeCard(20, 308, 520, 118);
            card1.Controls.Add(MakeCardTitle("运行状态", 16, 10));
            lblSunshine = MakeStatusRow(card1, 16, 38, "串流服务（Sunshine）");
            lblEasyTier = MakeStatusRow(card1, 16, 66, "虚拟网络（EasyTier）");
            lblWatchdog = MakeStatusRow(card1, 16, 94, "守护进程（值班员）");
            f.Controls.Add(card1);

            // 当前连接卡
            Panel card2 = MakeCard(20, 436, 520, 104);
            card2.Controls.Add(MakeCardTitle("当前连接", 16, 10));
            lblConnTitle = new Label();
            lblConnTitle.Text = "没有设备连接";
            lblConnTitle.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            lblConnTitle.ForeColor = Color.FromArgb(80, 86, 94);
            lblConnTitle.AutoSize = true;
            lblConnTitle.Location = new Point(16, 38);
            card2.Controls.Add(lblConnTitle);

            lblConnDetail = new Label();
            lblConnDetail.Text = "平板连上来后，这里会显示画面规格和连接时长";
            lblConnDetail.ForeColor = Color.FromArgb(120, 126, 134);
            lblConnDetail.AutoSize = true;
            lblConnDetail.Location = new Point(16, 70);
            card2.Controls.Add(lblConnDetail);
            f.Controls.Add(card2);

            // 本机信息卡
            Panel card3 = MakeCard(20, 550, 520, 92);
            card3.Controls.Add(MakeCardTitle("本机信息", 16, 10));
            lblLocalInfo = new Label();
            lblLocalInfo.ForeColor = Color.FromArgb(70, 76, 84);
            lblLocalInfo.AutoSize = true;
            lblLocalInfo.Location = new Point(16, 38);
            card3.Controls.Add(lblLocalInfo);

            var copyIp = MakeSmallButton("复制虚拟 IP", 300, 34);
            copyIp.Click += delegate
            {
                try
                {
                    Clipboard.SetText(VirtualIp);
                    SetDiag("已复制虚拟 IP：" + VirtualIp);
                }
                catch { }
            };
            card3.Controls.Add(copyIp);
            f.Controls.Add(card3);

            // 操作按钮
            var repair = MakeMainButton("一键体检修复", 20, 654, 200);
            repair.Click += delegate { StartRepair(); };
            f.Controls.Add(repair);

            var openWeb = MakeMainButton("打开管理网页", 232, 654, 148);
            openWeb.Click += delegate { OpenWeb(); };
            f.Controls.Add(openWeb);

            var openLog = MakeMainButton("打开日志文件夹", 392, 654, 148);
            openLog.Click += delegate { OpenFolder(Path.Combine(DataDir, "logs")); };
            f.Controls.Add(openLog);

            // 诊断输出
            txtDiag = new TextBox();
            txtDiag.Multiline = true;
            txtDiag.ReadOnly = true;
            txtDiag.ScrollBars = ScrollBars.Vertical;
            txtDiag.BackColor = Color.White;
            txtDiag.BorderStyle = BorderStyle.FixedSingle;
            txtDiag.Font = new Font("Microsoft YaHei UI", 9f);
            txtDiag.Location = new Point(20, 706);
            txtDiag.Size = new Size(520, 96);
            txtDiag.Text = "点「一键体检修复」可以检查并重启掉线的服务；修复需要管理员授权，会弹一次系统确认。";
            f.Controls.Add(txtDiag);

            var foot = new Label();
            foot.Text = "关闭本窗口不会退出，程序继续在右下角托盘运行";
            foot.ForeColor = Color.FromArgb(140, 146, 152);
            foot.AutoSize = true;
            foot.Location = new Point(22, 812);
            f.Controls.Add(foot);

            mainForm = f;
        }

        /// <summary>圆角白卡片：自绘抗锯齿边框，替代原来的方块 Panel</summary>
        class CardPanel : Panel
        {
            public CardPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);
                BackColor = Color.White;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Color bg = Parent == null ? Color.FromArgb(245, 246, 248) : Parent.BackColor;
                g.Clear(bg);

                var rect = new Rectangle(1, 1, Width - 3, Height - 3);
                using (var path = RoundRect(rect, 14))
                using (var brush = new SolidBrush(Color.White))
                using (var pen = new Pen(Color.FromArgb(228, 232, 236), 1))
                {
                    g.FillPath(brush, path);
                    g.DrawPath(pen, path);
                }
            }
        }

        static Panel MakeCard(int x, int y, int w, int h)
        {
            var p = new CardPanel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            return p;
        }

        static Label MakeCardTitle(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(120, 126, 134);
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        static Label MakeStatusRow(Panel parent, int x, int y, string name)
        {
            var txt = new Label();
            txt.Text = name;
            txt.AutoSize = true;
            txt.Font = new Font("Microsoft YaHei UI", 9f);
            txt.ForeColor = Color.FromArgb(70, 76, 84);
            txt.Location = new Point(x, y + 3);
            parent.Controls.Add(txt);

            // 右侧彩色药丸标签，替代原来的 ● 文字
            var pill = new Label();
            pill.Text = "检测中";
            pill.AutoSize = false;
            pill.Size = new Size(56, 22);
            pill.TextAlign = ContentAlignment.MiddleCenter;
            pill.Font = new Font("Microsoft YaHei UI", 9f);
            pill.ForeColor = Color.White;
            pill.BackColor = Color.FromArgb(160, 166, 172);
            pill.Location = new Point(parent.Width - 72, y);
            using (var path = RoundRect(new Rectangle(0, 0, 56, 22), 11))
            {
                pill.Region = new Region(path);
            }
            parent.Controls.Add(pill);

            txt.Tag = pill;
            return txt;
        }

        static Button MakeMainButton(string text, int x, int y, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 40);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(38, 166, 124);
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(24, 110, 82);
            b.Font = new Font("Microsoft YaHei UI", 10f);
            b.Cursor = Cursors.Hand;
            using (var path = RoundRect(new Rectangle(0, 0, w, 40), 10))
            {
                b.Region = new Region(path);
            }
            return b;
        }

        static Button MakeSmallButton(string text, int x, int y)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(104, 28);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(70, 76, 84);
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.Cursor = Cursors.Hand;
            using (var path = RoundRect(new Rectangle(0, 0, 104, 28), 8))
            {
                b.Region = new Region(path);
            }
            return b;
        }

        /// <summary>卡片右侧的小按钮</summary>
        static Button MakeCardButton(string text, int x, int y, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 34);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(38, 166, 124);
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(24, 110, 82);
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.Cursor = Cursors.Hand;
            using (var path = RoundRect(new Rectangle(0, 0, w, 34), 9))
            {
                b.Region = new Region(path);
            }
            return b;
        }

        static void ShowMainWindow()
        {
            if (mainForm == null) return;
            RefreshStatus();
            if (!mainForm.Visible)
            {
                mainForm.Show();
            }
            if (mainForm.WindowState == FormWindowState.Minimized)
            {
                mainForm.WindowState = FormWindowState.Normal;
            }
            mainForm.Activate();
            mainForm.TopMost = true;
            mainForm.TopMost = false;
        }

        // ── 状态刷新 ──────────────────────────────────────

        static void RefreshStatus()
        {
            bool sunshine = ServiceRunning("SunshineService");
            bool easytier = ServiceRunning("EasyTierService");
            bool watchdog = WatchdogAlive();
            bool allOk = sunshine && easytier && watchdog;

            string text = "串流服务:" + (sunshine ? "正常" : "异常")
                        + "  虚拟网络:" + (easytier ? "正常" : "异常");
            statusItem.Text = text;
            tray.Text = AppName + "  " + text;
            tray.Icon = allOk ? iconOk : iconWarn;

            if (sessionActive)
            {
                connItem.Text = "当前有设备连接 · " + ElapsedText();
            }
            else
            {
                connItem.Text = "当前没有设备连接";
            }

            if (lblSunshine != null && mainForm != null && mainForm.Visible)
            {
                SetRow(lblSunshine, sunshine, "串流服务（Sunshine）");
                SetRow(lblEasyTier, easytier, "虚拟网络（EasyTier）");
                SetRow(lblWatchdog, watchdog, "守护进程（值班员）");

                if (sessionActive)
                {
                    lblConnTitle.Text = "有设备正在连接 · 已用 " + ElapsedText();
                    lblConnTitle.ForeColor = Color.FromArgb(24, 110, 82);
                    var parts = new System.Collections.Generic.List<string>();
                    if (streamRes.Length > 0) parts.Add(streamRes);
                    if (streamFps.Length > 0) parts.Add(streamFps);
                    if (streamBitrate.Length > 0) parts.Add(streamBitrate);
                    string line1 = parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : "正在协商画面参数…";
                    string line2 = "";
                    if (displayName.Length > 0) line2 = "输出显示器：" + displayName;
                    if (clientUuid.Length > 0)
                    {
                        if (line2.Length > 0) line2 += "    ";
                        line2 += "设备标识：" + ShortUuid(clientUuid);
                    }
                    lblConnDetail.Text = line2.Length > 0 ? line1 + "\n" + line2 : line1;
                }
                else
                {
                    lblConnTitle.Text = "没有设备连接";
                    lblConnTitle.ForeColor = Color.FromArgb(80, 86, 94);
                    lblConnDetail.Text = "平板连上来后，这里会显示画面规格和连接时长";
                }

                lblLocalInfo.Text = "电脑名称：" + Environment.MachineName
                                  + "\n虚拟 IP：" + VirtualIp
                                  + "    串流端口：" + StreamPort;
            }

            RefreshAccountUi();
        }

        // ── 账号与控制码 ──────────────────────────────────

        static void RefreshAccountUi()
        {
            bool loggedIn = AccountClient.LoggedIn;
            string name = AccountClient.DisplayName;
            if (string.IsNullOrEmpty(name)) name = "已登录";
            string code = AuthGate.CurrentCode;

            accountItem.Text = loggedIn ? ("账号：" + name + "（点击可退出）") : "登录统一账号…";
            codeItem.Text = code.Length > 0
                ? ("控制码：" + code + "（点击重新生成）")
                : "生成临时控制码";

            if (lblAccount == null) return;

            lblAccount.Text = loggedIn ? name : "未登录";
            lblAccount.ForeColor = loggedIn ? Color.FromArgb(32, 36, 40) : Color.FromArgb(90, 96, 104);
            lblAccountDetail.Text = loggedIn
                ? "同一账号的平板可直接连接；其他账号需要临时控制码"
                : "登录后，同一账号的平板可直接连接这台电脑";
            btnAccount.Text = loggedIn ? "退出登录" : "登录";

            lblCode.Text = code.Length > 0 ? FormatCode(code) : "— — — — — —";
            if (code.Length > 0)
            {
                int mins = (int)Math.Ceiling((AuthGate.CodeExpire - DateTime.Now).TotalMinutes);
                if (mins < 0) mins = 0;
                lblCodeDetail.Text = "仅限这台电脑，约剩 " + mins + " 分钟；给别人临时连时使用";
            }
            else
            {
                lblCodeDetail.Text = "给非同一账号的人临时用：在平板上输入这串数字即可连接";
            }
            btnCopyCode.Enabled = code.Length > 0;
        }

        static string FormatCode(string code)
        {
            if (code.Length != 6) return code;
            return code.Substring(0, 3) + " " + code.Substring(3);
        }

        static void ShowAccountAction()
        {
            if (AccountClient.LoggedIn)
            {
                string name = AccountClient.DisplayName;
                if (string.IsNullOrEmpty(name)) name = "当前账号";
                var r = MessageBox.Show(
                    "当前登录：" + name + "\n\n退出后，同一账号直接连接将不可用，只能靠临时控制码连接。\n确定退出登录吗？",
                    AppName + " · 账号",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question);
                if (r == DialogResult.OK)
                {
                    AccountClient.Logout();
                    RefreshAccountUi();
                    SetDiag("已退出统一账号。现在只能用临时控制码让其他设备连接。");
                }
                return;
            }
            ShowLoginDialog();
        }

        static void GenerateCode()
        {
            string code = AuthGate.GenerateCode(10);
            try { Clipboard.SetText(code); } catch { }
            SetDiag("已生成临时控制码 " + FormatCode(code) + "，10 分钟内有效，已复制到剪贴板。\n"
                  + "非同一账号的人在平板上输入这串数字即可连接；到期后自动失效。");
            RefreshAccountUi();
        }

        static void GenerateAndCopyCode()
        {
            GenerateCode();
            ShowMainWindow();
        }

        static void CopyCode()
        {
            string code = AuthGate.CurrentCode;
            if (code.Length == 0)
            {
                SetDiag("当前没有有效控制码，点「生成」创建一个。");
                return;
            }
            try
            {
                Clipboard.SetText(code);
                SetDiag("控制码 " + FormatCode(code) + " 已复制到剪贴板。");
            }
            catch { }
        }

        static void ShowLoginDialog()
        {
            var dlg = new Form();
            dlg.Text = "登录统一账号";
            dlg.ClientSize = new Size(400, 520);
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.StartPosition = FormStartPosition.CenterScreen;
            dlg.BackColor = Color.White;
            dlg.Font = new Font("Microsoft YaHei UI", 9f);

            string mode = "sms";
            string verificationId = "";
            bool isUser = true;
            int countdown = 0;

            var title = new Label();
            title.Text = "登录统一账号";
            title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(32, 36, 40);
            title.AutoSize = true;
            title.Location = new Point(22, 16);
            dlg.Controls.Add(title);

            var tip = new Label();
            tip.Text = "登录后，同一账号的平板可以直接连接这台电脑";
            tip.ForeColor = Color.FromArgb(110, 116, 124);
            tip.AutoSize = true;
            tip.Location = new Point(24, 50);
            dlg.Controls.Add(tip);

            var tabSms = MakeTabButton("验证码登录", 24, 82);
            var tabPwd = MakeTabButton("密码登录", 122, 82);
            dlg.Controls.Add(tabSms);
            dlg.Controls.Add(tabPwd);

            var lblPhone = new Label();
            lblPhone.Text = "手机号";
            lblPhone.ForeColor = Color.FromArgb(70, 76, 84);
            lblPhone.AutoSize = true;
            lblPhone.Location = new Point(24, 128);
            dlg.Controls.Add(lblPhone);

            var prefix = new Label();
            prefix.Text = "+86";
            prefix.Font = new Font("Microsoft YaHei UI", 10f);
            prefix.ForeColor = Color.FromArgb(32, 36, 40);
            prefix.AutoSize = false;
            prefix.Size = new Size(46, 30);
            prefix.TextAlign = ContentAlignment.MiddleCenter;
            prefix.BackColor = Color.FromArgb(245, 246, 248);
            prefix.Location = new Point(24, 150);
            dlg.Controls.Add(prefix);

            var txtPhone = new TextBox();
            txtPhone.Location = new Point(70, 150);
            txtPhone.Size = new Size(306, 30);
            txtPhone.Font = new Font("Microsoft YaHei UI", 10f);
            txtPhone.MaxLength = 11;
            txtPhone.KeyPress += delegate(object s, KeyPressEventArgs e)
            {
                if (!char.IsDigit(e.KeyChar) && e.KeyChar != (char)8) e.Handled = true;
            };
            txtPhone.TextChanged += delegate
            {
                string cleaned = "";
                foreach (char c in txtPhone.Text)
                {
                    if (c >= '0' && c <= '9') cleaned += c;
                }
                if (cleaned.Length > 11) cleaned = cleaned.Substring(0, 11);
                if (cleaned != txtPhone.Text)
                {
                    int pos = txtPhone.SelectionStart;
                    txtPhone.Text = cleaned;
                    txtPhone.SelectionStart = Math.Min(pos, cleaned.Length);
                }
            };
            dlg.Controls.Add(txtPhone);

            var hintPhone = new Label();
            hintPhone.Text = "请输入 11 位手机号，系统会自动加 +86";
            hintPhone.Font = new Font("Microsoft YaHei UI", 9f);
            hintPhone.ForeColor = Color.FromArgb(102, 112, 133);
            hintPhone.AutoSize = true;
            hintPhone.Location = new Point(24, 188);
            dlg.Controls.Add(hintPhone);

            // 验证码登录区
            var lblCode = new Label();
            lblCode.Text = "验证码";
            lblCode.ForeColor = Color.FromArgb(70, 76, 84);
            lblCode.AutoSize = true;
            lblCode.Location = new Point(24, 216);
            dlg.Controls.Add(lblCode);

            var txtCode = new TextBox();
            txtCode.Location = new Point(24, 238);
            txtCode.Size = new Size(216, 30);
            txtCode.Font = new Font("Microsoft YaHei UI", 10f);
            txtCode.MaxLength = 6;
            txtCode.KeyPress += delegate(object s, KeyPressEventArgs e)
            {
                if (!char.IsDigit(e.KeyChar) && e.KeyChar != (char)8) e.Handled = true;
            };
            txtCode.TextChanged += delegate
            {
                string cleaned = "";
                foreach (char c in txtCode.Text)
                {
                    if (c >= '0' && c <= '9') cleaned += c;
                }
                if (cleaned.Length > 6) cleaned = cleaned.Substring(0, 6);
                if (cleaned != txtCode.Text)
                {
                    int pos = txtCode.SelectionStart;
                    txtCode.Text = cleaned;
                    txtCode.SelectionStart = Math.Min(pos, cleaned.Length);
                }
            };
            dlg.Controls.Add(txtCode);

            var btnSend = MakeFlatSmall("获取验证码", 248, 238, 128);
            dlg.Controls.Add(btnSend);

            var hintCode = new Label();
            hintCode.Text = "6 位数字验证码，60 秒内有效";
            hintCode.Font = new Font("Microsoft YaHei UI", 9f);
            hintCode.ForeColor = Color.FromArgb(102, 112, 133);
            hintCode.AutoSize = true;
            hintCode.Location = new Point(24, 276);
            dlg.Controls.Add(hintCode);

            var lblNick = new Label();
            lblNick.Text = "昵称（新账号）";
            lblNick.ForeColor = Color.FromArgb(70, 76, 84);
            lblNick.AutoSize = true;
            lblNick.Location = new Point(24, 300);
            lblNick.Visible = false;
            dlg.Controls.Add(lblNick);

            var txtNick = new TextBox();
            txtNick.Location = new Point(24, 322);
            txtNick.Size = new Size(352, 30);
            txtNick.Font = new Font("Microsoft YaHei UI", 10f);
            txtNick.MaxLength = 64;
            txtNick.Visible = false;
            dlg.Controls.Add(txtNick);

            var hintNick = new Label();
            hintNick.Text = "2–64 个字符，可稍后修改";
            hintNick.Font = new Font("Microsoft YaHei UI", 9f);
            hintNick.ForeColor = Color.FromArgb(102, 112, 133);
            hintNick.AutoSize = true;
            hintNick.Location = new Point(24, 360);
            hintNick.Visible = false;
            dlg.Controls.Add(hintNick);

            // 密码登录区
            var lblPwd = new Label();
            lblPwd.Text = "密码";
            lblPwd.ForeColor = Color.FromArgb(70, 76, 84);
            lblPwd.AutoSize = true;
            lblPwd.Location = new Point(24, 216);
            lblPwd.Visible = false;
            dlg.Controls.Add(lblPwd);

            var txtPwd = new TextBox();
            txtPwd.Location = new Point(24, 238);
            txtPwd.Size = new Size(266, 30);
            txtPwd.Font = new Font("Microsoft YaHei UI", 10f);
            txtPwd.PasswordChar = '●';
            txtPwd.Visible = false;
            dlg.Controls.Add(txtPwd);

            var btnEye = MakeFlatSmall("显示", 298, 238, 78);
            btnEye.Visible = false;
            dlg.Controls.Add(btnEye);

            var hintForgot = new Label();
            hintForgot.Text = "忘记密码请到账号中心，用手机号验证码登录后重设";
            hintForgot.Font = new Font("Microsoft YaHei UI", 9f);
            hintForgot.ForeColor = Color.FromArgb(102, 112, 133);
            hintForgot.AutoSize = true;
            hintForgot.Location = new Point(24, 276);
            hintForgot.Visible = false;
            dlg.Controls.Add(hintForgot);

            var errorLabel = new Label();
            errorLabel.Text = "";
            errorLabel.ForeColor = Color.FromArgb(220, 70, 70);
            errorLabel.AutoSize = false;
            errorLabel.Size = new Size(352, 34);
            errorLabel.Location = new Point(24, 392);
            dlg.Controls.Add(errorLabel);

            var ok = new Button();
            ok.Text = "登录 / 注册";
            ok.Location = new Point(24, 430);
            ok.Size = new Size(352, 40);
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderSize = 0;
            ok.BackColor = Color.FromArgb(38, 166, 124);
            ok.ForeColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Cursor = Cursors.Hand;
            dlg.Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(24, 478);
            cancel.Size = new Size(352, 26);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderSize = 0;
            cancel.BackColor = Color.White;
            cancel.ForeColor = Color.FromArgb(100, 106, 114);
            cancel.Font = new Font("Microsoft YaHei UI", 9f);
            cancel.Cursor = Cursors.Hand;
            cancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(cancel);

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                if (countdown > 0) countdown--;
                btnSend.Text = countdown > 0 ? (countdown + "s 后重发") : "获取验证码";
                btnSend.Enabled = countdown <= 0 && txtPhone.Text.Length == 11;
                if (countdown <= 0) timer.Stop();
            };

            Action showNickname = delegate
            {
                bool show = mode == "sms" && !isUser && verificationId.Length > 0;
                lblNick.Visible = show;
                txtNick.Visible = show;
                hintNick.Visible = show;
            };

            Action applyMode = delegate
            {
                bool sms = mode == "sms";
                tabSms.BackColor = sms ? Color.FromArgb(232, 246, 241) : Color.White;
                tabSms.ForeColor = sms ? Color.FromArgb(24, 110, 82) : Color.FromArgb(110, 116, 124);
                tabPwd.BackColor = sms ? Color.White : Color.FromArgb(232, 246, 241);
                tabPwd.ForeColor = sms ? Color.FromArgb(110, 116, 124) : Color.FromArgb(24, 110, 82);

                lblCode.Visible = sms;
                txtCode.Visible = sms;
                btnSend.Visible = sms;
                hintCode.Visible = sms;
                lblNick.Visible = false;
                txtNick.Visible = false;
                hintNick.Visible = false;
                lblPwd.Visible = !sms;
                txtPwd.Visible = !sms;
                btnEye.Visible = !sms;
                hintForgot.Visible = !sms;

                lblPhone.Text = sms ? "手机号" : "账号";
                prefix.Visible = sms;
                txtPhone.Location = new Point(sms ? 70 : 24, 150);
                txtPhone.Size = new Size(sms ? 306 : 352, 30);
                hintPhone.Text = sms
                    ? "请输入 11 位手机号，系统会自动加 +86"
                    : "用户名、邮箱或手机号；手机号会自动补 +86";
                ok.Text = sms ? "登录 / 注册" : "登录";
                errorLabel.Text = "";
                if (sms) showNickname();
            };

            tabSms.Click += delegate { if (ok.Enabled) { mode = "sms"; applyMode(); } };
            tabPwd.Click += delegate { if (ok.Enabled) { mode = "password"; applyMode(); } };
            applyMode();

            btnEye.Click += delegate
            {
                if (txtPwd.PasswordChar == '●')
                {
                    txtPwd.PasswordChar = '\0';
                    btnEye.Text = "隐藏";
                }
                else
                {
                    txtPwd.PasswordChar = '●';
                    btnEye.Text = "显示";
                }
            };

            btnSend.Click += delegate
            {
                errorLabel.Text = "";
                if (txtPhone.Text.Length != 11)
                {
                    errorLabel.Text = "请输入 11 位手机号";
                    return;
                }
                btnSend.Enabled = false;
                btnSend.Text = "发送中…";
                string phone = txtPhone.Text;
                var worker = new System.Threading.Thread(delegate()
                {
                    AccountClient.SendCodeResult r = AccountClient.SendCode(phone);
                    try
                    {
                        dlg.BeginInvoke((MethodInvoker)delegate
                        {
                            if (r.Error.Length == 0)
                            {
                                verificationId = r.VerificationId;
                                isUser = r.IsUser;
                                countdown = 60;
                                timer.Start();
                                btnSend.Text = "60s 后重发";
                                btnSend.Enabled = false;
                                showNickname();
                                errorLabel.ForeColor = Color.FromArgb(24, 110, 82);
                                errorLabel.Text = isUser
                                    ? "验证码已发送，请查收短信"
                                    : "验证码已发送，验证后将自动创建账号";
                            }
                            else
                            {
                                errorLabel.ForeColor = Color.FromArgb(220, 70, 70);
                                errorLabel.Text = r.Error;
                                btnSend.Text = "获取验证码";
                                btnSend.Enabled = true;
                            }
                        });
                    }
                    catch { }
                });
                worker.IsBackground = true;
                worker.Start();
            };

            ok.Click += delegate
            {
                errorLabel.Text = "";
                if (mode == "sms")
                {
                    if (txtPhone.Text.Length != 11) { errorLabel.Text = "请输入 11 位手机号"; return; }
                    if (verificationId.Length == 0) { errorLabel.Text = "请先获取验证码"; return; }
                    if (txtCode.Text.Length != 6) { errorLabel.Text = "请输入 6 位数字验证码"; return; }
                    if (txtNick.Visible)
                    {
                        int n = txtNick.Text.Trim().Length;
                        if (n > 0 && (n < 2 || n > 64)) { errorLabel.Text = "昵称需要 2–64 个字符"; return; }
                    }
                }
                else
                {
                    if (txtPhone.Text.Trim().Length == 0) { errorLabel.Text = "请输入账号"; return; }
                    if (txtPwd.Text.Length == 0) { errorLabel.Text = "请输入密码"; return; }
                }

                ok.Enabled = false;
                ok.Text = "请稍候…";
                string m = mode;
                string account = txtPhone.Text.Trim();
                string code = txtCode.Text;
                string pwd = txtPwd.Text;
                string nick = txtNick.Text.Trim();
                string vid = verificationId;
                bool registered = isUser;

                var worker = new System.Threading.Thread(delegate()
                {
                    string err = m == "sms"
                        ? AccountClient.VerifyAndLogin(account, vid, code, nick, registered)
                        : AccountClient.TryLoginPassword(account, pwd);
                    try
                    {
                        dlg.BeginInvoke((MethodInvoker)delegate
                        {
                            if (err.Length == 0)
                            {
                                dlg.DialogResult = DialogResult.OK;
                                dlg.Close();
                            }
                            else
                            {
                                ok.Enabled = true;
                                ok.Text = m == "sms" ? "登录 / 注册" : "登录";
                                errorLabel.ForeColor = Color.FromArgb(220, 70, 70);
                                errorLabel.Text = err;
                            }
                        });
                    }
                    catch { }
                });
                worker.IsBackground = true;
                worker.Start();
            };

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                RefreshAccountUi();
                SetDiag("已登录统一账号：" + (AccountClient.DisplayName ?? "")
                      + "\n同一账号的平板现在可以直接连接这台电脑。");
            }
            dlg.Dispose();
        }

        static Button MakeTabButton(string text, int x, int y)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(94, 30);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(110, 116, 124);
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.Cursor = Cursors.Hand;
            using (var path = RoundRect(new Rectangle(0, 0, 94, 30), 8))
            {
                b.Region = new Region(path);
            }
            return b;
        }

        static Button MakeFlatSmall(string text, int x, int y, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 30);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(70, 76, 84);
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.Cursor = Cursors.Hand;
            using (var path = RoundRect(new Rectangle(0, 0, w, 30), 8))
            {
                b.Region = new Region(path);
            }
            return b;
        }

        static void SetRow(Label label, bool ok, string name)
        {
            label.Text = name;
            label.ForeColor = Color.FromArgb(60, 66, 72);
            var pill = label.Tag as Label;
            if (pill != null)
            {
                pill.Text = ok ? "正常" : "异常";
                pill.BackColor = ok ? Color.FromArgb(38, 166, 124) : Color.FromArgb(210, 90, 60);
            }
        }

        static bool ServiceRunning(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch { return false; }
        }

        static bool WatchdogAlive()
        {
            // 值班员只在"连接/断开"这类事件时写日志，空闲时可能半小时不写一行，
            // 所以不能拿日志时间判断死活，直接看它的进程在不在。
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            string cmd = mo["CommandLine"] as string;
                            if (cmd != null && cmd.IndexOf("session-watchdog.ps1",
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                        finally { mo.Dispose(); }
                    }
                }
            }
            catch { }

            // 兜底：万一查不到进程信息，用日志时间粗略判断（放宽到 10 分钟）
            try
            {
                string p = Path.Combine(DataDir, "logs", "watchdog.log");
                if (!File.Exists(p)) return false;
                return (DateTime.Now - File.GetLastWriteTime(p)).TotalSeconds < 600;
            }
            catch { return false; }
        }

        static string ElapsedText()
        {
            if (!sessionActive || sessionStart == DateTime.MinValue) return "";
            var span = DateTime.Now - sessionStart;
            if (span.TotalHours >= 1)
                return string.Format("{0} 小时 {1} 分", (int)span.TotalHours, span.Minutes);
            if (span.TotalMinutes >= 1)
                return string.Format("{0} 分 {1} 秒", (int)span.TotalMinutes, span.Seconds);
            return string.Format("{0} 秒", Math.Max(0, (int)span.TotalSeconds));
        }

        static string ShortUuid(string uuid)
        {
            if (uuid.Length <= 8) return uuid;
            return uuid.Substring(0, 8);
        }

        // ── 日志增量解析 ──────────────────────────────────

        static void PollLog()
        {
            string path = SunshineLogPath;
            if (!File.Exists(path)) return;

            long len;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                len = fs.Length;
                if (len < logPos) logPos = 0;   // 日志被截断/轮转
                if (len == logPos) return;

                fs.Seek(logPos, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    logPos = fs.Length;
                    if (text.Length == 0) return;

                    string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string raw in lines)
                    {
                        ProcessLogLine(raw.Trim());
                    }
                }
            }
        }

        static void ProcessLogLine(string line)
        {
            if (line.IndexOf("CLIENT CONNECTED", StringComparison.Ordinal) >= 0)
            {
                sessionActive = true;
                sessionStart = DateTime.Now;
                streamRes = "";
                streamFps = "";
                streamBitrate = "";
                displayName = "";
                Notify("有设备正在连接这台电脑", "正在建立画面，稍后即可操作。");
                return;
            }
            if (line.IndexOf("CLIENT DISCONNECTED", StringComparison.Ordinal) >= 0)
            {
                sessionActive = false;
                activeSessions = 0;
                Notify("设备已断开", "远程连接已结束。");
                return;
            }
            int idx = line.IndexOf("client_uuid=", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int end = line.IndexOf(']', idx);
                string v = end > idx ? line.Substring(idx + 12, end - idx - 12) : line.Substring(idx + 12);
                clientUuid = v.Trim().TrimEnd(',');
                return;
            }
            idx = line.IndexOf("Client requested stream resolution", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int colon = line.LastIndexOf(':');
                if (colon >= 0) streamRes = line.Substring(colon + 1).Trim();
                return;
            }
            idx = line.IndexOf("Client framerate:", StringComparison.Ordinal);
            if (idx >= 0 && line.IndexOf(" fps", StringComparison.Ordinal) > 0)
            {
                string v = line.Substring(idx + 17).Trim();
                int sp = v.IndexOf(' ');
                if (sp > 0) streamFps = v.Substring(0, sp) + " 帧";
                return;
            }
            idx = line.IndexOf("Capping initial encoder bitrate", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int to = line.IndexOf(" to ", idx, StringComparison.Ordinal);
                int kbps = line.IndexOf(" Kbps", to, StringComparison.Ordinal);
                if (to > 0 && kbps > to)
                {
                    streamBitrate = "码率 " + line.Substring(to + 4, kbps - to - 4).Trim() + " Kbps";
                }
                return;
            }
            idx = line.IndexOf("Using client-specified display:", StringComparison.Ordinal);
            if (idx >= 0)
            {
                displayName = line.Substring(idx + 32).Trim();
                return;
            }
            idx = line.IndexOf("New streaming session started [active sessions:", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int c = line.IndexOf(':', idx + 49);
                int b = line.IndexOf(']', idx);
                if (c >= 0 && b > c)
                {
                    int n;
                    if (int.TryParse(line.Substring(c + 1, b - c - 1).Trim(), out n)) activeSessions = n;
                }
                return;
            }
        }

        static void Notify(string title, string body)
        {
            try
            {
                if (tray != null && tray.Visible)
                {
                    tray.ShowBalloonTip(4000, AppName, title + "\n" + body, ToolTipIcon.Info);
                }
            }
            catch { }
        }

        // ── 一键体检修复 ──────────────────────────────────

        static void StartRepair()
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--repair";
                psi.UseShellExecute = true;
                psi.Verb = "runas";     // 需要管理员权限才能重启服务
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                SetDiag("已发起体检修复。\n如果系统弹出授权窗口，请点“是”。修复完成后会弹出结果。");
            }
            catch (Exception ex)
            {
                SetDiag("没有获得管理员授权，修复已取消。\n原始提示：" + ex.Message);
            }
        }

        static string RunRepair()
        {
            var sb = new StringBuilder();
            sb.AppendLine("检查时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            if (!IsAdmin())
            {
                sb.AppendLine("当前没有管理员权限，无法重启服务。请重新打开程序后再试。");
                return sb.ToString();
            }

            // 1) 串流服务
            bool sunshine = ServiceRunning("SunshineService");
            sb.AppendLine("串流服务（Sunshine）：" + (sunshine ? "运行中" : "未运行，正在启动…"));
            if (!sunshine)
            {
                sunshine = EnsureService("SunshineService", sb, "串流服务");
            }

            // 2) 组网服务
            bool easytier = ServiceRunning("EasyTierService");
            sb.AppendLine("虚拟网络（EasyTier）：" + (easytier ? "运行中" : "未运行，正在启动…"));
            if (!easytier)
            {
                easytier = EnsureService("EasyTierService", sb, "虚拟网络");
            }

            // 3) 串流端口
            bool port = IsPortListening(StreamPort);
            sb.AppendLine("串流端口 " + StreamPort + "：" + (port ? "已监听" : "未监听"));
            if (!port && sunshine)
            {
                sb.AppendLine("  端口没起来，正在重启串流服务…");
                sunshine = RestartService("SunshineService", sb, "串流服务");
                port = IsPortListening(StreamPort);
                sb.AppendLine("  重启后端口：" + (port ? "已监听" : "仍未监听"));
            }

            // 4) 虚拟网卡地址
            bool vip = HasVirtualIp();
            sb.AppendLine("虚拟 IP " + VirtualIp + "：" + (vip ? "已就绪" : "未就绪"));
            if (!vip && easytier)
            {
                sb.AppendLine("  正在重启组网服务，等它重新挂载网卡…");
                easytier = RestartService("EasyTierService", sb, "虚拟网络");
                System.Threading.Thread.Sleep(4000);
                vip = HasVirtualIp();
                sb.AppendLine("  重启后虚拟 IP：" + (vip ? "已就绪" : "仍未就绪"));
            }

            // 5) 防火墙放行
            bool fw = FirewallRuleExists();
            sb.AppendLine("防火墙放行规则：" + (fw ? "已配置" : "缺失"));
            if (!fw)
            {
                sb.AppendLine("  正在补充防火墙规则…");
                AddFirewallRules(sb);
                fw = FirewallRuleExists();
                sb.AppendLine("  补充后：" + (fw ? "已配置" : "仍缺失（可能被安全软件拦截）"));
            }

            // 6) 守护进程
            bool watchdog = WatchdogAlive();
            sb.AppendLine("守护进程（值班员）：" + (watchdog ? "正常" : "最近没有心跳"));
            if (!watchdog)
            {
                sb.AppendLine("  请重启电脑，或重新运行安装包修复。");
            }

            sb.AppendLine();
            bool allOk = sunshine && easytier && port && vip && fw && watchdog;
            sb.AppendLine(allOk
                ? "结论：全部正常，可以正常发起远控。"
                : "结论：还有项目不正常，已把日志留在 C:\\ProgramData\\LingWangRemote\\logs。");
            return sb.ToString();
        }

        static bool EnsureService(string name, StringBuilder sb, string label)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                    sb.AppendLine("  已启动 " + label + "。");
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  启动 " + label + " 失败：" + ex.Message);
                return false;
            }
        }

        static bool RestartService(string name, StringBuilder sb, string label)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    sb.AppendLine("  已重启 " + label + "。");
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  重启 " + label + " 失败：" + ex.Message);
                return false;
            }
        }

        static bool IsAdmin()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        static bool HasVirtualIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address != null && ua.Address.ToString() == VirtualIp) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static bool IsPortListening(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect("127.0.0.1", port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(900);
                    if (ok)
                    {
                        c.EndConnect(ar);
                        return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        static bool FirewallRuleExists()
        {
            try
            {
                var p = new Process();
                p.StartInfo.FileName = "netsh";
                p.StartInfo.Arguments = "advfirewall firewall show rule name=LingWang-EasyTier";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return outp.IndexOf("LingWang-EasyTier", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static void AddFirewallRules(StringBuilder sb)
        {
            try
            {
                string core = Path.Combine(AppDir, "easytier", "easytier-core.exe");
                if (!File.Exists(core))
                {
                    sb.AppendLine("  找不到组网程序，跳过防火墙补充。");
                    return;
                }
                RunHidden("netsh", "advfirewall firewall delete rule name=\"LingWang-EasyTier\"");
                RunHidden("netsh", "advfirewall firewall add rule name=\"LingWang-EasyTier\" dir=in action=allow program=\"" + core + "\" enable=yes protocol=tcp");
                RunHidden("netsh", "advfirewall firewall add rule name=\"LingWang-EasyTier\" dir=in action=allow program=\"" + core + "\" enable=yes protocol=udp");
            }
            catch (Exception ex)
            {
                sb.AppendLine("  补充防火墙规则出错：" + ex.Message);
            }
        }

        static void RunHidden(string file, string args)
        {
            try
            {
                var p = new Process();
                p.StartInfo.FileName = file;
                p.StartInfo.Arguments = args;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.Start();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
            }
            catch { }
        }

        // ── 小工具 ────────────────────────────────────────

        static void SetDiag(string text)
        {
            lastDiag = text;
            if (txtDiag != null) txtDiag.Text = text;
        }

        static void OpenWeb()
        {
            try
            {
                Process.Start(new ProcessStartInfo(WebUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开管理网页：" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\""));
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开文件夹：" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static bool IsAutoStart()
        {
            try
            {
                string exe = "\"" + Application.ExecutablePath + "\"";
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key != null)
                    {
                        var v = key.GetValue(RunValue) as string;
                        if (v != null && string.Equals(v, exe, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                // 安装包写的是机器级启动项，这里也要认，否则会误显示"没开启"
                using (var key = Registry.LocalMachine.OpenSubKey(RunKey))
                {
                    if (key != null)
                    {
                        var v = key.GetValue(RunValue) as string;
                        if (v != null && string.Equals(v, exe, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        static void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enable)
                        key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue(RunValue, false);
                }
                if (!enable)
                {
                    // 同时清掉安装包写的机器级启动项，避免"关了又自己起来"
                    try
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(RunKey, true))
                        {
                            if (key != null) key.DeleteValue(RunValue, false);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机启动失败：" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 纯代码画一个圆角底 + 白色 L 字的图标，不依赖任何外部图片
        static Icon MakeIcon(Color baseColor)
        {
            int size = 32;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(baseColor))
                using (var path = RoundRect(new Rectangle(1, 1, size - 2, size - 2), 7))
                {
                    g.FillPath(brush, path);
                }
                using (var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var white = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString("L", font, white, new Rectangle(0, 1, size, size), sf);
                }
                IntPtr hicon = bmp.GetHicon();
                Icon ico = (Icon)Icon.FromHandle(hicon).Clone();
                DestroyIcon(hicon);
                return ico;
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
    }
}
