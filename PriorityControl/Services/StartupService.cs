using Microsoft.Win32;
using System.Diagnostics;

namespace PriorityControl.Services
{
    internal sealed class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PriorityControl";
        private const string TaskName = "PriorityControl Startup";

        public bool IsEnabled()
        {
            return IsRunKeyEnabled();
        }

        public void SetEnabled(bool enabled, string executablePath)
        {
            if (enabled)
            {
                SetRunKey(executablePath);
                DeleteScheduledTaskBestEffort();
                return;
            }

            RemoveRunKey();
            DeleteScheduledTaskBestEffort();
        }

        public void RefreshExecutablePathIfEnabled(string executablePath)
        {
            if (!IsEnabled())
            {
                return;
            }

            SetRunKey(executablePath);
        }

        private static bool IsRunKeyEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                string value = null;
                if (key != null)
                {
                    value = key.GetValue(ValueName) as string;
                }

                return !string.IsNullOrWhiteSpace(value);
            }
        }

        private static void SetRunKey(string executablePath)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.SetValue(ValueName, "\"" + executablePath + "\" --startup");
            }
        }

        private static void RemoveRunKey()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.DeleteValue(ValueName, false);
            }
        }

        private static void DeleteScheduledTaskBestEffort()
        {
            RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
        }

        private static int RunSchtasks(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return -1;
                    }

                    process.WaitForExit(5000);
                    return process.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }
    }
}
