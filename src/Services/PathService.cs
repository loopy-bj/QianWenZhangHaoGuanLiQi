using System;
using System.IO;

namespace QianwenSwitcher.Services
{
    /// <summary>集中管理本软件自身的所有路径（全部在 exe 同目录，保证绿色便携、不写 C 盘）</summary>
    public class PathService
    {
        public string AppDir { get; private set; }
        public string ConfigPath { get; private set; }
        public string ProfilesDir { get; private set; }
        public string SafetyDir { get; private set; }
        public string StagingDir { get; private set; }
        public string LogsDir { get; private set; }
        public string ExePath { get; private set; }

        public PathService()
        {
            ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            // 以 exe 实际所在目录为准（被外部宿主加载时 BaseDirectory 会指向宿主目录）
            AppDir = Path.GetDirectoryName(ExePath);
            if (string.IsNullOrEmpty(AppDir))
            {
                AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            }
            ConfigPath = Path.Combine(AppDir, "config.json");
            ProfilesDir = Path.Combine(AppDir, "profiles");
            SafetyDir = Path.Combine(ProfilesDir, "_safety");
            StagingDir = Path.Combine(ProfilesDir, "_staging");
            LogsDir = Path.Combine(AppDir, "logs");
        }

        public void EnsureDirs()
        {
            Directory.CreateDirectory(AppDir);
            Directory.CreateDirectory(ProfilesDir);
            Directory.CreateDirectory(SafetyDir);
            Directory.CreateDirectory(StagingDir);
            Directory.CreateDirectory(LogsDir);
        }

        /// <summary>启动时清理上次异常残留的暂存目录</summary>
        public void CleanStaging()
        {
            try
            {
                if (Directory.Exists(StagingDir))
                {
                    Directory.Delete(StagingDir, true);
                }
                Directory.CreateDirectory(StagingDir);
            }
            catch (Exception ex)
            {
                Logger.Warn("清理暂存目录失败: " + ex.Message);
            }
        }

        /// <summary>推断千问默认数据目录：%LOCALAPPDATA%\qianwen\User Data</summary>
        public static string DefaultUserDataPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "qianwen", "User Data");
        }

        /// <summary>推断千问 exe 的常见安装位置</summary>
        public static string GuessQianwenExe()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianwenApp", "qianwen.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "QianwenApp", "qianwen.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "QianwenApp", "qianwen.exe")
            };
            foreach (string p in candidates)
            {
                if (File.Exists(p)) return p;
            }
            return candidates[0];
        }

        public string NewStagingFolder()
        {
            string dir = Path.Combine(StagingDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
