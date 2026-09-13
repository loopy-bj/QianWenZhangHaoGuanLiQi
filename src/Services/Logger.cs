using System;
using System.IO;
using System.Text;

namespace QianwenSwitcher.Services
{
    /// <summary>简单的线程安全文件日志，按天存放在 logs 目录</summary>
    public static class Logger
    {
        private static readonly object Gate = new object();
        private static string _logDir = string.Empty;

        public static void Init(string logDir)
        {
            _logDir = logDir;
            try { Directory.CreateDirectory(logDir); } catch { }
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Warn(string message)
        {
            Write("WARN ", message);
        }

        public static void Error(string message, Exception ex)
        {
            string text = message;
            if (ex != null)
            {
                text = text + " -> " + ex.GetType().Name + ": " + ex.Message
                    + Environment.NewLine + ex.StackTrace;
            }
            Write("ERROR", text);
        }

        private static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(_logDir)) return;
            try
            {
                lock (Gate)
                {
                    string file = Path.Combine(_logDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] "
                        + message + Environment.NewLine;
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
