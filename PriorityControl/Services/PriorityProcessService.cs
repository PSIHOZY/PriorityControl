using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PriorityControl.Models;
using PriorityControl.Native;

namespace PriorityControl.Services
{
    internal sealed class PriorityProcessService : IDisposable
    {
        private sealed class RuntimeState : IDisposable
        {
            public RuntimeState(Process process, SafeJobHandle jobHandle, bool lockActive)
            {
                Process = process;
                JobHandle = jobHandle;
                IsLockActive = lockActive;
            }

            public Process Process { get; private set; }
            public SafeJobHandle JobHandle { get; private set; }
            public bool IsLockActive { get; set; }

            public void Dispose()
            {
                if (Process != null)
                {
                    Process.Dispose();
                }

                if (JobHandle != null)
                {
                    JobHandle.Dispose();
                }
            }
        }

        private sealed class ExternalProcessSnapshot
        {
            public int ProcessId;
            public string PriorityDisplay;
            public bool IsInAnyJob;
        }

        private readonly Dictionary<string, RuntimeState> _states =
            new Dictionary<string, RuntimeState>(StringComparer.OrdinalIgnoreCase);

        private readonly JobObjectService _jobObjectService = new JobObjectService();
        private readonly PrivilegeService _privilegeService = new PrivilegeService();

        public bool StartWithFixedPriority(AppEntry entry, out string error)
        {
            EnsureEntryId(entry);
            error = null;

            if (!EnsurePriorityPrivilege(entry.Priority, out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.ExePath) || !File.Exists(entry.ExePath))
            {
                error = "Executable file not found.";
                return false;
            }

            RuntimeState existingState;
            if (_states.TryGetValue(entry.Id, out existingState))
            {
                if (IsProcessAlive(existingState.Process))
                {
                    error = "Process is already running for this entry.";
                    return false;
                }

                CleanupState(entry.Id);
            }

            Process process = FindRunningProcessByPath(entry.ExePath);
            bool startedNew = false;

            if (process == null)
            {
                process = StartProcess(entry.ExePath, out error);
                if (process == null)
                {
                    return false;
                }

                startedNew = true;
            }

            if (!TryManageProcess(entry, process, true, out error))
            {
                if (startedNew)
                {
                    ShutdownProcessOnFailedStart(process);
                }
                else
                {
                    process.Dispose();
                }

                return false;
            }

            return true;
        }

        public bool RemovePriorityLock(AppEntry entry, out string error)
        {
            EnsureEntryId(entry);
            error = null;

            RuntimeState state;
            if (!TryGetAliveState(entry.Id, out state))
            {
                Process running = FindRunningProcessByPath(entry.ExePath);
                if (running != null)
                {
                    running.Dispose();
                    error = "Process is running but not managed by PriorityControl session.";
                    return false;
                }

                error = "Process is not running.";
                return false;
            }

            if (!_jobObjectService.RemovePriorityLimit(state.JobHandle, out error))
            {
                return false;
            }

            state.IsLockActive = false;
            entry.IsPriorityLocked = false;
            return true;
        }

        public bool ApplyPriorityLock(AppEntry entry, out string error)
        {
            EnsureEntryId(entry);
            error = null;

            if (!EnsurePriorityPrivilege(entry.Priority, out error))
            {
                return false;
            }

            RuntimeState state;
            if (!TryGetAliveState(entry.Id, out state))
            {
                if (!TryAttachToRunningProcess(entry, out state, out error))
                {
                    return false;
                }
            }

            if (!_jobObjectService.ApplyPriorityLimit(state.JobHandle, entry.Priority, out error))
            {
                return false;
            }

            TrySetProcessPriority(state.Process, entry.Priority, out error);
            state.IsLockActive = true;
            entry.IsPriorityLocked = true;
            return true;
        }

        public bool UpdateLockedPriority(AppEntry entry, out string error)
        {
            EnsureEntryId(entry);
            error = null;

            if (!EnsurePriorityPrivilege(entry.Priority, out error))
            {
                return false;
            }

            RuntimeState state;
            if (!TryGetAliveState(entry.Id, out state))
            {
                Process running = FindRunningProcessByPath(entry.ExePath);
                if (running != null)
                {
                    running.Dispose();
                    error = "Process is running but not managed. Click 'Apply priority lock' first.";
                    return false;
                }

                error = "Process is not running.";
                return false;
            }

            if (!state.IsLockActive)
            {
                error = "Priority lock is not active for this process.";
                return false;
            }

            if (!_jobObjectService.ApplyPriorityLimit(state.JobHandle, entry.Priority, out error))
            {
                return false;
            }

            TrySetProcessPriority(state.Process, entry.Priority, out error);
            return true;
        }

        public void RefreshStatus(AppEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            var list = new List<AppEntry>(1);
            list.Add(entry);
            RefreshStatuses(list);
        }

        public void RefreshStatuses(IList<AppEntry> entries)
        {
            if (entries == null)
            {
                return;
            }

            CleanupDeadStates();
            Dictionary<string, ExternalProcessSnapshot> external =
                BuildExternalProcessSnapshot(entries);

            foreach (AppEntry entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                EnsureEntryId(entry);

                RuntimeState state;
                if (TryGetAliveState(entry.Id, out state))
                {
                    UpdateManagedEntryStatus(entry, state);
                    continue;
                }

                string key = NormalizePath(entry.ExePath);
                ExternalProcessSnapshot snapshot;
                if (external.TryGetValue(key, out snapshot))
                {
                    entry.ProcessId = snapshot.ProcessId;
                    entry.IsPriorityLocked = false;

                    if (snapshot.IsInAnyJob)
                    {
                        entry.RuntimeStatus = "Running, external/in job (" + snapshot.PriorityDisplay + ")";
                    }
                    else
                    {
                        entry.RuntimeStatus = "Running, not locked (" + snapshot.PriorityDisplay + ")";
                    }
                }
                else
                {
                    SetNotRunning(entry);
                }
            }
        }

        public bool IsManaged(string entryId)
        {
            return !string.IsNullOrWhiteSpace(entryId) && _states.ContainsKey(entryId);
        }

        public void ReleaseEntry(AppEntry entry)
        {
            EnsureEntryId(entry);
            RuntimeState state;
            if (!_states.TryGetValue(entry.Id, out state))
            {
                return;
            }

            if (IsProcessAlive(state.Process) && state.IsLockActive)
            {
                string ignoredError;
                _jobObjectService.RemovePriorityLimit(state.JobHandle, out ignoredError);
            }

            CleanupState(entry.Id);
        }

        public void Dispose()
        {
            foreach (RuntimeState state in _states.Values)
            {
                if (state != null && IsProcessAlive(state.Process) && state.IsLockActive)
                {
                    string ignoredError;
                    _jobObjectService.RemovePriorityLimit(state.JobHandle, out ignoredError);
                }

                if (state != null)
                {
                    state.Dispose();
                }
            }

            _states.Clear();
        }

        private bool TryAttachToRunningProcess(AppEntry entry, out RuntimeState state, out string error)
        {
            state = null;
            error = null;

            Process process = FindRunningProcessByPath(entry.ExePath);
            if (process == null)
            {
                error = "Process is not running.";
                return false;
            }

            if (!TryManageProcess(entry, process, false, out error))
            {
                process.Dispose();
                return false;
            }

            if (!_states.TryGetValue(entry.Id, out state))
            {
                error = "Failed to track process state.";
                return false;
            }

            return true;
        }

        private bool TryManageProcess(AppEntry entry, Process process, bool applyInitialLock, out string error)
        {
            error = null;
            if (process == null)
            {
                error = "Process is not running.";
                return false;
            }

            string jobName = string.Concat("PriorityControl_", entry.Id, "_", process.Id);
            SafeJobHandle jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, jobName);
            if (jobHandle == null || jobHandle.IsInvalid)
            {
                error = "CreateJobObject failed.";
                return false;
            }

            if (applyInitialLock)
            {
                if (!_jobObjectService.ApplyPriorityLimit(jobHandle, entry.Priority, out error))
                {
                    jobHandle.Dispose();
                    return false;
                }
            }

            if (!_jobObjectService.AssignProcess(jobHandle, process.Handle, out error))
            {
                bool inAnyJob;
                if (TryIsProcessInAnyJob(process, out inAnyJob) && inAnyJob)
                {
                    error = "Process is already in another Job Object (cannot reassign).";
                }

                jobHandle.Dispose();
                return false;
            }

            if (applyInitialLock)
            {
                string priorityError;
                TrySetProcessPriority(process, entry.Priority, out priorityError);
            }

            CleanupState(entry.Id);
            _states[entry.Id] = new RuntimeState(process, jobHandle, applyInitialLock);

            entry.ProcessId = process.Id;
            entry.IsPriorityLocked = applyInitialLock;
            entry.RuntimeStatus = applyInitialLock ? "Running, locked" : "Running, not locked";
            return true;
        }

        private static Process StartProcess(string exePath, out string error)
        {
            error = null;
            try
            {
                string workingDirectory = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    workingDirectory = Environment.CurrentDirectory;
                }

                var startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory
                };

                Process process = Process.Start(startInfo);
                if (process == null)
                {
                    error = "Process was not started.";
                    return null;
                }

                return process;
            }
            catch (Exception ex)
            {
                error = "Failed to start process: " + ex.Message;
                return null;
            }
        }

        private Process FindRunningProcessByPath(string exePath)
        {
            string normalizedTarget = NormalizePath(exePath);
            if (string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return null;
            }

            string name = null;
            try
            {
                name = Path.GetFileNameWithoutExtension(normalizedTarget);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(name);
            }
            catch
            {
                return null;
            }

            Process matched = null;
            foreach (Process process in candidates)
            {
                bool keepProcess = false;
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    string processPath;
                    if (!TryGetProcessPath(process, out processPath))
                    {
                        continue;
                    }

                    if (string.Equals(
                        NormalizePath(processPath),
                        normalizedTarget,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        matched = process;
                        keepProcess = true;
                        break;
                    }
                }
                finally
                {
                    if (!keepProcess)
                    {
                        process.Dispose();
                    }
                }
            }

            if (matched == null)
            {
                return null;
            }

            return matched;
        }

        private Dictionary<string, ExternalProcessSnapshot> BuildExternalProcessSnapshot(IList<AppEntry> entries)
        {
            var result = new Dictionary<string, ExternalProcessSnapshot>(StringComparer.OrdinalIgnoreCase);
            var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (AppEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.ExePath))
                {
                    continue;
                }

                string fullPath = NormalizePath(entry.ExePath);
                if (string.IsNullOrWhiteSpace(fullPath))
                {
                    continue;
                }

                string fileNameWithoutExtension;
                try
                {
                    fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                {
                    continue;
                }

                processNames.Add(fileNameWithoutExtension);
            }

            foreach (string processName in processNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    continue;
                }

                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }

                        string processPath;
                        if (!TryGetProcessPath(process, out processPath))
                        {
                            continue;
                        }

                        string normalized = NormalizePath(processPath);
                        if (string.IsNullOrWhiteSpace(normalized) || result.ContainsKey(normalized))
                        {
                            continue;
                        }

                        uint priorityClass;
                        bool hasPriority = TryGetPriorityClass(process, out priorityClass);

                        bool isInAnyJob;
                        TryIsProcessInAnyJob(process, out isInAnyJob);

                        var snapshot = new ExternalProcessSnapshot();
                        snapshot.ProcessId = process.Id;
                        snapshot.PriorityDisplay = hasPriority ? PriorityMapper.ToDisplay(priorityClass) : "Unknown";
                        snapshot.IsInAnyJob = isInAnyJob;

                        result[normalized] = snapshot;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return result;
        }

        private void UpdateManagedEntryStatus(AppEntry entry, RuntimeState state)
        {
            entry.ProcessId = state.Process.Id;

            bool lockIsActive = state.IsLockActive;
            uint queriedPriorityClass;
            bool lockEnabledByJob;
            string queryError;

            if (_jobObjectService.TryReadPriorityLimit(
                state.JobHandle,
                out queriedPriorityClass,
                out lockEnabledByJob,
                out queryError))
            {
                lockIsActive = lockEnabledByJob;
                state.IsLockActive = lockEnabledByJob;
            }

            entry.IsPriorityLocked = lockIsActive;

            uint currentPriorityClass;
            bool hasPriority = TryGetPriorityClass(state.Process, out currentPriorityClass);
            string currentPriority = hasPriority ? PriorityMapper.ToDisplay(currentPriorityClass) : "Unknown";

            if (lockIsActive)
            {
                uint expected = PriorityMapper.ToNative(entry.Priority);
                if (hasPriority && currentPriorityClass != expected)
                {
                    entry.RuntimeStatus = "Running, locked (priority mismatch)";
                }
                else
                {
                    entry.RuntimeStatus = "Running, locked (" + currentPriority + ")";
                }
            }
            else
            {
                entry.RuntimeStatus = "Running, not locked (" + currentPriority + ")";
            }
        }

        private void CleanupDeadStates()
        {
            var deadIds = new List<string>();
            foreach (KeyValuePair<string, RuntimeState> pair in _states)
            {
                if (!IsProcessAlive(pair.Value.Process))
                {
                    deadIds.Add(pair.Key);
                }
            }

            foreach (string id in deadIds)
            {
                CleanupState(id);
            }
        }

        private bool TryGetAliveState(string entryId, out RuntimeState state)
        {
            state = null;
            if (!_states.TryGetValue(entryId, out state))
            {
                return false;
            }

            if (IsProcessAlive(state.Process))
            {
                return true;
            }

            CleanupState(entryId);
            state = null;
            return false;
        }

        private void CleanupState(string entryId)
        {
            RuntimeState state;
            if (_states.TryGetValue(entryId, out state))
            {
                _states.Remove(entryId);
                state.Dispose();
            }
        }

        private static bool IsProcessAlive(Process process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetProcessPath(Process process, out string path)
        {
            path = null;
            if (process == null)
            {
                return false;
            }

            try
            {
                if (process.MainModule == null)
                {
                    return false;
                }

                path = process.MainModule.FileName;
                return !string.IsNullOrWhiteSpace(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPriorityClass(Process process, out uint priorityClass)
        {
            priorityClass = 0;
            try
            {
                priorityClass = NativeMethods.GetPriorityClass(process.Handle);
                return priorityClass != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryIsProcessInAnyJob(Process process, out bool inAnyJob)
        {
            inAnyJob = false;
            if (process == null)
            {
                return false;
            }

            try
            {
                return NativeMethods.IsProcessInJob(process.Handle, IntPtr.Zero, out inAnyJob);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string raw = path.Trim().Trim('"');
            try
            {
                return Path.GetFullPath(raw);
            }
            catch
            {
                return raw;
            }
        }

        private static void ShutdownProcessOnFailedStart(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch
            {
                // Best effort cleanup on startup failure.
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void SetNotRunning(AppEntry entry)
        {
            entry.ProcessId = null;
            entry.IsPriorityLocked = false;
            entry.RuntimeStatus = "Not running";
        }

        private static void EnsureEntryId(AppEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
            }
        }

        private bool EnsurePriorityPrivilege(FixedPriority priority, out string error)
        {
            error = null;

            if (priority != FixedPriority.High && priority != FixedPriority.Realtime)
            {
                return true;
            }

            string privilegeError;
            if (_privilegeService.EnsureIncreaseBasePriorityPrivilege(out privilegeError))
            {
                return true;
            }

            error =
                "Cannot set " +
                priority +
                ". " +
                privilegeError;
            return false;
        }

        private static bool TrySetProcessPriority(Process process, FixedPriority priority, out string error)
        {
            error = null;
            if (process == null)
            {
                return false;
            }

            if (NativeMethods.SetPriorityClass(process.Handle, PriorityMapper.ToNative(priority)))
            {
                return true;
            }

            int win32Error = Marshal.GetLastWin32Error();
            error = "SetPriorityClass failed: Win32 " + win32Error;
            return false;
        }
    }
}
