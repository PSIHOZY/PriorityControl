using Microsoft.Win32;
using System;
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
            return IsScheduledTaskEnabled() || IsRunKeyEnabled();
        }

        public void SetEnabled(bool enabled, string executablePath)
        {
            if (enabled)
            {
                CreateScheduledTask(executablePath);
                RemoveRunKey();
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

            CreateScheduledTask(executablePath);
            RemoveRunKey();
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

        private static void RemoveRunKey()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.DeleteValue(ValueName, false);
            }
        }

        private static bool IsScheduledTaskEnabled()
        {
            return RunSchtasks("/Query /TN \"" + TaskName + "\"") == 0;
        }

        private static void CreateScheduledTask(string executablePath)
        {
            string taskCommand = "\\\"" + executablePath + "\\\" --startup";
            string arguments =
                "/Create /SC ONLOGON /TN \"" + TaskName + "\" /TR \"" +
                taskCommand +
                "\" /RL HIGHEST /F";

            int exitCode = RunSchtasks(arguments);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    "Failed to create elevated startup task. schtasks exit code: " + exitCode);
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
                CreateNoWindow = true
            };

            try
            {
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return -1;
                    }

                    if (!process.WaitForExit(5000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // best effort
                        }

                        return -1;
                    }

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
