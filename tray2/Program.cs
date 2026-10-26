using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LingWangRemote
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            // 提权后的网络切换子进程：必须在抢单实例锁之前执行，
            // 否则会因为主托盘已占着锁而立刻退出。
            if (args != null && args.Length >= 2 && args[0] == "--apply-network")
            {
                Environment.Exit(NetworkProvision.ApplyFromFile(args[1]));
                return;
            }

            // 高分屏下必须声明 DPI 感知，否则窗口会被系统按比例缩小，网页视口不够
            try
            {
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\LingWangTrayMutex", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try { AccountClient.Restore(); } catch { }
                // 校验口（47999）由串流服务托管，托盘不再自己监听，避免抢端口
                try { GateBridge.Refresh(); } catch { }
                // 组网参数核对放在主窗体里做：连不上自建节点时要由窗体弹窗询问用户

                Application.Run(new MainForm());
            }
        }
    }
}
