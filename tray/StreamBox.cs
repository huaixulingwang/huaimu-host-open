using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace LingWangRemote
{
    /// <summary>
    /// 电脑端与「自有串流实例」之间的本地小助手。
    ///
    /// 自有实例（sunshine-lw）跑在 48989 端口段，网页面板在 https://127.0.0.1:48990。
    /// 这里只用它的本地接口做一件事：生成一次性配对码（相当于自动帮用户在网页上填 PIN），
    /// 让平板可以全自动完成配对，用户不需要打开任何网页。
    ///
    /// 网页账号口令不写死在代码里，由安装脚本生成后放在实例配置目录的 stream-web.ini。
    /// </summary>
    static class StreamBox
    {
        static readonly object initLock = new object();
        static string userName = "";
        static string password = "";
        static bool loaded;

        const string WebBase = "https://127.0.0.1:48990";

        /// <summary>{安装目录}\sunshine-lw\config\stream-web.ini</summary>
        static string CredentialPath()
        {
            try
            {
                string trayDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string appDir = Path.GetDirectoryName(trayDir);
                if (!string.IsNullOrEmpty(appDir))
                {
                    return Path.Combine(appDir, "sunshine-lw", "config", "stream-web.ini");
                }
            }
            catch { }
            return "";
        }

        static void EnsureLoaded()
        {
            if (loaded) return;
            lock (initLock)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    string path = CredentialPath();
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();
                        if (key == "username") userName = val;
                        else if (key == "password") password = val;
                    }
                }
                catch { }
            }
        }

        public static bool Ready
        {
            get
            {
                EnsureLoaded();
                return userName.Length > 0 && password.Length > 0;
            }
        }

        class WebReply
        {
            public bool Ok;
            public string Body = "";
        }

        static WebReply Post(string path, string json)
        {
            var reply = new WebReply();
            EnsureLoaded();
            if (!Ready) return reply;

            using (var handler = new HttpClientHandler())
            {
                // 只对本机实例放行自签证书；这个回调只作用于本类创建的客户端，
                // 不影响账号中心等其它 HTTPS 请求。
                handler.ServerCertificateCustomValidationCallback =
                    (msg, cert, chain, errors) => true;
                handler.UseProxy = false;
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(8);
                    using (var req = new HttpRequestMessage(HttpMethod.Post, WebBase + path))
                    {
                        string basic = Convert.ToBase64String(
                            Encoding.UTF8.GetBytes(userName + ":" + password));
                        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                        req.Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
                        using (var resp = client.SendAsync(req).GetAwaiter().GetResult())
                        {
                            reply.Body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            reply.Ok = resp.IsSuccessStatusCode;
                        }
                    }
                }
            }
            return reply;
        }

        /// <summary>让实例生成一次性配对码，成功返回 4 位 PIN，失败返回空串</summary>
        public static string BeginPair()
        {
            try
            {
                WebReply reply = Post("/api/qr-pair", "{}");
                if (!reply.Ok) return "";
                return ExtractString(reply.Body, "pin");
            }
            catch
            {
                return "";
            }
        }

        /// <summary>配对结束后回收一次性配对码</summary>
        public static void CancelPair()
        {
            try
            {
                Post("/api/qr-pair/cancel", "{}");
            }
            catch { }
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
                    sb.Append(json[k]);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
