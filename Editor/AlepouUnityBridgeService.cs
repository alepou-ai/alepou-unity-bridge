using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Alepou.UnityBridge
{
    [InitializeOnLoad]
    internal static class AlepouUnityBridgeService
    {
        private const double CommandScanIntervalSeconds = 1.0;
        private const double HeartbeatIntervalSeconds = 2.0;
        private const int WatchdogIntervalMilliseconds = 2000;
        private const int EditorLoopStallMilliseconds = 10000;

        private static readonly string ExecutorGeneration = Guid.NewGuid().ToString("N");
        private static readonly object WatchdogStateGate = new object();
        private static AlepouUnityBridgeWindow executor;
        private static Timer watchdogTimer;
        private static long lastEditorLoopTickUtcTicks = DateTime.UtcNow.Ticks;
        private static int watchdogWriteActive;
        private static WatchdogCachedState watchdogState = new WatchdogCachedState();
        private static bool processorActive;
        private static bool initialSnapshotPending = true;
        private static double nextCommandScanAt;
        private static double nextHeartbeatAt;
        private static string lastScanAt;
        private static string activeCommandId;
        private static string lastCommandStatus;
        private static string lastCommandMessage;
        private static string serviceError;
        private static bool assetsChanged;

        internal static bool ProcessorActive { get { return processorActive && executor != null; } }

        internal static string CurrentStatus
        {
            get
            {
                if (!ProcessorActive) return "inactive";
                if (!string.IsNullOrWhiteSpace(serviceError)) return "degraded - " + serviceError;
                if (!string.IsNullOrWhiteSpace(activeCommandId)) return "processing " + activeCommandId;
                if (!AlepouUnityBridgeApprovalPolicy.AutomaticProcessingEnabled) return "stopped locally";
                if (EditorApplication.isCompiling) return "active; waiting for compilation";
                if (EditorApplication.isUpdating) return "active; waiting for asset import";
                if (executor != null && executor.WaitingForHumanCommandCount() > 0) return "active; waiting for human approval";
                return "active";
            }
        }

        static AlepouUnityBridgeService()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.delayCall += EnsureStarted;
        }

        internal static void RefreshConfiguration()
        {
            EnsureStarted();
            executor.ReloadSettings(true);
            executor.EnsureBridgeFolders();
            EnsureWatchdogStarted(executor.OutputPath);
            AlepouUnityBridgeTestRunner.Initialize(executor.OutputPath);
            AlepouUnityBridgeBuildRunner.Initialize(executor.OutputPath);
            AlepouUnityBridgeWorkflowRunner.Initialize(executor.OutputPath, executor);
            initialSnapshotPending = true;
            WriteHealth(true);
        }

        internal static void RefreshApprovalPolicyState()
        {
            EnsureStarted();
            serviceError = null;
            if (!AlepouUnityBridgeApprovalPolicy.AutomaticProcessingEnabled)
            {
                AlepouUnityBridgeWorkflowRunner.CancelActive("Automatic processing was stopped locally in Unity.");
            }
            WriteHealth(true);
            RepaintWindows();
        }

        internal static void ExportSnapshot(bool showMessage)
        {
            EnsureStarted();
            executor.ExportSnapshot(showMessage);
            WriteHealth(true);
        }

        internal static bool ApproveCommand(string pendingFile)
        {
            EnsureStarted();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                serviceError = "Unity is compiling or importing; the command remains pending.";
                WriteHealth(true);
                return false;
            }
            serviceError = null;
            var processed = executor.TryApplyPendingCommand(pendingFile, false);
            WriteHealth(true);
            RepaintWindows();
            return processed;
        }

        internal static bool RejectCommand(string pendingFile)
        {
            EnsureStarted();
            var processed = executor.TryRejectPendingCommand(pendingFile);
            WriteHealth(true);
            RepaintWindows();
            return processed;
        }

        internal static void NotifyCommandStarted(string commandId)
        {
            activeCommandId = commandId;
            lastCommandStatus = "processing";
            lastCommandMessage = null;
            serviceError = null;
            WriteHealth(true);
        }

        internal static void NotifyCommandFinished(string status, string message)
        {
            lastCommandStatus = status;
            lastCommandMessage = message;
            activeCommandId = null;
            WriteHealth(true);
        }

        internal static void MarkAssetsChanged()
        {
            assetsChanged = true;
        }

        internal static void RepaintWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<AlepouUnityBridgeWindow>())
            {
                if (window == null || ReferenceEquals(window, executor)) continue;
                window.Repaint();
            }
        }

        private static void EnsureStarted()
        {
            if (executor != null)
            {
                processorActive = true;
                return;
            }

            executor = ScriptableObject.CreateInstance<AlepouUnityBridgeWindow>();
            executor.hideFlags = HideFlags.HideAndDontSave;
            executor.ReloadSettings(true);
            executor.EnsureBridgeFolders();
            processorActive = true;
            EnsureWatchdogStarted(executor.OutputPath);
            AlepouUnityBridgeTestRunner.Initialize(executor.OutputPath);
            AlepouUnityBridgeBuildRunner.Initialize(executor.OutputPath);
            AlepouUnityBridgeWorkflowRunner.Initialize(executor.OutputPath, executor);
            var recovered = executor.RecoverInterruptedCommands();
            if (recovered > 0)
            {
                lastCommandStatus = "interrupted";
                lastCommandMessage = "Recovered " + recovered + " interrupted command(s) without replay.";
            }
            WriteHealth(true);
        }

        private static void OnEditorUpdate()
        {
            Interlocked.Exchange(ref lastEditorLoopTickUtcTicks, DateTime.UtcNow.Ticks);
            try
            {
                EnsureStarted();
                serviceError = null;
                if (assetsChanged)
                {
                    assetsChanged = false;
                    executor.MarkEditorChange();
                }
                executor.ServiceUpdate();
                AlepouUnityBridgeTestRunner.Tick();
                AlepouUnityBridgeWorkflowRunner.Tick();

                var now = EditorApplication.timeSinceStartup;
                if (initialSnapshotPending && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
                {
                    initialSnapshotPending = false;
                    executor.ExportSnapshot(false);
                }

                if (now >= nextCommandScanAt)
                {
                    nextCommandScanAt = now + CommandScanIntervalSeconds;
                    lastScanAt = DateTimeOffset.UtcNow.ToString("o");
                    if (executor.AutomaticProcessingEnabled && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
                    {
                        executor.ProcessAutoApprovals();
                    }
                }

                if (now >= nextHeartbeatAt)
                {
                    nextHeartbeatAt = now + HeartbeatIntervalSeconds;
                    WriteHealth(false);
                }
            }
            catch (Exception ex)
            {
                serviceError = ex.GetType().Name + ": " + ex.Message;
                Debug.LogException(ex);
                WriteHealth(true);
            }
        }

        private static void OnSelectionChanged()
        {
            EnsureStarted();
            executor.ServiceSelectionChanged();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            EnsureStarted();
            AlepouUnityBridgeWorkflowRunner.NotifyPlayModeStateChanged(change);
            executor.ServicePlayModeStateChanged(change);
            WriteHealth(true);
        }

        private static void OnHierarchyChanged()
        {
            EnsureStarted();
            executor.MarkEditorChange();
        }

        private static void OnUndoRedo()
        {
            EnsureStarted();
            executor.MarkEditorChange();
        }

        private static void BeforeAssemblyReload()
        {
            processorActive = false;
            serviceError = "Unity scripts are reloading.";
            WriteHealth(true);
            StopWatchdog("domain-reload");
        }

        private static void OnEditorQuitting()
        {
            processorActive = false;
            serviceError = "Unity Editor is closing.";
            WriteHealth(true);
            StopWatchdog("editor-closing");
        }

        [MenuItem("Tools/Alepou/Unity Bridge/STOP Automatic Processing", false, 100)]
        private static void EmergencyStopFromMenu()
        {
            AlepouUnityBridgeApprovalPolicy.EmergencyStop();
            RefreshApprovalPolicyState();
            Debug.LogWarning("Alepou Unity Bridge automatic processing stopped and the delegated grant was revoked.");
        }

        [MenuItem("Tools/Alepou/Unity Bridge/Resume Observation Processing", false, 101)]
        private static void ResumeObservationProcessingFromMenu()
        {
            AlepouUnityBridgeApprovalPolicy.ResumeObservationProcessing();
            RefreshApprovalPolicyState();
            Debug.Log("Alepou Unity Bridge observation processing resumed. Write delegation remains off until a new grant is enabled.");
        }

        internal static bool IsServiceExecutor(AlepouUnityBridgeWindow window)
        {
            return window != null && ReferenceEquals(window, executor);
        }

        private static bool IsWindowOpen()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<AlepouUnityBridgeWindow>())
            {
                if (window != null && !ReferenceEquals(window, executor)) return true;
            }
            return false;
        }

        private static void WriteHealth(bool force)
        {
            if (executor == null) return;
            try
            {
                var outputPath = executor.OutputPath;
                if (string.IsNullOrWhiteSpace(outputPath)) return;
                Directory.CreateDirectory(outputPath);
                var policy = AlepouUnityBridgeApprovalPolicy.Snapshot();
                var workflow = AlepouUnityBridgeWorkflowRunner.Snapshot();
                var waitingForHumanCount = executor.WaitingForHumanCommandCount();
                var health = new BridgeHealth
                {
                    schemaVersion = 1,
                    bridgeVersion = AlepouUnityBridgeWindow.BridgeVersion,
                    processorActive = ProcessorActive,
                    status = HealthStatus(),
                    heartbeatAt = DateTimeOffset.UtcNow.ToString("o"),
                    lastScanAt = lastScanAt,
                    executorGeneration = ExecutorGeneration,
                    windowOpen = IsWindowOpen(),
                    outputPath = outputPath,
                    unityVersion = Application.unityVersion,
                    projectName = Application.productName,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    playMode = EditorApplication.isPlaying,
                    pendingCount = SafeCount("pending"),
                    processingCount = SafeCount("processing"),
                    waitingForHumanCount = waitingForHumanCount,
                    activeCommandId = activeCommandId,
                    approvalProfile = policy.profile,
                    grantActive = policy.grantActive,
                    grantId = policy.grantId,
                    grantDurable = policy.durable,
                    grantExpiresAt = policy.expiresAt,
                    grantSessionBound = policy.sessionBound,
                    grantSessionBinding = policy.sessionBinding,
                    grantRemainingCommands = policy.remainingCommands,
                    grantRemainingActions = policy.remainingActions,
                    grantAllowedActions = policy.allowedActions,
                    automaticProcessingPaused = policy.automaticProcessingPaused,
                    activeWorkflowId = workflow.workflowId,
                    activeWorkflowSessionId = workflow.sessionId,
                    activeWorkflowStatus = workflow.status,
                    activeWorkflowStepId = workflow.currentStepId,
                    activeWorkflowStepType = workflow.currentStepType,
                    activeWorkflowStepPhase = workflow.stepPhase,
                    activeWorkflowMessage = workflow.message,
                    workflowPlayModeOwned = workflow.playModeOwned,
                    activeRuntimeCaptureId = workflow.runtimeCaptureId,
                    activeRuntimeCaptureResultFile = workflow.runtimeCaptureResultFile,
                    pendingWorkflowCount = workflow.pendingCount,
                    workflowWaitingForHuman = workflow.waitingForHuman,
                    blockedReason = BlockedReason(waitingForHumanCount),
                    lastCommandStatus = lastCommandStatus,
                    lastCommandMessage = lastCommandMessage
                };
                WriteAtomic(Path.Combine(outputPath, "bridge-health.json"), JsonUtility.ToJson(health, true));
                CacheWatchdogState(outputPath, health);
            }
            catch (Exception ex)
            {
                if (force) Debug.LogWarning("Alepou Unity Bridge could not write bridge-health.json: " + ex.Message);
            }
        }

        private static int SafeCount(string bucket)
        {
            try
            {
                var dir = executor.CommandsDir(bucket);
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string HealthStatus()
        {
            if (!ProcessorActive) return "inactive";
            if (!string.IsNullOrWhiteSpace(serviceError)) return "degraded";
            if (!string.IsNullOrWhiteSpace(activeCommandId)) return "processing";
            if (!AlepouUnityBridgeApprovalPolicy.AutomaticProcessingEnabled) return "stopped";
            if (EditorApplication.isCompiling) return "waiting-for-compile";
            if (EditorApplication.isUpdating) return "waiting-for-import";
            var workflow = AlepouUnityBridgeWorkflowRunner.Snapshot();
            if (workflow.active && workflow.waitingForHuman) return "waiting-for-approval";
            if (workflow.active) return "workflow-" + workflow.status;
            if (executor != null && executor.WaitingForHumanCommandCount() > 0) return "waiting-for-approval";
            return "healthy";
        }

        private static string BlockedReason(int waitingForHumanCount)
        {
            if (!string.IsNullOrWhiteSpace(serviceError)) return serviceError;
            if (!AlepouUnityBridgeApprovalPolicy.AutomaticProcessingEnabled) return "Automatic processing is stopped locally in Unity.";
            if (EditorApplication.isCompiling) return "Unity is compiling scripts.";
            if (EditorApplication.isUpdating) return "Unity is importing assets.";
            var workflow = AlepouUnityBridgeWorkflowRunner.Snapshot();
            if (workflow.active && !string.IsNullOrWhiteSpace(workflow.message)) return workflow.message;
            if (waitingForHumanCount > 0) return executor.FirstWaitingForHumanReason();
            return null;
        }

        private static void WriteAtomic(string filePath, string contents)
        {
            var tempPath = filePath + "." + ExecutorGeneration + ".tmp";
            File.WriteAllText(tempPath, contents ?? "", new UTF8Encoding(false));
            if (!File.Exists(filePath))
            {
                File.Move(tempPath, filePath);
                return;
            }
            try
            {
                File.Replace(tempPath, filePath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
            catch (IOException)
            {
                File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
        }

        private static void EnsureWatchdogStarted(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            lock (WatchdogStateGate)
            {
                watchdogState.outputPath = outputPath;
                watchdogState.bridgeVersion = AlepouUnityBridgeWindow.BridgeVersion;
                watchdogState.executorGeneration = ExecutorGeneration;
                watchdogState.processorExpectedActive = processorActive && executor != null;
                if (watchdogTimer == null)
                {
                    watchdogTimer = new Timer(
                        WriteWatchdogHeartbeat,
                        null,
                        WatchdogIntervalMilliseconds,
                        WatchdogIntervalMilliseconds);
                }
            }
        }

        private static void CacheWatchdogState(string outputPath, BridgeHealth health)
        {
            if (health == null || string.IsNullOrWhiteSpace(outputPath)) return;
            lock (WatchdogStateGate)
            {
                watchdogState.outputPath = outputPath;
                watchdogState.bridgeVersion = health.bridgeVersion;
                watchdogState.executorGeneration = health.executorGeneration;
                watchdogState.processorExpectedActive = health.processorActive;
                watchdogState.mainHealthStatus = health.status;
                watchdogState.activeCommandId = health.activeCommandId;
                watchdogState.activeWorkflowId = health.activeWorkflowId;
                watchdogState.activeWorkflowStepId = health.activeWorkflowStepId;
                watchdogState.activeWorkflowStepType = health.activeWorkflowStepType;
                watchdogState.activeWorkflowStepPhase = health.activeWorkflowStepPhase;
            }
        }

        private static void StopWatchdog(string terminalStatus)
        {
            Timer timer;
            lock (WatchdogStateGate)
            {
                watchdogState.processorExpectedActive = false;
                watchdogState.mainHealthStatus = terminalStatus;
                timer = watchdogTimer;
                watchdogTimer = null;
            }
            if (timer != null)
            {
                timer.Dispose();
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref watchdogWriteActive) == 0,
                    WatchdogIntervalMilliseconds);
            }
            WriteWatchdogHeartbeat(null);
        }

        // Timer-thread boundary: this method deliberately uses only System.* state
        // and file I/O. UnityEditor and UnityEngine APIs must remain on the editor thread.
        private static void WriteWatchdogHeartbeat(object ignored)
        {
            if (Interlocked.Exchange(ref watchdogWriteActive, 1) != 0) return;
            try
            {
                WatchdogCachedState state;
                lock (WatchdogStateGate)
                {
                    state = watchdogState.Copy();
                }
                if (string.IsNullOrWhiteSpace(state.outputPath)) return;
                var now = DateTime.UtcNow;
                var lastTickTicks = Interlocked.Read(ref lastEditorLoopTickUtcTicks);
                var lastTick = new DateTime(Math.Max(DateTime.MinValue.Ticks, lastTickTicks), DateTimeKind.Utc);
                var editorLoopAgeMs = Math.Max(0, (long)(now - lastTick).TotalMilliseconds);
                var stalled = state.processorExpectedActive && editorLoopAgeMs >= EditorLoopStallMilliseconds;
                var status = !state.processorExpectedActive
                    ? "inactive"
                    : stalled
                        ? "editor-loop-stalled"
                        : "editor-loop-responsive";
                var probableCause = stalled
                    ? "Unity's editor main loop has not ticked for " + editorLoopAgeMs +
                      " ms. A modal dialog or another blocking editor operation is likely; commands and workflows cannot progress until the main loop resumes."
                    : null;
                var json = "{\n" +
                           "  \"schemaVersion\": 1,\n" +
                           "  \"bridgeVersion\": " + JsonString(state.bridgeVersion) + ",\n" +
                           "  \"executorGeneration\": " + JsonString(state.executorGeneration) + ",\n" +
                           "  \"watchdogHeartbeatAt\": " + JsonString(now.ToString("o")) + ",\n" +
                           "  \"processorExpectedActive\": " + JsonBoolean(state.processorExpectedActive) + ",\n" +
                           "  \"status\": " + JsonString(status) + ",\n" +
                           "  \"editorLoopResponsive\": " + JsonBoolean(!stalled) + ",\n" +
                           "  \"editorLoopLastTickAt\": " + JsonString(lastTick.ToString("o")) + ",\n" +
                           "  \"editorLoopAgeMs\": " + editorLoopAgeMs + ",\n" +
                           "  \"probableCause\": " + JsonString(probableCause) + ",\n" +
                           "  \"mainHealthStatus\": " + JsonString(state.mainHealthStatus) + ",\n" +
                           "  \"activeCommandId\": " + JsonString(state.activeCommandId) + ",\n" +
                           "  \"activeWorkflowId\": " + JsonString(state.activeWorkflowId) + ",\n" +
                           "  \"activeWorkflowStepId\": " + JsonString(state.activeWorkflowStepId) + ",\n" +
                           "  \"activeWorkflowStepType\": " + JsonString(state.activeWorkflowStepType) + ",\n" +
                           "  \"activeWorkflowStepPhase\": " + JsonString(state.activeWorkflowStepPhase) + "\n" +
                           "}\n";
                WriteWatchdogAtomic(Path.Combine(state.outputPath, "bridge-watchdog.json"), json);
            }
            catch
            {
                // A watchdog must never destabilize the editor or call Unity logging APIs.
            }
            finally
            {
                Interlocked.Exchange(ref watchdogWriteActive, 0);
            }
        }

        private static void WriteWatchdogAtomic(string filePath, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            var tempPath = filePath + "." + ExecutorGeneration + ".watchdog.tmp";
            File.WriteAllText(tempPath, contents ?? "", new UTF8Encoding(false));
            if (!File.Exists(filePath))
            {
                File.Move(tempPath, filePath);
                return;
            }
            try { File.Replace(tempPath, filePath, null); }
            catch
            {
                File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
        }

        private static string JsonBoolean(bool value)
        {
            return value ? "true" : "false";
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32) builder.Append("\\u" + ((int)character).ToString("x4"));
                        else builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        [Serializable]
        private sealed class BridgeHealth
        {
            public int schemaVersion;
            public string bridgeVersion;
            public bool processorActive;
            public string status;
            public string heartbeatAt;
            public string lastScanAt;
            public string executorGeneration;
            public bool windowOpen;
            public string outputPath;
            public string unityVersion;
            public string projectName;
            public bool isCompiling;
            public bool isUpdating;
            public bool playMode;
            public int pendingCount;
            public int processingCount;
            public int waitingForHumanCount;
            public string activeCommandId;
            public string approvalProfile;
            public bool grantActive;
            public string grantId;
            public bool grantDurable;
            public string grantExpiresAt;
            public bool grantSessionBound;
            public string grantSessionBinding;
            public int grantRemainingCommands;
            public int grantRemainingActions;
            public string[] grantAllowedActions;
            public bool automaticProcessingPaused;
            public string activeWorkflowId;
            public string activeWorkflowSessionId;
            public string activeWorkflowStatus;
            public string activeWorkflowStepId;
            public string activeWorkflowStepType;
            public string activeWorkflowStepPhase;
            public string activeWorkflowMessage;
            public bool workflowPlayModeOwned;
            public string activeRuntimeCaptureId;
            public string activeRuntimeCaptureResultFile;
            public int pendingWorkflowCount;
            public bool workflowWaitingForHuman;
            public string blockedReason;
            public string lastCommandStatus;
            public string lastCommandMessage;
        }

        private sealed class WatchdogCachedState
        {
            public string outputPath;
            public string bridgeVersion;
            public string executorGeneration;
            public bool processorExpectedActive;
            public string mainHealthStatus;
            public string activeCommandId;
            public string activeWorkflowId;
            public string activeWorkflowStepId;
            public string activeWorkflowStepType;
            public string activeWorkflowStepPhase;

            public WatchdogCachedState Copy()
            {
                return (WatchdogCachedState)MemberwiseClone();
            }
        }
    }

    internal sealed class AlepouBridgeAssetWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFromAssetPaths)
        {
            AlepouUnityBridgeService.MarkAssetsChanged();
        }
    }
}
