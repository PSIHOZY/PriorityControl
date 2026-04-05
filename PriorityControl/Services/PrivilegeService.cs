using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PriorityControl.Native;

namespace PriorityControl.Services
{
    internal sealed class PrivilegeService
    {
        private const string SeIncreaseBasePriorityPrivilege = "SeIncreaseBasePriorityPrivilege";

        private bool _checkedIncreaseBasePriority;
        private bool _hasIncreaseBasePriorityPrivilege;
        private string _increaseBasePriorityError;

        public bool EnsureIncreaseBasePriorityPrivilege(out string error)
        {
            if (_checkedIncreaseBasePriority)
            {
                error = _increaseBasePriorityError;
                return _hasIncreaseBasePriorityPrivilege;
            }

            _checkedIncreaseBasePriority = true;
            _hasIncreaseBasePriorityPrivilege =
                TryEnablePrivilege(SeIncreaseBasePriorityPrivilege, out _increaseBasePriorityError);

            error = _increaseBasePriorityError;
            return _hasIncreaseBasePriorityPrivilege;
        }

        private static bool TryEnablePrivilege(string privilegeName, out string error)
        {
            error = null;
            IntPtr tokenHandle = IntPtr.Zero;

            using (Process currentProcess = Process.GetCurrentProcess())
            {
                if (!NativeMethods.OpenProcessToken(
                    currentProcess.Handle,
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                    out tokenHandle))
                {
                    error = "OpenProcessToken failed: Win32 " + Marshal.GetLastWin32Error();
                    return false;
                }
            }

            try
            {
                NativeMethods.LUID luid;
                if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out luid))
                {
                    error = "LookupPrivilegeValue failed: Win32 " + Marshal.GetLastWin32Error();
                    return false;
                }

                var tokenPrivileges = new NativeMethods.TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                };

                if (!NativeMethods.AdjustTokenPrivileges(
                    tokenHandle,
                    false,
                    ref tokenPrivileges,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    error = "AdjustTokenPrivileges failed: Win32 " + Marshal.GetLastWin32Error();
                    return false;
                }

                int lastError = Marshal.GetLastWin32Error();
                if (lastError == NativeMethods.ERROR_NOT_ALL_ASSIGNED)
                {
                    error =
                        "Current account does not have 'Increase scheduling priority' right " +
                        "(SeIncreaseBasePriorityPrivilege).";
                    return false;
                }

                return true;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(tokenHandle);
                }
            }
        }
    }
}
