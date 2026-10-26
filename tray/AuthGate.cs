using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LingWangRemote
{
    /// <summary>
    /// 本地控制校验口（电脑端）。
    /// 平板连接前先问这里：同账号放行，或输入电脑端生成的临时控制码放行。
    /// 只用 TcpListener 实现极简 HTTP，避免 HttpListener 需要管理员注册 URL 权限的问题。
    /// 监听 127.0.0.1:47999 与 EasyTier 虚拟 IP 10.126.126.1:47999。
    /// </summary>
    static class AuthGate
    {
        public const int Port = 47999;
        /// <summary>本机在虚拟网络里的固定 IP（安装时分配，每台电脑不同）</summary>
        static string VirtualIp { get { return HostAddress.Current; } }

        static TcpListener loopback;
        static TcpListener virt;
        static volatile bool running;
        static string deviceId = "";
        static readonly object codeLock = new object();
        static string currentCode = "";
        static DateTime codeExpire = DateTime.MinValue;
        static readonly Random rnd = new Random();

        public static string DeviceId
        {
            get
            {
                if (deviceId.Length == 0) deviceId = LoadDeviceId();
                return deviceId;
            }
        }

        public static bool Running { get { return running; } }

        /// <summary>
        /// 本机绑定的账号标识。
        /// 托盘（用户身份）能直接读登录态；串流服务（System 身份）读不到用户的令牌，
        /// 改读托盘登录时写下的机器级缓存，这样开机还没人登录桌面时也能校验对方账号。
        /// </summary>
        public static string LocalAccountId
        {
            get
            {
                if (AccountClient.LoggedIn) return AccountClient.AccountId;
                string[] parts = ReadMachineAccount();
                return parts.Length > 0 ? parts[0] : "";
            }
        }

        /// <summary>本机账号昵称（同上，未登录时读机器级缓存）</summary>
        public static string LocalAccountName
        {
            get
            {
                if (AccountClient.LoggedIn) return AccountClient.DisplayName == null ? "" : AccountClient.DisplayName;
                string[] parts = ReadMachineAccount();
                return parts.Length > 1 ? parts[1] : "";
            }
        }

        static string[] ReadMachineAccount()
        {
            try
            {
                string path = AccountClient.MachineAccountPath;
                if (!File.Exists(path)) return new string[0];
                string text = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (text.Length == 0) return new string[0];
                return text.Split('\t');
            }
            catch { return new string[0]; }
        }

        /// <summary>回环地址与虚拟网卡地址是否都已挂上监听（组网重启后用于重绑判断）</summary>
        public static bool AllBound { get { return loopback != null && virt != null; } }



        /// <summary>当前有效控制码，过期则返回空串</summary>
        public static string CurrentCode
        {
            get
            {
                lock (codeLock)
                {
                    if (currentCode.Length == 0 || DateTime.Now >= codeExpire) return "";
                    return currentCode;
                }
            }
        }

        public static DateTime CodeExpire { get { return codeExpire; } }

        /// <summary>生成 6 位临时控制码（默认 10 分钟）</summary>
        public static string GenerateCode(int minutes)
        {
            lock (codeLock)
            {
                currentCode = rnd.Next(100000, 1000000).ToString();
                codeExpire = DateTime.Now.AddMinutes(minutes);
                return currentCode;
            }
        }

        /// <summary>虚拟网卡可能还没起来，定时重试直到两个端口都监听成功</summary>
        public static void EnsureStarted()
        {
            // 先置位再起监听：否则 AcceptLoop 线程可能在 running=true 之前就跑起来，
            // 看到 false 直接退出，表现就是端口在监听但所有请求都没人应答。
            // 不在这里提前返回：虚拟网卡往往比监听晚就绪，必须能补挂缺失的那一个。
            running = true;
            bool any = false;

            if (loopback == null)
            {
                try
                {
                    loopback = new TcpListener(IPAddress.Loopback, Port);
                    loopback.Start();
                    ThreadPool.QueueUserWorkItem(AcceptLoop, loopback);
                    any = true;
                }
                catch { loopback = null; }
            }
            else any = true;

            if (virt == null)
            {
                try
                {
                    virt = new TcpListener(IPAddress.Parse(VirtualIp), Port);
                    virt.Start();
                    ThreadPool.QueueUserWorkItem(AcceptLoop, virt);
                    any = true;
                }
                catch { virt = null; }
            }
            else any = true;

            running = any;
        }

        public static void Stop()
        {
            running = false;
            try { if (loopback != null) loopback.Stop(); } catch { }
            try { if (virt != null) virt.Stop(); } catch { }
            loopback = null;
            virt = null;
        }

        static void AcceptLoop(object state)
        {
            var listener = (TcpListener)state;
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch
                {
                    break;
                }
            }
        }

        static void HandleClient(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;

                IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
                string ip = ep == null ? "" : ep.Address.ToString();
                bool allowedIp = ip == "127.0.0.1" || ip == "::1"
                              || ip.StartsWith("10.126.126.") || ip.StartsWith("::ffff:10.126.126.");
                if (!allowedIp)
                {
                    LogGate(ip, "?", "来源地址不在允许范围");
                    Respond(client.GetStream(), 403, "{\"ok\":false,\"reason\":\"来源地址不在允许范围\"}");
                    return;
                }

                var stream = client.GetStream();
                string method;
                string path;
                string body;
                ReadRequest(stream, out method, out path, out body);
                if (method.Length == 0)
                {
                    return;
                }

                int q = path.IndexOf('?');
                if (q >= 0) path = path.Substring(0, q);

                if (method == "GET" && path == "/v1/device")
                {
                    string verdict = RespondDevice(stream);
                    LogGate(ip, method + " " + path, verdict);
                }
                else if (method == "POST" && path == "/v1/verify")
                {
                    string verdict = RespondVerify(stream, body);
                    LogGate(ip, method + " " + path, verdict);
                }
                else if (method == "POST" && path == "/v1/code")
                {
                    string verdict = RespondCode(stream, body);
                    LogGate(ip, method + " " + path, verdict);
                }
                else if (method == "POST" && path == "/v1/pair")
                {
                    string verdict = RespondPair(stream, body);
                    LogGate(ip, method + " " + path, verdict);
                }
                else if (method == "GET" && path == "/v1/ping")
                {
                    Respond(stream, 200, "{\"ok\":true,\"product\":\"remote-control\"}");
                    LogGate(ip, method + " " + path, "ping");
                }
                else if (path.StartsWith("/v1/admin/"))
                {
                    // 管理接口只给本机的托盘用，其它来源一律拒绝
                    if (!(ip == "127.0.0.1" || ip == "::1"))
                    {
                        Respond(stream, 403, "{\"ok\":false,\"reason\":\"只允许本机调用\"}");
                        LogGate(ip, method + " " + path, "admin rejected");
                    }
                    else if (method == "GET" && path == "/v1/admin/code")
                    {
                        Respond(stream, 200, CodeJson());
                        LogGate(ip, method + " " + path, "code read");
                    }
                    else if (method == "POST" && path == "/v1/admin/code")
                    {
                        int minutes = ExtractInt(body, "minutes", 10);
                        if (minutes <= 0 || minutes > 120) minutes = 10;
                        GenerateCode(minutes);
                        Respond(stream, 200, CodeJson());
                        LogGate(ip, method + " " + path, "code generated");
                    }
                    else if (method == "GET" && path == "/v1/admin/status")
                    {
                        Respond(stream, 200,
                            "{\"ok\":true,\"running\":" + (running ? "true" : "false")
                            + ",\"loopback\":" + (loopback != null ? "true" : "false")
                            + ",\"virtual\":" + (virt != null ? "true" : "false") + "}");
                        LogGate(ip, method + " " + path, "status");
                    }
                    else
                    {
                        Respond(stream, 404, "{\"ok\":false,\"reason\":\"未知接口\"}");
                    }
                }
                else
                {
                    Respond(stream, 404, "{\"ok\":false,\"reason\":\"未知接口\"}");
                    LogGate(ip, method + " " + path, "未知接口");
                }
            }
            catch
            {
                // 单个请求失败不影响监听
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// 按字节读取 HTTP 请求。
        /// 不能把 Content-Length（字节数）当成字符数去读：请求体里有中文设备名时
        /// UTF-8 字节数大于字符数，按字符读会永远等不满，客户端只能超时。
        /// </summary>
        static void ReadRequest(Stream stream, out string method, out string path, out string body)
        {
            method = "";
            path = "/";
            body = "";

            var acc = new List<byte>();
            byte[] tmp = new byte[2048];
            int headerEnd = -1;
            while (headerEnd < 0 && acc.Count <= 65536)
            {
                int n = stream.Read(tmp, 0, tmp.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++) acc.Add(tmp[i]);
                headerEnd = FindHeaderEnd(acc);
            }
            if (headerEnd < 0) return;

            string headText = Encoding.UTF8.GetString(acc.GetRange(0, headerEnd).ToArray());
            string[] lines = headText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            string[] parts = lines[0].Split(' ');
            method = parts.Length > 0 ? parts[0] : "";
            path = parts.Length > 1 ? parts[1] : "/";

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lines[i].Substring(15).Trim(), out contentLength);
                }
            }
            if (contentLength < 0) contentLength = 0;

            byte[] bodyBytes = new byte[contentLength];
            int bodyStart = headerEnd + 4;
            int alreadyRead = Math.Max(0, Math.Min(contentLength, acc.Count - bodyStart));
            if (alreadyRead > 0)
            {
                Array.Copy(acc.ToArray(), bodyStart, bodyBytes, 0, alreadyRead);
            }
            int got = alreadyRead;
            while (got < contentLength)
            {
                int n = stream.Read(bodyBytes, got, contentLength - got);
                if (n <= 0) break;
                got += n;
            }
            body = Encoding.UTF8.GetString(bodyBytes, 0, got);
        }

        static int FindHeaderEnd(List<byte> data)
        {
            for (int i = 0; i + 3 < data.Count; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                {
                    return i;
                }
            }
            return -1;
        }

        static string RespondDevice(Stream stream)
        {
            string name = Environment.MachineName;
            string accountName = LocalAccountName;
            bool bound = LocalAccountId.Length > 0;
            string json = "{"
                + "\"ok\":true,"
                + "\"product\":\"remote-control\","
                + "\"deviceId\":" + J(DeviceId) + ","
                + "\"deviceName\":" + J(name) + ","
                + "\"loggedIn\":" + (bound ? "true" : "false") + ","
                + "\"accountName\":" + J(accountName) + ","
                + "\"sameAccountRequired\":true,"
                + "\"codeEnabled\":" + (CurrentCode.Length > 0 ? "true" : "false")
                + "}";
            Respond(stream, 200, json);
            return "device loggedIn=" + (bound ? "true" : "false") + " account=" + accountName;
        }

        static string RespondVerify(Stream stream, string body)
        {
            string token = ExtractString(body, "accessToken");

            // 续期放后台，避免账号中心慢时拖住连接握手
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { AccountClient.RefreshIfNeeded(); } catch { }
                });
            }
            catch { }

            string reason = AccountClient.VerifyPeerTokenFor(token, LocalAccountId);

            // 只认同一账号：不同账号或未登录一律拒绝，不在电脑端弹任何请求
            if (reason.Length == 0)
            {
                RespondAllowed(stream);
                return "verify allowed";
            }
            RespondDenied(stream, "账号不一致，请使用同一账号登录，或改用控制码连接");
            return "verify denied: " + reason;
        }

        static void RespondAllowed(Stream stream)
        {
            Respond(stream, 200,
                "{\"ok\":true,\"allowed\":true,\"reason\":\"\"}");
        }

        static void RespondDenied(Stream stream, string reason)
        {
            Respond(stream, 200,
                "{\"ok\":true,\"allowed\":false,\"reason\":" + J(reason) + "}");
        }

        static string RespondCode(Stream stream, string body)
        {
            string input = ExtractString(body, "code").Trim();
            string reason = "";
            lock (codeLock)
            {
                if (currentCode.Length == 0 || DateTime.Now >= codeExpire)
                {
                    reason = "这台电脑当前没有有效的控制码，请在电脑端生成一个";
                }
                else if (input != currentCode)
                {
                    reason = "控制码不正确";
                }
            }
            if (reason.Length == 0)
            {
                Respond(stream, 200, "{\"ok\":true,\"allowed\":true,\"reason\":\"\""
                    + NetworkExtra() + "}");
                return "code allowed";
            }
            Respond(stream, 200, "{\"ok\":true,\"allowed\":false,\"reason\":" + J(reason) + "}");
            return "code denied: " + reason;
        }

        /// <summary>
        /// 平板请求配对：同账号直接由电脑端代为确认，用户不需要打开任何网页。
        /// 电脑端调用自有实例的本地接口生成一次性配对码，平板拿这个码完成标准配对流程。
        /// </summary>
        static string RespondPair(Stream stream, string body)
        {
            string token = ExtractString(body, "accessToken");
            string name = ExtractString(body, "clientName");

            try { AccountClient.RefreshIfNeeded(); } catch { }

            string reason = AccountClient.VerifyPeerTokenFor(token, LocalAccountId);
            if (reason.Length > 0)
            {
                Respond(stream, 200,
                    "{\"ok\":true,\"allowed\":false,\"pin\":\"\",\"reason\":" + J("账号不一致，无法自动配对") + "}");
                return "pair denied: " + reason;
            }

            if (!StreamBox.Ready)
            {
                Respond(stream, 200,
                    "{\"ok\":true,\"allowed\":false,\"pin\":\"\",\"reason\":" + J("电脑端串流服务还没就绪，请稍后重试") + "}");
                return "pair denied: credential missing";
            }

            string pin = StreamBox.BeginPair();
            if (pin.Length == 0)
            {
                Respond(stream, 200,
                    "{\"ok\":true,\"allowed\":false,\"pin\":\"\",\"reason\":" + J("电脑端暂时无法生成配对码，请确认串流服务正在运行") + "}");
                return "pair denied: begin failed";
            }

            Respond(stream, 200,
                "{\"ok\":true,\"allowed\":true,\"pin\":" + J(pin) + ",\"reason\":\"\""
                + NetworkExtra() + "}");
            return "pair allowed for " + name;
        }

        /// <summary>
        /// 放行时附带本机的组网身份（网络名 + 密码），平板配对成功后保存，
        /// 之后就用这套值组网。这样安装包里不需要再写死一个固定密码。
        /// </summary>
        static string NetworkExtra()
        {
            try
            {
                string json = MachineNetwork.EffectiveJson(LocalAccountId);
                if (string.IsNullOrEmpty(json)) return "";
                return ",\"network\":" + json;
            }
            catch { return ""; }
        }

        static readonly object logLock = new object();

        /// <summary>控制码的当前状态（供本机托盘读取）</summary>
        static string CodeJson()
        {
            string code = CurrentCode;
            int minutes = 0;
            if (code.Length > 0) minutes = (int)Math.Ceiling((CodeExpire - DateTime.Now).TotalMinutes);
            if (minutes < 0) minutes = 0;
            return "{\"ok\":true,\"code\":" + J(code) + ",\"minutes\":" + minutes + "}";
        }

        static int ExtractInt(string json, string key, int fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;
            string pattern = "\"" + key + "\"";
            int i = json.IndexOf(pattern, StringComparison.Ordinal);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i + pattern.Length);
            if (colon < 0) return fallback;
            int p = colon + 1;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            int start = p;
            while (p < json.Length && char.IsDigit(json[p])) p++;
            if (p == start) return fallback;
            int value;
            return int.TryParse(json.Substring(start, p - start), out value) ? value : fallback;
        }

        /// <summary>把每次门禁请求写入 authgate.log，便于事后定位连接失败原因</summary>
        static void LogGate(string ip, string request, string verdict)
        {
            try
            {
                string dir = Path.Combine(@"C:\ProgramData\LingWangRemote", "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "authgate.log");
                lock (logLock)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(file);
                        if (fi.Exists && fi.Length > 1024 * 1024)
                        {
                            string[] lines = File.ReadAllLines(file);
                            int keep = lines.Length / 2;
                            File.WriteAllLines(file, SubArray(lines, keep, lines.Length - keep), Encoding.UTF8);
                        }
                    }
                    catch { }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + "\t" + ip + "\t" + request + "\t" + verdict;
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        static string[] SubArray(string[] src, int start, int count)
        {
            if (count <= 0) return new string[0];
            string[] dst = new string[count];
            Array.Copy(src, start, dst, 0, count);
            return dst;
        }

        static string DisplayNameSafe()
        {
            try
            {
                return AccountClient.DisplayName == null ? "" : AccountClient.DisplayName;
            }
            catch { return ""; }
        }

        static void Respond(Stream stream, int status, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 " + status + " OK\r\n"
                        + "Content-Type: application/json; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            stream.Write(hb, 0, hb.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
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
            sb.Append('"');
            return sb.ToString();
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
                    else sb.Append(e);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string LoadDeviceId()
        {
            string dir = Path.Combine(@"C:\ProgramData\LingWangRemote", "config");
            string path = Path.Combine(dir, "device-id.txt");
            try
            {
                if (File.Exists(path))
                {
                    string saved = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (saved.Length > 0) return saved;
                }
            }
            catch { }

            string id = "LW-" + Block(4) + "-" + Block(4);
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, id, Encoding.UTF8);
            }
            catch
            {
                // ProgramData 不可写时退到当前用户目录
                try
                {
                    string local = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LingWangRemote");
                    Directory.CreateDirectory(local);
                    path = Path.Combine(local, "device-id.txt");
                    if (File.Exists(path))
                    {
                        string saved = File.ReadAllText(path, Encoding.UTF8).Trim();
                        if (saved.Length > 0) return saved;
                    }
                    File.WriteAllText(path, id, Encoding.UTF8);
                }
                catch { }
            }
            return id;
        }

        static string Block(int len)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append(chars[rnd.Next(chars.Length)]);
            return sb.ToString();
        }
    }
}
