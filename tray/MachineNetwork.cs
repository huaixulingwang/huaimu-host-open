using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LingWangRemote
{
    /// <summary>
    /// 本机的组网身份（网络名 + 网络密码）。
    ///
    /// 以前这两个值是构建安装包时写死的，等于把"家门钥匙"随安装包发出去：
    /// 任何人拿到安装包就能加入同一个虚拟网络。现在改成：
    ///   - 每台电脑首次运行时随机生成一套，只存在本机；
    ///   - 平板配对时（同账号 或 输入控制码通过后）由校验口把这两个值下发给平板；
    ///   - 安装包里不再包含任何固定密码。
    ///
    /// 登录账号时仍优先用按账号派生的一套（多台电脑共用同一账号网络），
    /// 这里只负责"没登录 / 首次配对"这一层的身份。
    /// </summary>
    static class MachineNetwork
    {
        const string DataDir = @"C:\ProgramData\LingWangRemote";

        static readonly object gate = new object();
        static string cachedName = "";
        static string cachedSecret = "";

        static string FilePath
        {
            get { return Path.Combine(DataDir, "network.json"); }
        }

        public static string Name
        {
            get { Ensure(); return cachedName; }
        }

        public static string Secret
        {
            get { Ensure(); return cachedSecret; }
        }

        /// <summary>下发给平板用的 JSON 片段（不含本机虚拟 IP）</summary>
        public static string Json()
        {
            Ensure();
            if (cachedName.Length == 0 || cachedSecret.Length == 0) return "";
            return "{\"name\":" + Quote(cachedName) + ",\"secret\":" + Quote(cachedSecret) + "}";
        }

        /// <summary>
        /// 当前实际使用的组网身份：
        /// 已登录账号 → 按账号派生（多台电脑共用同一账号网络，与平板端算法一致）；
        /// 未登录 → 本机随机生成的那一套。
        /// </summary>
        public static string EffectiveJson(string accountId)
        {
            if (!string.IsNullOrEmpty(accountId))
            {
                return "{\"name\":" + Quote(DeriveName(accountId))
                    + ",\"secret\":" + Quote(DeriveSecret(accountId)) + "}";
            }
            return Json();
        }

        /// <summary>账号 ID → 网络名（算法必须与平板端 NetworkIdentity.ets 完全一致）</summary>
        public static string DeriveName(string uid)
        {
            string b = "lw-net-v1|" + (uid == null ? "" : uid);
            return "lw-" + Hex32(Hash32(b + "|n1", 5381)) + Hex32(Hash32(b + "|n2", 52711));
        }

        /// <summary>账号 ID → 网络密码</summary>
        public static string DeriveSecret(string uid)
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

        static void Ensure()
        {
            if (cachedName.Length > 0 && cachedSecret.Length > 0) return;
            lock (gate)
            {
                if (cachedName.Length > 0 && cachedSecret.Length > 0) return;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        string text = File.ReadAllText(FilePath, Encoding.UTF8);
                        string name = ReadField(text, "name");
                        string secret = ReadField(text, "secret");
                        if (name.Length > 0 && secret.Length > 0)
                        {
                            cachedName = name;
                            cachedSecret = secret;
                            return;
                        }
                    }
                }
                catch { }

                string newName = "lw-" + Hex(8);
                string newSecret = Hex(16);
                try
                {
                    Directory.CreateDirectory(DataDir);
                    string json = "{\"name\":\"" + newName + "\",\"secret\":\"" + newSecret + "\"}";
                    File.WriteAllText(FilePath, json, Encoding.UTF8);
                }
                catch
                {
                    // 写不进磁盘也不能让组网整个挂掉：本次进程内先用着这套值
                }
                cachedName = newName;
                cachedSecret = newSecret;
            }
        }

        static string Hex(int bytes)
        {
            byte[] buffer = new byte[bytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            StringBuilder sb = new StringBuilder(buffer.Length * 2);
            for (int i = 0; i < buffer.Length; i++) sb.Append(buffer[i].ToString("x2"));
            return sb.ToString();
        }

        static string ReadField(string json, string key)
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
                if (json[k] == '"') break;
                if (json[k] == '\\' && k + 1 < json.Length) { k++; sb.Append(json[k]); continue; }
                sb.Append(json[k]);
            }
            return sb.ToString();
        }

        static string Quote(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
