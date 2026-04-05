using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;

namespace PriorityControl.Services
{
    internal sealed class ElevationService
    {
        public bool IsAdministrator
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }

        public bool TryRestartElevated(string[] args, out string error)
        {
            error = null;
            string[] safeArgs = args ?? new string[0];
            string argumentLine = BuildArguments(safeArgs);

            var startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = argumentLine,
                UseShellExecute = true,
                Verb = "runas"
            };

            try
            {
                Process.Start(startInfo);
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    error = "UAC request canceled.";
                }
                else
                {
                    error = "Failed to restart elevated: " + ex.Message;
                }

                return false;
            }
            catch (Exception ex)
            {
                error = "Failed to restart elevated: " + ex.Message;
                return false;
            }
        }

        private static string BuildArguments(string[] args)
        {
            return string.Join(" ", args.Select(QuoteArgument));
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return value.Contains(" ")
                ? "\"" + value.Replace("\"", "\\\"") + "\""
                : value;
        }
    }
}
