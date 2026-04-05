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
            if (IsScheduledTaskEnabled())
            {
                return true;
            }

            return IsRunKeyEnabled();
        }

        public void SetEnabled(bool enabled, string executablePath)
        {
            if (enabled)
            {
                if (TryCreateOrUpdateScheduledTask(executablePath))
                {
                    RemoveRunKey();
                    return;
                }

                SetRunKey(executablePath);
                return;
            }

            RemoveRunKey();
            DeleteScheduledTask();
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

        private static bool IsScheduledTaskEnabled()
        {
            int exitCode = RunSchtasks("/Query /TN \"" + TaskName + "\"");
            return exitCode == 0;
        }

        private static bool TryCreateOrUpdateScheduledTask(string executablePath)
        {
            string action = "\\\"" + executablePath + "\\\" --startup";

            string argsHighest =
                "/Create /TN \"" +
                TaskName +
                "\" /TR \"" +
                action +
                "\" /SC ONLOGON /RL HIGHEST /F";

            if (RunSchtasks(argsHighest) == 0)
            {
                return true;
            }

            string argsLimited =
                "/Create /TN \"" +
                TaskName +
                "\" /TR \"" +
                action +
                "\" /SC ONLOGON /RL LIMITED /F";

            return RunSchtasks(argsLimited) == 0;
        }

        private static void DeleteScheduledTask()
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

                    process.WaitForExit(10000);
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
