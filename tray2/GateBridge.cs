using System;
using System.IO;
using System.Net;
using System.Text;

namespace LingWangRemote
{
    /// <summary>
    /// 托盘的校验口代理。
    ///
    /// 校验口（47999）已经交给串流服务托管：开机后、用户还没登录桌面时托盘并不在运行，
    /// 平板必须能直接问服务"这是哪台电脑、能不能连"。托盘因此不再自己监听这个端口，
    /// 只通过本机管理接口读取/生成控制码，避免两个进程抢同一个端口。
    /// </summary>
    static class GateBridge
    {
        const string Base = "http://127.0.0.1:47999";

        /// <summary>校验口是否在服务里跑起来（托盘状态灯用）</summary>
        public static bool Running { get; private set; }

        /// <summary>当前有效的临时控制码，没有则为空串</summary>
        public static string Code { get; private set; }

        /// <summary>控制码剩余分钟数</summary>
        public static int Minutes { get; private set; }

        static GateBridge()
        {
            Code = "";
            Minutes = 0;
        }

        /// <summary>刷新一次状态；服务没起来时返回"未运行"，不抛异常</summary>
        public static void Refresh()
        {
            try
            {
                string status = Request("GET", "/v1/admin/status", null);
                Running = status.IndexOf("\"running\":true", StringComparison.Ordinal) >= 0;
                if (!Running)
                {
                    Code = "";
                    Minutes = 0;
                    return;
                }
                string body = Request("GET", "/v1/admin/code", null);
                Code = Str(body, "code");
                Minutes = Int(body, "minutes");
            }
            catch
            {
                Running = false;
                Code = "";
                Minutes = 0;
            }
        }

        /// <summary>让服务生成一个新的控制码，返回生成的码（失败返回空串）</summary>
        public static string Generate(int minutes)
        {
            try
            {
                string body = Request("POST", "/v1/admin/code", "{\"minutes\":" + minutes + "}");
                Code = Str(body, "code");
                Minutes = Int(body, "minutes");
                Running = true;
                return Code;
            }
            catch
            {
                return "";
            }
        }

        static string Request(string method, string path, string body)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Base + path);
            req.Method = method;
            req.Timeout = 1500;
            req.ReadWriteTimeout = 1500;
            req.Proxy = null;
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream())
                {
                    s.Write(data, 0, data.Length);
                }
            }
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        static string Str(string json, string key)
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
                if (json[k] == '\\' && k + 1 < json.Length) { k++; sb.Append(json[k]); continue; }
                if (json[k] == '"') break;
                sb.Append(json[k]);
            }
            return sb.ToString();
        }

        static int Int(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            string pattern = "\"" + key + "\"";
            int i = json.IndexOf(pattern, StringComparison.Ordinal);
            if (i < 0) return 0;
            int colon = json.IndexOf(':', i + pattern.Length);
            if (colon < 0) return 0;
            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            int start = p;
            while (p < json.Length && char.IsDigit(json[p])) p++;
            int value;
            return (p > start && int.TryParse(json.Substring(start, p - start), out value)) ? value : 0;
        }
    }
}
