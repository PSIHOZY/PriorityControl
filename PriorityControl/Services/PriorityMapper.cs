using System;
using PriorityControl.Models;
using PriorityControl.Native;

namespace PriorityControl.Services
{
    internal static class PriorityMapper
    {
        public static uint ToNative(FixedPriority priority)
        {
            switch (priority)
            {
                case FixedPriority.Realtime:
                    return NativeMethods.REALTIME_PRIORITY_CLASS;
                case FixedPriority.High:
                    return NativeMethods.HIGH_PRIORITY_CLASS;
                case FixedPriority.AboveNormal:
                    return NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS;
                case FixedPriority.Normal:
                    return NativeMethods.NORMAL_PRIORITY_CLASS;
                case FixedPriority.BelowNormal:
                    return NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;
                case FixedPriority.Idle:
                    return NativeMethods.IDLE_PRIORITY_CLASS;
                default:
                    throw new ArgumentOutOfRangeException("priority", priority, "Unknown priority.");
            }
        }

        public static string ToDisplay(uint nativePriorityClass)
        {
            switch (nativePriorityClass)
            {
                case NativeMethods.REALTIME_PRIORITY_CLASS:
                    return "Realtime";
                case NativeMethods.HIGH_PRIORITY_CLASS:
                    return "High";
                case NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS:
                    return "AboveNormal";
                case NativeMethods.NORMAL_PRIORITY_CLASS:
                    return "Normal";
                case NativeMethods.BELOW_NORMAL_PRIORITY_CLASS:
                    return "BelowNormal";
                case NativeMethods.IDLE_PRIORITY_CLASS:
                    return "Idle";
                default:
                    return "Unknown";
            }
        }
    }
}
