using System;
using Microsoft.Win32;

namespace QianwenSwitcher.Services
{
    /// <summary>通过 HKCU Run 项设置/取消开机自启（仅当前用户，不需要管理员权限）</summary>
    public class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "QianwenSwitcher";
        private readonly string _exePath;

        public AutoStartService(string exePath)
        {
            _exePath = exePath;
        }

        public bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    if (v == null) return false;
                    string s = v.ToString();
                    return s.Length > 0 && s.IndexOf(_exePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取开机自启状态失败: " + ex.Message);
                return false;
            }
        }

        public bool Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        key.SetValue(ValueName, "\"" + _exePath + "\" --minimized");
                        Logger.Info("已开启开机自启");
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                        Logger.Info("已关闭开机自启");
                    }
                }
                return IsEnabled() == enabled;
            }
            catch (Exception ex)
            {
                Logger.Error("设置开机自启失败", ex);
                return false;
            }
        }
    }
}
