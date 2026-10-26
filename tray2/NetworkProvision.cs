using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LingWangRemote
{
    /// <summary>
    /// 账号专属虚拟网络。
    ///
    /// 登录账号后，把 EasyTier 的网络名/密码换成由账号 ID 派生出来的一组值，
    /// 让每个账号的设备各自待在一个独立的小网络里：
    ///   - 不同账号之间互相看不见，也不会再出现"不同用户分到同一个虚拟 IP"的撞号；
    ///   - 同一个账号仍共用一个小网段（约 250 个地址），家用/个人几台电脑完全够用。
    ///
    /// 改系统服务的启动参数需要管理员权限，所以首次切换会弹一次授权窗口，
    /// 之后参数已匹配就不会再弹。派生算法必须与平板端 NetworkIdentity.ets 完全一致。
    /// </summary>
    static class NetworkProvision
    {
        const string SvcName = "EasyTierService";
        const string SvcParamsKey = @"SYSTEM\CurrentControlSet\Services\EasyTierService\Parameters";

        // 自建会合节点：只帮牵线，不转发数据
        const string HubPeers =
            "-p tcp://hub.example.com:11010 -p wss://hub.example.com:11012";
        // 公共节点：仅在用户明确选择时启用；配合 p2p-only 后它也只能帮打洞
        const string PublicPeer = "-p tcp://et1.fuis.top:11010";
        // 只与已建立直连的设备通信：从机制上杜绝任何服务器替我们转发画面
        const string P2POnly = "--p2p-only true";

        const string ModeHub = "hub";
        const string ModePublic = "public";

        /// <summary>
        /// 自建节点连不上时的询问回调：返回 true = 改用公共节点，false = 暂不连接。
        /// 由界面层注入；未注入时按「暂不连接」处理，绝不静默使用公共节点。
        /// </summary>
        public static Func<bool> ConfirmUsePublicNode;

        /// <summary>
        /// 账号专属网络的总开关。
        /// 两端已同时切到账号网络（2026-10-26 01:29 实测直连成功），此开关恢复开启。
        /// 若平板端回退到老网络，必须先把这里改回 false，避免单端切换导致两边互相看不见。
        /// </summary>
        static readonly bool AccountNetworkEnabled = true;

        static readonly object gate = new object();
        static string lastAttemptUid = "";

        static string LocalDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LingWangRemote");
            }
        }

        static string PendingPath { get { return Path.Combine(LocalDir, "network-pending.json"); } }

        static string ModePath { get { return Path.Combine(LocalDir, "node-mode.txt"); } }

        static string LogPath { get { return Path.Combine(LocalDir, "network.log"); } }

        // ── 节点模式（自建 / 公共） ───────────────────────

        /// <summary>当前节点模式：hub=只用自建，public=用户已同意临时用公共节点</summary>
        static string LoadMode()
        {
            try
            {
                if (!File.Exists(ModePath)) return ModeHub;
                string text = File.ReadAllText(ModePath, Encoding.UTF8).Trim();
                return text == ModePublic ? ModePublic : ModeHub;
            }
            catch { return ModeHub; }
        }

        static void SaveMode(bool allowPublic)
        {
            try
            {
                Directory.CreateDirectory(LocalDir);
                File.WriteAllText(ModePath, allowPublic ? ModePublic : ModeHub, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>自建会合节点是否连得上（只测端口通不通，不传任何数据）</summary>
        static bool HubReachable()
        {
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("hub.example.com", 11010, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>拼装服务启动参数；allowPublic 才追加公共节点，且始终只走直连</summary>
        static string BuildArgs(string name, string secret, string ip, bool allowPublic)
        {
            string peers = allowPublic ? HubPeers + " " + PublicPeer : HubPeers;
            return "--network-name " + name + " --network-secret " + secret
                 + " -i " + ip + " " + peers + " " + P2POnly;
        }

        // ── 派生 ─────────────────────────────────────────

        /// <summary>账号 ID → 网络名（与平板端一致）</summary>
        public static string NetworkName(string uid)
        {
            string b = "lw-net-v1|" + (uid == null ? "" : uid);
            return "lw-" + Hex32(Hash32(b + "|n1", 5381)) + Hex32(Hash32(b + "|n2", 52711));
        }

        /// <summary>账号 ID → 网络密码（与平板端一致）</summary>
        public static string NetworkSecret(string uid)
        {
            string b = "lw-net-v1|" + (uid == null ? "" : uid);
            return Hex32(Hash32(b + "|s1", 33))
                 + Hex32(Hash32(b + "|s2", 131))
                 + Hex32(Hash32(b + "|s3", 17))
                 + Hex32(Hash32(b + "|s4", 29));
        }

        static uint Hash32(string text, uint seed)
        {
            uint h = seed;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < bytes.Length; i++)
            {
                h = unchecked(h * 33 + bytes[i]);
            }
            return h;
        }

        static string Hex32(uint value)
        {
            return value.ToString("x8");
        }

        // ── 当前状态 ─────────────────────────────────────

        /// <summary>EasyTierService 当前的启动参数（读不到返回空串）</summary>
        public static string CurrentArgs()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(SvcParamsKey))
                {
                    if (key == null) return "";
                    object value = key.GetValue("AppParameters");
                    if (value == null) return "";
                    string[] multi = value as string[];
                    if (multi != null) return string.Join(" ", multi);
                    return value.ToString();
                }
            }
            catch { return ""; }
        }

        /// <summary>当前账号的网络是否已经生效（参数形态也要对得上）</summary>
        public static bool IsApplied()
        {
            if (!AccountClient.LoggedIn) return false;
            string uid = AccountClient.AccountId;
            string args = CurrentArgs();
            string name = NetworkName(uid);
            string secret = NetworkSecret(uid);
            if (args.IndexOf("--network-name " + name, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (args.IndexOf("--network-secret " + secret, StringComparison.OrdinalIgnoreCase) < 0) return false;
            // 必须是「只走直连」的新参数形态，老参数（带公共节点、可被中转）视为未生效，触发一次升级
            if (args.IndexOf(P2POnly, StringComparison.OrdinalIgnoreCase) < 0) return false;
            bool hasPublic = args.IndexOf("et1.fuis.top", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasPublic == (LoadMode() == ModePublic);
        }

        // ── 触发切换 ─────────────────────────────────────

        /// <summary>后台检查并按需发起一次授权切换；同一账号本进程内只尝试一次</summary>
        public static void EnsureAppliedAsync()
        {
            if (!AccountNetworkEnabled) return;
            if (!AccountClient.LoggedIn) return;
            string uid = AccountClient.AccountId;
            lock (gate)
            {
                if (lastAttemptUid == uid) return;
                lastAttemptUid = uid;
            }
            ThreadPool.QueueUserWorkItem(delegate { EnsureApplied(); });
        }

        static void EnsureApplied()
        {
            try
            {
                if (!AccountClient.LoggedIn) return;

                bool allowPublic = false;
                if (HubReachable())
                {
                    // 自建节点通着，一律优先自建；之前临时用过的公共节点自动收起
                    if (LoadMode() == ModePublic) Log("自建会合节点已恢复，自动停用公共节点");
                    SaveMode(false);
                }
                else if (LoadMode() == ModePublic)
                {
                    // 上次已经问过并同意，本次沿用，不再重复打扰
                    allowPublic = true;
                    Log("自建会合节点仍不可用，沿用用户已同意的公共节点");
                }
                else
                {
                    // 自建节点不通：必须问用户，绝不静默切换
                    Log("自建会合节点连不上，等待用户选择");
                    Func<bool> ask = ConfirmUsePublicNode;
                    bool usePublic = false;
                    try { usePublic = ask != null && ask(); }
                    catch { usePublic = false; }
                    if (!usePublic)
                    {
                        Log("用户选择暂不连接，本次不切换网络参数");
                        return;
                    }
                    allowPublic = true;
                    SaveMode(true);
                    Log("用户同意使用公共节点（仅帮打洞，不中转）");
                }

                if (IsApplied())
                {
                    Log("已在该账号的网络里，无需切换");
                    return;
                }

                string uid = AccountClient.AccountId;
                string name = NetworkName(uid);
                string secret = NetworkSecret(uid);
                string ip = HostAddress.Current;

                Directory.CreateDirectory(LocalDir);
                string json = "{ \"name\": " + Q(name)
                            + ", \"secret\": " + Q(secret)
                            + ", \"ip\": " + Q(ip)
                            + ", \"public\": " + (allowPublic ? "true" : "false") + " }";
                File.WriteAllText(PendingPath, json, Encoding.UTF8);
                Log("准备切到账号专属网络 " + name);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--apply-network \"" + PendingPath + "\"";
                psi.UseShellExecute = true;
                psi.Verb = "runas";          // 改系统服务必须提权，系统会弹一次授权
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit();
                    }
                }
                catch (Exception ex)
                {
                    Log("没有获得管理员授权，网络切换未完成：" + ex.Message);
                    return;
                }

                if (IsApplied())
                {
                    Log("账号专属网络已生效");
                    RebindGate();
                }
                else
                {
                    Log("授权已完成但参数仍未匹配，请查看 network-apply.log");
                }
            }
            catch (Exception ex)
            {
                Log("切换失败：" + ex.Message);
            }
        }

        /// <summary>组网重启后虚拟网卡会短暂消失，等服务把校验口重新挂上</summary>
        static void RebindGate()
        {
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(2000);
                try
                {
                    GateBridge.Refresh();
                    if (GateBridge.Running) return;
                }
                catch { }
            }
            Log("校验口重绑超时，稍后重启托盘可恢复");
        }

        // ── 提权后真正执行 ───────────────────────────────

        /// <summary>由 --apply-network 调用（管理员身份）：写服务参数并重启组网</summary>
        public static int ApplyFromFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    LogApply("待处理文件不存在：" + path);
                    return 2;
                }
                string json = File.ReadAllText(path, Encoding.UTF8);
                string name = ExtractString(json, "name");
                string secret = ExtractString(json, "secret");
                string ip = ExtractString(json, "ip");
                if (name.Length == 0 || secret.Length == 0)
                {
                    LogApply("待处理文件内容不完整");
                    return 3;
                }
                if (!HostAddress.IsValid(ip)) ip = HostAddress.Current;

                bool allowPublic = ExtractString(json, "public") == "true";

                string root = Path.GetDirectoryName(Path.GetDirectoryName(Application.ExecutablePath));
                string nssm = Path.Combine(root, "nssm", "nssm.exe");
                if (!File.Exists(nssm))
                {
                    LogApply("找不到 nssm：" + nssm);
                    return 4;
                }

                string args = BuildArgs(name, secret, ip, allowPublic);

                int rc = Run(nssm, "set EasyTierService AppParameters \"" + args + "\"", 20000);
                LogApply("nssm set 退出码=" + rc);
                if (rc != 0) return 5;

                rc = Run(nssm, "restart EasyTierService", 60000);
                LogApply("nssm restart 退出码=" + rc);
                if (rc != 0) return 6;

                try { File.Delete(path); } catch { }
                LogApply("完成：网络=" + name + " 本机地址=" + ip);
                return 0;
            }
            catch (Exception ex)
            {
                LogApply("异常：" + ex.Message);
                return 9;
            }
        }

        static int Run(string file, string arguments, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process p = Process.Start(psi))
            {
                if (p == null) return -1;
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return -2;
                }
                return p.ExitCode;
            }
        }

        // ── 日志与极简 JSON ──────────────────────────────

        static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(LocalDir);
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        static void LogApply(string message)
        {
            try
            {
                string dir = @"C:\ProgramData\LingWangRemote\logs";
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "network-apply.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
            Log(message);
        }

        static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string pattern = "\"" + key + "\"";
            int i = json.IndexOf(pattern, StringComparison.Ordinal);
            if (i < 0) return "";
            int colon = json.IndexOf(':', i + pattern.Length);
            if (colon < 0) return "";
            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length || json[p] != '"') return "";
            StringBuilder sb = new StringBuilder();
            for (int k = p + 1; k < json.Length; k++)
            {
                char c = json[k];
                if (c == '\\' && k + 1 < json.Length) { k++; sb.Append(json[k]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Q(string value)
        {
            if (value == null) value = "";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
