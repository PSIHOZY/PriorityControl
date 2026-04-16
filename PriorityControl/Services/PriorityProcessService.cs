using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PriorityControl.Models;
using PriorityControl.Native;

namespace PriorityControl.Services
{
    internal sealed class PriorityProcessService : IDisposable
    {
        private const uint KnownJobAccess =
            NativeMethods.JOB_OBJECT_QUERY |
            NativeMethods.JOB_OBJECT_SET_ATTRIBUTES |
            NativeMethods.JOB_OBJECT_ASSIGN_PROCESS;

        private const int BindRetryIntervalMs = 100;
        private const int BindDurationNewProcessMs = 2000;
        private const int BindDurationExistingProcessMs = 800;

        private static readonly string[] JobNamePrefixes = { "Global\\", string.Empty };

        private readonly JobObjectService _jobObjectService = new JobObjectService();
        private readonly PrivilegeService _privilegeService = new PrivilegeService();
        private readonly HashSet<string> _sessionPinnedProcessKeys = new HashSet<string>(StringComparer.Ordinal);

        public bool StartWithFixedPriority(AppEntry entry, out string error)
        {
            return StartWithFixedPriority(entry, false, out error);
        }

        public bool StartWithFixedPriority(AppEntry entry, bool forceRestartExisting, out string error)
        {
            EnsureEntryId(entry);
            error = null;
            string exePath = ResolveAndUpdateExecutablePath(entry);

            if (!EnsurePriorityPrivilege(entry.Priority, out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                error = "Executable file not found: " + exePath;
                return false;
            }

            if (forceRestartExisting)
            {
                SafeJobHandle forcedJobHandle;
                if (!TryCreateManagedJobHandle(entry.Id, out forcedJobHandle, out error))
                {
                    return false;
                }

                using (forcedJobHandle)
                {
                    Process forcedProcess;
                    if (!RestartTargetProcessInJob(exePath, forcedJobHandle, out forcedProcess, out error))
                    {
                        return false;
                    }

                    forcedProcess.Dispose();

                    bool forcedBound = TryBindAndPinMatchingProcesses(
                        exePath,
                        forcedJobHandle,
                        BindDurationNewProcessMs,
                        BindRetryIntervalMs);

                    if (!forcedBound)
                    {
                        error = "Failed to bind running process to PriorityControl job.";
                        return false;
                    }

                    if (!_jobObjectService.ApplyPriorityLimit(forcedJobHandle, entry.Priority, out error))
                    {
                        return false;
                    }

                    TrySetPriorityForProcessesInJob(exePath, forcedJobHandle, entry.Priority);
                }

                entry.ProcessId = null;
                entry.IsPriorityLocked = true;
                entry.RuntimeStatus = "Running, locked";
                return true;
            }

            Process process;
            bool startedNew;

            process = FindFirstRunningProcessByPath(exePath);
            startedNew = false;
            if (process == null)
            {
                process = StartProcess(exePath, out error);
                if (process == null)
                {
                    return false;
                }

                startedNew = true;
            }

            SafeJobHandle jobHandle;
            if (!TryCreateManagedJobHandle(entry.Id, out jobHandle, out error))
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

            try
            {
                if (!TryEnsureProcessAssignedToJob(process, jobHandle, out error))
                {
                    if (IsAnotherJobAssignmentError(error))
                    {
                        process.Dispose();

                        Process restartedInJob;
                        if (!RestartTargetProcessInJob(exePath, jobHandle, out restartedInJob, out error))
                        {
                            return false;
                        }

                        process = restartedInJob;
                        startedNew = true;
                    }
                    else if (startedNew)
                    {
                        Process replacement = WaitForMatchingProcessByPath(exePath, process.Id, 5000);
                        if (replacement != null)
                        {
                            process.Dispose();
                            process = replacement;

                            if (!TryEnsureProcessAssignedToJob(process, jobHandle, out error))
                            {
                                ShutdownProcessOnFailedStart(process);
                                return false;
                            }
                        }
                        else
                        {
                            ShutdownProcessOnFailedStart(process);
                            return false;
                        }
                    }
                    else
                    {
                        process.Dispose();
                        return false;
                    }
                }

                if (!_jobObjectService.ApplyPriorityLimit(jobHandle, entry.Priority, out error))
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

                string priorityError;
                TrySetProcessPriority(process, entry.Priority, out priorityError);
                process.Dispose();

                bool boundToAtLeastOne = TryBindAndPinMatchingProcesses(
                    exePath,
                    jobHandle,
                    startedNew ? BindDurationNewProcessMs : BindDurationExistingProcessMs,
                    BindRetryIntervalMs);

                if (!boundToAtLeastOne)
                {
                    error = "Failed to bind running process to PriorityControl job.";
                    return false;
                }

                entry.ProcessId = null;
                entry.IsPriorityLocked = true;
                entry.RuntimeStatus = "Running, locked";
                return true;
            }
            finally
            {
                jobHandle.Dispose();
            }
        }

        public bool RemovePriorityLock(AppEntry entry, out string error)
        {
            EnsureEntryId(entry);
            error = null;
            string exePath = ResolveAndUpdateExecutablePath(entry);

            List<int> runningPids = FindRunningProcessIdsByPath(exePath);
            SafeJobHandle jobHandle;
            if (!TryOpenKnownJobHandle(entry.Id, runningPids, out jobHandle))
            {
                if (runningPids.Count > 0)
                {
                    error = "Process is running but not in PriorityControl job.";
                }
                else
                {
                    error = "Process is not running.";
                }

                return false;
            }

            using (jobHandle)
            {
                if (!IsAnyRunningProcessInJob(exePath, jobHandle))
                {
                    error = "Process is running but not in PriorityControl job.";
                    return false;
                }

                if (!_jobObjectService.RemovePriorityLimit(jobHandle, out error))
                {
                    return false;
                }
            }

            entry.IsPriorityLocked = false;
            return true;
        }

        public bool ApplyPriorityLock(AppEntry entry, out string error)
        {
            return ApplyOrUpdatePriorityLock(entry, out error);
        }

        public bool UpdateLockedPriority(AppEntry entry, out string error)
        {
            return ApplyOrUpdatePriorityLock(entry, out error);
        }

        public void RefreshStatus(AppEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            var one = new List<AppEntry>(1) { entry };
            RefreshStatuses(one);
        }

        public void RefreshStatuses(IList<AppEntry> entries)
        {
            if (entries == null)
            {
                return;
            }

            foreach (AppEntry entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                EnsureEntryId(entry);
                string exePath = ResolveAndUpdateExecutablePath(entry);

                List<Process> processes = FindRunningProcessesByPath(exePath);
                if (processes.Count == 0)
                {
                    SetNotRunning(entry);
                    continue;
                }

                List<int> pids = new List<int>(processes.Count);
                for (int i = 0; i < processes.Count; i++)
                {
                    pids.Add(processes[i].Id);
                }

                Process statusProcess = processes[0];
                bool knownLockEnabled = false;
                string knownJobPriorityDisplay = "Unknown";
                bool knownJobFound = false;

                SafeJobHandle knownJobHandle;
                if (TryOpenKnownJobHandle(entry.Id, pids, out knownJobHandle))
                {
                    using (knownJobHandle)
                    {
                        Process managedProcess = FindFirstProcessInJob(processes, knownJobHandle);
                        if (managedProcess != null)
                        {
                            knownJobFound = true;
                            statusProcess = managedProcess;

                            uint knownPriorityClass;
                            string queryError;
                            if (_jobObjectService.TryReadPriorityLimit(
                                knownJobHandle,
                                out knownPriorityClass,
                                out knownLockEnabled,
                                out queryError))
                            {
                                knownJobPriorityDisplay = PriorityMapper.ToDisplay(knownPriorityClass);
                            }
                            else
                            {
                                knownLockEnabled = false;
                            }
                        }
                    }
                }

                entry.ProcessId = statusProcess.Id;

                uint selectedPriorityClass;
                bool hasPriority = TryGetPriorityClass(statusProcess, out selectedPriorityClass);
                string selectedPriority = hasPriority
                    ? PriorityMapper.ToDisplay(selectedPriorityClass)
                    : "Unknown";

                if (knownJobFound)
                {
                    if (knownLockEnabled)
                    {
                        entry.IsPriorityLocked = true;
                        if (selectedPriority != knownJobPriorityDisplay)
                        {
                            entry.RuntimeStatus = "Running, locked (priority mismatch)";
                        }
                        else
                        {
                            entry.RuntimeStatus = "Running, locked (" + knownJobPriorityDisplay + ")";
                        }
                    }
                    else
                    {
                        entry.IsPriorityLocked = false;
                        bool inAnyJob;
                        TryIsProcessInAnyJob(statusProcess, out inAnyJob);
                        entry.RuntimeStatus = inAnyJob
                            ? "Running, external/in job (" + selectedPriority + ")"
                            : "Running, not locked (" + selectedPriority + ")";
                    }
                }
                else
                {
                    entry.IsPriorityLocked = false;
                    bool inAnyJob;
                    TryIsProcessInAnyJob(statusProcess, out inAnyJob);
                    entry.RuntimeStatus = inAnyJob
                        ? "Running, external/in job (" + selectedPriority + ")"
                        : "Running, not locked (" + selectedPriority + ")";
                }

                for (int i = 0; i < processes.Count; i++)
                {
                    processes[i].Dispose();
                }
            }
        }

        public bool IsManaged(string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return false;
            }

            SafeJobHandle handle;
            if (!TryOpenKnownJobHandle(entryId, null, out handle))
            {
                return false;
            }

            handle.Dispose();
            return true;
        }

        public void ReleaseEntry(AppEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            string ignoredError;
            RemovePriorityLock(entry, out ignoredError);
        }

        public void Dispose()
        {
            // Stateless service: no persistent process/job handles are held.
        }

        private bool ApplyOrUpdatePriorityLock(AppEntry entry, out string error)
        {
            EnsureEntryId(entry);
            error = null;
            string exePath = ResolveAndUpdateExecutablePath(entry);

            if (!EnsurePriorityPrivilege(entry.Priority, out error))
            {
                return false;
            }

            Process process = FindFirstRunningProcessByPath(exePath);
            if (process == null)
            {
                error = "Process is not running.";
                return false;
            }

            List<int> runningPids = FindRunningProcessIdsByPath(exePath);
            SafeJobHandle jobHandle;

            if (!TryOpenKnownJobHandle(entry.Id, runningPids, out jobHandle))
            {
                if (!TryCreateManagedJobHandle(entry.Id, out jobHandle, out error))
                {
                    process.Dispose();
                    return false;
                }
            }

            using (jobHandle)
            {
                if (!TryEnsureProcessAssignedToJob(process, jobHandle, out error))
                {
                    if (IsAnotherJobAssignmentError(error))
                    {
                        process.Dispose();

                        Process restartedInJob;
                        if (!RestartTargetProcessInJob(exePath, jobHandle, out restartedInJob, out error))
                        {
                            return false;
                        }

                        process = restartedInJob;
                    }
                    else
                    {
                        process.Dispose();
                        return false;
                    }
                }

                if (!_jobObjectService.ApplyPriorityLimit(jobHandle, entry.Priority, out error))
                {
                    process.Dispose();
                    return false;
                }

                string priorityError;
                TrySetProcessPriority(process, entry.Priority, out priorityError);

                bool boundToAtLeastOne = TryBindAndPinMatchingProcesses(
                    exePath,
                    jobHandle,
                    BindDurationExistingProcessMs,
                    BindRetryIntervalMs);

                if (!boundToAtLeastOne)
                {
                    process.Dispose();
                    error = "Failed to bind running process to PriorityControl job.";
                    return false;
                }
            }

            process.Dispose();
            entry.IsPriorityLocked = true;
            return true;
        }

        private static Process RestartTargetProcess(string exePath, out string error)
        {
            error = null;

            List<Process> running = FindRunningProcessesByPathStatic(exePath);
            for (int i = 0; i < running.Count; i++)
            {
                try
                {
                    if (!running[i].HasExited)
                    {
                        running[i].Kill();
                        running[i].WaitForExit(4000);
                    }
                }
                catch
                {
                    // continue best-effort
                }
                finally
                {
                    running[i].Dispose();
                }
            }

            return StartProcess(exePath, out error);
        }

        private bool RestartTargetProcessInJob(
            string exePath,
            SafeJobHandle jobHandle,
            out Process process,
            out string error)
        {
            process = null;
            error = null;

            List<Process> running = FindRunningProcessesByPathStatic(exePath);
            for (int i = 0; i < running.Count; i++)
            {
                try
                {
                    if (!running[i].HasExited)
                    {
                        running[i].Kill();
                        running[i].WaitForExit(4000);
                    }
                }
                catch
                {
                    // continue best-effort
                }
                finally
                {
                    running[i].Dispose();
                }
            }

            return StartProcessInJob(exePath, jobHandle, out process, out error);
        }

        private bool StartProcessInJob(
            string exePath,
            SafeJobHandle jobHandle,
            out Process process,
            out string error)
        {
            process = null;
            error = null;

            string workingDirectory = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = Environment.CurrentDirectory;
            }

            var startupInfo = new NativeMethods.STARTUPINFO();
            startupInfo.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.STARTUPINFO));

            NativeMethods.PROCESS_INFORMATION processInfo;
            string commandLine = "\"" + exePath + "\"";
            bool created = NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_SUSPENDED,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                error = "CreateProcess failed: Win32 " + Marshal.GetLastWin32Error();
                return false;
            }

            bool success = false;
            try
            {
                string assignError;
                if (!_jobObjectService.AssignProcess(jobHandle, processInfo.hProcess, out assignError))
                {
                    error = assignError;
                    return false;
                }

                if (NativeMethods.ResumeThread(processInfo.hThread) == 0xFFFFFFFF)
                {
                    error = "ResumeThread failed: Win32 " + Marshal.GetLastWin32Error();
                    return false;
                }

                process = Process.GetProcessById((int)processInfo.dwProcessId);
                success = true;
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to start process in job: " + ex.Message;
                return false;
            }
            finally
            {
                if (!success && processInfo.hProcess != IntPtr.Zero)
                {
                    try
                    {
                        Process.GetProcessById((int)processInfo.dwProcessId).Kill();
                    }
                    catch
                    {
                        // best effort
                    }
                }

                if (processInfo.hThread != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processInfo.hThread);
                }

                if (processInfo.hProcess != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processInfo.hProcess);
                }
            }
        }

        private bool TryEnsureProcessAssignedToJob(Process process, SafeJobHandle jobHandle, out string error)
        {
            error = null;

            bool inAnyJob;
            if (TryIsProcessInAnyJob(process, out inAnyJob) && inAnyJob)
            {
                bool inThisJob;
                if (NativeMethods.IsProcessInJob(
                    process.Handle,
                    jobHandle.DangerousGetHandle(),
                    out inThisJob) && inThisJob)
                {
                    return true;
                }

                error = "Process is already in another Job Object (cannot reassign).";
                return false;
            }

            if (!_jobObjectService.AssignProcess(jobHandle, process.Handle, out error))
            {
                bool inThisJob;
                if (NativeMethods.IsProcessInJob(
                    process.Handle,
                    jobHandle.DangerousGetHandle(),
                    out inThisJob) && inThisJob)
                {
                    return true;
                }

                return false;
            }

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

        private Process FindFirstRunningProcessByPath(string exePath)
        {
            List<Process> list = FindRunningProcessesByPath(exePath);
            if (list.Count == 0)
            {
                return null;
            }

            Process first = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                list[i].Dispose();
            }

            return first;
        }

        private Process WaitForMatchingProcessByPath(string exePath, int skipProcessId, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                List<Process> list = FindRunningProcessesByPath(exePath);
                Process selected = null;
                for (int i = 0; i < list.Count; i++)
                {
                    Process candidate = list[i];
                    if (candidate.Id == skipProcessId)
                    {
                        candidate.Dispose();
                        continue;
                    }

                    if (selected == null)
                    {
                        selected = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }

                if (selected != null)
                {
                    return selected;
                }

                Thread.Sleep(250);
            }

            return null;
        }

        private List<int> FindRunningProcessIdsByPath(string exePath)
        {
            var ids = new List<int>();
            List<Process> list = FindRunningProcessesByPath(exePath);
            for (int i = 0; i < list.Count; i++)
            {
                ids.Add(list[i].Id);
                list[i].Dispose();
            }

            return ids;
        }

        private List<Process> FindRunningProcessesByPath(string exePath)
        {
            return FindRunningProcessesByPathStatic(ResolveExecutablePath(exePath));
        }

        private static List<Process> FindRunningProcessesByPathStatic(string exePath)
        {
            var matches = new List<Process>();

            string normalizedTarget = NormalizePath(exePath);
            if (string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return matches;
            }

            string name;
            try
            {
                name = Path.GetFileNameWithoutExtension(normalizedTarget);
            }
            catch
            {
                return matches;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return matches;
            }

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(name);
            }
            catch
            {
                return matches;
            }

            foreach (Process process in candidates)
            {
                bool keep = false;
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
                        matches.Add(process);
                        keep = true;
                    }
                }
                catch
                {
                    // Some processes can deny query access; skip them.
                }
                finally
                {
                    if (!keep)
                    {
                        process.Dispose();
                    }
                }
            }

            return matches;
        }

        private bool TryOpenKnownJobHandle(string entryId, IList<int> candidateProcessIds, out SafeJobHandle jobHandle)
        {
            foreach (string stableName in BuildStableJobNames(entryId))
            {
                jobHandle = NativeMethods.OpenJobObject(KnownJobAccess, false, stableName);
                if (jobHandle != null && !jobHandle.IsInvalid)
                {
                    return true;
                }

                if (jobHandle != null)
                {
                    jobHandle.Dispose();
                }
            }

            if (candidateProcessIds != null)
            {
                for (int i = 0; i < candidateProcessIds.Count; i++)
                {
                    foreach (string legacyName in BuildLegacyJobNames(entryId, candidateProcessIds[i]))
                    {
                        jobHandle = NativeMethods.OpenJobObject(KnownJobAccess, false, legacyName);
                        if (jobHandle != null && !jobHandle.IsInvalid)
                        {
                            return true;
                        }

                        if (jobHandle != null)
                        {
                            jobHandle.Dispose();
                        }
                    }
                }
            }

            jobHandle = null;
            return false;
        }

        private bool TryCreateManagedJobHandle(string entryId, out SafeJobHandle jobHandle, out string error)
        {
            jobHandle = null;
            error = null;
            int lastError = 0;

            foreach (string stableName in BuildStableJobNames(entryId))
            {
                jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, stableName);
                if (jobHandle != null && !jobHandle.IsInvalid)
                {
                    return true;
                }

                if (jobHandle != null)
                {
                    jobHandle.Dispose();
                }

                lastError = Marshal.GetLastWin32Error();
            }

            error = "CreateJobObject failed: Win32 " + lastError;
            return false;
        }

        private static Process FindFirstProcessInJob(IList<Process> processes, SafeJobHandle jobHandle)
        {
            if (processes == null || jobHandle == null || jobHandle.IsInvalid)
            {
                return null;
            }

            IntPtr rawJobHandle = jobHandle.DangerousGetHandle();
            for (int i = 0; i < processes.Count; i++)
            {
                Process process = processes[i];
                if (process == null)
                {
                    continue;
                }

                try
                {
                    bool inThisJob;
                    if (NativeMethods.IsProcessInJob(process.Handle, rawJobHandle, out inThisJob) && inThisJob)
                    {
                        return process;
                    }
                }
                catch
                {
                    // ignore and continue
                }
            }

            return null;
        }

        private bool TryBindAndPinMatchingProcesses(
            string exePath,
            SafeJobHandle jobHandle,
            int durationMs,
            int intervalMs)
        {
            if (jobHandle == null || jobHandle.IsInvalid)
            {
                return false;
            }

            if (durationMs < 0)
            {
                durationMs = 0;
            }

            if (intervalMs < 0)
            {
                intervalMs = 0;
            }

            bool hasAtLeastOneBoundProcess = false;
            var pinnedProcessIds = new HashSet<int>();
            Stopwatch watch = Stopwatch.StartNew();

            while (true)
            {
                List<Process> processes = FindRunningProcessesByPath(exePath);
                for (int i = 0; i < processes.Count; i++)
                {
                    Process process = processes[i];
                    try
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }

                        bool processInManagedJob = false;
                        bool assignedToManagedJobInThisPass = false;
                        bool inThisJob;
                        if (NativeMethods.IsProcessInJob(
                            process.Handle,
                            jobHandle.DangerousGetHandle(),
                            out inThisJob) && inThisJob)
                        {
                            hasAtLeastOneBoundProcess = true;
                            processInManagedJob = true;
                        }
                        else
                        {
                            bool inAnyJob;
                            if (!(TryIsProcessInAnyJob(process, out inAnyJob) && inAnyJob))
                            {
                                string assignError;
                                if (_jobObjectService.AssignProcess(jobHandle, process.Handle, out assignError))
                                {
                                    bool nowInThisJob;
                                    if (NativeMethods.IsProcessInJob(
                                        process.Handle,
                                        jobHandle.DangerousGetHandle(),
                                        out nowInThisJob) && nowInThisJob)
                                    {
                                        hasAtLeastOneBoundProcess = true;
                                        processInManagedJob = true;
                                        assignedToManagedJobInThisPass = true;
                                    }
                                }
                            }
                        }

                        if (processInManagedJob &&
                            assignedToManagedJobInThisPass &&
                            !pinnedProcessIds.Contains(process.Id))
                        {
                            string pinKey = BuildSessionPinKey(process);
                            bool alreadyPinnedInSession =
                                !string.IsNullOrWhiteSpace(pinKey) &&
                                _sessionPinnedProcessKeys.Contains(pinKey);

                            if (!alreadyPinnedInSession && TryPinJobHandleInsideProcess(process, jobHandle))
                            {
                                pinnedProcessIds.Add(process.Id);

                                if (!string.IsNullOrWhiteSpace(pinKey))
                                {
                                    _sessionPinnedProcessKeys.Add(pinKey);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // continue best effort
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                // As soon as we have at least one confirmed process in our job,
                // we can return immediately to keep UI interactions responsive.
                if (hasAtLeastOneBoundProcess)
                {
                    return true;
                }

                if (watch.ElapsedMilliseconds >= durationMs)
                {
                    break;
                }

                if (intervalMs > 0)
                {
                    Thread.Sleep(intervalMs);
                }
            }

            return hasAtLeastOneBoundProcess;
        }

        private bool IsAnyRunningProcessInJob(string exePath, SafeJobHandle jobHandle)
        {
            if (jobHandle == null || jobHandle.IsInvalid)
            {
                return false;
            }

            List<Process> processes = FindRunningProcessesByPath(exePath);
            bool found = false;
            for (int i = 0; i < processes.Count; i++)
            {
                Process process = processes[i];
                try
                {
                    bool inThisJob;
                    if (NativeMethods.IsProcessInJob(
                        process.Handle,
                        jobHandle.DangerousGetHandle(),
                        out inThisJob) && inThisJob)
                    {
                        found = true;
                    }
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    process.Dispose();
                }
            }

            return found;
        }

        private void TrySetPriorityForProcessesInJob(
            string exePath,
            SafeJobHandle jobHandle,
            FixedPriority priority)
        {
            if (jobHandle == null || jobHandle.IsInvalid)
            {
                return;
            }

            List<Process> processes = FindRunningProcessesByPath(exePath);
            for (int i = 0; i < processes.Count; i++)
            {
                Process process = processes[i];
                try
                {
                    bool inThisJob;
                    if (NativeMethods.IsProcessInJob(
                        process.Handle,
                        jobHandle.DangerousGetHandle(),
                        out inThisJob) && inThisJob)
                    {
                        string ignoredError;
                        TrySetProcessPriority(process, priority, out ignoredError);
                    }
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool TryPinJobHandleInsideProcess(Process process, SafeJobHandle jobHandle)
        {
            if (process == null || jobHandle == null || jobHandle.IsInvalid)
            {
                return false;
            }

            IntPtr targetProcessHandle = IntPtr.Zero;
            try
            {
                targetProcessHandle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_DUP_HANDLE,
                    false,
                    process.Id);

                if (targetProcessHandle == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr duplicatedHandle;
                return NativeMethods.DuplicateHandle(
                    NativeMethods.GetCurrentProcess(),
                    jobHandle.DangerousGetHandle(),
                    targetProcessHandle,
                    out duplicatedHandle,
                    0,
                    false,
                    NativeMethods.DUPLICATE_SAME_ACCESS);
            }
            finally
            {
                if (targetProcessHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(targetProcessHandle);
                }
            }
        }

        private static bool TryGetProcessPath(Process process, out string path)
        {
            path = null;
            if (process == null)
            {
                return false;
            }

            IntPtr processHandle = IntPtr.Zero;
            try
            {
                processHandle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    process.Id);

                if (processHandle == IntPtr.Zero)
                {
                    return false;
                }

                int capacity = 1024;
                var builder = new StringBuilder(capacity);
                if (!NativeMethods.QueryFullProcessImageName(processHandle, 0, builder, ref capacity))
                {
                    return false;
                }

                path = builder.ToString();
                return !string.IsNullOrWhiteSpace(path);
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processHandle);
                }
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

        private string ResolveAndUpdateExecutablePath(AppEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            string current = entry.ExePath ?? string.Empty;
            string resolved = ResolveExecutablePath(current);
            if (!string.Equals(
                    NormalizePath(current),
                    NormalizePath(resolved),
                    StringComparison.OrdinalIgnoreCase))
            {
                entry.ExePath = resolved;
            }

            return entry.ExePath ?? string.Empty;
        }

        private static string ResolveExecutablePath(string exePath)
        {
            string normalizedTarget = NormalizePath(exePath);
            if (string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return string.Empty;
            }

            if (File.Exists(normalizedTarget))
            {
                return normalizedTarget;
            }

            try
            {
                string fileName = Path.GetFileName(normalizedTarget);
                string directoryPath = Path.GetDirectoryName(normalizedTarget);
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(directoryPath))
                {
                    return normalizedTarget;
                }

                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Name.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedTarget;
                }

                DirectoryInfo rootDirectory = directory.Parent;
                if (rootDirectory == null || !rootDirectory.Exists)
                {
                    return normalizedTarget;
                }

                string bestPath = null;
                bool bestHasVersion = false;
                Version bestVersion = null;
                DateTime bestWriteUtc = DateTime.MinValue;

                DirectoryInfo[] appDirectories;
                try
                {
                    appDirectories = rootDirectory.GetDirectories("app-*");
                }
                catch
                {
                    appDirectories = new DirectoryInfo[0];
                }

                for (int i = 0; i < appDirectories.Length; i++)
                {
                    DirectoryInfo appDirectory = appDirectories[i];
                    string candidatePath = Path.Combine(appDirectory.FullName, fileName);
                    if (!File.Exists(candidatePath))
                    {
                        continue;
                    }

                    Version candidateVersion;
                    bool candidateHasVersion = TryParseAppDirectoryVersion(appDirectory.Name, out candidateVersion);
                    DateTime candidateWriteUtc = DateTime.MinValue;
                    try
                    {
                        candidateWriteUtc = appDirectory.LastWriteTimeUtc;
                    }
                    catch
                    {
                        // best effort
                    }

                    bool takeCandidate = false;
                    if (bestPath == null)
                    {
                        takeCandidate = true;
                    }
                    else if (candidateHasVersion && bestHasVersion)
                    {
                        int compare = candidateVersion.CompareTo(bestVersion);
                        takeCandidate = compare > 0 || (compare == 0 && candidateWriteUtc > bestWriteUtc);
                    }
                    else if (candidateHasVersion && !bestHasVersion)
                    {
                        takeCandidate = true;
                    }
                    else if (!candidateHasVersion && !bestHasVersion)
                    {
                        takeCandidate = candidateWriteUtc > bestWriteUtc;
                    }

                    if (takeCandidate)
                    {
                        bestPath = candidatePath;
                        bestHasVersion = candidateHasVersion;
                        bestVersion = candidateVersion;
                        bestWriteUtc = candidateWriteUtc;
                    }
                }

                if (!string.IsNullOrWhiteSpace(bestPath))
                {
                    return bestPath;
                }
            }
            catch
            {
                // best effort fallback below
            }

            string fileNameFallback;
            try
            {
                fileNameFallback = Path.GetFileName(normalizedTarget);
            }
            catch
            {
                fileNameFallback = null;
            }

            string processResolvedPath = TryResolveExecutablePathFromRunningProcesses(normalizedTarget, fileNameFallback);
            if (!string.IsNullOrWhiteSpace(processResolvedPath))
            {
                return processResolvedPath;
            }

            return normalizedTarget;
        }

        private static bool TryParseAppDirectoryVersion(string directoryName, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                return false;
            }

            const string prefix = "app-";
            if (!directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string raw = directoryName.Substring(prefix.Length);
            int suffixSeparator = raw.IndexOf('-');
            if (suffixSeparator > 0)
            {
                raw = raw.Substring(0, suffixSeparator);
            }

            Version parsed;
            if (!Version.TryParse(raw, out parsed))
            {
                return false;
            }

            version = parsed;
            return true;
        }

        private static string TryResolveExecutablePathFromRunningProcesses(string originalPath, string expectedFileName)
        {
            if (string.IsNullOrWhiteSpace(expectedFileName))
            {
                return null;
            }

            string processName;
            try
            {
                processName = Path.GetFileNameWithoutExtension(expectedFileName);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            string preferredRoot = TryGetPreferredRootDirectory(originalPath);
            string fallbackPath = null;

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(processName);
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                Process candidate = candidates[i];
                try
                {
                    if (candidate.HasExited)
                    {
                        continue;
                    }

                    string candidatePath;
                    if (!TryGetProcessPath(candidate, out candidatePath))
                    {
                        continue;
                    }

                    string normalizedCandidate = NormalizePath(candidatePath);
                    if (string.IsNullOrWhiteSpace(normalizedCandidate) || !File.Exists(normalizedCandidate))
                    {
                        continue;
                    }

                    string candidateFileName;
                    try
                    {
                        candidateFileName = Path.GetFileName(normalizedCandidate);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.Equals(candidateFileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(preferredRoot) &&
                        normalizedCandidate.StartsWith(preferredRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return normalizedCandidate;
                    }

                    if (string.IsNullOrWhiteSpace(fallbackPath))
                    {
                        fallbackPath = normalizedCandidate;
                    }
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    candidate.Dispose();
                }
            }

            return fallbackPath;
        }

        private static string TryGetPreferredRootDirectory(string originalPath)
        {
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                return null;
            }

            try
            {
                string normalized = NormalizePath(originalPath);
                string directory = Path.GetDirectoryName(normalized);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return null;
                }

                var appDirectory = new DirectoryInfo(directory);
                if (!appDirectory.Name.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                DirectoryInfo rootDirectory = appDirectory.Parent;
                return rootDirectory != null ? NormalizePath(rootDirectory.FullName) : null;
            }
            catch
            {
                return null;
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

            error = "Cannot set " + priority + ". " + privilegeError;
            return false;
        }

        private static bool IsAnotherJobAssignmentError(string error)
        {
            return !string.IsNullOrWhiteSpace(error) &&
                error.IndexOf("another Job Object", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildSessionPinKey(Process process)
        {
            if (process == null)
            {
                return null;
            }

            try
            {
                return process.Id.ToString() + "|" + process.StartTime.ToUniversalTime().Ticks.ToString();
            }
            catch
            {
                return null;
            }
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

        private static IEnumerable<string> BuildStableJobNames(string entryId)
        {
            foreach (string prefix in JobNamePrefixes)
            {
                yield return prefix + "PriorityControl_" + entryId;
            }
        }

        private static IEnumerable<string> BuildLegacyJobNames(string entryId, int processId)
        {
            foreach (string prefix in JobNamePrefixes)
            {
                yield return prefix + "PriorityControl_" + entryId + "_" + processId;
            }
        }
    }
}
