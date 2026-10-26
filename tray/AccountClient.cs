using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LingWangRemote
{
    /// <summary>
    /// 统一账号中心客户端（电脑端）。
    /// 只依赖 .NET Framework 自带组件，同步请求，由调用方放到后台线程执行。
    /// 访问令牌 / 续期令牌写 Windows 凭据管理器，账号资料写 ProgramData 的 json。
    /// </summary>
    static class AccountClient
    {
        public const string EnvId = "your-env-id";
        public const string ApiBase = "https://your-env-id.api.tcloudbasegateway.com";

        const string CredAccess = "LingWangRemote/access_token";
        const string CredRefresh = "LingWangRemote/refresh_token";

        public static string AccessToken { get; private set; }
        public static string AccountId { get; private set; }   // 登录返回的 sub
        public static string DisplayName { get; private set; }

        static DateTime expiresAt = DateTime.MinValue;
        static readonly object gate = new object();

        public static bool LoggedIn
        {
            get { return !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(AccountId); }
        }

        public static string MetaPath
        {
            get
            {
                // 托盘多数时候以普通用户身份运行，ProgramData 未必可写，放当前用户目录
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LingWangRemote");
                return Path.Combine(dir, "account.json");
            }
        }

        static AccountClient()
        {
            // .NET Framework 4.0 默认不启用 TLS 1.2，CloudBase 网关要求
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            }
            catch { }
        }

        // ── 登录 / 续期 ──────────────────────────────────

        /// <summary>手机号规范成 "+86 13800138000"，账号中心只认这个格式</summary>
        public static string NormalizePhone(string input)
        {
            if (input == null) return "";
            string trimmed = input.Trim();
            string compact = trimmed.Replace(" ", "");
            if (compact.StartsWith("+")) return trimmed;
            if (compact.Length == 11 && compact.StartsWith("1")) return "+86 " + compact;
            return compact;
        }

        public static string LoginPassword(string username, string password)
        {
            string json = "{ \"username\": " + Q(NormalizePhone(username))
                        + ", \"password\": " + Q(password) + " }";
            string body = PostJson("/auth/v1/signin?client_id=" + EnvId, json, null);
            ApplyLogin(body);
            return "";
        }

        /// <summary>服务端错误码 → 中文提示（不显示英文原文）</summary>
        public static string ExplainError(string body)
        {
            string code = ExtractString(body, "error_code");
            if (code == "4043") return "账号或密码不正确";
            if (code == "4029") return "这个账号还没有设置密码，请先用手机号验证码登录，再到账号中心设置密码";
            if (code == "4018" || code == "4028" || code == "4041" || code == "4042")
                return "验证码不正确或已过期，请重新获取";
            if (code == "429") return "操作太频繁，请稍后再试";
            return "操作失败，请稍后再试";
        }

        /// <summary>发送验证码的结果</summary>
        public class SendCodeResult
        {
            public string Error = "";
            public string VerificationId = "";
            public bool IsUser = true;
        }

        /// <summary>手机号首次登录即注册：先发验证码，再校验并按新/老用户登录</summary>
        public static SendCodeResult SendCode(string phone11)
        {
            var result = new SendCodeResult();
            try
            {
                string json = "{ \"phone_number\": " + Q(NormalizePhone(phone11)) + " }";
                string body = PostJson("/auth/v1/verification?client_id=" + EnvId, json, null);
                result.VerificationId = ExtractString(body, "verification_id");
                result.IsUser = ExtractString(body, "is_user") != "false";
                if (result.VerificationId.Length == 0)
                {
                    result.Error = "账号服务没有返回验证码标识，请稍后再试";
                }
            }
            catch (AccountApiException ex)
            {
                result.Error = ExplainError(ex.Body);
            }
            catch
            {
                result.Error = "网络连接失败，请检查网络后重试";
            }
            return result;
        }

        /// <summary>校验验证码并完成登录 / 注册，返回 "" 表示成功</summary>
        public static string VerifyAndLogin(string phone11, string verificationId, string code, string nickname, bool isUser)
        {
            try
            {
                string vt = VerifyCode(verificationId, code);
                string phone = NormalizePhone(phone11);
                string body;
                if (isUser)
                {
                    body = PostJson("/auth/v1/signin?client_id=" + EnvId,
                        "{ \"username\": " + Q(phone)
                        + ", \"verification_token\": " + Q(vt) + " }", null);
                }
                else
                {
                    string name = string.IsNullOrEmpty(nickname) ? "远控用户" : nickname;
                    body = PostJson("/auth/v1/signup?client_id=" + EnvId,
                        "{ \"phone_number\": " + Q(phone)
                        + ", \"verification_code\": " + Q(code)
                        + ", \"verification_token\": " + Q(vt)
                        + ", \"name\": " + Q(name) + " }", null);
                }
                ApplyLogin(body);
                return "";
            }
            catch (AccountApiException ex)
            {
                return ExplainError(ex.Body);
            }
            catch
            {
                return "网络连接失败，请检查网络后重试";
            }
        }

        static string VerifyCode(string verificationId, string code)
        {
            string body = PostJson("/auth/v1/verification/verify?client_id=" + EnvId,
                "{ \"verification_id\": " + Q(verificationId)
                + ", \"verification_code\": " + Q(code) + " }", null);
            string vt = ExtractString(body, "verification_token");
            if (vt.Length == 0)
            {
                throw new AccountApiException(200, "{ \"error_code\": \"4018\" }");
            }
            return vt;
        }

        /// <summary>返回 "" 表示成功，否则是错误提示</summary>
        public static string TryLoginPassword(string username, string password)
        {
            try
            {
                LoginPassword(username, password);
                return "";
            }
            catch (AccountApiException ex)
            {
                return ExplainError(ex.Body);
            }
            catch (Exception)
            {
                return "网络连接失败，请检查网络后重试";
            }
        }

        /// <summary>检查本地保存的登录态；必要时续期。返回是否仍登录</summary>
        public static bool Restore()
        {
            lock (gate)
            {
                if (AccessToken != null) return LoggedIn;
                string meta = ReadMeta();
                if (meta.Length == 0) return false;
                AccountId = ExtractString(meta, "accountId");
                DisplayName = ExtractString(meta, "displayName");
                string exp = ExtractString(meta, "expiresAt");
                long ticks;
                if (long.TryParse(exp, out ticks))
                {
                    try { expiresAt = new DateTime(ticks); } catch { expiresAt = DateTime.MinValue; }
                }
                AccessToken = CredReadSecret(CredAccess);
                if (LoggedIn)
                {
                    // 恢复登录态时同步刷新机器级账号缓存：服务靠它在无人登录时校验对方
                    WriteMachineAccount();
                    RegisterDeviceAsync();
                }
                return LoggedIn;
            }
        }

        /// <summary>令牌快过期或遇到 401 时续期。必须在后台线程调用。</summary>
        public static void RefreshIfNeeded()
        {
            lock (gate)
            {
                if (!LoggedIn) return;
                if (expiresAt > DateTime.Now.AddMinutes(10)) return;
                DoRefresh();
            }
        }

        static void DoRefresh()
        {
            string refresh = CredReadSecret(CredRefresh);
            if (refresh.Length == 0) { Clear(); return; }
            string json = "{ \"grant_type\": \"refresh_token\", \"refresh_token\": " + Q(refresh) + " }";
            try
            {
                string body = PostJson("/auth/v1/token?client_id=" + EnvId, json, null);
                ApplyLogin(body);
            }
            catch (AccountApiException)
            {
                Clear();
            }
        }

        static void ApplyLogin(string body)
        {
            string access = ExtractString(body, "access_token");
            string refresh = ExtractString(body, "refresh_token");
            string sub = ExtractString(body, "sub");
            string expires = ExtractString(body, "expires_in");
            int seconds = 7200;
            int.TryParse(expires, out seconds);
            if (seconds <= 0) seconds = 7200;

            AccessToken = access;
            AccountId = sub;
            expiresAt = DateTime.Now.AddSeconds(seconds);

            CredWriteSecret(CredAccess, access);
            if (refresh.Length > 0) CredWriteSecret(CredRefresh, refresh);

            // 昵称从 user/me 取，失败就先用账号 ID
            DisplayName = "";
            try
            {
                string me = GetJson("/auth/v1/user/me", access);
                string name = ExtractString(me, "name");
                if (name.Length == 0) name = ExtractString(me, "username");
                if (name.Length == 0) name = sub;
                DisplayName = name;
            }
            catch { DisplayName = sub; }

            WriteMeta();
            WriteMachineAccount();
            RegisterDeviceAsync();
        }

        public static void Logout()
        {
            Clear();
        }

        static void Clear()
        {
            AccessToken = null;
            AccountId = null;
            DisplayName = null;
            expiresAt = DateTime.MinValue;
            CredDeleteSecret(CredAccess);
            CredDeleteSecret(CredRefresh);
            try { if (File.Exists(MetaPath)) File.Delete(MetaPath); } catch { }
            ClearMachineAccount();
        }

        /// <summary>
        /// 机器级账号缓存：开机后、用户登录桌面前，串流服务需要它来判断"对方是不是同一个人"。
        /// 只存账号标识和昵称，不含任何令牌，退出登录即删除。
        /// </summary>
        public static readonly string MachineAccountPath = @"C:\ProgramData\LingWangRemote\account.txt";

        static void WriteMachineAccount()
        {
            try
            {
                string dir = Path.GetDirectoryName(MachineAccountPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(MachineAccountPath,
                    (AccountId == null ? "" : AccountId) + "\t" + (DisplayName == null ? "" : DisplayName),
                    Encoding.UTF8);
            }
            catch { }
        }

        static void ClearMachineAccount()
        {
            try { if (File.Exists(MachineAccountPath)) File.Delete(MachineAccountPath); } catch { }
        }

        // ── 设备名单 ─────────────────────────────────────

        /// <summary>登录成功后把本机登记到账号的设备名单（后台执行，失败不影响连接）</summary>
        public static void RegisterDeviceAsync()
        {
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { RegisterDevice(); } catch { }
                });
            }
            catch { }
        }

        static void RegisterDevice()
        {
            string token, uid;
            lock (gate)
            {
                token = AccessToken;
                uid = AccountId;
            }
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uid)) return;

            // 按 (owner_id, device_id) 幂等写入：先删后插，不依赖 on_conflict
            string rowPath = "/v1/rdb/rest/devices?owner_id=eq." + Uri.EscapeDataString(uid)
                           + "&device_id=eq." + Uri.EscapeDataString(AuthGate.DeviceId);
            try { Request("DELETE", rowPath, null, token); } catch { }

            string json = "{"
                + "\"owner_id\":" + Q(uid) + ","
                + "\"device_id\":" + Q(AuthGate.DeviceId) + ","
                + "\"device_name\":" + Q(Environment.MachineName) + ","
                + "\"device_type\":\"pc\","
                + "\"product_code\":\"remote-control\","
                + "\"app_version\":" + Q(AppVersion()) + ","
                + "\"sunshine_id\":" + Q(ReadSunshineUniqueId()) + ","
                + "\"virtual_ip\":" + Q(HostAddress.Current) + ","
                + "\"last_seen_at\":" + Q(DateTime.UtcNow.ToString("o"))
                + "}";
            PostJson("/v1/rdb/rest/devices", json, token);
        }

        /// <summary>账号下的设备名单（原始 JSON，交界面解析）</summary>
        public static string ListDevices()
        {
            string token;
            lock (gate) { token = AccessToken; }
            if (string.IsNullOrEmpty(token)) return "[]";
            return GetJson("/v1/rdb/rest/devices?select=id,device_id,device_name,device_type,last_seen_at,sunshine_id,virtual_ip&order=last_seen_at.desc", token);
        }

        /// <summary>从账号里删除一台设备（只删名单记录，不影响该设备本机登录）</summary>
        public static string DeleteDevice(string deviceId)
        {
            string token, uid;
            lock (gate)
            {
                token = AccessToken;
                uid = AccountId;
            }
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uid)) return "请先登录";
            string path = "/v1/rdb/rest/devices?owner_id=eq." + Uri.EscapeDataString(uid)
                        + "&device_id=eq." + Uri.EscapeDataString(deviceId ?? "");
            Request("DELETE", path, null, token);
            return "";
        }

        static string AppVersion()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : v.ToString(3);
            }
            catch { return ""; }
        }

        /// <summary>本机 Sunshine 的唯一标识（/serverinfo 免登录可读），供平板识别同一台电脑</summary>
        static string ReadSunshineUniqueId()
        {
            try
            {
                // 仍然读社区实例的 uniqueid：这是这台电脑在账号里的稳定身份，
                // 换到新实例会让历史设备记录对不上，和串流端口无关。
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:47989/serverinfo");
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                req.UserAgent = "LingWangRemote/0.2";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string xml = sr.ReadToEnd();
                    int a = xml.IndexOf("<uniqueid>", StringComparison.OrdinalIgnoreCase);
                    if (a < 0) return "";
                    a += "<uniqueid>".Length;
                    int b = xml.IndexOf("</uniqueid>", a, StringComparison.OrdinalIgnoreCase);
                    if (b < 0) return "";
                    return xml.Substring(a, b - a).Trim();
                }
            }
            catch { return ""; }
        }

        // ── 校验（供本地校验口调用）────────────────────

        /// <summary>
        /// 校验平板带来的令牌是否属于本机当前登录的账号。
        /// 用 /auth/v1/user/me 验证令牌真实有效，并取出它自己的 sub。
        /// 不会改动电脑端本地的登录态。
        /// </summary>
        public static string VerifyPeerToken(string peerToken)
        {
            if (!LoggedIn) return "这台电脑还没有登录账号";
            return VerifyPeerTokenFor(peerToken, AccountId);
        }

        /// <summary>
        /// 用指定的本机账号标识校验对方凭证。
        /// 串流服务以 System 身份运行、看不到当前用户的登录态，此时改读机器级账号文件。
        /// </summary>
        public static string VerifyPeerTokenFor(string peerToken, string localUid)
        {
            if (peerToken == null || peerToken.Length == 0) return "没有携带登录凭证";
            if (string.IsNullOrEmpty(localUid)) return "这台电脑还没有登录账号";

            // 本地解析 JWT：不访问账号中心，电脑端可以毫秒级响应
            string payload = DecodeJwtPayload(peerToken);
            if (payload.Length == 0) return "登录凭证无效";

            string peerId = ExtractString(payload, "sub");
            if (peerId.Length == 0) return "登录凭证无效";

            long exp = ExtractJwtNumber(payload, "exp");
            if (exp > 0 && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60)
            {
                return "登录凭证已过期，请重新登录";
            }
            if (peerId != localUid)
            {
                return "对方的账号与这台电脑绑定的账号不一致";
            }
            return "";
        }

        /// <summary>解出 JWT 的 payload（Base64Url → JSON 文本），失败返回空串</summary>
        static string DecodeJwtPayload(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return "";
                string seg = parts[1].Replace('-', '+').Replace('_', '/');
                switch (seg.Length % 4)
                {
                    case 2: seg += "=="; break;
                    case 3: seg += "="; break;
                }
                return Encoding.UTF8.GetString(Convert.FromBase64String(seg));
            }
            catch { return ""; }
        }

        /// <summary>从 JSON 文本里取一个数字字段（如 exp），取不到返回 0</summary>
        static long ExtractJwtNumber(string json, string key)
        {
            string token = "\"" + key + "\"";
            int i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += token.Length;
            while (i < json.Length && (json[i] == ':' || json[i] == ' ')) i++;
            long value = 0;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9')
            {
                value = value * 10 + (json[i] - '0');
                i++;
            }
            return value;
        }

        // ── HTTP ─────────────────────────────────────────

        public class AccountApiException : Exception
        {
            public int StatusCode;
            public string Body;
            public AccountApiException(int status, string body) : base("HTTP " + status)
            {
                StatusCode = status;
                Body = body;
            }
        }

        static string PostJson(string path, string json, string token)
        {
            return Request("POST", path, json, token);
        }

        static string GetJson(string path, string token)
        {
            return Request("GET", path, null, token);
        }

        static string Request(string method, string path, string json, string token)
        {
            string url = path.StartsWith("http") ? path : ApiBase + path;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.ContentType = "application/json";
            req.UserAgent = "LingWangRemote/0.2";
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers["Authorization"] = "Bearer " + token;
            }

            if (json != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
                using (resp)
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    throw new AccountApiException((int)resp.StatusCode, sr.ReadToEnd());
                }
            }
        }

        // ── 极简 JSON 读取（只处理已知扁平字段）─────────

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
                            string hex = json.Substring(k + 1, 4);
                            int code;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
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
            // 数字 / 布尔
            int start = p;
            while (p < json.Length && (char.IsDigit(json[p]) || json[p] == '-' || json[p] == '.')) p++;
            if (p > start) return json.Substring(start, p - start);
            return "";
        }

        /// <summary>JSON 字符串转义，避免额外引用 System.Web.Extensions</summary>
        static string Q(string s)
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

        // ── 本地元数据 ───────────────────────────────────

        static string ReadMeta()
        {
            try
            {
                if (!File.Exists(MetaPath)) return "";
                return File.ReadAllText(MetaPath, Encoding.UTF8);
            }
            catch { return ""; }
        }

        static void WriteMeta()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MetaPath));
                string json = "{ \"accountId\": " + Q(AccountId)
                            + ", \"displayName\": " + Q(DisplayName)
                            + ", \"expiresAt\": " + Q(expiresAt.Ticks.ToString()) + " }";
                File.WriteAllText(MetaPath, json, Encoding.UTF8);
            }
            catch { }
        }

        // ── Windows 凭据管理器 ───────────────────────────

        const uint CRED_TYPE_GENERIC = 1;
        const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr cred);

        static void CredWriteSecret(string target, string secret)
        {
            byte[] blob = Encoding.UTF8.GetBytes(secret == null ? "" : secret);
            if (blob.Length == 0) return;
            IntPtr ptr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, ptr, blob.Length);
                var cred = new CREDENTIAL();
                cred.Type = CRED_TYPE_GENERIC;
                cred.TargetName = target;
                cred.UserName = "LingWangRemote";
                cred.CredentialBlobSize = (uint)blob.Length;
                cred.CredentialBlob = ptr;
                cred.Persist = CRED_PERSIST_LOCAL_MACHINE;
                CredWrite(ref cred, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        static string CredReadSecret(string target)
        {
            IntPtr ptr;
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out ptr)) return "";
            try
            {
                var cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
                byte[] blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                CredFree(ptr);
            }
        }

        static void CredDeleteSecret(string target)
        {
            try { CredDelete(target, CRED_TYPE_GENERIC, 0); } catch { }
        }
    }
}
