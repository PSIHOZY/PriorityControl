using System;
using System.Runtime.InteropServices;
using PriorityControl.Models;
using PriorityControl.Native;

namespace PriorityControl.Services
{
    internal sealed class JobObjectService
    {
        private static readonly uint ExtendedLimitInfoSize =
            (uint)Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

        public bool ApplyPriorityLimit(SafeJobHandle jobHandle, FixedPriority priority, out string error)
        {
            error = null;

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_PRIORITY_CLASS;
            info.BasicLimitInformation.PriorityClass = PriorityMapper.ToNative(priority);

            // This call enables JOB_OBJECT_LIMIT_PRIORITY_CLASS and locks process priority.
            bool ok = NativeMethods.SetInformationJobObject(
                jobHandle,
                NativeMethods.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ref info,
                ExtendedLimitInfoSize);

            if (!ok)
            {
                error = "SetInformationJobObject failed: Win32 " + Marshal.GetLastWin32Error();
            }

            return ok;
        }

        public bool RemovePriorityLimit(SafeJobHandle jobHandle, out string error)
        {
            error = null;

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();

            // LimitFlags = 0 removes previously set limits (including priority lock)
            // while process remains assigned to the same Job Object.
            bool ok = NativeMethods.SetInformationJobObject(
                jobHandle,
                NativeMethods.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ref info,
                ExtendedLimitInfoSize);

            if (!ok)
            {
                error = "Failed to remove Job Object limit: Win32 " + Marshal.GetLastWin32Error();
            }

            return ok;
        }

        public bool AssignProcess(SafeJobHandle jobHandle, IntPtr processHandle, out string error)
        {
            error = null;
            bool ok = NativeMethods.AssignProcessToJobObject(jobHandle, processHandle);
            if (!ok)
            {
                error = "AssignProcessToJobObject failed: Win32 " + Marshal.GetLastWin32Error();
            }

            return ok;
        }

        public bool TryReadPriorityLimit(
            SafeJobHandle jobHandle,
            out uint priorityClass,
            out bool limitEnabled,
            out string error)
        {
            priorityClass = 0;
            limitEnabled = false;
            error = null;

            NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
            bool ok = NativeMethods.QueryInformationJobObject(
                jobHandle,
                NativeMethods.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                out info,
                ExtendedLimitInfoSize,
                IntPtr.Zero);

            if (!ok)
            {
                error = "QueryInformationJobObject failed: Win32 " + Marshal.GetLastWin32Error();
                return false;
            }

            priorityClass = info.BasicLimitInformation.PriorityClass;
            limitEnabled = (info.BasicLimitInformation.LimitFlags & NativeMethods.JOB_OBJECT_LIMIT_PRIORITY_CLASS) != 0;
            return true;
        }
    }
}
