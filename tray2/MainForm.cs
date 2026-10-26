using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LingWangRemote
{
    /// <summary>
    /// 电脑端主窗口：WebView2 承载 HTML 界面，C# 负责状态和动作。
    /// 托盘图标与后台逻辑保持不变。
    /// </summary>
    class MainForm : Form
    {
        const string AppName = "OpenRemoteDesk";
        const string DataDir = @"C:\ProgramData\LingWangRemote";
        // 只操作我们自己的串流服务；社区版 SunshineService 一律不碰
        const string StreamService = "LingWangStreamService";

        WebView2 web;
        NotifyIcon tray;
        System.Windows.Forms.Timer pollTimer;
        Icon iconOk;
        Icon iconError;

        bool sessionActive = false;
        string sessionStartedAt = "";
        string streamRes = "";
        string streamFps = "";
        string streamBitrate = "";

        /// <summary>关闭按钮行为：ask=每次询问，hide=隐藏到托盘，exit=退出程序</summary>
        string closeAction = "ask";

        // 开机自启：直接读写当前用户的启动项，注册表即真实状态，不另存配置
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "LingWangTray";

        static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(RunValueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch { return false; }
        }

        static void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch { }
        }

        string UiSettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LingWangRemote", "ui-settings.json");
            }
        }

        public MainForm()
        {
            Text = AppName + " · 电脑端";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 660);
            MinimumSize = new Size(900, 600);
            BackColor = Color.FromArgb(176, 184, 194);
            Padding = new Padding(2);
            KeyPreview = true;

            iconOk = LoadIco("tray_ok.ico");
            iconError = LoadIco("tray_error.ico");
            // 任务栏缩略图/预览窗左上角显示的就是窗口图标，必须显式设置成我们的标
            try { Icon = iconOk; } catch { }

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);

            Load += async delegate
            {
                ApplyDpiSize();
                await InitWebAsync();
                // 常驻托盘：首次启动不弹窗口，双击托盘图标打开
                Hide();
            };

            // 从任务栏最小化状态恢复时，通知网页重新布局
            Resize += delegate
            {
                if (WindowState == FormWindowState.Normal && web.CoreWebView2 != null)
                {
                    try { web.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))"); } catch { }
                }
            };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            LoadUiSettings();
            BuildTray();

            // 自建会合节点连不上时，由这里询问用户：用公共节点，还是这次先不连
            NetworkProvision.ConfirmUsePublicNode = AskUsePublicNode;
            // 登录态恢复后补一次参数核对（老版本可能还挂着公共节点）
            try { NetworkProvision.EnsureAppliedAsync(); } catch { }

            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 2000;
            pollTimer.Tick += delegate
            {
                try
                {
                    GateBridge.Refresh();
                    PollSession();
                    PushState();
                }
                catch { }
            };
            pollTimer.Start();
        }

        /// <summary>
        /// 自建会合节点连不上时的询问：返回 true=改用公共节点，false=这次先不连。
        /// 公共节点只用来牵线，连接仍限制在设备直连，画面不会经过任何服务器。
        /// </summary>
        bool AskUsePublicNode()
        {
            if (InvokeRequired)
            {
                try { return (bool)Invoke(new Func<bool>(AskUsePublicNode)); }
                catch { return false; }
            }
            DialogResult r = MessageBox.Show(
                "暂时连不上我们自己的会合节点。\n\n"
                + "是否改用公共节点继续？\n"
                + "  · 公共节点由第三方提供，只用来帮你和设备互相找到\n"
                + "  · 连接仍然只走设备直连，画面不会经过任何服务器\n\n"
                + "选「是」＝ 用公共节点继续\n"
                + "选「否」＝ 这次先不联网（下次启动会再试自己的节点）",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            return r == DialogResult.Yes;
        }

        /// <summary>高 DPI 屏幕上按缩放比例放大窗口，保证网页拿到足够的逻辑尺寸</summary>
        void ApplyDpiSize()
        {
            // DeviceDpi 在部分高分屏上仍返回 96，直接读窗口实际 DPI 更可靠
            float scale = 1f;
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                if (dpi == 0)
                {
                    using (var g = Graphics.FromHwnd(Handle))
                    {
                        dpi = (uint)g.DpiX;
                    }
                }
                if (dpi > 0) scale = dpi / 96f;
            }
            catch { }
            if (scale < 1f) scale = 1f;

            ClientSize = new Size((int)(1000 * scale), (int)(660 * scale));
            MinimumSize = new Size((int)(900 * scale), (int)(600 * scale));
            CenterToScreen();
        }

        async System.Threading.Tasks.Task InitWebAsync()
        {
            try
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LingWangRemote", "WebView2");
                Directory.CreateDirectory(dataDir);

                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await web.EnsureCoreWebView2Async(env);

                var core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;

                string uiDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
                core.SetVirtualHostNameToFolderMapping(
                    "lingwang.ui", uiDir, CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += OnWebMessage;

                // 高分屏下网页拿到的视口可能偏小，按实际视口补偿一次窗口尺寸
                core.NavigationCompleted += async delegate
                {
                    try
                    {
                        string raw = await core.ExecuteScriptAsync("window.innerWidth");
                        int cssWidth = 0;
                        int.TryParse(raw, out cssWidth);
                        if (cssWidth > 0 && cssWidth < 900)
                        {
                            float factor = 1000f / cssWidth;
                            ClientSize = new Size(
                                (int)(ClientSize.Width * factor),
                                (int)(ClientSize.Height * factor));
                            CenterToScreen();
                        }
                    }
                    catch { }
                };

                core.Navigate("https://lingwang.ui/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "界面引擎（WebView2）启动失败。\n\n"
                    + "请安装 Microsoft Edge WebView2 运行库后重试。\n\n"
                    + "详细信息：" + ex.Message,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json = e.WebMessageAsJson;
            string action = GetStr(json, "action");
            string id = GetStr(json, "id");

            switch (action)
            {
                case "ready":
                    PushState();
                    break;
                case "viewport":
                    {
                        int width = 0;
                        int.TryParse(GetStr(json, "width"), out width);
                        if (width > 0 && width < 900)
                        {
                            float factor = 1000f / width;
                            ClientSize = new Size(
                                (int)(ClientSize.Width * factor),
                                (int)(ClientSize.Height * factor));
                            CenterToScreen();
                        }
                    }
                    break;
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "close":
                    DoCloseAction();
                    break;
                case "closeResolve":
                    {
                        string act = GetStr(json, "action");
                        if (GetStr(json, "remember") == "1")
                        {
                            closeAction = act == "hide" ? "hide" : "exit";
                            SaveUiSettings();
                        }
                        if (act == "hide")
                        {
                            Hide();
                        }
                        else
                        {
                            ExitApp();
                        }
                    }
                    break;
                case "getSettings":
                    RunAsync(id, delegate
                    {
                        return "{\"closeAction\":\"" + closeAction + "\",\"autoStart\":"
                            + (IsAutoStartEnabled() ? "true" : "false") + "}";
                    });
                    break;
                case "setSettings":
                    {
                        string value = GetStr(json, "closeAction");
                        if (value == "ask" || value == "hide" || value == "exit")
                        {
                            closeAction = value;
                            SaveUiSettings();
                        }
                        string autoStart = GetStr(json, "autoStart");
                        if (autoStart == "1" || autoStart == "0")
                        {
                            SetAutoStart(autoStart == "1");
                        }
                    }
                    break;
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, 0x2, 0);
                    break;
                case "openLogs":
                    OpenFolder(Path.Combine(DataDir, "logs"));
                    break;
                case "copy":
                    try { Clipboard.SetText(GetStr(json, "text")); } catch { }
                    break;
                case "genCode":
                    GateBridge.Generate(10);
                    PushState();
                    break;
                case "logout":
                    AccountClient.Logout();
                    PushState();
                    // 退出登录必须立刻断掉正在进行的远程会话，否则"退出登录"形同虚设
                    RevokeActiveSession();
                    break;
                case "listDevices":
                    RunAsync(id, delegate
                    {
                        return BuildJson(new string[] { "devices", AccountClient.ListDevices() });
                    });
                    break;
                case "deleteDevice":
                    RunAsync(id, delegate
                    {
                        string err = AccountClient.DeleteDevice(GetStr(json, "deviceId"));
                        return BuildJson(new string[] { "error", err });
                    });
                    break;
                case "sendCode":
                    RunAsync(id, delegate
                    {
                        var r = AccountClient.SendCode(GetStr(json, "phone"));
                        return BuildJson(new string[] {
                            "error", r.Error,
                            "isUser", r.IsUser ? "1" : "0",
                            "verificationId", r.VerificationId
                        });
                    });
                    break;
                case "verifyLogin":
                    RunAsync(id, delegate
                    {
                        string err = AccountClient.VerifyAndLogin(
                            GetStr(json, "phone"),
                            GetStr(json, "verificationId"),
                            GetStr(json, "code"),
                            GetStr(json, "nickname"),
                            GetStr(json, "isUser") == "1");
                        if (err.Length == 0) NetworkProvision.EnsureAppliedAsync();
                        return BuildJson(new string[] { "error", err });
                    });
                    break;
                case "loginPassword":
                    RunAsync(id, delegate
                    {
                        string err = AccountClient.TryLoginPassword(
                            GetStr(json, "account"), GetStr(json, "password"));
                        if (err.Length == 0) NetworkProvision.EnsureAppliedAsync();
                        return BuildJson(new string[] { "error", err });
                    });
                    break;
            }
        }

        /// <summary>后台执行账号请求，完成后回调网页</summary>
        void RunAsync(string id, Func<string> work)
        {
            var thread = new Thread(delegate ()
            {
                string resultJson = "";
                try { resultJson = work(); }
                catch (Exception ex) { resultJson = BuildJson(new string[] { "error", ex.Message }); }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        web.CoreWebView2.ExecuteScriptAsync(
                            "window.onBridgeResult && window.onBridgeResult("
                            + BuildJson(new string[] { "id", id, "data", resultJson }) + ")");
                        PushState();
                    });
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void LoadUiSettings()
        {
            try
            {
                string path = UiSettingsPath;
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path, Encoding.UTF8);
                string value = GetStr(json, "closeAction");
                if (value == "ask" || value == "hide" || value == "exit")
                {
                    closeAction = value;
                }
            }
            catch { }
        }

        void SaveUiSettings()
        {
            try
            {
                string path = UiSettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\"closeAction\":\"" + closeAction + "\"}", Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>点关闭按钮：按用户设置决定询问、隐藏还是退出</summary>
        void DoCloseAction()
        {
            if (closeAction == "hide")
            {
                Hide();
            }
            else if (closeAction == "exit")
            {
                ExitApp();
            }
            else
            {
                try { web.CoreWebView2.ExecuteScriptAsync("window.showCloseDialog && window.showCloseDialog()"); } catch { }
            }
        }

        /// <summary>退出托盘程序：同时停掉串流与组网服务，避免留下后台常驻</summary>
        void ExitApp()
        {
            try { if (tray != null) tray.Visible = false; } catch { }
            StopStreamingServices();
            Application.Exit();
        }

        /// <summary>
        /// 停止串流服务与组网服务。
        /// 托盘通常是普通权限启动的，停服务需要管理员；先直接停，失败再用一次提权补齐。
        /// </summary>
        void StopStreamingServices()
        {
            bool stopped = TryStopService(StreamService);
            stopped = TryStopService("EasyTierService") && stopped;
            if (!stopped)
            {
                ElevateStopServices();
            }
        }

        static bool TryStopService(string name)
        {
            try
            {
                using (var controller = new ServiceController(name))
                {
                    if (controller.Status == ServiceControllerStatus.Stopped)
                    {
                        return true;
                    }
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>普通权限停不了服务时，用一次 UAC 提权把两个服务停掉</summary>
        static void ElevateStopServices()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"foreach($s in 'LingWangStreamService','EasyTierService'){ sc.exe stop $s | Out-Null }\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch
            {
                // 用户取消了 UAC：尊重用户选择，托盘仍然退出
            }
        }

        /// <summary>
        /// 退出登录后立刻结束当前串流会话：重启 Sunshine 服务会断开已连接的客户端。
        /// 普通权限停不了服务时，用一次 UAC 提权完成。
        /// </summary>
        void RevokeActiveSession()
        {
            bool restarted = TryRestartSunshine();
            if (!restarted)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"sc.exe stop LingWangStreamService | Out-Null; Start-Sleep -Milliseconds 1200; sc.exe start LingWangStreamService | Out-Null\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                catch { }
            }
        }

        static bool TryRestartSunshine()
        {
            try
            {
                using (var controller = new ServiceController(StreamService))
                {
                    if (controller.Status != ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
                    }
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        void PushState()
        {
            if (web.CoreWebView2 == null) return;

            bool sunshine = ServiceRunning(StreamService);
            bool easytier = ServiceRunning("EasyTierService");

            if (tray != null)
            {
                tray.Icon = (sunshine && easytier) ? iconOk : iconError;
            }
            string gateCode = GateBridge.Code;
            int gateMinutes = GateBridge.Minutes;

            string accountName = AccountClient.DisplayName == null ? "" : AccountClient.DisplayName;
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"sunshine\":").Append(sunshine ? "true" : "false").Append(",");
            sb.Append("\"easytier\":").Append(easytier ? "true" : "false").Append(",");
            sb.Append("\"sessionActive\":").Append(sessionActive ? "true" : "false").Append(",");
            sb.Append("\"sessionText\":").Append(J(sessionText())).Append(",");
            sb.Append("\"sessionDetail\":").Append(J(sessionDetail())).Append(",");
            sb.Append("\"machine\":").Append(J(Environment.MachineName)).Append(",");
            sb.Append("\"virtualIp\":").Append(J(HostAddress.Current)).Append(",");
            sb.Append("\"deviceId\":").Append(J(AuthGate.DeviceId)).Append(",");
            sb.Append("\"loggedIn\":").Append(AccountClient.LoggedIn ? "true" : "false").Append(",");
            sb.Append("\"accountName\":").Append(J(accountName)).Append(",");
            sb.Append("\"code\":").Append(J(gateCode)).Append(",");
            sb.Append("\"codeMinutes\":").Append(gateMinutes).Append(",");
            sb.Append("\"netScoped\":").Append(NetworkProvision.IsApplied() ? "true" : "false").Append(",");
            sb.Append("\"gateRunning\":").Append(GateBridge.Running ? "true" : "false");
            sb.Append("}");

            string script = "window.renderState && window.renderState(" + sb.ToString() + ")";
            try { web.CoreWebView2.ExecuteScriptAsync(script); } catch { }
        }

        string sessionText()
        {
            if (!sessionActive) return "没有设备连接";
            if (sessionStartedAt.Length > 0) return "有设备正在连接";
            return "有设备正在连接";
        }

        string sessionDetail()
        {
            if (!sessionActive) return "平板连上来后，这里会显示画面规格";
            var parts = new System.Collections.Generic.List<string>();
            if (streamRes.Length > 0) parts.Add(streamRes);
            if (streamFps.Length > 0) parts.Add(streamFps);
            if (streamBitrate.Length > 0) parts.Add(streamBitrate);
            return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : "正在协商画面参数…";
        }

        // ── Sunshine 日志解析（只做会话与画面规格）─────────

        string SunshineLogPath()
        {
            // 托盘在 {安装目录}\tray\，我们自己的串流实例日志在上一级的 sunshine-lw\config\ 下
            string trayDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string appDir = Path.GetDirectoryName(trayDir);
            if (!string.IsNullOrEmpty(appDir))
            {
                string p = Path.Combine(appDir, "sunshine-lw", "config", "sunshine.log");
                if (File.Exists(p)) return p;
            }
            string alt = Path.Combine(DataDir, "logs", "sunshine.log");
            if (File.Exists(alt)) return alt;
            return "";
        }

        // 只读新增日志行，避免每次从头扫描把旧会话当成当前状态
        long logOffset = -1;
        string logPath = "";

        void PollSession()
        {
            try
            {
                string path = SunshineLogPath();
                if (path.Length == 0 || !File.Exists(path)) return;

                var fi = new FileInfo(path);
                if (logOffset < 0 || path != logPath || fi.Length < logOffset)
                {
                    // 首次或日志轮转：从尾部 300KB 建立初始状态
                    logPath = path;
                    logOffset = Math.Max(0, fi.Length - 300000);
                    sessionActive = false;
                    streamRes = "";
                    streamFps = "";
                    streamBitrate = "";
                }

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(logOffset, SeekOrigin.Begin);
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            HandleLogLine(line);
                        }
                    }
                    logOffset = fs.Length;
                }
            }
            catch { }
        }

        void HandleLogLine(string line)
        {
            if (line.IndexOf("New streaming session started", StringComparison.Ordinal) >= 0)
            {
                sessionActive = true;
                return;
            }
            if (line.IndexOf("Stopping streaming session", StringComparison.Ordinal) >= 0
                || line.IndexOf("streaming session terminated", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("CLIENT DISCONNECTED", StringComparison.Ordinal) >= 0)
            {
                sessionActive = false;
                streamRes = "";
                streamFps = "";
                streamBitrate = "";
                return;
            }
            if (line.IndexOf("Client requested stream resolution", StringComparison.Ordinal) >= 0)
            {
                int i = line.LastIndexOf(':');
                if (i > 0) streamRes = line.Substring(i + 1).Trim();
                return;
            }
            const string fpsTag = "Client framerate:";
            int fpsIdx = line.IndexOf(fpsTag, StringComparison.Ordinal);
            if (fpsIdx >= 0)
            {
                string v = line.Substring(fpsIdx + fpsTag.Length).Trim();
                int sp = v.IndexOf(' ');
                streamFps = (sp > 0 ? v.Substring(0, sp) : v) + " 帧";
                return;
            }
            if (line.IndexOf("Capping initial encoder bitrate", StringComparison.Ordinal) >= 0)
            {
                int to = line.IndexOf(" to ", StringComparison.Ordinal);
                if (to > 0)
                {
                    int kbps = line.IndexOf(" Kbps", to, StringComparison.Ordinal);
                    if (kbps > to)
                    {
                        streamBitrate = "码率 " + line.Substring(to + 4, kbps - to - 4).Trim() + " Kbps";
                    }
                }
            }
        }

        /// <summary>从程序目录加载 ico；缺失时退回系统默认图标</summary>
        static Icon LoadIco(string fileName)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            return SystemIcons.Application;
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

        // ── 托盘 ──────────────────────────────────────────

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = iconOk;
            tray.Text = AppName;
            tray.Visible = true;
            tray.DoubleClick += delegate { ToggleMain(); };

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("打开主界面…");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += delegate { ShowMain(); };
            menu.Items.Add(openItem);

            var codeItem = new ToolStripMenuItem("生成临时控制码");
            codeItem.Click += delegate
            {
                string newCode = GateBridge.Generate(10);
                if (newCode.Length > 0) { try { Clipboard.SetText(newCode); } catch { } }
                ShowMain();
            };
            menu.Items.Add(codeItem);

            var accountItem = new ToolStripMenuItem("账号…");
            accountItem.Click += delegate
            {
                ShowMain();
                if (web.CoreWebView2 != null)
                {
                    web.CoreWebView2.ExecuteScriptAsync("window.gotoPage && window.gotoPage('account')");
                }
            };
            menu.Items.Add(accountItem);

            menu.Items.Add(new ToolStripSeparator());

            var logItem = new ToolStripMenuItem("打开日志文件夹…");
            logItem.Click += delegate { OpenFolder(Path.Combine(DataDir, "logs")); };
            menu.Items.Add(logItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出程序（同时停止远控服务）");
            exitItem.Click += delegate { ExitApp(); };
            menu.Items.Add(exitItem);

            tray.ContextMenuStrip = menu;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // 无边框窗口默认没有 WS_MINIMIZEBOX，任务栏图标第二次点击无法触发最小化
                cp.Style |= 0x20000; // WS_MINIMIZEBOX
                return cp;
            }
        }

        /// <summary>托盘双击：已显示则收起，否则打开</summary>
        void ToggleMain()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                Hide();
            }
            else
            {
                ShowMain();
            }
        }

        void ShowMain()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();

            // WebView2 在 Hide/Show 后不自动重新布局，强制触发一次 resize
            if (web.CoreWebView2 != null)
            {
                try { web.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))"); } catch { }
            }
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", path);
            }
            catch { }
        }

        // ── 小工具 ────────────────────────────────────────

        static string BuildJson(string[] kv)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(kv[i]).Append("\":");
                if (kv[i] == "isUser")
                {
                    sb.Append(kv[i + 1] == "1" ? "true" : "false");
                }
                else
                {
                    sb.Append(J(kv[i + 1]));
                }
            }
            return sb.Append("}").ToString();
        }

        static string GetStr(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string pattern = "\"" + key + "\"";
            int i = json.IndexOf(pattern, StringComparison.Ordinal);
            if (i < 0) return "";
            int colon = json.IndexOf(':', i + pattern.Length);
            if (colon < 0) return "";
            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length) return "";
            if (json[p] == '"')
            {
                var sb = new StringBuilder();
                for (int k = p + 1; k < json.Length; k++)
                {
                    char c = json[k];
                    if (c == '\\' && k + 1 < json.Length)
                    {
                        k++;
                        char e = json[k];
                        if (e == 'n') sb.Append('\n');
                        else if (e == 't') sb.Append('\t');
                        else if (e == 'r') sb.Append('\r');
                        else if (e == 'u' && k + 4 < json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(k + 1, 4),
                                System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                k += 4;
                            }
                        }
                        else sb.Append(e);
                        continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                }
                return sb.ToString();
            }
            int start = p;
            while (p < json.Length && (char.IsDigit(json[p]) || json[p] == '-' || json[p] == '.')) p++;
            return p > start ? json.Substring(start, p - start) : "";
        }

        static string J(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }
}
