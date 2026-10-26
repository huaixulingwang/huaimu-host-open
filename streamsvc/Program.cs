using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace LingWangStream
{
    /// <summary>
    /// OpenRemoteDesk专用串流服务。
    /// 独占一套 Sunshine 实例（目录 ..\sunshine-lw），端口段 48984/48989/48990/49010，
    /// 与社区版 Sunshine（47984/47989/47990/48010）完全隔离。
    ///
    /// 唯一职责：以 System 身份把 Sunshine.exe 拉进当前登录桌面会话，并看住它。
    /// 不复用社区版的服务外壳，避免两套实例互相干扰。
    /// </summary>
    class StreamService : ServiceBase
    {
        const string SvcName = "LingWangStreamService";
        static readonly string Root = ResolveRoot();
        static readonly string LogDir = @"C:\ProgramData\LingWangRemote\logs";
        static readonly string LogPath = Path.Combine(LogDir, "stream-service.log");

        static readonly object LogLock = new object();
        static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);

        Thread worker;
        Thread gateThread;
        IntPtr childHandle = IntPtr.Zero;
        int childPid;

        public StreamService()
        {
            ServiceName = SvcName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        /// <summary>服务程序放在 &lt;根&gt;\streamsvc\ 下，实例根目录固定为 &lt;根&gt;\sunshine-lw</summary>
        static string ResolveRoot()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string baseDir = Path.GetDirectoryName(exeDir);
                return Path.Combine(baseDir, "sunshine-lw");
            }
            catch
            {
                return @"E:\LingWangRemote\LingWangRemote\sunshine-lw";
            }
        }

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg;
                lock (LogLock)
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    {
                        File.Copy(LogPath, LogPath + ".1", true);
                        File.Delete(LogPath);
                    }
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch { }
        }

        protected override void OnStart(string[] args)
        {
            Log("=== service start, root=" + Root + " ===");
            StopEvent.Reset();

            // 校验口必须由服务托管：开机后、用户还没登录桌面时托盘并不在运行，
            // 平板只能靠这里拿到"这是哪台电脑、能不能连"的回答。
            try { LingWangRemote.AuthGate.EnsureStarted(); } catch (Exception ex) { Log("校验口启动失败: " + ex.Message); }

            gateThread = new Thread(GateLoop);
            gateThread.IsBackground = true;
            gateThread.Start();

            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>每 5 秒补挂一次校验口：开机时虚拟网卡通常比服务晚就绪</summary>
        void GateLoop()
        {
            while (!StopEvent.WaitOne(5000))
            {
                try
                {
                    if (!LingWangRemote.AuthGate.AllBound)
                    {
                        LingWangRemote.AuthGate.EnsureStarted();
                        if (LingWangRemote.AuthGate.AllBound)
                        {
                            Log("校验口已就绪（回环 + 虚拟网卡）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("校验口补挂失败: " + ex.Message);
                }
            }
        }

        protected override void OnStop()
        {
            Log("=== service stop requested ===");
            StopEvent.Set();
            try { LingWangRemote.AuthGate.Stop(); } catch { }
            KillChild("stop");
            try { if (worker != null) worker.Join(5000); } catch { }
        }

        protected override void OnShutdown()
        {
            OnStop();
        }

        void WorkerLoop()
        {
            int fastFailures = 0;

            while (!StopEvent.WaitOne(0))
            {
                if (!Directory.Exists(Root))
                {
                    Log("实例目录不存在: " + Root + "，10 秒后重试");
                    if (StopEvent.WaitOne(10000)) break;
                    continue;
                }

                uint session = Native.WTSGetActiveConsoleSessionId();
                if (session == 0xFFFFFFFF)
                {
                    Log("当前没有已登录的控制台会话，10 秒后重试");
                    if (StopEvent.WaitOne(10000)) break;
                    continue;
                }

                var sw = Stopwatch.StartNew();
                bool started = StartChild(session);
                if (!started)
                {
                    if (StopEvent.WaitOne(5000)) break;
                    continue;
                }

                Log("sunshine.exe 已启动 pid=" + childPid + " session=" + session);
                WaitForChild();

                sw.Stop();
                int code = 0;
                try { code = LastExitCode; } catch { }
                Log("sunshine.exe 退出 pid=" + childPid + " code=" + code + " 运行时长=" + (int)sw.Elapsed.TotalSeconds + "s");

                if (StopEvent.WaitOne(0)) break;   // 主动停止，不再拉起

                // 秒退视为异常，连续 3 次后停止重试，避免死循环刷日志
                if (sw.Elapsed.TotalSeconds < 60)
                {
                    fastFailures++;
                    if (fastFailures > 3)
                    {
                        Log("连续快速退出 " + (fastFailures - 1) + " 次，停止自动拉起；请检查实例配置");
                        break;
                    }
                }
                else
                {
                    fastFailures = 0;
                }

                if (StopEvent.WaitOne(5000)) break;
            }

            Log("worker 结束");
        }

        int LastExitCode;

        bool StartChild(uint session)
        {
            IntPtr hProcToken = IntPtr.Zero;
            IntPtr hDup = IntPtr.Zero;
            IntPtr env = IntPtr.Zero;
            try
            {
                // 服务本身以 LocalSystem 运行：复制自己的令牌并切到目标会话，
                // 这样 Sunshine 才能在登录桌面里有效做事（虚拟屏/采集都需要管理权限）。
                uint access = Native.TOKEN_QUERY | Native.TOKEN_DUPLICATE | Native.TOKEN_ASSIGN_PRIMARY;
                if (!Native.OpenProcessToken(Native.GetCurrentProcess(), access, out hProcToken))
                {
                    Log("OpenProcessToken 失败: " + Marshal.GetLastWin32Error());
                    return false;
                }

                if (!Native.DuplicateTokenEx(hProcToken, Native.MAXIMUM_ALLOWED, IntPtr.Zero,
                        Native.SecurityImpersonation, Native.TokenPrimary, out hDup))
                {
                    Log("DuplicateTokenEx 失败: " + Marshal.GetLastWin32Error());
                    return false;
                }

                uint sid = session;
                if (!Native.SetTokenInformation(hDup, Native.TokenSessionId, ref sid, 4))
                {
                    Log("SetTokenInformation(TokenSessionId) 失败: " + Marshal.GetLastWin32Error());
                    return false;
                }

                Native.CreateEnvironmentBlock(out env, hDup, false);

                var si = new Native.STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(Native.STARTUPINFO));
                si.lpDesktop = @"winsta0\default";
                // 引擎必须完全隐身：不建控制台窗口，也不让窗口闪一下
                si.dwFlags = Native.STARTF_USESHOWWINDOW;
                si.wShowWindow = Native.SW_HIDE;

                string exePath = Path.Combine(Root, "Sunshine.exe");
                string cmdLine = "\"" + exePath + "\"";
                const uint flags = Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW;

                Native.PROCESS_INFORMATION pi;
                bool ok = Native.CreateProcessAsUser(hDup, exePath, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false, flags, env, Root, ref si, out pi);
                if (!ok)
                {
                    Log("CreateProcessAsUser 失败: " + Marshal.GetLastWin32Error());
                    return false;
                }

                childHandle = pi.hProcess;
                childPid = (int)pi.dwProcessId;
                Native.CloseHandle(pi.hThread);
                return true;
            }
            catch (Exception ex)
            {
                Log("StartChild 异常: " + ex.Message);
                return false;
            }
            finally
            {
                if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
                if (hDup != IntPtr.Zero) Native.CloseHandle(hDup);
                if (hProcToken != IntPtr.Zero) Native.CloseHandle(hProcToken);
            }
        }

        void WaitForChild()
        {
            if (childHandle == IntPtr.Zero) return;

            // 分片等待，保证服务收到停止指令时能立刻响应
            while (!StopEvent.WaitOne(1000))
            {
                uint r = Native.WaitForSingleObject(childHandle, 0);
                if (r == Native.WAIT_OBJECT_0) break;
            }

            uint code;
            if (Native.GetExitCodeProcess(childHandle, out code)) LastExitCode = (int)code;
            Native.CloseHandle(childHandle);
            childHandle = IntPtr.Zero;
        }

        void KillChild(string reason)
        {
            int pid = childPid;
            if (pid <= 0) return;

            Log("结束 sunshine.exe pid=" + pid + " (" + reason + ")");
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(8000); }
            }
            catch (Exception ex)
            {
                Log("taskkill 失败: " + ex.Message);
                try { if (childHandle != IntPtr.Zero) Native.TerminateProcess(childHandle, 0); } catch { }
            }
            childPid = 0;
        }

        /// <summary>排查用：前台运行（需管理员），回车后退出</summary>
        public void RunConsole()
        {
            Console.WriteLine("实例目录 = " + Root);
            OnStart(new string[0]);
            Console.WriteLine("已拉起，按回车停止…");
            Console.ReadLine();
            OnStop();
        }
    }

    static class Native
    {
        public const uint TOKEN_QUERY = 0x0008;
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        public const uint MAXIMUM_ALLOWED = 0x02000000;
        public const int SecurityImpersonation = 2;
        public const int TokenPrimary = 1;
        public const int TokenSessionId = 12;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const int STARTF_USESHOWWINDOW = 0x00000001;
        public const short SW_HIDE = 0;
        public const uint WAIT_OBJECT_0 = 0x00000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            ref uint tokenInformation, uint tokenInformationLength);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
    }

    static class Program
    {
        static void Main(string[] args)
        {
            if (Environment.UserInteractive)
            {
                // 排查用前台模式：第一次运行前请确认没有串流在进行
                new StreamService().RunConsole();
                return;
            }
            ServiceBase.Run(new StreamService());
        }
    }
}
