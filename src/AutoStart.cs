using System;
using System.Reflection;
using Microsoft.Win32;

namespace SnipTranslate
{
    /// <summary>
    /// 开机自启。写的是当前用户的 Run 键，不需要管理员权限：
    ///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run
    ///
    /// 状态以注册表为准（不额外存配置），这样你在「任务管理器 → 启动」
    /// 里手动关掉之后，托盘菜单的勾也会跟着消失。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SnipTranslate";

        public static string ExePath
        {
            get
            {
                try
                {
                    string p = Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrEmpty(p)) return p;
                }
                catch { }
                return System.Windows.Forms.Application.ExecutablePath;
            }
        }

        /// <summary>是否已设置自启，并且指向的就是当前这个 exe。</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    {
                        if (k == null) return false;
                        string v = k.GetValue(ValueName) as string;
                        if (string.IsNullOrEmpty(v)) return false;
                        // exe 被挪过位置的话，旧路径也算「没设好」
                        return v.Trim().Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>打开自启。返回 null 表示成功，否则是错误描述。</summary>
        public static string Enable()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return "打不开注册表 Run 键";
                    k.SetValue(ValueName, "\"" + ExePath + "\"", RegistryValueKind.String);
                }
                Log.Info("已开启开机自启: " + ExePath);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("开启开机自启失败: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>关闭自启。返回 null 表示成功。</summary>
        public static string Disable()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null && k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                Log.Info("已关闭开机自启");
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("关闭开机自启失败: " + ex.Message);
                return ex.Message;
            }
        }
    }
}
