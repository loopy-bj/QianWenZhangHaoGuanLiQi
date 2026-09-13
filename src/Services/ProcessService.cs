using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace QianwenSwitcher.Services
{
    /// <summary>千问进程树的枚举、优雅关闭、文件句柄等待与重启</summary>
    public class ProcessService
    {
        private readonly string _installRoot;

        public ProcessService(string qianwenExePath)
        {
            _installRoot = GetInstallRoot(qianwenExePath);
        }

        private static string GetInstallRoot(string exePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(exePath))
                {
                    return Path.GetDirectoryName(exePath).TrimEnd('\\');
                }
            }
            catch { }
            return string.Empty;
        }

        public bool IsQianwenRunning()
        {
            return FindProcesses().Count > 0;
        }

        // 看门狗/更新器等无窗口辅助进程名（路径在千问生态目录下时也会按路径命中）
        private static readonly string[] WatchdogNames =
        {
            "RestartAgent", "agent_host", "agent_node_launcher",
            "qwen-command-runner", "notification_helper", "cua"
        };

        public List<Process> FindProcesses()
        {
            List<Process> result = new List<Process>();
            Process[] all;
            try
            {
                all = Process.GetProcesses();
            }
            catch
            {
                return result;
            }

            string updaterRoot;
            try
            {
                updaterRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianwenUpdater");
            }
            catch { updaterRoot = null; }

            foreach (Process p in all)
            {
                try
                {
                    string n = p.ProcessName ?? "";
                    // 永远排除切换器自身
                    if (n.IndexOf("switcher", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    bool nameMatch =
                        string.Equals(n, "qianwen", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("qianwen_", StringComparison.OrdinalIgnoreCase) ||
                        Array.IndexOf(WatchdogNames, n) >= 0;

                    string path = SafeGetPath(p);
                    bool match;
                    if (string.IsNullOrEmpty(path))
                    {
                        match = nameMatch;
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(path) ?? "";
                        bool underInstall = !string.IsNullOrEmpty(_installRoot) &&
                            dir.IndexOf(_installRoot, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool underUpdater = !string.IsNullOrEmpty(updaterRoot) &&
                            dir.IndexOf(updaterRoot, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            string.Equals(n, "updater", StringComparison.OrdinalIgnoreCase);
                        match = nameMatch || underInstall || underUpdater;
                    }
                    if (match) result.Add(p);
                }
                catch { }
            }
            return result;
        }

        private static string SafeGetPath(Process p)
        {
            // 优先用 MainModule（同位数时最快）
            try
            {
                if (p.MainModule != null && !string.IsNullOrEmpty(p.MainModule.FileName))
                    return p.MainModule.FileName;
            }
            catch { }

            // MainModule 失败（位数不同/权限不足）时，用 WMI 兜底
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + p.Id))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string path = mo["ExecutablePath"] as string;
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// 优雅关闭千问：先强杀看门狗/重启器（防止其重新拉起主进程），
        /// 再对有窗口的主进程发关闭消息，超时后强杀；
        /// 最后多轮短促清扫，抓住在崩溃-重启循环中被拉起的残留进程。
        /// </summary>
        public void StopAll(Action<string> report)
        {
            // 看门狗/重启器/助手进程：没有窗口、会重新拉起千问，必须最先直接强杀
            HashSet<string> watchdogs = new HashSet<string>(new[]
            {
                "RestartAgent", "agent_host", "agent_node_launcher",
                "qwen-command-runner", "notification_helper", "cua"
            }, StringComparer.OrdinalIgnoreCase);

            // 阶段 1：优雅关闭主进程 + 强杀看门狗
            for (int round = 0; round < 5; round++)
            {
                List<Process> procs = FindProcesses();
                if (procs.Count == 0) break;

                if (report != null) report("正在关闭千问（第 " + (round + 1) + " 轮，剩余 " + procs.Count + " 个进程）…");
                Logger.Info("停止千问进程，第 " + (round + 1) + " 轮: " +
                    string.Join(", ", procs.Select(p => p.ProcessName).ToArray()));

                List<Process> windowed = new List<Process>();
                foreach (Process p in procs)
                {
                    bool isDog = false;
                    try { isDog = watchdogs.Contains(p.ProcessName); }
                    catch { }

                    if (isDog)
                    {
                        // 看门狗立即强杀，不等它拉起新进程
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                    else
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                p.CloseMainWindow();
                                windowed.Add(p);
                            }
                        }
                        catch { }
                    }
                }

                if (!WaitAllExit(windowed, 4000))
                {
                    foreach (Process p in windowed)
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                    WaitAllExit(windowed, 5000);
                }

                // 强杀本轮所有未退出的剩余进程（无窗口的渲染/GPU 子进程等）
                foreach (Process p in procs)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
                foreach (Process p in procs)
                {
                    try { p.Dispose(); } catch { }
                }
                Thread.Sleep(800);
            }

            // 阶段 2：短促清扫。千问崩溃后可能被看门狗重新拉起，
            // 看门狗已死的情况下新拉起的进程不会再有人重启，这里抓掉即可。
            for (int i = 0; i < 12; i++)
            {
                List<Process> leftovers = FindProcesses();
                if (leftovers.Count == 0) break;
                Logger.Info("检测到重新拉起/残留进程，第 " + (i + 1) + " 次强杀: " +
                    string.Join(", ", leftovers.Select(p => p.ProcessName).ToArray()));
                foreach (Process p in leftovers)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
                Thread.Sleep(500);
            }
        }

        /// <summary>
        /// 轻量强杀：枚举一次并立即 Kill 全部匹配进程，供还原删除目录失败时快速解除占用。
        /// </summary>
        public void ForceKillAll()
        {
            List<Process> procs = FindProcesses();
            if (procs.Count == 0) return;
            Logger.Info("删除被占用，强制清除进程: " +
                string.Join(", ", procs.Select(p => p.ProcessName).ToArray()));
            foreach (Process p in procs)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            Thread.Sleep(300);
        }

        // ----------------------------------------------------------------
        // Restart Manager：精确找出打开某目录下文件句柄的进程并查杀。
        // 不依赖进程名匹配，任何被千问生态进程占用的文件都能解除锁定。
        // ----------------------------------------------------------------
        public void KillLockers(string targetPath)
        {
            try
            {
                List<string> files = new List<string>();
                if (Directory.Exists(targetPath))
                {
                    files.AddRange(Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories));
                }
                else if (File.Exists(targetPath))
                {
                    files.Add(targetPath);
                }
                if (files.Count == 0) return;

                List<int> pids = RestartManagerLocker.WhoLocks(files);
                if (pids.Count == 0) return;

                foreach (int pid in pids)
                {
                    string name = "?";
                    bool ecosystem = false;
                    try
                    {
                        Process lp = Process.GetProcessById(pid);
                        name = lp.ProcessName;
                        ecosystem = IsEcosystemProcess(lp);
                        // 系统进程/资源管理器绝不强杀；非生态进程（如搜索索引器）只记录，
                        // 它们的占用是瞬时的，靠重试即可成功。
                        if (!ecosystem)
                        {
                            Logger.Warn("文件被非千问进程占用，等待其自行释放: " + pid + "/" + name);
                            continue;
                        }
                        string parent = TryGetParentDescription(pid);
                        Logger.Warn("Restart Manager 发现占用进程，强杀: " + pid + "/" + name +
                            (string.IsNullOrEmpty(parent) ? "" : "（父进程: " + parent + "）"));
                        try { if (!lp.HasExited) lp.Kill(); } catch { }
                    }
                    catch { }
                }
                Thread.Sleep(400);
            }
            catch (Exception ex)
            {
                Logger.Warn("KillLockers 异常: " + ex.Message);
            }
        }

        /// <summary>判断进程是否属于千问生态（按安装目录、更新器目录或进程名）</summary>
        private bool IsEcosystemProcess(Process p)
        {
            try
            {
                string n = p.ProcessName ?? "";
                if (n.IndexOf("switcher", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (string.Equals(n, "qianwen", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("qianwen_", StringComparison.OrdinalIgnoreCase) ||
                    Array.IndexOf(WatchdogNames, n) >= 0)
                    return true;

                string path = SafeGetPath(p);
                if (string.IsNullOrEmpty(path)) return false;
                string dir = Path.GetDirectoryName(path) ?? "";
                if (!string.IsNullOrEmpty(_installRoot) &&
                    dir.IndexOf(_installRoot, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string updaterRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianwenUpdater");
                if (dir.IndexOf(updaterRoot, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    string.Equals(n, "updater", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static string TryGetParentDescription(int pid)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object o = mo["ParentProcessId"];
                        if (o == null) return null;
                        int ppid = Convert.ToInt32(o);
                        try
                        {
                            Process pp = Process.GetProcessById(ppid);
                            return ppid + "/" + pp.ProcessName;
                        }
                        catch { return ppid + "/(已退出)"; }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 启动还原守护：后台每 250ms 清扫一次千问生态进程，
        /// 防止杀进程后被看门狗/更新器/崩溃恢复机制重新拉起，
        /// 在快照创建与目录替换的整个窗口期内保持数据目录无人占用。
        /// Dispose 后停止（必须在重新启动千问之前释放）。
        /// </summary>
        public RestoreGuard StartRestoreGuard()
        {
            return new RestoreGuard(this);
        }

        private static bool WaitAllExit(List<Process> procs, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                bool alive = false;
                foreach (Process p in procs)
                {
                    try { if (!p.HasExited) { alive = true; break; } }
                    catch { }
                }
                if (!alive) return true;
                Thread.Sleep(200);
                waited += 200;
            }
            return false;
        }

        /// <summary>
        /// 轮询等待关键文件句柄被释放。除了 LOCK/Cookies/Local State，
        /// 还会扫描所有受管 leveldb 目录下的 LOCK 文件及 IndexedDB 数据文件，
        /// 避免某个子库未释放就开始还原。
        /// </summary>
        public bool WaitFilesReleased(string userDataPath, Action<string> report)
        {
            // 第一轮：先等已知的关键文件
            string[] known = new string[]
            {
                Path.Combine(userDataPath, @"Default\Local Storage\leveldb\LOCK"),
                Path.Combine(userDataPath, @"Default\Network\Cookies"),
                Path.Combine(userDataPath, "Local State")
            };

            List<string> probeFiles = new List<string>(known);

            // 收集 User Data 下所有 LOCK 文件（leveldb 会在各子目录放 LOCK 文件）
            try
            {
                string[] lockFiles = Directory.GetFiles(userDataPath, "LOCK",
                    SearchOption.AllDirectories);
                foreach (string lf in lockFiles)
                {
                    if (!probeFiles.Contains(lf, StringComparer.OrdinalIgnoreCase))
                        probeFiles.Add(lf);
                }
            }
            catch { }

            // 收集 IndexedDB / Local Storage 下的 .ldb/.log 数据文件（最多取 10 个做探测），
            // 因为这些文件被锁时 LOCK 可能已释放但实际数据文件仍被占用
            try
            {
                string[] dataExts = { "*.ldb", "*.log" };
                string[] probeDirs =
                {
                    Path.Combine(userDataPath, @"Default\Local Storage\leveldb"),
                    Path.Combine(userDataPath, @"Default\IndexedDB")
                };
                int added = 0;
                foreach (string pd in probeDirs)
                {
                    if (!Directory.Exists(pd)) continue;
                    foreach (string ext in dataExts)
                    {
                        try
                        {
                            string[] fs = Directory.GetFiles(pd, ext, SearchOption.AllDirectories);
                            foreach (string f in fs)
                            {
                                if (!probeFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                                {
                                    probeFiles.Add(f);
                                    if (++added >= 10) break;
                                }
                            }
                        }
                        catch { }
                        if (added >= 10) break;
                    }
                    if (added >= 10) break;
                }
            }
            catch { }

            for (int i = 0; i < 80; i++) // 最多 20 秒
            {
                bool allFree = true;
                foreach (string file in probeFiles)
                {
                    if (!File.Exists(file)) continue;
                    FileStream fs = null;
                    try
                    {
                        fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch
                    {
                        allFree = false;
                    }
                    finally
                    {
                        if (fs != null) fs.Dispose();
                    }
                    if (!allFree) break;
                }
                if (allFree)
                {
                    if (i > 2 && report != null) report("文件句柄已释放");
                    return true;
                }
                Thread.Sleep(250);
            }
            return false;
        }

        public bool Start(string exePath, string workingDir)
        {
            try
            {
                if (!File.Exists(exePath))
                {
                    Logger.Warn("千问 exe 不存在: " + exePath);
                    return false;
                }
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.WorkingDirectory = string.IsNullOrEmpty(workingDir)
                    ? Path.GetDirectoryName(exePath) : workingDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
                Logger.Info("已启动千问: " + exePath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("启动千问失败", ex);
                return false;
            }
        }
    }

    /// <summary>还原窗口期的后台进程守护：周期性强杀任何千问生态进程</summary>
    public sealed class RestoreGuard : IDisposable
    {
        private readonly ProcessService _svc;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private readonly HashSet<int> _reported = new HashSet<int>();
        private readonly object _gate = new object();
        private int _killCount;

        public RestoreGuard(ProcessService svc)
        {
            _svc = svc;
            _thread = new Thread(Loop) { IsBackground = true, Name = "RestoreGuard" };
            _thread.Start();
            Logger.Info("还原守护已启动（250ms 周期清扫）");
        }

        public int KillCount { get { lock (_gate) { return _killCount; } } }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    List<Process> procs = _svc.FindProcesses();
                    if (procs.Count > 0)
                    {
                        lock (_gate) { _killCount += procs.Count; }
                        foreach (Process p in procs)
                        {
                            try
                            {
                                int pid = p.Id;
                                bool first;
                                lock (_gate) { first = _reported.Add(pid); }
                                if (first)
                                {
                                    string parent = SafeParent(pid);
                                    Logger.Warn("还原窗口期发现进程复活，强杀: " + pid + "/" + p.ProcessName +
                                        (string.IsNullOrEmpty(parent) ? "" : "（父进程: " + parent + "）"));
                                }
                            }
                            catch { }
                            try { if (!p.HasExited) p.Kill(); } catch { }
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
                Thread.Sleep(250);
            }
        }

        private static string SafeParent(int pid)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object o = mo["ParentProcessId"];
                        if (o == null) return null;
                        int ppid = Convert.ToInt32(o);
                        try { return ppid + "/" + Process.GetProcessById(ppid).ProcessName; }
                        catch { return ppid + "/(已退出)"; }
                    }
                }
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            _running = false;
            try { if (_thread != null) _thread.Join(2000); } catch { }
            Logger.Info("还原守护已停止，累计强杀 " + KillCount + " 个复活进程");
        }
    }

    /// <summary>Restart Manager (rstrtmgr.dll) 封装：返回占用指定文件的进程 PID 列表</summary>
    internal static class RestartManagerLocker
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, IntPtr rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
            ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        public static List<int> WhoLocks(List<string> files)
        {
            List<int> result = new List<int>();
            uint handle;
            string key = Guid.NewGuid().ToString();
            if (RmStartSession(out handle, 0, key) != 0) return result;
            try
            {
                if (RmRegisterResources(handle, (uint)files.Count, files.ToArray(),
                    0, IntPtr.Zero, 0, null) != 0) return result;

                uint needed = 0, count = 0, reason = 0;
                int rv = RmGetList(handle, out needed, ref count, null, ref reason);
                if (needed == 0) return result;

                RM_PROCESS_INFO[] infos = new RM_PROCESS_INFO[needed];
                count = needed;
                rv = RmGetList(handle, out needed, ref count, infos, ref reason);
                if (rv != 0) return result;

                for (uint i = 0; i < count; i++)
                {
                    result.Add(infos[i].Process.dwProcessId);
                }
            }
            finally
            {
                RmEndSession(handle);
            }
            return result;
        }
    }
}
