using System;
using System.IO;

namespace LingWangRemote
{
    /// <summary>
    /// 本机在 EasyTier 虚拟网络里的固定 IP。
    /// 安装时由 install-post 写入 C:\ProgramData\LingWangRemote\virtual-ip.txt，
    /// 每台电脑一个不同地址（10.126.126.4 起），避免同一账号多台电脑撞地址。
    /// 老安装没有这个文件时沿用 10.126.126.1，保持既有配对和连接记录可用。
    /// </summary>
    static class HostAddress
    {
        public const string LegacyIp = "10.126.126.1";
        public static readonly string StorePath = @"C:\ProgramData\LingWangRemote\virtual-ip.txt";

        static string cached;

        public static string Current
        {
            get
            {
                if (cached != null) return cached;
                cached = LegacyIp;
                try
                {
                    if (File.Exists(StorePath))
                    {
                        string text = File.ReadAllText(StorePath).Trim();
                        if (IsValid(text)) cached = text;
                    }
                }
                catch { }
                return cached;
            }
        }

        /// <summary>
        /// 10.126.126.1 或 10.126.126.4~250。
        /// .2 留给外部 EasyTier App，.3 留给平板，电脑端不能占。
        /// </summary>
        public static bool IsValid(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            const string prefix = "10.126.126.";
            if (!ip.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int last;
            if (!int.TryParse(ip.Substring(prefix.Length), out last)) return false;
            if (last == 2 || last == 3) return false;
            return last >= 1 && last <= 250;
        }
    }
}
