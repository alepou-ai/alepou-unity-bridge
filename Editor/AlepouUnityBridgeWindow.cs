using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Alepou.UnityBridge
{
    [Serializable]
    internal sealed class ConsoleEntry
    {
        public string time;
        public string level;
        public string condition;
        public string stackTrace;
        public string activeScene;
        public bool playMode;
        public string selectedObject;
    }

    [Serializable]
    internal sealed class RuntimeProbeRequest
    {
        public string objectPath;
        public string[] componentTypes;
        public string[] propertyPaths;
    }

    [Serializable]
    internal sealed class RuntimeCaptureRequest
    {
        public string workflowId;
        public string captureId;
        public string consoleSinceAt;
        public int maxConsoleEntries;
        public RuntimeProbeRequest[] probes;
    }

    [Serializable]
    internal sealed class RuntimeCaptureResult
    {
        public int schemaVersion;
        public string captureId;
        public string workflowId;
        public string status;
        public string message;
        public string capturedAt;
        public string playModeState;
        public string gameViewFile;
        public string resultFile;
        public string consoleSinceAt;
        public int consoleMatchedCount;
        public int consoleReturnedCount;
        public bool consoleTruncated;
        public RuntimeConsoleEntry[] consoleEntries;
        public RuntimeObjectProbe[] probes;
    }

    [Serializable]
    internal sealed class RuntimeConsoleEntry
    {
        public string time;
        public string level;
        public string condition;
        public string stackTrace;
        public string activeScene;
        public string selectedObject;
    }

    [Serializable]
    internal sealed class RuntimeObjectProbe
    {
        public string objectPath;
        public bool found;
        public string message;
        public bool activeSelf;
        public bool activeInHierarchy;
        public RuntimeComponentProbe[] components;
    }

    [Serializable]
    internal sealed class RuntimeComponentProbe
    {
        public string requestedType;
        public string type;
        public string enabled;
        public RuntimePropertyProbe[] properties;
    }

    [Serializable]
    internal sealed class RuntimePropertyProbe
    {
        public string path;
        public string displayName;
        public string type;
        public string value;
    }

    [InitializeOnLoad]
    internal static class AlepouUnityBridgeLogCapture
    {
        internal static readonly List<ConsoleEntry> Entries = new List<ConsoleEntry>();
        private const int MaxEntries = 500;

        static AlepouUnityBridgeLogCapture()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            Entries.Add(new ConsoleEntry
            {
                time = DateTimeOffset.Now.ToString("o"),
                level = type.ToString(),
                condition = condition ?? "",
                stackTrace = stackTrace ?? "",
                activeScene = SceneManager.GetActiveScene().path,
                playMode = EditorApplication.isPlaying,
                selectedObject = AlepouUnityBridgeWindow.ObjectPath(Selection.activeGameObject)
            });
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }
        }
    }

    public sealed class AlepouUnityBridgeWindow : EditorWindow
    {
        internal const string BridgeVersion = "0.18.0";
        private const string OutputPathKey = "Alepou.UnityBridge.OutputPath";
        private const string AutoExportKey = "Alepou.UnityBridge.AutoExport";
        private const string AutoExportIntervalKey = "Alepou.UnityBridge.AutoExportIntervalSeconds";
        private const string ExportOnChangeKey = "Alepou.UnityBridge.ExportOnChange";
        private static readonly int[] AutoExportIntervals = { 0, 5, 15, 30, 60 };
        private string outputPath;
        private bool autoExport;
        private int autoExportIntervalSeconds;
        private double nextIntervalExportAt;
        private bool exportOnChange;
        private bool eventExportDirty;
        private double nextEventExportAt;
        private bool prevCompiling;
        private bool prevUpdating;
        private Vector2 scroll;
        private string lastMessage = "Ready.";
        private bool showCustomGrant;
        private int grantDurationMinutes = 240;
        private int grantMaxCommands = 100;
        private int grantMaxActions = 500;
        private string grantSessionBinding = "";
        private AlepouUnityBindingDiscoveryResult bindingDiscovery;
        private int bindingSessionIndex;
        private bool showManualSessionBinding;
        private bool[] customGrantActions;
        private readonly Dictionary<string, string> pendingPolicyDecisionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal string OutputPath { get { return outputPath; } }
        internal bool AutomaticProcessingEnabled { get { return AlepouUnityBridgeApprovalPolicy.AutomaticProcessingEnabled; } }

        [MenuItem("Tools/Alepou/Unity Bridge/Open Bridge Window", false, 0)]
        public static void Open()
        {
            var existing = FindUserWindow();
            if (existing != null)
            {
                existing.Show();
                existing.Focus();
                return;
            }

            // CreateWindow, never GetWindow: GetWindow reuses the first instance it finds,
            // which is the service's headless executor. That instance has no parent
            // container, so GetWindow only focuses it and nothing appears on screen.
            var inspectorType = InspectorWindowType();
            var window = inspectorType != null
                ? CreateWindow<AlepouUnityBridgeWindow>("Alepou Bridge", inspectorType)
                : CreateWindow<AlepouUnityBridgeWindow>("Alepou Bridge");
            window.Show();
            window.Focus();
        }

        private static AlepouUnityBridgeWindow FindUserWindow()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<AlepouUnityBridgeWindow>())
            {
                if (window == null) continue;
                if (AlepouUnityBridgeService.IsServiceExecutor(window)) continue;
                if ((window.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                return window;
            }
            return null;
        }

        // Docks beside the Inspector the first time the window is created so the bridge
        // behaves like a normal panel. Unity keeps the user's own placement after that.
        private static Type InspectorWindowType()
        {
            var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("UnityEditor.InspectorWindow");
                if (type != null) return type;
            }
            return null;
        }

        private void OnEnable()
        {
            ReloadSettings(true);
            customGrantActions = new bool[AlepouUnityBridgeApprovalPolicy.CustomizableActions.Length];
            prevCompiling = EditorApplication.isCompiling;
            prevUpdating = EditorApplication.isUpdating;
            EnsureBridgeFolders();
            EditorApplication.delayCall -= RefreshAlepouBindingDiscovery;
            EditorApplication.delayCall += RefreshAlepouBindingDiscovery;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= RefreshAlepouBindingDiscovery;
        }

        internal void ReloadSettings(bool resetSchedule = false)
        {
            outputPath = EditorPrefs.GetString(OutputPathKey, DefaultOutputPath());
            autoExport = EditorPrefs.GetBool(AutoExportKey, false);
            var nextInterval = NormalizeInterval(EditorPrefs.GetInt(AutoExportIntervalKey, 0));
            if (resetSchedule || nextInterval != autoExportIntervalSeconds)
            {
                nextIntervalExportAt = EditorApplication.timeSinceStartup + Math.Max(1, nextInterval);
            }
            autoExportIntervalSeconds = nextInterval;
            exportOnChange = EditorPrefs.GetBool(ExportOnChangeKey, true);
        }

        internal void MarkEditorChange() { eventExportDirty = true; }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Alepou Unity Bridge", EditorStyles.boldLabel);
            var health = AlepouUnityBridgeService.CurrentStatus;
            EditorGUILayout.HelpBox(
                "The command processor runs whenever this Unity project is open; this window is an optional dashboard and approval surface.\n" +
                "Processor: " + health,
                AlepouUnityBridgeService.ProcessorActive ? MessageType.Info : MessageType.Warning);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                outputPath = EditorGUILayout.TextField("Bridge Path", outputPath);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Use Default"))
                    {
                        outputPath = DefaultOutputPath();
                    }
                    if (GUILayout.Button("Save Path"))
                    {
                        EditorPrefs.SetString(OutputPathKey, outputPath);
                        EnsureBridgeFolders();
                        AlepouUnityBridgeService.RefreshConfiguration();
                        lastMessage = "Saved bridge path.";
                    }
                }
                autoExport = EditorGUILayout.ToggleLeft("Auto export on selection changes", autoExport);
                EditorPrefs.SetBool(AutoExportKey, autoExport);
                var currentIntervalIndex = Mathf.Max(0, Array.IndexOf(AutoExportIntervals, autoExportIntervalSeconds));
                var labels = AutoExportIntervals.Select(v => v == 0 ? "Off" : v + " seconds").ToArray();
                var nextIntervalIndex = EditorGUILayout.Popup("Interval Export", currentIntervalIndex, labels);
                autoExportIntervalSeconds = AutoExportIntervals[Mathf.Clamp(nextIntervalIndex, 0, AutoExportIntervals.Length - 1)];
                EditorPrefs.SetInt(AutoExportIntervalKey, autoExportIntervalSeconds);
                exportOnChange = EditorGUILayout.ToggleLeft("Export on editor changes (hierarchy / undo / compile / assets)", exportOnChange);
                EditorPrefs.SetBool(ExportOnChangeKey, exportOnChange);
            }

            DrawApprovalPolicy();
            DrawWorkflowStatus();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Export Snapshot", GUILayout.Height(32)))
                {
                    AlepouUnityBridgeService.ExportSnapshot(true);
                    lastMessage = "Snapshot export requested.";
                }
                if (GUILayout.Button("Open Folder", GUILayout.Height(32)))
                {
                    EnsureBridgeFolders();
                    EditorUtility.RevealInFinder(outputPath);
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Command Inbox", EditorStyles.boldLabel);
            DrawPendingCommands();

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(lastMessage, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void DrawApprovalPolicy()
        {
            var policy = AlepouUnityBridgeApprovalPolicy.Snapshot();
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Delegated Automatic Approval", EditorStyles.boldLabel);
                if (policy.automaticProcessingPaused)
                {
                    EditorGUILayout.HelpBox(
                        "EMERGENCY STOP is active. No command, including observation, will be claimed automatically.",
                        MessageType.Error);
                    if (GUILayout.Button("Resume Observation Commands"))
                    {
                        AlepouUnityBridgeApprovalPolicy.ResumeObservationProcessing();
                        AlepouUnityBridgeService.RefreshApprovalPolicyState();
                        lastMessage = "Automatic observation processing resumed; writes still require a grant or human approval.";
                    }
                    return;
                }

                if (policy.grantActive)
                {
                    EditorGUILayout.LabelField("Profile", policy.profile);
                    if (policy.durable)
                    {
                        EditorGUILayout.LabelField("Duration", "Until explicitly revoked or rebound");
                        EditorGUILayout.LabelField("Budget", "No command/action counter");
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Expires", policy.expiresAt ?? "(unknown)");
                        EditorGUILayout.LabelField(
                            "Budget",
                            policy.remainingCommands + "/" + policy.maxCommands + " commands; " +
                            policy.remainingActions + "/" + policy.maxActions + " actions remaining");
                    }
                    EditorGUILayout.LabelField("Grant ID", policy.grantId ?? "(none)");
                    if (policy.sessionBound)
                    {
                        EditorGUILayout.LabelField("Session binding", policy.sessionBinding);
                    }
                    EditorGUILayout.HelpBox(policy.riskSummary, MessageType.Warning);
                    if (policy.allowedActions != null && policy.allowedActions.Length > 0)
                    {
                        EditorGUILayout.LabelField("Delegated actions", string.Join(", ", policy.allowedActions), EditorStyles.wordWrappedLabel);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Revoke Grant"))
                        {
                            AlepouUnityBridgeApprovalPolicy.RevokeGrant();
                            AlepouUnityBridgeService.RefreshApprovalPolicyState();
                            lastMessage = "Delegated automatic approval revoked.";
                        }
                        if (GUILayout.Button("STOP Automatic Processing"))
                        {
                            AlepouUnityBridgeApprovalPolicy.EmergencyStop();
                            AlepouUnityBridgeService.RefreshApprovalPolicyState();
                            lastMessage = "Emergency stop enabled and the grant revoked.";
                        }
                    }
                    return;
                }

                EditorGUILayout.HelpBox(
                    "Default: bounded observation commands can run automatically. Use Trusted Development for a normal AI edit/refresh/test session, Reversible Workspace for writes without code execution, or Custom Expert for an exact capability list.",
                    MessageType.Info);
                if (GUILayout.Button("STOP Automatic Processing"))
                {
                    AlepouUnityBridgeApprovalPolicy.EmergencyStop();
                    AlepouUnityBridgeService.RefreshApprovalPolicyState();
                    lastMessage = "Emergency stop enabled; even observation commands will wait.";
                    return;
                }

                var suggestedSession = AlepouUnityBridgeWorkflowRunner.SuggestedSessionBinding();
                if (string.IsNullOrWhiteSpace(grantSessionBinding) && !string.IsNullOrWhiteSpace(suggestedSession))
                {
                    grantSessionBinding = suggestedSession;
                }

                var trustedActions = AlepouUnityBridgeApprovalPolicy.TrustedDevelopmentActions;
                var trustedRisk = AlepouUnityBridgeApprovalPolicy.RiskSummary(trustedActions);
                EditorGUILayout.HelpBox(
                    "Recommended — Trusted Development: a durable project/session-scoped switch for the normal reversible authoring loop plus refresh/import/compile, focused Unity tests, and bounded workflow-owned Play Mode capture. It stays active until you revoke it or trust another session; there are no arbitrary expiry or command counters. Builds, global settings, destructive changes, lighting bake, menu execution, and device actions stay outside the grant.\n\n" +
                    trustedRisk,
                    MessageType.Warning);
                DrawAlepouSessionBindingPicker(suggestedSession);
                if (string.IsNullOrWhiteSpace(grantSessionBinding))
                {
                    EditorGUILayout.HelpBox(
                        "Trusted Development is disabled until a running Alepou session is selected or a manual fallback ID is entered.",
                        MessageType.Info);
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Bounded grant settings", EditorStyles.boldLabel);
                grantDurationMinutes = EditorGUILayout.IntSlider("Expiry (minutes)", grantDurationMinutes, 5, 480);
                grantMaxCommands = EditorGUILayout.IntSlider("Command budget", grantMaxCommands, 1, 200);
                grantMaxActions = EditorGUILayout.IntSlider("Action budget", grantMaxActions, 1, 1000);

                var reversibleActions = AlepouUnityBridgeApprovalPolicy.ReversibleActions;
                var reversibleRisk = AlepouUnityBridgeApprovalPolicy.RiskSummary(reversibleActions);
                EditorGUILayout.HelpBox("Reversible Workspace profile: " + reversibleRisk, MessageType.Warning);
                if (GUILayout.Button("Enable Reversible Workspace Grant"))
                {
                    var confirmed = EditorUtility.DisplayDialog(
                        "Enable delegated automatic approval?",
                        reversibleRisk + "\n\nExpires in " + grantDurationMinutes + " minutes. " +
                        "Budget: " + grantMaxCommands + " commands / " + grantMaxActions + " actions.\n\n" +
                        "The AI cannot extend this grant. You can revoke or stop it immediately from Unity.",
                        "Enable Grant",
                        "Cancel");
                    if (confirmed)
                    {
                        AlepouUnityBridgeApprovalPolicy.EnableReversibleGrant(
                            grantDurationMinutes,
                            grantMaxCommands,
                            grantMaxActions,
                            grantSessionBinding);
                        AlepouUnityBridgeService.RefreshApprovalPolicyState();
                        lastMessage = "Reversible Workspace grant enabled.";
                    }
                }

                showCustomGrant = EditorGUILayout.Foldout(showCustomGrant, "Custom Expert Grant", true);
                if (!showCustomGrant) return;
                var customizableActions = AlepouUnityBridgeApprovalPolicy.CustomizableActions;
                if (customGrantActions == null || customGrantActions.Length != customizableActions.Length)
                {
                    customGrantActions = new bool[customizableActions.Length];
                }
                EditorGUILayout.HelpBox(
                    "Select exact actions. refresh_assets may compile project code; run_tests executes tests; build_player runs build hooks; shader import, lighting bake, persisted prefab/material edits, and the fixed menu allowlist have their own entries. Workflow-owned Play Mode is reserved for session-bound Trusted Development. Destructive operations, general project settings, baked-light clearing, and device actions cannot be included.",
                    MessageType.Warning);
                for (var i = 0; i < customizableActions.Length; i++)
                {
                    customGrantActions[i] = EditorGUILayout.ToggleLeft(customizableActions[i], customGrantActions[i]);
                }
                var selected = customizableActions.Where((action, index) => customGrantActions[index]).ToArray();
                if (selected.Length > 0)
                {
                    EditorGUILayout.HelpBox(AlepouUnityBridgeApprovalPolicy.RiskSummary(selected), MessageType.Warning);
                }
                using (new EditorGUI.DisabledScope(selected.Length == 0))
                {
                    if (GUILayout.Button("Enable Custom Grant"))
                    {
                        var risk = AlepouUnityBridgeApprovalPolicy.RiskSummary(selected);
                        var confirmed = EditorUtility.DisplayDialog(
                            "Enable custom delegated approval?",
                            risk + "\n\nActions:\n- " + string.Join("\n- ", selected) + "\n\n" +
                            "Expires in " + grantDurationMinutes + " minutes. " +
                            "Budget: " + grantMaxCommands + " commands / " + grantMaxActions + " actions.",
                            "Enable Custom Grant",
                            "Cancel");
                        if (confirmed)
                        {
                            AlepouUnityBridgeApprovalPolicy.EnableCustomGrant(
                                selected,
                                grantDurationMinutes,
                                grantMaxCommands,
                                grantMaxActions,
                                grantSessionBinding);
                            AlepouUnityBridgeService.RefreshApprovalPolicyState();
                            lastMessage = "Custom delegated approval grant enabled.";
                        }
                    }
                }
            }
        }

        private void DrawAlepouSessionBindingPicker(string suggestedSession)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Alepou project and session", EditorStyles.boldLabel);
            if (GUILayout.Button("Refresh Alepou Sessions"))
            {
                RefreshAlepouBindingDiscovery();
            }

            if (bindingDiscovery == null)
            {
                EditorGUILayout.HelpBox(
                    "Click Refresh Alepou Sessions to match this Unity project to the running Alepou app. No session ID needs to be copied.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    bindingDiscovery.message,
                    bindingDiscovery.ok && (bindingDiscovery.sessions?.Length ?? 0) > 0 ? MessageType.Info : MessageType.Warning);
                if (bindingDiscovery.ok)
                {
                    EditorGUILayout.LabelField(
                        "Matched project",
                        (!string.IsNullOrWhiteSpace(bindingDiscovery.projectName)
                            ? bindingDiscovery.projectName
                            : bindingDiscovery.projectId) + "\n" + bindingDiscovery.projectPath,
                        EditorStyles.wordWrappedLabel);
                }

                var sessions = bindingDiscovery.sessions ?? Array.Empty<AlepouUnityBindingSession>();
                if (sessions.Length > 0)
                {
                    var matching = Array.FindIndex(
                        sessions,
                        session => string.Equals(session.sessionId, grantSessionBinding, StringComparison.Ordinal));
                    if (matching >= 0) bindingSessionIndex = matching;
                    bindingSessionIndex = Mathf.Clamp(bindingSessionIndex, 0, sessions.Length - 1);
                    var labels = sessions.Select(AlepouUnityBridgeDiscovery.SessionLabel).ToArray();
                    var nextIndex = EditorGUILayout.Popup("Running session", bindingSessionIndex, labels);
                    if (nextIndex != bindingSessionIndex || string.IsNullOrWhiteSpace(grantSessionBinding))
                    {
                        bindingSessionIndex = nextIndex;
                        grantSessionBinding = sessions[bindingSessionIndex].sessionId;
                    }
                }
            }

            showManualSessionBinding = EditorGUILayout.Foldout(
                showManualSessionBinding,
                "Manual session ID fallback",
                true);
            if (showManualSessionBinding)
            {
                grantSessionBinding = EditorGUILayout.TextField("Alepou session ID", grantSessionBinding);
                if (!string.IsNullOrWhiteSpace(suggestedSession)
                    && !string.Equals(suggestedSession, grantSessionBinding, StringComparison.Ordinal)
                    && GUILayout.Button("Use Latest Workflow Session"))
                {
                    grantSessionBinding = suggestedSession;
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(grantSessionBinding)))
            {
                if (GUILayout.Button("Bind + Trust This Alepou Session", GUILayout.Height(28)))
                {
                    var binding = grantSessionBinding.Trim();
                    AlepouUnityBridgeApprovalPolicy.EnableTrustedDevelopmentGrant(binding);
                    AlepouUnityBridgeService.RefreshApprovalPolicyState();
                    lastMessage = "Trusted Development is on for Alepou session " + binding + ".";
                }
            }
        }

        private void RefreshAlepouBindingDiscovery()
        {
            if (!this) return;
            bindingDiscovery = AlepouUnityBridgeDiscovery.DiscoverCurrentProject();
            var sessions = bindingDiscovery.sessions ?? Array.Empty<AlepouUnityBindingSession>();
            if (bindingDiscovery.ok && sessions.Length > 0)
            {
                var matching = Array.FindIndex(
                    sessions,
                    session => string.Equals(session.sessionId, grantSessionBinding, StringComparison.Ordinal));
                bindingSessionIndex = matching >= 0 ? matching : 0;
                grantSessionBinding = sessions[bindingSessionIndex].sessionId;
                showManualSessionBinding = false;
            }
            else
            {
                showManualSessionBinding = true;
            }
            Repaint();
        }

        private void DrawWorkflowStatus()
        {
            var workflow = AlepouUnityBridgeWorkflowRunner.Snapshot();
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Durable Workflows", EditorStyles.boldLabel);
                if (!workflow.active)
                {
                    EditorGUILayout.LabelField(workflow.pendingCount == 0
                        ? "No active or queued workflows."
                        : workflow.pendingCount + " workflow(s) queued.");
                    return;
                }
                EditorGUILayout.LabelField("Workflow", workflow.title ?? workflow.workflowId);
                EditorGUILayout.LabelField("Status", workflow.status ?? "unknown");
                EditorGUILayout.LabelField("Step", (workflow.currentStepId ?? "(none)") + " · " + (workflow.currentStepType ?? "unknown"));
                if (!string.IsNullOrWhiteSpace(workflow.stepPhase)) EditorGUILayout.LabelField("Phase", workflow.stepPhase);
                if (!string.IsNullOrWhiteSpace(workflow.testRunId))
                {
                    var testRun = AlepouUnityBridgeTestRunner.Snapshot(workflow.testRunId);
                    EditorGUILayout.LabelField("Test run", workflow.testRunId + " · " + testRun.status);
                    EditorGUILayout.LabelField(
                        "Test counts",
                        testRun.passedCount + " passed · " + testRun.failedCount + " failed · " +
                        testRun.skippedCount + " skipped · " + testRun.inconclusiveCount + " inconclusive");
                    if (!string.IsNullOrWhiteSpace(testRun.currentTestName))
                    {
                        EditorGUILayout.LabelField("Current test", testRun.currentTestName, EditorStyles.wordWrappedLabel);
                    }
                }
                if (!string.IsNullOrWhiteSpace(workflow.buildJobId))
                {
                    var buildJob = AlepouUnityBridgeBuildRunner.Snapshot(workflow.buildJobId);
                    EditorGUILayout.LabelField("Build job", workflow.buildJobId + " · " + buildJob.status);
                    EditorGUILayout.LabelField(
                        "Build target",
                        (buildJob.target ?? "unknown") + " · " + (buildJob.progressStage ?? "unknown"));
                    if (!string.IsNullOrWhiteSpace(buildJob.outputProjectPath))
                    {
                        EditorGUILayout.LabelField("Build output", buildJob.outputProjectPath, EditorStyles.wordWrappedLabel);
                    }
                    if (!string.IsNullOrWhiteSpace(buildJob.artifactManifestFile))
                    {
                        EditorGUILayout.LabelField("Artifact manifest", buildJob.artifactManifestFile, EditorStyles.wordWrappedLabel);
                    }
                }
                if (workflow.playModeOwned)
                {
                    EditorGUILayout.HelpBox(
                        "This workflow owns the current Play Mode session. Cancelling or using STOP Automatic Processing requests an immediate return to Edit Mode before the workflow is finalized.",
                        MessageType.Warning);
                }
                if (!string.IsNullOrWhiteSpace(workflow.runtimeCaptureId))
                {
                    EditorGUILayout.LabelField("Runtime capture", workflow.runtimeCaptureId);
                    if (!string.IsNullOrWhiteSpace(workflow.runtimeCaptureResultFile))
                    {
                        EditorGUILayout.LabelField("Runtime evidence", workflow.runtimeCaptureResultFile, EditorStyles.wordWrappedLabel);
                    }
                }
                if (!string.IsNullOrWhiteSpace(workflow.message)) EditorGUILayout.HelpBox(workflow.message, workflow.waitingForHuman ? MessageType.Warning : MessageType.Info);
                if (workflow.waitingForHuman)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Approve Current Step"))
                        {
                            lastMessage = AlepouUnityBridgeWorkflowRunner.ApproveCurrentGate()
                                ? "Workflow gate approved."
                                : "No workflow gate is waiting.";
                        }
                        if (GUILayout.Button("Reject Current Step"))
                        {
                            lastMessage = AlepouUnityBridgeWorkflowRunner.RejectCurrentGate()
                                ? "Workflow gate rejected."
                                : "No workflow gate is waiting.";
                        }
                    }
                }
                var stopsPlayMode = workflow.playModeOwned ||
                                    string.Equals(workflow.currentStepType, "enter_play_mode", StringComparison.Ordinal) ||
                                    string.Equals(workflow.currentStepType, "exit_play_mode", StringComparison.Ordinal);
                if (GUILayout.Button(stopsPlayMode ? "STOP Play Mode + Cancel Workflow" : "Cancel Active Workflow"))
                {
                    lastMessage = AlepouUnityBridgeWorkflowRunner.CancelActive("Cancelled by a human in the Unity Bridge window.")
                        ? (stopsPlayMode
                            ? "Play Mode stop and workflow cancellation requested."
                            : "Active workflow cancellation requested.")
                        : "No workflow was active.";
                }
            }
        }

        internal void ServiceSelectionChanged()
        {
            if (!autoExport) return;
            ExportSnapshot(false);
            AlepouUnityBridgeService.RepaintWindows();
        }

        internal void ServiceUpdate()
        {
            ReloadSettings();
            var now = EditorApplication.timeSinceStartup;

            // Fixed-interval export (legacy behavior).
            if (autoExportIntervalSeconds > 0 && now >= nextIntervalExportAt)
            {
                nextIntervalExportAt = now + autoExportIntervalSeconds;
                ExportSnapshot(false);
                AlepouUnityBridgeService.RepaintWindows();
            }

            // Detect compile/import finishing and external asset changes -> refresh state.
            var compiling = EditorApplication.isCompiling;
            var updating = EditorApplication.isUpdating;
            if ((prevCompiling && !compiling) || (prevUpdating && !updating)) eventExportDirty = true;
            prevCompiling = compiling;
            prevUpdating = updating;
            // Event-driven export (debounced), only while Unity is idle.
            if (exportOnChange && eventExportDirty && !compiling && !updating && now >= nextEventExportAt)
            {
                eventExportDirty = false;
                nextEventExportAt = now + 1.0;
                ExportSnapshot(false);
                AlepouUnityBridgeService.RepaintWindows();
            }

        }

        internal bool ProcessAutoApprovals()
        {
            string[] pending;
            try { pending = Directory.GetFiles(CommandsDir("pending"), "*.json"); }
            catch { return false; }
            var processedAny = false;
            foreach (var file in pending.OrderBy(File.GetLastWriteTimeUtc))
            {
                string raw;
                try { raw = File.ReadAllText(file); }
                catch { continue; }
                var command = ParseCommand(raw, file);
                var decision = EvaluatePolicy(command, false);
                if (!decision.allowed)
                {
                    RecordPendingPolicyDecisionOnce(file, command, raw, decision);
                    continue;
                }
                if (TryApplyPendingCommand(file, true)) processedAny = true;
            }
            if (processedAny) AlepouUnityBridgeService.RepaintWindows();
            return processedAny;
        }

        internal void ServicePlayModeStateChanged(PlayModeStateChange change)
        {
            if (!autoExport && autoExportIntervalSeconds <= 0) return;
            ExportSnapshot(false);
            AlepouUnityBridgeService.RepaintWindows();
        }

        private static int NormalizeInterval(int raw)
        {
            return AutoExportIntervals.Contains(raw) ? raw : 0;
        }

        private static string DefaultOutputPath()
        {
            return Path.Combine(ProjectRoot(), "plan", "unity");
        }

        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        internal string CommandsDir(string name)
        {
            return Path.Combine(outputPath, "commands", name);
        }

        internal void EnsureBridgeFolders()
        {
            Directory.CreateDirectory(outputPath);
            foreach (var name in new[] { "pending", "processing", "approved", "applied", "rejected", "failed", "archive" })
            {
                Directory.CreateDirectory(CommandsDir(name));
            }
            Directory.CreateDirectory(Path.Combine(outputPath, "recipes"));
            WriteRecipeFiles();
        }

        private void DrawPendingCommands()
        {
            EnsureBridgeFolders();
            var pending = Directory.GetFiles(CommandsDir("pending"), "*.json").OrderBy(File.GetLastWriteTimeUtc).ToArray();
            if (pending.Length == 0)
            {
                EditorGUILayout.LabelField("No pending commands.");
                return;
            }

            foreach (var file in pending)
            {
                string raw;
                try { raw = File.ReadAllText(file); }
                catch (IOException) { continue; }
                var command = ParseCommand(raw, file);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField(command.titleOrId, EditorStyles.boldLabel);
                    if (!string.IsNullOrWhiteSpace(command.summary))
                    {
                        EditorGUILayout.LabelField(command.summary, EditorStyles.wordWrappedLabel);
                    }
                    EditorGUILayout.LabelField(Path.GetFileName(file), EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Approve"))
                        {
                            lastMessage = AlepouUnityBridgeService.ApproveCommand(file)
                                ? "Command handed to the processor."
                                : "Command is no longer pending or Unity is busy.";
                            GUIUtility.ExitGUI();
                        }
                        if (GUILayout.Button("Reject"))
                        {
                            lastMessage = AlepouUnityBridgeService.RejectCommand(file)
                                ? "Command rejected."
                                : "Command is no longer pending.";
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        public void ExportSnapshot(bool showMessage = true)
        {
            EnsureBridgeFolders();
            WriteText("bridge-state.json", JsonUtility.ToJson(BuildBridgeState(), true));
            WriteText("status.md", BuildStatusMarkdown());
            WriteText("capabilities.json", BuildCapabilitiesJson());
            WriteText("compile-status.json", JsonUtility.ToJson(BuildCompileStatus(), true));
            WriteText("active-scene.json", JsonUtility.ToJson(BuildActiveScene(), true));
            WriteText("open-scenes.json", JsonUtility.ToJson(BuildOpenScenes(), true));
            WriteText("scene-hierarchy.json", JsonUtility.ToJson(BuildHierarchySnapshot(), true));
            WriteText("selection.json", JsonUtility.ToJson(BuildSelectionSnapshot(), true));
            WriteText("inspector-snapshot.json", JsonUtility.ToJson(BuildInspectorSnapshot(), true));
            WriteText("selected-subtree-inspector.json", JsonUtility.ToJson(BuildSelectedSubtreeInspectorSnapshot(), true));
            WriteText("console.jsonl", BuildConsoleJsonl());
            WriteText("console-summary.md", BuildConsoleSummary());
            WriteText("build-settings.json", JsonUtility.ToJson(BuildBuildSettings(), true));
            WriteText("packages.json", JsonUtility.ToJson(BuildPackagesSnapshot(), true));
            WriteText("asset-index.json", JsonUtility.ToJson(BuildAssetIndex(), true));
            WriteText("diagnostics.md", BuildDiagnostics());
            var commandResults = BuildCommandResultLog();
            WriteText("command-results.json", JsonUtility.ToJson(commandResults, true));
            WriteText("command-results.md", BuildCommandResultsMarkdown(commandResults));
            if (showMessage)
            {
                lastMessage = "Snapshot exported to " + outputPath;
            }
        }

        // AI-eyes: render the Scene view, Game view, and selected supported Editor windows
        // to PNG so the AI can actually see the editor/game. Pull-only (via a
        // capture_view command), downscaled to a
        // <=1280px long edge — a deliberate, opt-in "look". See plan/unity-bridge-vision.md.
        private const int MaxViewCaptureEdge = 1280;
        private const int MaxUiBuilderCaptureEdge = 1920;
        private const string UiBuilderWindowTypeName = "Unity.UI.Builder.Builder";
        private const string UiBuilderImageFile = "ui-builder-view.png";
        private const string UiBuilderMetadataFile = "ui-builder-view.json";

        private string CaptureView(BridgeAction action, List<string> changed)
        {
            var which = string.IsNullOrWhiteSpace(action.target) ? "both" : action.target.Trim().ToLowerInvariant();
            if (which != "scene" && which != "game" && which != "both" && which != "ui-builder")
            {
                throw new InvalidOperationException("capture_view target must be scene, game, both, or ui-builder.");
            }
            var captured = new List<string>();
            if (which == "scene" || which == "both")
            {
                var scenePath = CaptureSceneView();
                if (scenePath != null) { captured.Add(scenePath); changed.Add(scenePath); }
            }
            if (which == "game" || which == "both")
            {
                var gamePath = CaptureGameView();
                if (gamePath != null) { captured.Add(gamePath); changed.Add(gamePath); }
            }
            if (which == "ui-builder")
            {
                captured.Add(CaptureUiBuilderWindow(changed));
            }
            if (captured.Count == 0)
            {
                throw new InvalidOperationException("capture_view captured nothing (no active Scene view, and no enabled Camera for the Game view).");
            }
            return "capture_view " + string.Join(", ", captured);
        }

        private string OpenUiBuilderWindow(BridgeAction action, List<string> changed)
        {
            if (!string.IsNullOrWhiteSpace(action.assetPath))
            {
                return OpenUiBuilderDocument(action.assetPath, changed);
            }

            var existing = FindUiBuilderWindow();
            if (existing != null)
            {
                existing.Focus();
                changed.Add("Editor/UI Builder");
                return "open_ui_builder focused the existing UI Builder window";
            }

            var builderType = ResolveUiBuilderWindowType();
            var showWindow = builderType == null
                ? null
                : builderType.GetMethod(
                    "ShowWindow",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
            if (showWindow == null)
            {
                throw new InvalidOperationException("This Unity version does not expose the UI Builder window opener.");
            }

            EditorWindow opened;
            try
            {
                opened = showWindow.Invoke(null, null) as EditorWindow;
            }
            catch (TargetInvocationException ex)
            {
                var cause = ex.InnerException ?? ex;
                throw new InvalidOperationException("Unity could not open UI Builder: " + cause.Message, cause);
            }
            if (opened == null) opened = FindUiBuilderWindow();
            if (opened == null)
            {
                throw new InvalidOperationException("Unity invoked the UI Builder opener but no UI Builder window appeared.");
            }
            opened.Focus();
            changed.Add("Editor/UI Builder");
            return "open_ui_builder opened and focused UI Builder";
        }

        private string OpenUiBuilderDocument(string rawAssetPath, List<string> changed)
        {
            var assetPath = NormalizeAssetPath(rawAssetPath, "assetPath", false);
            if (!assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("open_ui_builder assetPath must end with .uxml: " + rawAssetPath);
            }
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                throw new InvalidOperationException("open_ui_builder assetPath must identify a UXML file, not a folder: " + assetPath);
            }

            var existing = FindUiBuilderWindow();
            if (existing != null && existing.hasUnsavedChanges)
            {
                throw new InvalidOperationException(
                    "UI Builder has unsaved changes. open_ui_builder refused to load another document because Unity could open a blocking save prompt; save or discard those changes explicitly first.");
            }

            var document = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(assetPath);
            if (document == null)
            {
                throw new InvalidOperationException("UXML VisualTreeAsset not found: " + assetPath);
            }
            if (!AssetDatabase.OpenAsset(document))
            {
                throw new InvalidOperationException("Unity did not accept the UXML document for opening: " + assetPath);
            }

            var opened = FindUiBuilderWindow();
            if (opened == null)
            {
                throw new InvalidOperationException("Unity opened the UXML asset but no UI Builder window appeared: " + assetPath);
            }
            opened.Focus();
            changed.Add("Editor/UI Builder");
            return "open_ui_builder opened " + assetPath + " in UI Builder";
        }

        private string FrameUiBuilderDocument()
        {
            var uiBuilder = FindUiBuilderWindow();
            if (uiBuilder == null)
            {
                throw new InvalidOperationException(
                    "UI Builder is not open. Open the intended UXML document, then submit frame_ui_builder_document in a new command.");
            }

            var builderType = uiBuilder.GetType();
            var documentProperty = builderType.GetProperty("document", BindingFlags.Public | BindingFlags.Instance);
            var document = documentProperty == null ? null : documentProperty.GetValue(uiBuilder, null);
            var documentPathProperty = document == null
                ? null
                : document.GetType().GetProperty("uxmlPath", BindingFlags.Public | BindingFlags.Instance);
            var documentPath = documentPathProperty == null
                ? null
                : documentPathProperty.GetValue(document, null) as string;
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                throw new InvalidOperationException(
                    "UI Builder does not have a loaded UXML document. Open an imported Assets/.../*.uxml document before framing it.");
            }

            var documentRootProperty = builderType.GetProperty(
                "documentRootElement",
                BindingFlags.Public | BindingFlags.Instance);
            var documentRoot = documentRootProperty == null
                ? null
                : documentRootProperty.GetValue(uiBuilder, null) as VisualElement;
            if (documentRoot == null)
            {
                throw new InvalidOperationException(
                    "UI Builder has a loaded UXML document but its document root is not ready. Wait for one editor update, then submit frame_ui_builder_document again.");
            }
            var frameTarget = ResolveUiBuilderFrameTarget(documentRoot);

            var viewportProperty = builderType.GetProperty("viewport", BindingFlags.Public | BindingFlags.Instance);
            var viewport = viewportProperty == null ? null : viewportProperty.GetValue(uiBuilder, null);
            if (viewport == null)
            {
                throw new InvalidOperationException(
                    "UI Builder is open but its viewport is not ready. Wait for one editor update, then submit frame_ui_builder_document again.");
            }

            var frameMethod = ResolveUiBuilderFrameDocumentMethod();
            if (frameMethod == null || !frameMethod.DeclaringType.IsInstanceOfType(viewport))
            {
                throw new InvalidOperationException(
                    "This Unity version does not expose the UI Builder whole-document framing API. Capture remains available without automatic framing.");
            }

            try
            {
                // Use UI Builder's own Frame Selected path. Capture is responsible for preserving
                // the complete editor-window extent; framing only changes transient viewport state.
                frameMethod.Invoke(viewport, new object[] { frameTarget });
                uiBuilder.Repaint();
            }
            catch (TargetInvocationException ex)
            {
                var cause = ex.InnerException ?? ex;
                throw new InvalidOperationException("Unity could not frame the UI Builder document: " + cause.Message, cause);
            }

            var targetLabel = string.IsNullOrWhiteSpace(frameTarget.name)
                ? frameTarget.GetType().Name
                : "#" + frameTarget.name;
            return "frame_ui_builder_document requested Unity's native fit for " + documentPath +
                " using " + targetLabel +
                "; wait for one editor update before capture_view";
        }

        private static VisualElement ResolveUiBuilderFrameTarget(VisualElement documentRoot)
        {
            var target = documentRoot;
            var targetArea = VisibleArea(documentRoot.worldBound);
            var pending = new Stack<VisualElement>();
            for (var index = documentRoot.childCount - 1; index >= 0; index--)
            {
                pending.Push(documentRoot[index]);
            }

            while (pending.Count > 0)
            {
                var candidate = pending.Pop();
                var candidateArea = VisibleArea(candidate.worldBound);
                if (candidateArea > targetArea)
                {
                    target = candidate;
                    targetArea = candidateArea;
                }
                for (var index = candidate.childCount - 1; index >= 0; index--)
                {
                    pending.Push(candidate[index]);
                }
            }

            return target;
        }

        private static float VisibleArea(Rect bounds)
        {
            if (float.IsNaN(bounds.width) || float.IsInfinity(bounds.width) || bounds.width <= 0f ||
                float.IsNaN(bounds.height) || float.IsInfinity(bounds.height) || bounds.height <= 0f)
            {
                return 0f;
            }
            return bounds.width * bounds.height;
        }

        private string CloseUiBuilderWindow(List<string> changed)
        {
            var uiBuilder = FindUiBuilderWindow();
            if (uiBuilder == null) return "close_ui_builder UI Builder was already closed";
            if (uiBuilder.hasUnsavedChanges)
            {
                throw new InvalidOperationException(
                    "UI Builder has unsaved changes. close_ui_builder refused to close it because Unity would open a blocking save prompt; save or discard those changes explicitly first.");
            }
            uiBuilder.Close();
            changed.Add("Editor/UI Builder");
            return "close_ui_builder closed UI Builder";
        }

        private string CaptureUiBuilderWindow(List<string> changed)
        {
            var metadata = new EditorWindowCaptureMetadata
            {
                schemaVersion = 1,
                target = "ui-builder",
                status = "failed",
                captureApi = "UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow",
                requestedAt = DateTimeOffset.Now.ToString("o"),
                imageFile = UiBuilderImageFile,
                metadataFile = UiBuilderMetadataFile
            };
            var uiBuilder = FindUiBuilderWindow();
            if (uiBuilder == null)
            {
                FailUiBuilderCapture(metadata,
                    "UI Builder is not open. Open Window > UI Toolkit > UI Builder, load the UXML document, then submit a new capture_view command with target ui-builder.",
                    changed);
            }

            metadata.windowType = uiBuilder.GetType().FullName;
            metadata.windowTitle = uiBuilder.titleContent == null ? "" : uiBuilder.titleContent.text;
            metadata.sourceWidthPoints = Math.Max(1, Mathf.RoundToInt(uiBuilder.position.width));
            metadata.sourceHeightPoints = Math.Max(1, Mathf.RoundToInt(uiBuilder.position.height));
            metadata.pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;

            var captureMethod = ResolveCaptureEditorWindowMethod();
            if (captureMethod == null)
            {
                FailUiBuilderCapture(metadata,
                    "This Unity version does not expose the editor-window capture helper required for UI Builder evidence. Scene and Game capture remain available.",
                    changed);
            }

            var sourceWidthPixels = Math.Max(1, Mathf.RoundToInt(metadata.sourceWidthPoints * metadata.pixelsPerPoint));
            var sourceHeightPixels = Math.Max(1, Mathf.RoundToInt(metadata.sourceHeightPoints * metadata.pixelsPerPoint));
            metadata.sourceWidthPixels = sourceWidthPixels;
            metadata.sourceHeightPixels = sourceHeightPixels;
            metadata.capturedAtSourceResolution = true;
            metadata.maxLongEdgePixels = MaxUiBuilderCaptureEdge;
            var scale = Mathf.Min(1f, (float)MaxUiBuilderCaptureEdge / Mathf.Max(sourceWidthPixels, sourceHeightPixels));
            metadata.outputWidthPixels = Math.Max(1, Mathf.RoundToInt(sourceWidthPixels * scale));
            metadata.outputHeightPixels = Math.Max(1, Mathf.RoundToInt(sourceHeightPixels * scale));
            metadata.downscaledAfterCapture = metadata.outputWidthPixels != sourceWidthPixels ||
                metadata.outputHeightPixels != sourceHeightPixels;

            var previousWindow = EditorWindow.focusedWindow;
            metadata.previousWindowType = previousWindow == null ? "" : previousWindow.GetType().FullName;
            metadata.focusChanged = previousWindow != uiBuilder;
            metadata.focusRestored = !metadata.focusChanged;

            var sourceRenderTexture = new RenderTexture(sourceWidthPixels, sourceHeightPixels, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear
            };
            RenderTexture outputRenderTexture = null;
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            string failure = null;
            try
            {
                sourceRenderTexture.Create();
                if (metadata.focusChanged) uiBuilder.Focus();
                uiBuilder.Repaint();
                if (!uiBuilder.hasFocus)
                {
                    failure = "Unity did not give UI Builder focus, so it refused the editor-window capture.";
                }
                else
                {
                    object invocationResult;
                    try
                    {
                        invocationResult = captureMethod.Invoke(null, new object[] { uiBuilder, sourceRenderTexture });
                    }
                    catch (TargetInvocationException ex)
                    {
                        var cause = ex.InnerException ?? ex;
                        throw new InvalidOperationException("Unity's editor-window capture helper failed: " + cause.Message, cause);
                    }
                    if (!(invocationResult is bool) || !(bool)invocationResult)
                    {
                        failure = "Unity refused to capture the focused UI Builder window.";
                    }
                    else
                    {
                        if (metadata.downscaledAfterCapture)
                        {
                            outputRenderTexture = new RenderTexture(
                                metadata.outputWidthPixels,
                                metadata.outputHeightPixels,
                                0,
                                RenderTextureFormat.ARGB32);
                            outputRenderTexture.Create();
                            Graphics.Blit(sourceRenderTexture, outputRenderTexture);
                        }

                        RenderTexture.active = outputRenderTexture == null
                            ? sourceRenderTexture
                            : outputRenderTexture;
                        texture = new Texture2D(metadata.outputWidthPixels, metadata.outputHeightPixels, TextureFormat.RGBA32, false);
                        texture.ReadPixels(new Rect(0, 0, metadata.outputWidthPixels, metadata.outputHeightPixels), 0, 0);
                        texture.Apply();
                        if (!HasVisibleEditorPixels(texture))
                        {
                            failure = "Unity returned a blank UI Builder frame. If the window was just opened, wait for one editor update and submit capture_view in a new command.";
                        }
                        else
                        {
                            Directory.CreateDirectory(outputPath);
                            File.WriteAllBytes(Path.Combine(outputPath, UiBuilderImageFile), texture.EncodeToPNG());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (outputRenderTexture != null)
                {
                    outputRenderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(outputRenderTexture);
                }
                sourceRenderTexture.Release();
                UnityEngine.Object.DestroyImmediate(sourceRenderTexture);
                if (metadata.focusChanged && previousWindow != null)
                {
                    previousWindow.Focus();
                    metadata.focusRestored = previousWindow.hasFocus;
                    if (!metadata.focusRestored && string.IsNullOrWhiteSpace(failure))
                    {
                        failure = "UI Builder was captured, but Unity did not restore the previously focused editor window.";
                    }
                }
                else if (metadata.focusChanged)
                {
                    metadata.focusRestored = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(failure))
            {
                FailUiBuilderCapture(metadata, failure, changed);
            }

            metadata.status = "captured";
            metadata.message = !metadata.focusChanged
                ? "Captured the already-focused live UI Builder editor window."
                : previousWindow == null
                    ? "Captured the live UI Builder editor window; Unity had no previously focused editor window to restore."
                    : "Captured the live UI Builder editor window and restored the previous Unity focus.";
            metadata.capturedAt = DateTimeOffset.Now.ToString("o");
            WriteUiBuilderCaptureMetadata(metadata, changed);
            changed.Add(UiBuilderImageFile);
            return UiBuilderImageFile;
        }

        private static EditorWindow FindUiBuilderWindow()
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(window => window != null &&
                    string.Equals(window.GetType().FullName, UiBuilderWindowTypeName, StringComparison.Ordinal))
                .OrderByDescending(window => window.hasFocus)
                .FirstOrDefault();
        }

        private static Type ResolveUiBuilderWindowType()
        {
            return Type.GetType(UiBuilderWindowTypeName + ", UnityEditor.UIBuilderModule", false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(UiBuilderWindowTypeName, false))
                    .FirstOrDefault(type => type != null);
        }

        private static MethodInfo ResolveUiBuilderFrameDocumentMethod()
        {
            var builderType = ResolveUiBuilderWindowType();
            var viewportProperty = builderType == null
                ? null
                : builderType.GetProperty("viewport", BindingFlags.Public | BindingFlags.Instance);
            var viewportType = viewportProperty == null ? null : viewportProperty.PropertyType;
            return viewportType == null
                ? null
                : viewportType.GetMethod(
                    "FitViewport",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(VisualElement) },
                    null);
        }

        private static MethodInfo ResolveCaptureEditorWindowMethod()
        {
            var internalEditorUtility = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditorInternal.InternalEditorUtility", false))
                .FirstOrDefault(type => type != null);
            return internalEditorUtility == null
                ? null
                : internalEditorUtility.GetMethod(
                    "CaptureEditorWindow",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(EditorWindow), typeof(RenderTexture) },
                    null);
        }

        private static bool HasVisibleEditorPixels(Texture2D texture)
        {
            if (texture == null) return false;
            var pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0) return false;
            var step = Math.Max(1, pixels.Length / 4096);
            var visibleSamples = 0;
            for (var index = 0; index < pixels.Length; index += step)
            {
                var pixel = pixels[index];
                if (pixel.a > 8 && (pixel.r > 8 || pixel.g > 8 || pixel.b > 8))
                {
                    visibleSamples++;
                    if (visibleSamples >= 8) return true;
                }
            }
            return false;
        }

        private void FailUiBuilderCapture(EditorWindowCaptureMetadata metadata, string message, List<string> changed)
        {
            var staleImagePath = Path.Combine(outputPath, UiBuilderImageFile);
            if (File.Exists(staleImagePath)) File.Delete(staleImagePath);
            metadata.status = "failed";
            metadata.message = message;
            metadata.capturedAt = DateTimeOffset.Now.ToString("o");
            WriteUiBuilderCaptureMetadata(metadata, changed);
            throw new InvalidOperationException(message);
        }

        private void WriteUiBuilderCaptureMetadata(EditorWindowCaptureMetadata metadata, List<string> changed)
        {
            WriteText(UiBuilderMetadataFile, JsonUtility.ToJson(metadata, true));
            if (!changed.Contains(UiBuilderMetadataFile)) changed.Add(UiBuilderMetadataFile);
        }

        private string CaptureSceneView()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null) return null;
            return RenderCameraToPng(view.camera, "scene-view.png");
        }

        private string CaptureGameView()
        {
            return CaptureGameView("game-view.png");
        }

        private string CaptureGameView(string relativeFileName)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                foreach (var candidate in Camera.allCameras)
                {
                    if (candidate != null && candidate.enabled) { cam = candidate; break; }
                }
            }
            if (cam == null) return null;
            return RenderCameraToPng(cam, relativeFileName);
        }

        private string RenderCameraToPng(Camera cam, string fileName)
        {
            var width = cam.pixelWidth > 0 ? cam.pixelWidth : 1280;
            var height = cam.pixelHeight > 0 ? cam.pixelHeight : 720;
            var scale = Mathf.Min(1f, (float)MaxViewCaptureEdge / Mathf.Max(width, height));
            var renderWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            var renderHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));

            var rt = new RenderTexture(renderWidth, renderHeight, 24);
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(renderWidth, renderHeight, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, renderWidth, renderHeight), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                Directory.CreateDirectory(outputPath);
                var fullPath = Path.Combine(outputPath, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllBytes(fullPath, png);
                return fileName.Replace("\\", "/");
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        internal RuntimeCaptureResult CaptureRuntime(RuntimeCaptureRequest request)
        {
            if (request == null) throw new InvalidOperationException("runtime_capture requires a request.");
            var workflowId = SafeCaptureToken(request.workflowId, "workflowId");
            var captureId = SafeCaptureToken(request.captureId, "captureId");
            var maxConsoleEntries = Math.Max(1, Math.Min(100, request.maxConsoleEntries > 0 ? request.maxConsoleEntries : 50));
            var probes = request.probes ?? Array.Empty<RuntimeProbeRequest>();
            if (probes.Length > 16) throw new InvalidOperationException("runtime_capture exceeds the 16-object probe limit.");

            var relativeDirectory = Path.Combine("runtime-captures", workflowId, captureId);
            var result = new RuntimeCaptureResult
            {
                schemaVersion = 1,
                captureId = captureId,
                workflowId = workflowId,
                status = "failed",
                message = "Runtime capture did not complete.",
                capturedAt = DateTimeOffset.UtcNow.ToString("o"),
                playModeState = EditorApplication.isPlaying ? "playing" : "edit-mode",
                consoleSinceAt = request.consoleSinceAt,
                consoleEntries = Array.Empty<RuntimeConsoleEntry>(),
                probes = Array.Empty<RuntimeObjectProbe>()
            };
            result.resultFile = Path.Combine(relativeDirectory, "runtime-capture.json").Replace("\\", "/");
            if (File.Exists(Path.Combine(outputPath, result.resultFile)))
            {
                throw new InvalidOperationException("runtime_capture captureId already has evidence for this workflow and cannot be overwritten.");
            }

            try
            {
                if (!EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                {
                    throw new InvalidOperationException("runtime_capture requires Unity to be stably in Play Mode.");
                }

                var gameViewFile = CaptureGameView(Path.Combine(relativeDirectory, "game-view.png"));
                if (string.IsNullOrWhiteSpace(gameViewFile))
                {
                    throw new InvalidOperationException("runtime_capture found no enabled Camera for the bounded Game-view image.");
                }
                result.gameViewFile = gameViewFile;

                DateTimeOffset consoleSince;
                if (!DateTimeOffset.TryParse(request.consoleSinceAt, out consoleSince))
                {
                    consoleSince = DateTimeOffset.UtcNow;
                    result.consoleSinceAt = consoleSince.ToString("o");
                }
                var consoleMatches = AlepouUnityBridgeLogCapture.Entries
                    .Where(entry => entry.playMode && EntryAtOrAfter(entry, consoleSince))
                    .ToArray();
                result.consoleMatchedCount = consoleMatches.Length;
                result.consoleEntries = consoleMatches
                    .Skip(Math.Max(0, consoleMatches.Length - maxConsoleEntries))
                    .Select(RuntimeConsoleEntryFor)
                    .ToArray();
                result.consoleReturnedCount = result.consoleEntries.Length;
                result.consoleTruncated = result.consoleMatchedCount > result.consoleReturnedCount;

                var probeResults = probes.Select(CaptureRuntimeProbe).ToArray();
                result.probes = probeResults;
                var failedProbe = probeResults.FirstOrDefault(probe => !probe.found || !string.IsNullOrWhiteSpace(probe.message));
                if (failedProbe != null)
                {
                    throw new InvalidOperationException(
                        "runtime_capture probe failed for " + failedProbe.objectPath + ": " +
                        (failedProbe.message ?? "target was not found."));
                }

                result.status = "completed";
                result.message =
                    "Captured one bounded Game-view image, " +
                    result.consoleReturnedCount.ToString(CultureInfo.InvariantCulture) + " console entr" +
                    (result.consoleReturnedCount == 1 ? "y" : "ies") + ", and " +
                    probeResults.Length.ToString(CultureInfo.InvariantCulture) + " targeted object probe(s).";
            }
            catch (Exception ex)
            {
                result.status = "failed";
                result.message = ex.Message;
            }

            WriteText(result.resultFile, JsonUtility.ToJson(result, true));
            WriteText("runtime-capture-status.json", JsonUtility.ToJson(result, true));
            return result;
        }

        private RuntimeObjectProbe CaptureRuntimeProbe(RuntimeProbeRequest request)
        {
            var result = new RuntimeObjectProbe
            {
                objectPath = request == null ? null : request.objectPath,
                found = false,
                components = Array.Empty<RuntimeComponentProbe>()
            };
            if (request == null || string.IsNullOrWhiteSpace(request.objectPath))
            {
                result.message = "A runtime probe requires objectPath.";
                return result;
            }
            if (request.objectPath.Trim().Length > 512)
            {
                result.message = "A runtime probe objectPath exceeds 512 characters.";
                return result;
            }
            var requestedTypes = (request.componentTypes ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requestedTypes.Length == 0)
            {
                result.message = "A runtime probe requires at least one explicit componentTypes entry.";
                return result;
            }
            if (requestedTypes.Length > 8)
            {
                result.message = "A runtime probe exceeds the 8-component-type limit.";
                return result;
            }
            if (requestedTypes.Any(value => value.Length > 256))
            {
                result.message = "A runtime probe component type exceeds 256 characters.";
                return result;
            }
            var requestedProperties = (request.propertyPaths ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requestedProperties.Length > 32)
            {
                result.message = "A runtime probe exceeds the 32-property limit.";
                return result;
            }
            if (requestedProperties.Any(value => value.Length > 512))
            {
                result.message = "A runtime probe property path exceeds 512 characters.";
                return result;
            }

            var go = FindObjectByPath(request.objectPath);
            if (go == null)
            {
                result.message = "Runtime object was not found in the active scene.";
                return result;
            }
            result.objectPath = ObjectPath(go);
            result.activeSelf = go.activeSelf;
            result.activeInHierarchy = go.activeInHierarchy;

            var componentResults = new List<RuntimeComponentProbe>();
            var missingTypes = new List<string>();
            var components = go.GetComponents<Component>().Where(component => component != null).ToArray();
            foreach (var requestedType in requestedTypes)
            {
                var matches = components.Where(component =>
                    string.Equals(component.GetType().Name, requestedType, StringComparison.Ordinal) ||
                    string.Equals(component.GetType().FullName, requestedType, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 0)
                {
                    missingTypes.Add(requestedType);
                    continue;
                }
                var remainingComponentBudget = Math.Max(0, 16 - componentResults.Count);
                if (matches.Length > remainingComponentBudget)
                {
                    result.message = "A runtime probe exceeds the 16-component result limit.";
                }
                foreach (var component in matches.Take(remainingComponentBudget))
                {
                    string propertyError;
                    var properties = RuntimePropertiesFor(component, requestedProperties, out propertyError);
                    componentResults.Add(new RuntimeComponentProbe
                    {
                        requestedType = requestedType,
                        type = component.GetType().FullName ?? component.GetType().Name,
                        enabled = component is Behaviour behaviour ? behaviour.enabled.ToString().ToLowerInvariant() : "n/a",
                        properties = properties
                    });
                    if (!string.IsNullOrWhiteSpace(propertyError))
                    {
                        result.message = propertyError;
                    }
                }
            }
            result.components = componentResults.ToArray();
            result.found = true;
            if (missingTypes.Count > 0)
            {
                result.message = "Requested component type(s) were not found: " + string.Join(", ", missingTypes) + ".";
            }
            return result;
        }

        private RuntimePropertyProbe[] RuntimePropertiesFor(Component component, string[] requestedPaths, out string error)
        {
            error = null;
            var properties = new List<RuntimePropertyProbe>();
            try
            {
                var serialized = new SerializedObject(component);
                if (requestedPaths != null && requestedPaths.Length > 0)
                {
                    var missing = new List<string>();
                    foreach (var pathValue in requestedPaths)
                    {
                        var property = serialized.FindProperty(pathValue);
                        if (property == null)
                        {
                            missing.Add(pathValue);
                            continue;
                        }
                        properties.Add(RuntimePropertyFor(property));
                    }
                    if (missing.Count > 0)
                    {
                        error = "Requested serialized property path(s) were not found on " +
                                component.GetType().Name + ": " + string.Join(", ", missing) + ".";
                    }
                    return properties.ToArray();
                }

                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren) && properties.Count < 50)
                {
                    enterChildren = false;
                    if (iterator.propertyPath == "m_Script") continue;
                    properties.Add(RuntimePropertyFor(iterator));
                }
                return properties.ToArray();
            }
            catch (Exception ex)
            {
                error = "Could not serialize runtime component " + component.GetType().Name + ": " + ex.Message;
                return properties.ToArray();
            }
        }

        private static RuntimePropertyProbe RuntimePropertyFor(SerializedProperty property)
        {
            return new RuntimePropertyProbe
            {
                path = property.propertyPath,
                displayName = property.displayName,
                type = property.propertyType.ToString(),
                value = Clip(SerializedValue(property), 2000)
            };
        }

        private static RuntimeConsoleEntry RuntimeConsoleEntryFor(ConsoleEntry entry)
        {
            return new RuntimeConsoleEntry
            {
                time = entry.time,
                level = entry.level,
                condition = Clip(entry.condition, 2000),
                stackTrace = Clip(entry.stackTrace, 8000),
                activeScene = entry.activeScene,
                selectedObject = entry.selectedObject
            };
        }

        private static bool EntryAtOrAfter(ConsoleEntry entry, DateTimeOffset baseline)
        {
            DateTimeOffset timestamp;
            return entry != null && DateTimeOffset.TryParse(entry.time, out timestamp) && timestamp >= baseline;
        }

        private static string SafeCaptureToken(string value, string label)
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > 120 ||
                trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')))
            {
                throw new InvalidOperationException("runtime_capture " + label + " is invalid.");
            }
            return trimmed;
        }

        private static string Clip(string value, int limit)
        {
            var text = value ?? "";
            return text.Length <= limit ? text : text.Substring(0, limit) + "…";
        }

        // ── Project & object visibility (t-367) ──
        // Summarized, capped asset index so the AI has a project map without a huge dump.
        private const int AssetIndexListCap = 300;
        private const int AssetQueryCap = 200;

        private AssetIndex BuildAssetIndex()
        {
            var scenes = FindAssetPaths("t:Scene", AssetIndexListCap, out var sceneTotal);
            var prefabs = FindAssetPaths("t:Prefab", AssetIndexListCap, out var prefabTotal);
            var scriptableObjects = FindAssetPaths("t:ScriptableObject", AssetIndexListCap, out var soTotal);
            return new AssetIndex
            {
                exportedAt = DateTimeOffset.Now.ToString("o"),
                listCap = AssetIndexListCap,
                counts = new AssetCounts
                {
                    scenes = sceneTotal,
                    prefabs = prefabTotal,
                    scriptableObjects = soTotal,
                    materials = CountAssets("t:Material"),
                    scripts = CountAssets("t:Script"),
                    textures = CountAssets("t:Texture"),
                    audioClips = CountAssets("t:AudioClip"),
                    animationClips = CountAssets("t:AnimationClip"),
                    shaders = CountAssets("t:Shader")
                },
                scenes = scenes,
                prefabs = prefabs,
                scriptableObjects = scriptableObjects,
                scenesTruncated = sceneTotal > scenes.Length,
                prefabsTruncated = prefabTotal > prefabs.Length,
                scriptableObjectsTruncated = soTotal > scriptableObjects.Length,
                folders = BuildTopLevelFolderCounts(),
                note = "Summarized index. For targeted lookups submit a query_assets command (e.g. type=Prefab, value=Player); for full values submit read_object (scene path) or read_asset (Assets/... path)."
            };
        }

        private string[] FindAssetPaths(string filter, int cap, out int total)
        {
            var guids = AssetDatabase.FindAssets(filter);
            total = guids.Length;
            return guids.Take(cap).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        private int CountAssets(string filter)
        {
            return AssetDatabase.FindAssets(filter).Length;
        }

        private FolderCount[] BuildTopLevelFolderCounts()
        {
            var result = new List<FolderCount>();
            try
            {
                foreach (var dir in Directory.GetDirectories(Application.dataPath))
                {
                    var rel = "Assets/" + Path.GetFileName(dir);
                    result.Add(new FolderCount { folder = rel, assetCount = AssetDatabase.FindAssets("t:Object", new[] { rel }).Length });
                }
            }
            catch
            {
                // best-effort
            }
            return result.OrderBy(f => f.folder, StringComparer.Ordinal).ToArray();
        }

        private string QueryAssets(BridgeAction action)
        {
            var typePart = string.IsNullOrWhiteSpace(action.targetType) ? "" : "t:" + action.targetType.Trim() + " ";
            var filter = (typePart + (action.value ?? "")).Trim();
            if (filter.Length == 0) filter = "t:Object";
            var guids = AssetDatabase.FindAssets(filter);
            var results = guids.Take(AssetQueryCap).Select(guid =>
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                return new AssetQueryHit { path = assetPath, type = assetType != null ? assetType.Name : "Unknown", guid = guid };
            }).ToArray();
            var payload = new AssetQueryResult
            {
                filter = filter,
                count = guids.Length,
                returned = results.Length,
                truncated = guids.Length > results.Length,
                exportedAt = DateTimeOffset.Now.ToString("o"),
                results = results
            };
            WriteText("query-results.json", JsonUtility.ToJson(payload, true));
            return "query_assets " + filter + " -> " + results.Length + "/" + guids.Length + " (query-results.json)";
        }

        private string ReadObject(BridgeAction action)
        {
            var pathValue = action.TargetPath();
            var go = FindObjectByPath(pathValue);
            if (go == null) throw new InvalidOperationException("Object not found: " + pathValue);
            var payload = new ObjectReadResult
            {
                path = ObjectPath(go),
                found = true,
                exportedAt = DateTimeOffset.Now.ToString("o"),
                components = go.GetComponents<Component>().Select(InspectorComponentFor).ToArray()
            };
            WriteText("read-object.json", JsonUtility.ToJson(payload, true));
            return "read_object " + ObjectPath(go) + " (read-object.json)";
        }

        private string ReadAsset(BridgeAction action)
        {
            var assetPath = action.AssetPathForAction();
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj == null) throw new InvalidOperationException("Asset not found: " + assetPath);
            InspectorComponent[] components = obj is GameObject prefab
                ? prefab.GetComponents<Component>().Select(InspectorComponentFor).ToArray()
                : new[] { InspectorComponentForObject(obj) };
            var payload = new AssetReadResult
            {
                path = assetPath,
                type = obj.GetType().Name,
                found = true,
                exportedAt = DateTimeOffset.Now.ToString("o"),
                components = components
            };
            WriteText("read-asset.json", JsonUtility.ToJson(payload, true));
            return "read_asset " + assetPath + " (read-asset.json)";
        }

        private BridgeState BuildBridgeState()
        {
            return new BridgeState
            {
                bridgeVersion = BridgeVersion,
                enabled = true,
                autoExport = autoExport,
                autoExportIntervalSeconds = autoExportIntervalSeconds,
                watchCommands = AlepouUnityBridgeApprovalPolicy.AutomaticProcessingEnabled,
                lastExportedAt = DateTimeOffset.Now.ToString("o"),
                unityVersion = Application.unityVersion,
                projectName = Application.productName,
                projectRoot = ProjectRoot(),
                planUnityPath = outputPath,
                playMode = EditorApplication.isPlaying,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating
            };
        }

        private string BuildStatusMarkdown()
        {
            var active = SceneManager.GetActiveScene();
            var diagnostics = GatherDiagnostics();
            var errors = AlepouUnityBridgeLogCapture.Entries.Count(e => e.level == LogType.Error.ToString() || e.level == LogType.Exception.ToString() || e.level == LogType.Assert.ToString());
            var warnings = AlepouUnityBridgeLogCapture.Entries.Count(e => e.level == LogType.Warning.ToString());
            var lines = new List<string>
            {
                "# Unity Bridge Status",
                "",
                "Snapshot: " + DateTimeOffset.Now.ToString("o"),
                "Unity: " + Application.unityVersion,
                "Project: " + Application.productName,
                "Bridge: " + BridgeVersion,
                "Processor: " + AlepouUnityBridgeService.CurrentStatus,
                "",
                "## Editor",
                "- Play Mode: " + EditorApplication.isPlaying,
                "- Compiling: " + EditorApplication.isCompiling,
                "- Active scene: " + active.path,
                "- Scene dirty: " + active.isDirty,
                "- Selected object: " + (ObjectPath(Selection.activeGameObject) ?? "(none)"),
                "",
                "## Pull-only view capture",
                "- Targets: scene, game, both, ui-builder",
                "- UI Builder capture API: " + (ResolveCaptureEditorWindowMethod() == null ? "unavailable" : "available"),
                "- UI Builder whole-document framing API: " + (ResolveUiBuilderFrameDocumentMethod() == null ? "unavailable" : "available"),
                "- UI Builder window: " + (FindUiBuilderWindow() == null ? "closed" : "open"),
                "- UI Builder window control: delegated open/close plus auto-approved transient whole-document framing; open accepts an optional Assets/.../*.uxml assetPath; document switches and close refuse unsaved work.",
                "- UI Builder capture briefly focuses that window, then restores the previous Unity focus.",
                "",
                "## Console",
                "- Errors: " + errors,
                "- Warnings: " + warnings,
                "- Last error: " + LastErrorSummary(),
                "",
                "## Compile",
                "- Compiling: " + EditorApplication.isCompiling,
                "- Updating assets: " + EditorApplication.isUpdating,
                "- Compile errors captured: " + GatherCompileErrors().Length,
                "",
                "## Build",
                "- Active build target: " + EditorUserBuildSettings.activeBuildTarget,
                "- Scenes in build: " + EditorBuildSettings.scenes.Length,
                "",
                "## Diagnostics"
            };
            lines.AddRange(diagnostics.Count == 0 ? new[] { "- No critical diagnostics detected by the MVP bridge." } : diagnostics.Select(d => "- " + d));
            return string.Join("\n", lines) + "\n";
        }

        private ActiveSceneSnapshot BuildActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            return new ActiveSceneSnapshot
            {
                path = scene.path,
                name = scene.name,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                rootCount = scene.rootCount
            };
        }

        private OpenScenesSnapshot BuildOpenScenes()
        {
            var scenes = new List<SceneSnapshot>();
            var active = SceneManager.GetActiveScene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new SceneSnapshot
                {
                    path = scene.path,
                    name = scene.name,
                    isLoaded = scene.isLoaded,
                    isDirty = scene.isDirty,
                    isActive = scene.handle == active.handle
                });
            }
            return new OpenScenesSnapshot { scenes = scenes.ToArray() };
        }

        private HierarchySnapshot BuildHierarchySnapshot()
        {
            var scene = SceneManager.GetActiveScene();
            var nodes = new List<HierarchyNode>();
            foreach (var root in scene.GetRootGameObjects())
            {
                CollectHierarchyNode(root, null, 0, nodes, 12, 3000);
            }
            return new HierarchySnapshot
            {
                scene = scene.path,
                exportedAt = DateTimeOffset.Now.ToString("o"),
                maxExportedDepth = 12,
                nodeCount = nodes.Count,
                nodes = nodes.ToArray()
            };
        }

        private void CollectHierarchyNode(GameObject go, string parentPath, int depth, List<HierarchyNode> nodes, int maxDepth, int maxNodes)
        {
            if (go == null || nodes.Count >= maxNodes) return;
            var comps = go.GetComponents<Component>().Select(ComponentInfoFor).ToArray();
            var pathValue = ObjectPath(go);
            nodes.Add(new HierarchyNode
            {
                name = go.name,
                path = pathValue,
                parentPath = parentPath,
                depth = depth,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                instanceId = go.GetInstanceID(),
                globalObjectId = GlobalId(go),
                prefabSource = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go),
                components = comps,
                childCount = go.transform.childCount,
                childrenTruncated = depth >= maxDepth && go.transform.childCount > 0
            });
            if (depth >= maxDepth || nodes.Count >= maxNodes) return;
            foreach (Transform child in go.transform)
            {
                CollectHierarchyNode(child.gameObject, pathValue, depth + 1, nodes, maxDepth, maxNodes);
                if (nodes.Count >= maxNodes) return;
            }
        }

        private ComponentInfo ComponentInfoFor(Component component)
        {
            if (component == null)
            {
                return new ComponentInfo { type = "(missing script)", enabled = "false", missing = true };
            }
            var enabled = component is Behaviour b ? b.enabled :
                component is Renderer r ? r.enabled :
                component is Collider c ? c.enabled :
                (bool?)null;
            return new ComponentInfo
            {
                type = component.GetType().Name,
                enabled = enabled.HasValue ? enabled.Value.ToString().ToLowerInvariant() : "n/a",
                missing = false,
                scriptGuid = ScriptGuid(component)
            };
        }

        private SelectionSnapshot BuildSelectionSnapshot()
        {
            var selected = Selection.gameObjects.Select(go => new SelectedObjectInfo
            {
                type = "GameObject",
                name = go.name,
                path = ObjectPath(go),
                instanceId = go.GetInstanceID(),
                globalObjectId = GlobalId(go)
            }).ToArray();
            return new SelectionSnapshot { selectedObjects = selected };
        }

        private InspectorSnapshot BuildInspectorSnapshot()
        {
            var go = Selection.activeGameObject;
            if (go == null) return new InspectorSnapshot { selectedObject = null, components = Array.Empty<InspectorComponent>() };
            return new InspectorSnapshot
            {
                selectedObject = ObjectPath(go),
                components = go.GetComponents<Component>().Select(InspectorComponentFor).ToArray()
            };
        }

        private SelectedSubtreeInspectorSnapshot BuildSelectedSubtreeInspectorSnapshot()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
            {
                return new SelectedSubtreeInspectorSnapshot
                {
                    selectedObject = null,
                    exportedAt = DateTimeOffset.Now.ToString("o"),
                    maxExportedDepth = 0,
                    objectCount = 0,
                    objects = Array.Empty<SelectedSubtreeObject>()
                };
            }

            var objects = new List<SelectedSubtreeObject>();
            CollectSelectedSubtreeObject(selected, null, 0, objects, 8, 500);
            return new SelectedSubtreeInspectorSnapshot
            {
                selectedObject = ObjectPath(selected),
                exportedAt = DateTimeOffset.Now.ToString("o"),
                maxExportedDepth = 8,
                objectCount = objects.Count,
                objects = objects.ToArray()
            };
        }

        private void CollectSelectedSubtreeObject(GameObject go, string parentPath, int depth, List<SelectedSubtreeObject> objects, int maxDepth, int maxObjects)
        {
            if (go == null || objects.Count >= maxObjects) return;
            var pathValue = ObjectPath(go);
            objects.Add(new SelectedSubtreeObject
            {
                name = go.name,
                path = pathValue,
                parentPath = parentPath,
                depth = depth,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                instanceId = go.GetInstanceID(),
                globalObjectId = GlobalId(go),
                prefabSource = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go),
                childCount = go.transform.childCount,
                childrenTruncated = depth >= maxDepth && go.transform.childCount > 0,
                components = go.GetComponents<Component>().Select(InspectorComponentFor).ToArray()
            });
            if (depth >= maxDepth || objects.Count >= maxObjects) return;
            foreach (Transform child in go.transform)
            {
                CollectSelectedSubtreeObject(child.gameObject, pathValue, depth + 1, objects, maxDepth, maxObjects);
                if (objects.Count >= maxObjects) return;
            }
        }

        private InspectorComponent InspectorComponentFor(Component component)
        {
            if (component == null)
            {
                return new InspectorComponent
                {
                    type = "(missing script)",
                    enabled = "false",
                    properties = Array.Empty<InspectorProperty>()
                };
            }
            var enabled = component is Behaviour b ? b.enabled : (bool?)null;
            return new InspectorComponent
            {
                type = component.GetType().Name,
                enabled = enabled.HasValue ? enabled.Value.ToString().ToLowerInvariant() : "n/a",
                scriptGuid = ScriptGuid(component),
                properties = SerializePropertiesOf(component)
            };
        }

        // Serializes any UnityEngine.Object's visible serialized fields (capped at 200)
        // into readable InspectorProperty rows. Shared by components and standalone assets.
        private InspectorProperty[] SerializePropertiesOf(UnityEngine.Object target)
        {
            var props = new List<InspectorProperty>();
            try
            {
                var so = new SerializedObject(target);
                var iterator = so.GetIterator();
                var enterChildren = true;
                var count = 0;
                while (iterator.NextVisible(enterChildren) && count < 200)
                {
                    enterChildren = false;
                    if (iterator.propertyPath == "m_Script") continue;
                    props.Add(new InspectorProperty
                    {
                        path = iterator.propertyPath,
                        displayName = iterator.displayName,
                        type = iterator.propertyType.ToString(),
                        value = SerializedValue(iterator)
                    });
                    count++;
                }
            }
            catch (Exception ex)
            {
                props.Add(new InspectorProperty { path = "(error)", displayName = "Export error", type = "Error", value = ex.Message });
            }
            return props.ToArray();
        }

        private InspectorComponent InspectorComponentForObject(UnityEngine.Object obj)
        {
            return new InspectorComponent
            {
                type = obj.GetType().Name,
                enabled = "n/a",
                scriptGuid = null,
                properties = SerializePropertiesOf(obj)
            };
        }

        private BuildSettingsSnapshot BuildBuildSettings()
        {
            return new BuildSettingsSnapshot
            {
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                selectedBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                scenesInBuild = EditorBuildSettings.scenes.Select((scene, index) => new BuildSceneInfo
                {
                    path = scene.path,
                    enabled = scene.enabled,
                    index = index
                }).ToArray()
            };
        }

        private PackagesSnapshot BuildPackagesSnapshot()
        {
            var manifestPath = Path.Combine(ProjectRoot(), "Packages", "manifest.json");
            var lockPath = Path.Combine(ProjectRoot(), "Packages", "packages-lock.json");
            return new PackagesSnapshot
            {
                manifestPath = "Packages/manifest.json",
                lockPath = "Packages/packages-lock.json",
                manifestJson = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : "",
                lockJson = File.Exists(lockPath) ? File.ReadAllText(lockPath) : ""
            };
        }

        private string BuildConsoleJsonl()
        {
            return string.Join("\n", AlepouUnityBridgeLogCapture.Entries.Select(e =>
                "{" +
                "\"time\":\"" + EscapeJson(e.time) + "\"," +
                "\"level\":\"" + EscapeJson(e.level) + "\"," +
                "\"condition\":\"" + EscapeJson(e.condition) + "\"," +
                "\"stackTrace\":\"" + EscapeJson(e.stackTrace) + "\"," +
                "\"activeScene\":\"" + EscapeJson(e.activeScene) + "\"," +
                "\"playMode\":" + (e.playMode ? "true" : "false") + "," +
                "\"selectedObject\":\"" + EscapeJson(e.selectedObject) + "\"" +
                "}")) + "\n";
        }

        private string BuildConsoleSummary()
        {
            var errors = AlepouUnityBridgeLogCapture.Entries.Where(e => e.level == "Error" || e.level == "Exception" || e.level == "Assert").ToArray();
            var warnings = AlepouUnityBridgeLogCapture.Entries.Where(e => e.level == "Warning").ToArray();
            var lines = new List<string>
            {
                "# Console Summary",
                "",
                "## Current counts",
                "- Errors: " + errors.Length,
                "- Warnings: " + warnings.Length,
                "- Logs captured by bridge: " + AlepouUnityBridgeLogCapture.Entries.Count,
                "",
                "## Last error",
                errors.Length == 0 ? "None captured by the bridge in this editor session." : errors.Last().condition
            };
            return string.Join("\n", lines) + "\n";
        }

        private string BuildDiagnostics()
        {
            var diagnostics = GatherDiagnostics();
            var lines = new List<string>
            {
                "# Unity Diagnostics",
                "",
                "Snapshot: " + DateTimeOffset.Now.ToString("o"),
                "",
                diagnostics.Count == 0 ? "## No Critical Diagnostics" : "## Findings"
            };
            lines.AddRange(diagnostics.Count == 0 ? new[] { "No obvious issues detected by the MVP bridge." } : diagnostics.Select((d, i) => (i + 1) + ". " + d));
            return string.Join("\n", lines) + "\n";
        }

        private CompileStatusSnapshot BuildCompileStatus()
        {
            var compileErrors = GatherCompileErrors();
            var allErrors = AlepouUnityBridgeLogCapture.Entries
                .Where(e => e.level == "Error" || e.level == "Exception" || e.level == "Assert")
                .ToArray();
            var warnings = AlepouUnityBridgeLogCapture.Entries.Where(e => e.level == "Warning").ToArray();
            return new CompileStatusSnapshot
            {
                checkedAt = DateTimeOffset.Now.ToString("o"),
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                hasCompileErrors = compileErrors.Length > 0,
                capturedErrorCount = allErrors.Length,
                capturedWarningCount = warnings.Length,
                compileErrors = compileErrors
            };
        }

        internal bool CompileHasErrors()
        {
            return GatherCompileErrors().Length > 0;
        }

        private CompileDiagnostic[] GatherCompileErrors()
        {
            var entries = AlepouUnityBridgeLogCapture.Entries
                .Where(IsLikelyCompileError)
                .ToArray();
            return entries
                .Skip(Math.Max(0, entries.Length - 20))
                .Select(e => new CompileDiagnostic
                {
                    time = e.time,
                    level = e.level,
                    message = e.condition,
                    stackTrace = e.stackTrace,
                    activeScene = e.activeScene
                })
                .ToArray();
        }

        private static bool IsLikelyCompileError(ConsoleEntry entry)
        {
            if (entry == null) return false;
            if (entry.level != "Error" && entry.level != "Exception" && entry.level != "Assert") return false;
            var text = ((entry.condition ?? "") + "\n" + (entry.stackTrace ?? "")).ToLowerInvariant();
            return text.Contains("error cs") || text.Contains("compilation") || text.Contains("compiler") || text.Contains("assets/");
        }

        private List<string> GatherDiagnostics()
        {
            var diagnostics = new List<string>();
            var scene = SceneManager.GetActiveScene();
            if (scene.isDirty) diagnostics.Add("Active scene has unsaved changes.");
            if (EditorApplication.isCompiling) diagnostics.Add("Unity is compiling scripts.");
            var roots = scene.GetRootGameObjects();
            var all = roots.SelectMany(Flatten).ToArray();
            var missingScripts = all.Sum(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);
            if (missingScripts > 0) diagnostics.Add("Missing scripts in active scene: " + missingScripts);
            if (all.Count(go => go.GetComponent<Camera>() != null && go.activeInHierarchy) == 0) diagnostics.Add("No active Camera found in the active scene.");
            if (all.Count(go => go.GetComponent<AudioListener>() != null && go.activeInHierarchy) > 1) diagnostics.Add("Multiple active AudioListeners found.");
            var lastError = LastErrorSummary();
            if (lastError != "none") diagnostics.Add("Last captured Console error: " + lastError);
            return diagnostics;
        }

        private static IEnumerable<GameObject> Flatten(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.transform)
            {
                foreach (var go in Flatten(child.gameObject))
                {
                    yield return go;
                }
            }
        }

        private string LastErrorSummary()
        {
            var entry = AlepouUnityBridgeLogCapture.Entries.LastOrDefault(e => e.level == "Error" || e.level == "Exception" || e.level == "Assert");
            return entry == null ? "none" : entry.condition;
        }

        private string BuildCapabilitiesJson()
        {
            return "{\n" +
                "  \"schemaVersion\": 1,\n" +
                "  \"readCapabilities\": [\"status.export\", \"console.export\", \"compileStatus.export\", \"scene.hierarchy.export\", \"selection.export\", \"inspector.export\", \"selectedSubtree.inspector.export\", \"buildSettings.export\", \"packages.export\", \"diagnostics.export\", \"commandResults.export\", \"recipes.export\", \"view.capture\", \"editorWindow.uiBuilder.capture\", \"editorWindow.uiBuilder.frameDocument\", \"runtime.capture\", \"assetIndex.export\", \"query_assets\", \"read_object\", \"read_asset\"],\n" +
                "  \"writeCapabilities\": [\"export_snapshot\", \"capture_view\", \"frame_ui_builder_document\", \"query_assets\", \"read_object\", \"read_asset\", \"open_ui_builder\", \"close_ui_builder\", \"refresh_assets\", \"save_scene\", \"select_object\", \"ping_asset\", \"create_gameobject\", \"destroy_gameobject\", \"set_parent\", \"set_transform\", \"add_component\", \"remove_component\", \"assign_reference\", \"set_property\", \"create_folder\", \"create_scriptable_object_asset\", \"set_asset_property\", \"assign_asset_reference\", \"move_asset\", \"rename_asset\", \"delete_asset\", \"set_player_setting\", \"set_build_scenes\", \"create_prefab\", \"instantiate_prefab\", \"edit_prefab\", \"create_material\", \"set_material_property\", \"create_shader_asset\", \"create_animation_clip\", \"create_light\", \"bake_lighting\", \"clear_baked_lighting\", \"batch_edit_objects\", \"execute_menu_item\"],\n" +
                "  \"viewCaptureTargets\": [\"scene\", \"game\", \"both\", \"ui-builder\"],\n" +
                "  \"uiBuilderCapture\": { \"supported\": " + (ResolveCaptureEditorWindowMethod() == null ? "false" : "true") + ", \"requiresOpenWindow\": true, \"requiresPostOpenEditorUpdate\": true, \"temporarilyFocusesWindow\": true, \"restoresPreviousUnityFocus\": true, \"capturesFullWindowBeforeDownscale\": true, \"maxLongEdgePixels\": " + MaxUiBuilderCaptureEdge + " },\n" +
                "  \"uiBuilderControl\": { \"openSupported\": true, \"closeSupported\": true, \"frameDocumentSupported\": " + (ResolveUiBuilderFrameDocumentMethod() == null ? "false" : "true") + ", \"frameTarget\": \"largestRenderedDocumentElement\", \"frameRequiresLoadedDocument\": true, \"requiresPostFrameEditorUpdate\": true, \"frameChangesOnlyEditorViewport\": true, \"optionalOpenAssetPath\": \"Assets/.../*.uxml\", \"refusesUnsavedDocumentSwitch\": true, \"refusesUnsavedClose\": true },\n" +
                "  \"workflowCapabilities\": [\"durableManifest.v1\", \"barrier.command\", \"barrier.waitEditorIdle\", \"barrier.waitCompileIdle\", \"barrier.requireCompileClean\", \"barrier.runTests\", \"barrier.buildPlayer\", \"barrier.enterPlayMode\", \"barrier.runtimeCapture\", \"barrier.exitPlayMode\", \"barrier.humanGate\", \"barrier.checkpoint\", \"resumeAfterDomainReload\", \"timeout\", \"cancel\", \"scriptGeneration.byFileEdit\", \"sceneSetup.orderedCommandBatch\", \"assetCreation.scriptableObject\", \"projectMaintenance.approvedBatch\", \"resultInspection.commandResults\", \"runtimeCapture.v1\", \"buildArtifactManifest.v1\"],\n" +
                "  \"processorLifecycle\": \"alwaysWhileUnityEditorIsOpen\",\n" +
                "  \"defaultAutoApprovedActions\": [\"export_snapshot\", \"capture_view\", \"frame_ui_builder_document\", \"query_assets\", \"read_object\", \"read_asset\"],\n" +
                "  \"delegatableActions\": [\"open_ui_builder\", \"close_ui_builder\", \"select_object\", \"ping_asset\", \"refresh_assets\", \"save_scene\", \"create_gameobject\", \"set_parent\", \"set_transform\", \"add_component\", \"assign_reference\", \"set_property\", \"create_folder\", \"create_scriptable_object_asset\", \"set_asset_property\", \"assign_asset_reference\", \"move_asset\", \"rename_asset\", \"run_tests\", \"build_player\", \"create_prefab\", \"instantiate_prefab\", \"edit_prefab\", \"create_material\", \"set_material_property\", \"create_shader_asset\", \"create_animation_clip\", \"create_light\", \"bake_lighting\", \"batch_edit_objects\", \"execute_menu_item\"],\n" +
                "  \"trustedDevelopmentOnlyActions\": [\"enter_play_mode\", \"exit_play_mode\"],\n" +
                "  \"humanOnlyActions\": [\"destroy_gameobject\", \"remove_component\", \"delete_asset\", \"set_player_setting\", \"set_build_scenes\", \"clear_baked_lighting\", \"device_*\"],\n" +
                "  \"approvalContract\": \"Delegated command mutations and workflow state transitions require the active grantId and a matching sessionId when the grant is session-bound; include the stable owning Alepou sessionId on commands and workflows that should wake their originating Planner session. Authoritative policy is stored locally by Unity, not under plan/unity.\",\n" +
                "  \"recipeFiles\": [\"recipes/prototype-game-loop.md\", \"recipes/command-batch-schema.md\", \"recipes/workflow-schema.md\", \"recipes/structured-test-runs.md\", \"recipes/structured-build-jobs.md\", \"recipes/runtime-play-mode.md\", \"recipes/deep-authoring.md\", \"recipes/lode-runner-starter.md\"],\n" +
                "  \"unsupported\": [\"arbitraryCSharpEval\", \"unapprovedDestructiveChanges\"]\n" +
                "}\n";
        }

        private void WriteRecipeFiles()
        {
            WriteTextIfChanged(Path.Combine("recipes", "prototype-game-loop.md"), BuildPrototypeGameLoopRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "command-batch-schema.md"), BuildCommandBatchSchemaRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "workflow-schema.md"), BuildWorkflowSchemaRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "structured-test-runs.md"), BuildStructuredTestRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "structured-build-jobs.md"), BuildStructuredBuildRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "runtime-play-mode.md"), BuildRuntimePlayModeRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "deep-authoring.md"), BuildDeepAuthoringRecipe());
            WriteTextIfChanged(Path.Combine("recipes", "lode-runner-starter.md"), BuildLodeRunnerStarterRecipe());
        }

        private string BuildPrototypeGameLoopRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Prototype Game Loop Recipe",
                "",
                "Use this recipe when an AI is asked to create a playable Unity prototype.",
                "",
                "## Required Loop",
                "",
                "1. Read `status.md`, `capabilities.json`, `compile-status.json`, `command-results.md`, and only the targeted scene/inspector files needed for the task.",
                "2. Write C# scripts as normal project files under `Assets/`, usually `Assets/Scripts/<Feature>/`.",
                "3. Create one pending command that runs `refresh_assets` and `export_snapshot`.",
                "4. Wait for its command result. `refresh_assets` requires a matching delegated grant or a human Approve click because it can import, compile, and reload the Unity domain.",
                "5. Re-read `compile-status.json` until `isCompiling` and `isUpdating` are both false.",
                "6. If `hasCompileErrors` is true or `console-summary.md` reports errors, fix scripts first and repeat the refresh checkpoint.",
                "7. Queue one substantial approved command batch for asset creation, scene wiring, project settings, build scene cleanup, `save_scene`, and `export_snapshot` whenever those actions do not require an intermediate compile/result checkpoint.",
                "8. Wait for Unity approval, then inspect `command-results.md`, the latest result JSON, `status.md`, `scene-hierarchy.json`, and relevant inspector snapshots.",
                "9. Iterate with complete batches. Avoid spending turns on one administrative command at a time unless the next action genuinely depends on the previous Unity result.",
                "",
                "## Refresh Command Template",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"commandId\": \"cmd-refresh-assets-after-scripts\",",
                "  \"title\": \"Refresh assets after script generation\",",
                "  \"actions\": [",
                "    { \"action\": \"refresh_assets\" },",
                "    { \"action\": \"export_snapshot\" }",
                "  ]",
                "}",
                "```",
                "",
                "## Rules",
                "",
                "- Do not claim scene or Inspector changes are applied until a command result confirms it.",
                "- Keep script creation and scene wiring in separate steps so Unity can compile before components are attached.",
                "- Use bridge asset commands for ScriptableObject data/config assets instead of hand-editing `.asset` YAML.",
                "- Prefer one complete, named approval batch per phase over many tiny approval requests. Put independent asset, scene, setting, build-scene, save, and snapshot actions in the same command.",
                "- If a batch fails, inspect the failed result JSON before generating a retry.",
                ""
            }) + "\n";
        }

        private string BuildCommandBatchSchemaRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Command Batch Schema",
                "",
                "Command files live under `commands/pending/`. The always-running Unity service atomically claims commands under `commands/processing/`. The optional Bridge window shows pending files and provides manual approval for actions outside the selected auto-approval policy.",
                "",
                "## Top Level",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"commandId\": \"cmd-short-kebab-id\",",
                "  \"grantId\": \"optional-active-local-grant-id\",",
                "  \"sessionId\": \"stable-owning-alepou-session-id\",",
                "  \"wakeOnTerminal\": false,",
                "  \"title\": \"Human readable title\",",
                "  \"summary\": \"Optional short summary\",",
                "  \"actions\": []",
                "}",
                "```",
                "",
                "## Supported Actions",
                "",
                "- `export_snapshot`: writes fresh bridge state.",
                "- `capture_view`: captures `scene`, `game`, `both`, or `ui-builder` evidence. UI Builder is captured at its full native window size before the complete frame is optionally downsized to a 1920-pixel long edge; Scene/Game retain their 1280-pixel cap. After opening UI Builder, capture it in a later command so Unity can paint the window.",
                "- `frame_ui_builder_document`: invokes UI Builder's native whole-document fit on the largest rendered document element without changing project assets. Capture in a later command so UI Toolkit can repaint.",
                "- `open_ui_builder`: opens or focuses UI Builder; optional `assetPath` loads an exact Assets/.../*.uxml VisualTreeAsset through Unity's asset-opening path. Document switches refuse unsaved work.",
                "- `close_ui_builder`: closes UI Builder only when it has no unsaved changes; otherwise fails without opening a modal prompt.",
                "- `refresh_assets`: asks Unity to import changed assets and start compilation if scripts changed.",
                "- `save_scene`: saves the active scene, or a loaded scene by `scenePath`/`path`.",
                "- `select_object`: selects a GameObject path or an asset path.",
                "- `ping_asset`: selects and pings an asset path.",
                "- `create_gameobject`: creates a GameObject path, including missing parents.",
                "- `destroy_gameobject`: destroys a GameObject path through Undo.",
                "- `set_parent`: reparents an object under another object path or to the scene root.",
                "- `set_transform`: sets world/local position, rotation, and scale.",
                "- `add_component`: adds an already compiled component by type name or full name.",
                "- `remove_component`: removes an existing component through Undo.",
                "- `assign_reference`: assigns a serialized object reference field.",
                "- `set_property`: sets simple int, bool, float, string, or enum serialized fields.",
                "- `create_folder`: creates an `Assets/...` folder path, including missing parents.",
                "- `create_scriptable_object_asset`: creates an `Assets/.../*.asset` file from a compiled ScriptableObject type.",
                "- `set_asset_property`: sets simple serialized fields on an asset. For arrays/lists, pass `values` as an array of strings.",
                "- `assign_asset_reference`: assigns an asset reference to a serialized object reference field on a scene component.",
                "- `move_asset`: moves an asset to another `Assets/...` path.",
                "- `rename_asset`: renames an asset in its current folder.",
                "- `delete_asset`: deletes an asset path. Folder deletion requires `allowFolder: true`.",
                "- `set_player_setting`: sets whitelisted PlayerSettings fields such as `productName`, `companyName`, `bundleVersion`, and per-target `applicationIdentifier`.",
                "- `set_build_scenes`: replaces EditorBuildSettings scenes from `paths`, `scenes`, or `values`.",
                "- `create_prefab` / `instantiate_prefab` / `edit_prefab`: create a prefab from a scene object, instantiate a prefab, or persist one bounded transform/serialized-field prefab edit.",
                "- `create_material` / `set_material_property`: create a material from an exact shader or set one typed float/int/color/vector/texture property.",
                "- `create_shader_asset`: writes only one of the fixed `unlit-color` or `unlit-texture` templates; arbitrary shader source is never accepted.",
                "- `create_animation_clip`: creates a bounded clip from explicit component/property curves and keyframes.",
                "- `create_light`, `bake_lighting`, `clear_baked_lighting`: typed lighting creation/bake/clear actions with separate policy classification.",
                "- `batch_edit_objects`: applies at most 100 explicit transform or serialized-field edits.",
                "- `execute_menu_item`: invokes only `Assets/Refresh`, `File/Save`, or `File/Save Project`; arbitrary menu paths are rejected.",
                "",
                "## Delegated Approval",
                "",
                "- Observation actions (`export_snapshot`, `capture_view`, `frame_ui_builder_document`, `query_assets`, `read_object`, `read_asset`) can run automatically without a grant.",
                "- Every other automatic action must be included in the active Unity-local grant. Copy its opaque `grantId` into the command or workflow manifest; `sessionId` is required when the grant is session-bound and otherwise recommended so Alepou can wake the exact originating Planner session.",
                "- Set `wakeOnTerminal: true` only when the owning Planner turn will end waiting for this exact result. Without it Alepou still publishes a notification but does not create a continuation turn.",
                "- A command cannot enable, extend, or widen its grant. Expiry and command/action budgets are enforced when Unity atomically claims it.",
                "- Trusted Development is a session-bound preset for the routine reversible authoring + refresh/import/compile + focused-test loop, plus bounded workflow-owned Play Mode entry, runtime capture, and exit. Builds, shader import, lighting bake, and allowlisted menu execution remain separately selected Custom Expert capabilities.",
                "- Destructive operations, general project settings, baked-light clearing, and device actions remain human-only. Play Mode automation requires the exact active Trusted Development grant and matching session; it is not available to Reversible Workspace or Custom Expert grants.",
                "- Policy decisions are recorded in `approval-audit.jsonl`; each result records its approval rule, grant id, and frozen request hash.",
                "",
                "## Wiring Batch Shape",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"commandId\": \"cmd-wire-prototype-scene\",",
                "  \"title\": \"Wire prototype scene\",",
                "  \"actions\": [",
                "    { \"action\": \"create_gameobject\", \"path\": \"GameRoot/Managers\" },",
                "    { \"action\": \"create_gameobject\", \"path\": \"Player\" },",
                "    { \"action\": \"add_component\", \"objectPath\": \"Player\", \"component\": \"PlayerController\" },",
                "    { \"action\": \"set_property\", \"objectPath\": \"Player\", \"component\": \"PlayerController\", \"field\": \"moveSpeed\", \"value\": \"6\" },",
                "    { \"action\": \"assign_reference\", \"objectPath\": \"GameRoot/Managers\", \"component\": \"GameManager\", \"field\": \"player\", \"target\": \"Player\", \"targetType\": \"GameObject\" },",
                "    { \"action\": \"export_snapshot\" }",
                "  ]",
                "}",
                "```",
                "",
                "## ScriptableObject Asset Batch Shape",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"commandId\": \"cmd-create-level-asset\",",
                "  \"title\": \"Create level data asset\",",
                "  \"actions\": [",
                "    { \"action\": \"create_folder\", \"path\": \"Assets/Data/Levels\" },",
                "    { \"action\": \"create_scriptable_object_asset\", \"assetPath\": \"Assets/Data/Levels/Level01.asset\", \"type\": \"LodeRunnerLevelAsset\" },",
                "    { \"action\": \"set_asset_property\", \"assetPath\": \"Assets/Data/Levels/Level01.asset\", \"field\": \"levelName\", \"value\": \"Level 01\" },",
                "    { \"action\": \"set_asset_property\", \"assetPath\": \"Assets/Data/Levels/Level01.asset\", \"field\": \"rows\", \"values\": [\"########################################\", \"#                                      #\"] },",
                "    { \"action\": \"assign_asset_reference\", \"objectPath\": \"GameRoot/Managers\", \"component\": \"LevelManager\", \"field\": \"levelAsset\", \"assetPath\": \"Assets/Data/Levels/Level01.asset\" },",
                "    { \"action\": \"export_snapshot\" }",
                "  ]",
                "}",
                "```",
                "",
                "## Maintenance Batch Shape",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"commandId\": \"cmd-project-maintenance\",",
                "  \"title\": \"Save scene and update project settings\",",
                "  \"actions\": [",
                "    { \"action\": \"set_player_setting\", \"setting\": \"productName\", \"value\": \"LodeRunnerVR\" },",
                "    { \"action\": \"set_build_scenes\", \"paths\": [\"Assets/Scenes/LodeRunnerGameplay.unity\"] },",
                "    { \"action\": \"save_scene\" },",
                "    { \"action\": \"export_snapshot\" }",
                "  ]",
                "}",
                "```",
                ""
            }) + "\n";
        }

        private string BuildWorkflowSchemaRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Durable Workflow Schema",
                "",
                "Use a workflow when later Unity work depends on asynchronous import, compilation, a child command result, or a human gate. The Bridge persists the active step and resumes it after assembly/domain reload; the model should read `workflow-status.json` or the final result instead of polling Unity every turn.",
                "",
                "## Files",
                "",
                "- Put child command JSON under `workflows/inputs/`.",
                "- Put the workflow manifest under `workflows/pending/`.",
                "- The Bridge freezes referenced child inputs under `workflows/running/` when it atomically claims the manifest.",
                "- Progress is written to `workflow-status.json` and `workflows/running/<workflowId>.state.json`.",
                "- Final evidence lands under `workflows/completed/`, `workflows/failed/`, or `workflows/cancelled/`.",
                "- Request cancellation by writing any JSON object to `workflows/cancel/<workflowId>.json`.",
                "",
                "## Child command (`workflows/inputs/refresh-after-scripts.json`)",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"commandId\": \"cmd-refresh-after-scripts\",",
                "  \"grantId\": \"required-if-automatically-delegated\",",
                "  \"sessionId\": \"stable-owning-alepou-session-id\",",
                "  \"actions\": [{ \"action\": \"refresh_assets\" }]",
                "}",
                "```",
                "",
                "## Workflow manifest (`workflows/pending/compile-checkpoint.json`)",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"workflowId\": \"wf-compile-checkpoint\",",
                "  \"title\": \"Refresh and require clean compilation\",",
                "  \"grantId\": \"same-as-child-command-when-present\",",
                "  \"sessionId\": \"same-as-child-command-when-present\",",
                "  \"wakeOnTerminal\": true,",
                "  \"timeoutSeconds\": 900,",
                "  \"steps\": [",
                "    { \"stepId\": \"refresh\", \"type\": \"command\", \"commandFile\": \"refresh-after-scripts.json\", \"commandId\": \"cmd-refresh-after-scripts\", \"timeoutSeconds\": 300 },",
                "    { \"stepId\": \"idle\", \"type\": \"wait_editor_idle\", \"timeoutSeconds\": 300 },",
                "    { \"stepId\": \"compile-clean\", \"type\": \"require_compile_clean\", \"timeoutSeconds\": 300 },",
                "    { \"stepId\": \"review\", \"type\": \"human_gate\", \"message\": \"Compilation is clean. Continue to the next phase?\", \"timeoutSeconds\": 1800 },",
                "    { \"stepId\": \"done\", \"type\": \"checkpoint\" }",
                "  ]",
                "}",
                "```",
                "",
                "## Step types in v1",
                "",
                "- `command`: submits one frozen child command and waits for its applied/failed/rejected result.",
                "- `wait_editor_idle` / `wait_compile_idle`: wait until import and compilation are stably idle.",
                "- `require_compile_clean`: requires stable idle and no captured compiler errors.",
                "- `run_tests`: runs one filtered EditMode or PlayMode Unity Test Framework job and advances only on a clean pass.",
                "- `build_player`: runs one validated Unity BuildPipeline job and advances only after its versioned artifact manifest is written.",
                "- `enter_play_mode`: uses a matching session-bound Trusted Development grant or waits for direct human approval, requests Play Mode, and advances only after Unity reports `EnteredPlayMode`.",
                "- `runtime_capture`: while stably playing, writes one bounded Game-view image, a capped console delta, and explicit object/component/property probes.",
                "- `exit_play_mode`: uses a matching session-bound Trusted Development grant or waits for direct human approval, requests Edit Mode, and advances only after Unity reports `EnteredEditMode`.",
                "- `human_gate`: pauses until approved/rejected from Unity.",
                "- `checkpoint`: records an immediate durable boundary.",
                "",
                "Set `wakeOnTerminal: true` only when the owning Planner turn will finish waiting for this exact workflow. Completed/failed results may then continue an idle owning session. Human gates, cancellations, and results arriving while the Planner/provider is already active remain notifications and never interrupt or replay into a later turn.",
                "",
                "Read `runtime-play-mode.md` before using Play Mode. A matching session-bound Trusted Development grant can authorize enter and exit automatically for the owning workflow; otherwise each transition waits for direct human approval. Runtime capture is bounded observation only after Unity is stably playing. Child commands, tests, and builds remain subject to their exact manual/delegated approval capability. Device work remains a separate server-owned capability, and a build grant never authorizes installation, launch, logs, capture, or any other device action.",
                ""
            }) + "\n";
        }

        private string BuildStructuredTestRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Structured Unity Test Runs",
                "",
                "Use a `run_tests` workflow step instead of scraping the Console or invoking the Unity test UI. It supports EditMode and PlayMode jobs, persists progress through the workflow, writes bounded JSON evidence, and saves the full NUnit XML beside a per-test JSONL log.",
                "",
                "Tests execute project code. A session-bound Trusted Development grant auto-approves only filtered runs with at least one assembly, namespace, category, fixture, or exact-test filter. Unfiltered whole-suite runs require a direct human approval or a time/budget/session-bounded Custom Expert grant containing `run_tests`. Reversible Workspace never includes tests.",
                "",
                "```json",
                "{",
                "  \"stepId\": \"focused-tests\",",
                "  \"type\": \"run_tests\",",
                "  \"testRunId\": \"optional-stable-id\",",
                "  \"mode\": \"EditMode\",",
                "  \"assemblyNames\": [\"MyProject.Editor.Tests\"],",
                "  \"namespaceNames\": [\"MyProject.Tests\"],",
                "  \"categoryNames\": [\"Fast\"],",
                "  \"fixtureNames\": [\"MyProject.Tests.InventoryTests\"],",
                "  \"testNames\": [\"MyProject.Tests.InventoryTests.AddsItem\"],",
                "  \"timeoutSeconds\": 600",
                "}",
                "```",
                "",
                "Every populated filter family is combined with AND semantics. Values inside one family are alternatives. Namespace and fixture values match the beginning of a test's full name; individual test names are exact Unity Test Framework full names.",
                "",
                "Progress is written to `test-run-status.json`. Terminal results land under `test-runs/completed|failed|cancelled/<testRunId>.result.json`; the result points to `test-runs/logs/<testRunId>.xml` and `.jsonl`. The bounded result contains selected totals, pass/fail/skip/inconclusive counts, duration, and at most 50 truncated failure summaries. A filter matching zero tests fails instead of producing a false green.",
                "",
                "A failed, cancelled, timed-out, or zero-match run stops the parent workflow. Cancelling the workflow requests cancellation from the Unity Test Framework before the workflow result is finalized.",
                ""
            }) + "\n";
        }

        private string BuildStructuredBuildRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Structured Unity Build Jobs",
                "",
                "Use a `build_player` workflow step instead of invoking BuildPipeline through a menu item or arbitrary editor code. The runner validates build support, scenes, options, a compile-clean idle preflight, an optional prior clean test result, and a collision-free output below `Builds/Alepou/`.",
                "",
                "Builds execute project build hooks and can consume substantial CPU, time, and disk space. They require direct approval for the waiting workflow step or a time/budget/session-bounded Custom Expert grant containing `build_player`. The Reversible Workspace preset never includes builds.",
                "",
                "```json",
                "{",
                "  \"stepId\": \"quest-apk\",",
                "  \"type\": \"build_player\",",
                "  \"buildJobId\": \"optional-stable-id\",",
                "  \"target\": \"Android\",",
                "  \"scenes\": [\"Assets/Scenes/Main.unity\"],",
                "  \"options\": [\"Development\", \"DetailedBuildReport\"],",
                "  \"outputPath\": \"Builds/Alepou/quest-smoke/MyGame.apk\",",
                "  \"requireTestsPassed\": true,",
                "  \"requiredTestRunId\": \"optional-explicit-clean-test-id\",",
                "  \"timeoutSeconds\": 3600",
                "}",
                "```",
                "",
                "If `scenes` is omitted, the enabled EditorBuildSettings scenes are used. `target` defaults to Unity's active build target. Output is always constrained to `Builds/Alepou/`; an existing file or directory is never overwritten. Unsafe options such as AutoRunPlayer, ShowBuiltPlayer, patching, and external-project generation are rejected.",
                "",
                "Progress is written to `build-job-status.json`. Terminal results land under `build-jobs/completed|failed|cancelled/<buildJobId>.result.json`, with the full BuildReport message stream in `build-jobs/logs/<buildJobId>.jsonl`. A successful job also writes `build-jobs/artifacts/<buildJobId>.artifact.json` using the `alepou.unity-build-artifact/v1` contract: exact project-relative and absolute artifact paths, file/directory/standalone-bundle kind, size, SHA-256, target metadata, scene count, options, duration, warnings, and errors.",
                "",
                "The artifact manifest is only a handoff. Building does not authorize a deployment adapter, connected headset discovery, installation, launch, device logs, captures, or performance tracing. Those operations require their own exact device identity, artifact manifest, and separately granted capability.",
                ""
            }) + "\n";
        }

        private string BuildRuntimePlayModeRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Play Mode Control and Runtime Capture",
                "",
                "Use these typed workflow barriers for a bounded runtime observation. Never emulate Play Mode with a generic command or claim success when a state-change request is merely submitted.",
                "",
                "`enter_play_mode` and `exit_play_mode` run automatically only when the workflow carries the active Trusted Development grant id and its exact Alepou session binding. Reversible Workspace and Custom Expert grants cannot authorize them; without a match, each transition pauses for a direct click in the Unity Bridge window. Entry advances only after Unity reports `EnteredPlayMode`; exit advances only after `EnteredEditMode`. If a workflow-owned Play Mode session is cancelled, rejected, fails, or times out, the runner requests Edit Mode before writing its terminal result.",
                "",
                "```json",
                "{",
                "  \"schemaVersion\": 1,",
                "  \"workflowId\": \"wf-runtime-check\",",
                "  \"timeoutSeconds\": 900,",
                "  \"steps\": [",
                "    { \"stepId\": \"play\", \"type\": \"enter_play_mode\", \"timeoutSeconds\": 180 },",
                "    {",
                "      \"stepId\": \"capture\",",
                "      \"type\": \"runtime_capture\",",
                "      \"captureId\": \"first-frame\",",
                "      \"maxConsoleEntries\": 50,",
                "      \"probes\": [",
                "        { \"objectPath\": \"GameRoot/Player\", \"componentTypes\": [\"PlayerController\"], \"propertyPaths\": [\"speed\", \"isGrounded\"] },",
                "        { \"objectPath\": \"GameRoot/Player\", \"componentTypes\": [\"Transform\"], \"propertyPaths\": [\"m_LocalPosition\", \"m_LocalRotation\"] }",
                "      ],",
                "      \"timeoutSeconds\": 120",
                "    },",
                "    { \"stepId\": \"stop\", \"type\": \"exit_play_mode\", \"timeoutSeconds\": 180 }",
                "  ]",
                "}",
                "```",
                "",
                "A capture writes `runtime-captures/<workflowId>/<captureId>/game-view.png` and `runtime-capture.json`, plus the latest bounded result at `runtime-capture-status.json`. It includes only Play Mode console entries since entry or the previous capture, capped to 1-100 entries; conditions are capped at 2,000 characters and stack traces at 8,000.",
                "",
                "Each capture supports at most 16 probe rows. Every row must name one active-scene object path and 1-8 exact component names or full type names. Use separate rows when component types need different serialized `propertyPaths`. At most 32 property paths may be requested per row; when omitted, the first 50 visible serialized properties of the named component are returned. A missing object, component, property, or enabled Game-view Camera fails the barrier and is recorded rather than producing a false green.",
                "",
                "The local **STOP Play Mode + Cancel Workflow** control and **Tools > Alepou > Unity Bridge > STOP Automatic Processing** remain available throughout the run. Stopping automatic processing revokes the grant, requests workflow cancellation, and returns a workflow-owned runtime session to Edit Mode.",
                ""
            }) + "\n";
        }

        private string BuildDeepAuthoringRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Deep Authoring Commands",
                "",
                "These actions close common prefab, material, animation, lighting, and repeated-edit gaps without exposing arbitrary reflection, arbitrary source, or arbitrary menu execution. Put them in the normal `commands/pending/` envelope and use the exact grant capability shown in `capabilities.json`.",
                "",
                "## Prefabs",
                "",
                "```json",
                "{ \"action\": \"create_prefab\", \"objectPath\": \"Enemies/Guard\", \"assetPath\": \"Assets/Prefabs/Guard.prefab\" }",
                "{ \"action\": \"instantiate_prefab\", \"assetPath\": \"Assets/Prefabs/Guard.prefab\", \"parent\": \"Level/Enemies\", \"newName\": \"Guard02\", \"localPosition\": { \"x\": 4, \"y\": 0, \"z\": 2 } }",
                "{ \"action\": \"edit_prefab\", \"assetPath\": \"Assets/Prefabs/Guard.prefab\", \"childPath\": \"Visual\", \"component\": \"MeshRenderer\", \"field\": \"m_Enabled\", \"value\": \"true\" }",
                "```",
                "",
                "`create_prefab` and all asset creators refuse to overwrite. `edit_prefab` accepts transform fields and/or one scalar or scalar-array serialized property; it never invokes arbitrary methods on prefab components.",
                "",
                "## Materials and fixed shader templates",
                "",
                "```json",
                "{ \"action\": \"create_shader_asset\", \"assetPath\": \"Assets/Shaders/GeneratedTint.shader\", \"shaderName\": \"Alepou/Generated/Tint\", \"template\": \"unlit-color\" }",
                "{ \"action\": \"create_material\", \"assetPath\": \"Assets/Materials/Guard.mat\", \"shaderAssetPath\": \"Assets/Shaders/GeneratedTint.shader\", \"propertyName\": \"_BaseColor\", \"color\": { \"r\": 0.2, \"g\": 0.7, \"b\": 1, \"a\": 1 } }",
                "{ \"action\": \"set_material_property\", \"assetPath\": \"Assets/Materials/Guard.mat\", \"propertyName\": \"_BaseColor\", \"valueType\": \"color\", \"color\": { \"r\": 1, \"g\": 0.25, \"b\": 0.1, \"a\": 1 } }",
                "```",
                "",
                "`valueType` is `float`, `int`, `color`, `vector`, or `texture`. Shader creation only supports the built-in `unlit-color` and `unlit-texture` source templates. A request cannot supply shader code.",
                "",
                "## Animation clip",
                "",
                "```json",
                "{",
                "  \"action\": \"create_animation_clip\",",
                "  \"assetPath\": \"Assets/Animations/Bob.anim\",",
                "  \"frameRate\": 60,",
                "  \"loopTime\": true,",
                "  \"curves\": [",
                "    { \"relativePath\": \"\", \"componentType\": \"Transform\", \"propertyName\": \"m_LocalPosition.y\", \"keys\": [",
                "      { \"time\": 0, \"value\": 0 }, { \"time\": 0.5, \"value\": 0.25 }, { \"time\": 1, \"value\": 0 }",
                "    ] }",
                "  ]",
                "}",
                "```",
                "",
                "A clip is capped at 100 curves and 200 keys per curve. Component lookup is used only to bind Unity animation curves; no methods are invoked.",
                "",
                "## Lighting, bounded multi-object edits, and menu allowlist",
                "",
                "```json",
                "{ \"action\": \"create_light\", \"objectPath\": \"Lighting/Key\", \"lightType\": \"Directional\", \"intensity\": 1.2, \"rotationEuler\": { \"x\": 45, \"y\": -30, \"z\": 0 } }",
                "{ \"action\": \"batch_edit_objects\", \"objectEdits\": [",
                "  { \"objectPath\": \"Props/A\", \"localPosition\": { \"x\": -2, \"y\": 0, \"z\": 0 } },",
                "  { \"objectPath\": \"Props/B\", \"localPosition\": { \"x\": 2, \"y\": 0, \"z\": 0 } }",
                "] }",
                "{ \"action\": \"execute_menu_item\", \"menuPath\": \"File/Save\" }",
                "```",
                "",
                "`edit_prefab` and `set_material_property` are included in the session-bound Trusted Development preset; fixed-template shader creation remains an exact Custom Expert capability. `batch_edit_objects` is capped at 100 explicit objects. `bake_lighting` is a separately selectable, potentially long Custom Expert capability. `clear_baked_lighting` always remains human-only. Menu execution accepts only `Assets/Refresh`, `File/Save`, and `File/Save Project`; every other path fails closed. To intentionally assign zero light intensity, provide `setIntensity: true` with `intensity: 0`.",
                "",
                "Unity materializes omitted nested vector/color objects as zeroes. Non-zero vectors/colors are detected automatically. To intentionally assign an all-zero vector or black/transparent color, set the matching presence flag: `setPosition`, `setRotationEuler`, `setLocalPosition`, `setLocalRotationEuler`, `setLocalScale`, or `setColor`. Omitted values never zero an existing transform or color.",
                ""
            }) + "\n";
        }

        private string BuildLodeRunnerStarterRecipe()
        {
            return string.Join("\n", new[]
            {
                "# Lode Runner Starter Recipe",
                "",
                "Use this as the first-pass object/script breakdown for a small Lode Runner style prototype.",
                "",
                "## Script Pass",
                "",
                "Create scripts first, then run the refresh-assets checkpoint before attaching them:",
                "",
                "- `Assets/Scripts/LodeRunner/LodeRunnerGameManager.cs`: score, win/lose state, restart.",
                "- `Assets/Scripts/LodeRunner/LodeRunnerPlayerController.cs`: grid movement, ladder climbing, rope/bar traversal, pickup trigger handling.",
                "- `Assets/Scripts/LodeRunner/LodeRunnerEnemy.cs`: simple patrol/chase movement.",
                "- `Assets/Scripts/LodeRunner/LodeRunnerPickup.cs`: gold pickup trigger.",
                "- `Assets/Scripts/LodeRunner/LodeRunnerTile.cs`: marks brick, ladder, rope, solid, spawn, and exit tiles.",
                "",
                "## Scene Pass",
                "",
                "After compile status is clean, create a bounded scene batch:",
                "",
                "- `GameRoot/Managers` with `LodeRunnerGameManager`.",
                "- `Level/Bricks`, `Level/Ladders`, `Level/Ropes`, and `Level/Pickups` containers.",
                "- `Player` with placeholder sprite/cube, collider, rigidbody if needed, and `LodeRunnerPlayerController`.",
                "- `Enemies/Enemy_01` with placeholder sprite/cube, collider, and `LodeRunnerEnemy`.",
                "- `Main Camera` positioned for a 2D side view.",
                "",
                "Keep the first batch small enough that a failed command can be diagnosed from `command-results.md` and retried without rebuilding the whole scene.",
                ""
            }) + "\n";
        }

        private CommandResultLog BuildCommandResultLog()
        {
            var entries = new List<CommandResultEntry>();
            foreach (var bucket in new[] { "applied", "failed", "rejected" })
            {
                var dir = CommandsDir(bucket);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.result.json"))
                {
                    CommandResult result = null;
                    try
                    {
                        result = JsonUtility.FromJson<CommandResult>(File.ReadAllText(file));
                    }
                    catch
                    {
                        result = null;
                    }
                    result = result ?? new CommandResult();
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var commandStem = stem.EndsWith(".result", StringComparison.Ordinal)
                        ? stem.Substring(0, stem.Length - ".result".Length) + ".command.json"
                        : stem + ".command.json";
                    var commandFile = Path.Combine(Path.GetDirectoryName(file), commandStem);
                    entries.Add(new CommandResultEntry
                    {
                        bucket = bucket,
                        resultFile = RelativeBridgePath(file),
                        commandFile = File.Exists(commandFile) ? RelativeBridgePath(commandFile) : "",
                        modifiedAt = File.GetLastWriteTimeUtc(file).ToString("o"),
                        commandId = result.commandId,
                        workflowId = result.workflowId,
                        sessionId = result.sessionId,
                        wakeOnTerminal = result.wakeOnTerminal,
                        status = string.IsNullOrWhiteSpace(result.status) ? bucket : result.status,
                        message = result.message,
                        processedAt = result.processedAt,
                        approvalProfile = result.approvalProfile,
                        approvalRule = result.approvalRule,
                        grantId = result.grantId,
                        requestHash = result.requestHash,
                        actionsApplied = result.actionsApplied ?? Array.Empty<string>(),
                        objectsChanged = result.objectsChanged ?? Array.Empty<string>()
                    });
                }
            }
            return new CommandResultLog
            {
                generatedAt = DateTimeOffset.Now.ToString("o"),
                pendingCommandCount = PendingCommandCount(),
                recentResults = entries
                    .OrderByDescending(e => e.modifiedAt)
                    .Take(20)
                    .ToArray()
            };
        }

        private string BuildCommandResultsMarkdown(CommandResultLog log)
        {
            var lines = new List<string>
            {
                "# Unity Command Results",
                "",
                "Generated: " + log.generatedAt,
                "Pending commands: " + log.pendingCommandCount,
                ""
            };
            if (log.recentResults == null || log.recentResults.Length == 0)
            {
                lines.Add("No command results found yet.");
                return string.Join("\n", lines) + "\n";
            }
            lines.Add("## Recent Results");
            foreach (var result in log.recentResults)
            {
                var message = (result.message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                lines.Add("- [" + result.bucket + "] " + (result.commandId ?? "(unknown)") + " at " + (result.processedAt ?? result.modifiedAt) + ": " + (string.IsNullOrWhiteSpace(message) ? result.status : message));
                if (!string.IsNullOrWhiteSpace(result.resultFile)) lines.Add("  - result: `" + result.resultFile + "`");
                if (!string.IsNullOrWhiteSpace(result.commandFile)) lines.Add("  - command: `" + result.commandFile + "`");
                if (!string.IsNullOrWhiteSpace(result.approvalRule))
                {
                    lines.Add("  - approval: " + result.approvalRule + " (" + (result.approvalProfile ?? "unknown") + ")" +
                        (string.IsNullOrWhiteSpace(result.grantId) ? "" : ", grant `" + result.grantId + "`"));
                }
                if (!string.IsNullOrWhiteSpace(result.requestHash)) lines.Add("  - request hash: `" + result.requestHash + "`");
                if (result.actionsApplied != null && result.actionsApplied.Length > 0) lines.Add("  - actions: " + string.Join("; ", result.actionsApplied));
                if (result.objectsChanged != null && result.objectsChanged.Length > 0) lines.Add("  - objects: " + string.Join("; ", result.objectsChanged));
            }
            return string.Join("\n", lines) + "\n";
        }

        internal int PendingCommandCount()
        {
            var dir = CommandsDir("pending");
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.json").Length;
        }

        internal int ProcessingCommandCount()
        {
            var dir = CommandsDir("processing");
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.json").Length;
        }

        internal int WaitingForHumanCommandCount()
        {
            return WaitingPolicyDecisions().Count;
        }

        internal string FirstWaitingForHumanReason()
        {
            var waiting = WaitingPolicyDecisions();
            return waiting.Count == 0 ? null : waiting[0].reason;
        }

        private List<ApprovalDecision> WaitingPolicyDecisions()
        {
            var decisions = new List<ApprovalDecision>();
            var dir = CommandsDir("pending");
            if (!Directory.Exists(dir)) return decisions;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var command = ParseCommand(raw, file);
                    var decision = EvaluatePolicy(command, false);
                    if (!decision.allowed) decisions.Add(decision);
                }
                catch
                {
                    decisions.Add(ApprovalDecision.Deny(
                        "unreadable-command",
                        "A pending command cannot be read or parsed.",
                        AlepouUnityBridgeApprovalPolicy.ManualProfile,
                        null));
                }
            }
            return decisions;
        }

        private string RelativeBridgePath(string file)
        {
            var full = Path.GetFullPath(file);
            var root = Path.GetFullPath(outputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(root.Length).Replace("\\", "/");
            }
            return full.Replace("\\", "/");
        }

        private ApprovalDecision EvaluatePolicy(BridgeCommand command, bool consume)
        {
            return AlepouUnityBridgeApprovalPolicy.Evaluate(
                command == null ? null : command.grantId,
                command == null ? null : command.sessionId,
                command == null ? Array.Empty<string>() : command.Actions().Select(action => action.ActionName()),
                consume);
        }

        private void RecordPendingPolicyDecisionOnce(string file, BridgeCommand command, string raw, ApprovalDecision decision)
        {
            string modified;
            try { modified = File.GetLastWriteTimeUtc(file).Ticks.ToString(CultureInfo.InvariantCulture); }
            catch { modified = "unknown"; }
            var signature = modified + "|" + decision.rule + "|" + (decision.grantId ?? "");
            string existing;
            if (pendingPolicyDecisionCache.TryGetValue(file, out existing) && string.Equals(existing, signature, StringComparison.Ordinal)) return;
            pendingPolicyDecisionCache[file] = signature;
            RecordPolicyDecision(command, file, raw, decision, "waiting-for-human");
        }

        private void RecordPolicyDecision(BridgeCommand command, string file, string raw, ApprovalDecision decision, string phase)
        {
            try
            {
                var actions = command == null
                    ? Array.Empty<string>()
                    : command.Actions().Select(action => action.ActionName()).ToArray();
                var entry = new ApprovalAuditEntry
                {
                    decidedAt = DateTimeOffset.UtcNow.ToString("o"),
                    commandId = command == null ? null : command.commandId,
                    commandFile = Path.GetFileName(file),
                    phase = phase,
                    allowed = decision.allowed,
                    rule = decision.rule,
                    reason = decision.reason,
                    profile = decision.profile,
                    grantId = decision.grantId,
                    sessionBound = command != null && !string.IsNullOrWhiteSpace(command.sessionId),
                    requestHash = ComputeRequestHash(raw),
                    actionCount = actions.Length,
                    actions = actions
                };
                var auditPath = Path.Combine(outputPath, "approval-audit.jsonl");
                var lines = File.Exists(auditPath)
                    ? File.ReadAllLines(auditPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
                    : new List<string>();
                lines.Add(JsonUtility.ToJson(entry));
                if (lines.Count > 200) lines = lines.Skip(lines.Count - 200).ToList();
                WriteText("approval-audit.jsonl", string.Join("\n", lines) + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Alepou Unity Bridge could not write approval audit: " + ex.Message);
            }
        }

        private void ReturnClaimedCommandToPending(string claimedFile)
        {
            var destination = Path.Combine(CommandsDir("pending"), Path.GetFileName(claimedFile));
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    CommandsDir("pending"),
                    Path.GetFileNameWithoutExtension(claimedFile) + "-policy-wait-" +
                    Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            }
            File.Move(claimedFile, destination);
        }

        private static string ComputeRequestHash(string raw)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw ?? "")))
                    .Replace("-", "")
                    .ToLowerInvariant();
            }
        }

        private static void DecorateResult(CommandResult result, ApprovalDecision decision, string requestHash, BridgeCommand command)
        {
            result.schemaVersion = 1;
            result.workflowId = command == null ? null : command.workflowId;
            result.sessionId = command == null ? null : command.sessionId;
            result.wakeOnTerminal = command != null && command.wakeOnTerminal;
            result.approvalProfile = decision == null ? null : decision.profile;
            result.approvalRule = decision == null ? null : decision.rule;
            result.grantId = decision == null ? null : decision.grantId;
            result.requestHash = requestHash;
        }

        internal bool TryApplyPendingCommand(string file, bool automatic)
        {
            string previewRaw;
            BridgeCommand previewCommand;
            try
            {
                previewRaw = File.ReadAllText(file);
                previewCommand = ParseCommand(previewRaw, file);
            }
            catch
            {
                return false;
            }
            var previewDecision = automatic
                ? EvaluatePolicy(previewCommand, false)
                : AlepouUnityBridgeApprovalPolicy.ManualApproval();
            if (!previewDecision.allowed)
            {
                RecordPendingPolicyDecisionOnce(file, previewCommand, previewRaw, previewDecision);
                return false;
            }

            string claimedFile;
            string raw;
            BridgeCommand command;
            if (!TryClaimPendingCommand(file, out claimedFile, out command, out raw)) return false;

            var requestHash = ComputeRequestHash(raw);
            var decision = automatic
                ? EvaluatePolicy(command, false)
                : AlepouUnityBridgeApprovalPolicy.ManualApproval();
            if (!decision.allowed)
            {
                ReturnClaimedCommandToPending(claimedFile);
                RecordPolicyDecision(command, claimedFile, raw, decision, "claim-denied");
                return false;
            }

            AlepouUnityBridgeService.NotifyCommandStarted(command.commandId);
            CommandResult result;
            try
            {
                if (HasCompletedCommandId(command.commandId))
                {
                    result = new CommandResult
                    {
                        commandId = command.commandId,
                        status = "failed",
                        message = "Duplicate commandId was not executed: " + command.commandId,
                        processedAt = DateTimeOffset.Now.ToString("o"),
                        actionsApplied = Array.Empty<string>(),
                        objectsChanged = Array.Empty<string>()
                    };
                    DecorateResult(result, decision, requestHash, command);
                    RecordPolicyDecision(command, claimedFile, raw, decision, "duplicate-not-executed");
                    MoveCommand(claimedFile, "failed", result, raw);
                    ExportSnapshot(false);
                    lastMessage = result.message;
                }
                else
                {
                    if (automatic)
                    {
                        decision = EvaluatePolicy(command, true);
                        if (!decision.allowed)
                        {
                            ReturnClaimedCommandToPending(claimedFile);
                            RecordPolicyDecision(command, claimedFile, raw, decision, "claim-denied");
                            AlepouUnityBridgeService.NotifyCommandFinished("waiting-for-human", decision.reason);
                            return false;
                        }
                    }
                    RecordPolicyDecision(command, claimedFile, raw, decision, automatic ? "delegated-claim" : "manual-claim");
                    result = ApplyCommand(claimedFile, command, raw, decision, requestHash);
                }
            }
            catch (Exception ex)
            {
                result = new CommandResult
                {
                    commandId = command.commandId,
                    status = "failed",
                    message = "Command processor failed: " + ex.Message,
                    processedAt = DateTimeOffset.Now.ToString("o"),
                    actionsApplied = Array.Empty<string>(),
                    objectsChanged = Array.Empty<string>()
                };
                DecorateResult(result, decision, requestHash, command);
                try { MoveCommand(claimedFile, "failed", result, raw); }
                catch { }
                lastMessage = result.message;
            }
            AlepouUnityBridgeService.NotifyCommandFinished(result.status, result.message);
            return true;
        }

        internal bool TryRejectPendingCommand(string file)
        {
            string claimedFile;
            string raw;
            BridgeCommand command;
            if (!TryClaimPendingCommand(file, out claimedFile, out command, out raw)) return false;
            var decision = AlepouUnityBridgeApprovalPolicy.ManualRejection();
            var requestHash = ComputeRequestHash(raw);
            RecordPolicyDecision(command, claimedFile, raw, decision, "manual-rejection");
            AlepouUnityBridgeService.NotifyCommandStarted(command.commandId);
            CommandResult result;
            if (HasCompletedCommandId(command.commandId))
            {
                result = new CommandResult
                {
                    commandId = command.commandId,
                    status = "failed",
                    message = "Duplicate commandId was not processed: " + command.commandId,
                    processedAt = DateTimeOffset.Now.ToString("o"),
                    actionsApplied = Array.Empty<string>(),
                    objectsChanged = Array.Empty<string>()
                };
                DecorateResult(result, decision, requestHash, command);
                MoveCommand(claimedFile, "failed", result, raw);
            }
            else
            {
                result = RejectCommand(claimedFile, command, raw, decision, requestHash);
            }
            ExportSnapshot(false);
            AlepouUnityBridgeService.NotifyCommandFinished(result.status, result.message);
            return true;
        }

        internal int RecoverInterruptedCommands()
        {
            EnsureBridgeFolders();
            var recovered = 0;
            foreach (var file in Directory.GetFiles(CommandsDir("processing"), "*.json"))
            {
                string raw;
                try { raw = File.ReadAllText(file); }
                catch { raw = ""; }
                var command = ParseCommand(raw, file);
                var result = new CommandResult
                {
                    commandId = command.commandId,
                    status = "interrupted",
                    message = "Unity reloaded while this claimed command was processing. It was not replayed; inspect the project before retrying with a new commandId.",
                    processedAt = DateTimeOffset.Now.ToString("o"),
                    actionsApplied = Array.Empty<string>(),
                    objectsChanged = Array.Empty<string>()
                };
                DecorateResult(result, ApprovalDecision.Deny(
                    "interrupted-recovery",
                    result.message,
                    AlepouUnityBridgeApprovalPolicy.ManualProfile,
                    command.grantId), ComputeRequestHash(raw), command);
                try
                {
                    MoveCommand(file, "failed", result, raw);
                    recovered++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Alepou Unity Bridge could not recover interrupted command " + file + ": " + ex.Message);
                }
            }
            if (recovered > 0)
            {
                lastMessage = "Recovered " + recovered + " interrupted command(s) without replaying them.";
                ExportSnapshot(false);
            }
            return recovered;
        }

        private bool TryClaimPendingCommand(string file, out string claimedFile, out BridgeCommand command, out string raw)
        {
            claimedFile = null;
            command = null;
            raw = null;
            try
            {
                var fullFile = Path.GetFullPath(file);
                var pendingRoot = Path.GetFullPath(CommandsDir("pending")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(fullFile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(parent, pendingRoot, StringComparison.OrdinalIgnoreCase)) return false;

                Directory.CreateDirectory(CommandsDir("processing"));
                claimedFile = Path.Combine(CommandsDir("processing"), Path.GetFileName(fullFile));
                if (File.Exists(claimedFile)) return false;
                File.Move(fullFile, claimedFile);

                raw = File.ReadAllText(claimedFile);
                command = ParseCommand(raw, claimedFile);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private bool HasCompletedCommandId(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId)) return false;
            foreach (var bucket in new[] { "applied", "failed", "rejected", "archive" })
            {
                var dir = CommandsDir(bucket);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.result.json"))
                {
                    try
                    {
                        var result = JsonUtility.FromJson<CommandResult>(File.ReadAllText(file));
                        if (result != null && string.Equals(result.commandId, commandId, StringComparison.Ordinal)) return true;
                    }
                    catch
                    {
                        // A malformed historical result cannot prove this command already ran.
                    }
                }
            }
            return false;
        }

        private CommandResult ApplyCommand(string file, BridgeCommand command, string raw, ApprovalDecision decision, string requestHash)
        {
            var applied = new List<string>();
            var changed = new List<string>();
            var result = new CommandResult { commandId = command.commandId, status = "applied", message = "Applied.", processedAt = DateTimeOffset.Now.ToString("o") };
            DecorateResult(result, decision, requestHash, command);
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(string.IsNullOrWhiteSpace(command.titleOrId) ? "Alepou Unity command" : command.titleOrId);
            try
            {
                var actions = command.Actions();
                if (actions.Length == 0) throw new InvalidOperationException("Command did not contain any actions.");
                foreach (var action in actions)
                {
                    applied.Add(ApplyAction(action, changed));
                }
                Undo.CollapseUndoOperations(undoGroup);
                result.actionsApplied = applied.ToArray();
                result.objectsChanged = changed.Distinct().ToArray();
                MoveCommand(file, "applied", result, raw);
                ExportSnapshot(false);
                lastMessage = "Applied command " + command.titleOrId;
            }
            catch (Exception ex)
            {
                Undo.CollapseUndoOperations(undoGroup);
                result.status = "failed";
                result.message = ex.Message;
                result.actionsApplied = applied.ToArray();
                result.objectsChanged = changed.Distinct().ToArray();
                MoveCommand(file, "failed", result, raw);
                ExportSnapshot(false);
                lastMessage = "Command failed: " + ex.Message;
            }
            return result;
        }

        private string ApplyAction(BridgeAction action, List<string> changed)
        {
            var actionName = action.ActionName();
            if (actionName == "export_snapshot")
            {
                ExportSnapshot(false);
                return "export_snapshot";
            }
            if (actionName == "capture_view")
            {
                return CaptureView(action, changed);
            }
            if (actionName == "frame_ui_builder_document")
            {
                return FrameUiBuilderDocument();
            }
            if (actionName == "open_ui_builder")
            {
                return OpenUiBuilderWindow(action, changed);
            }
            if (actionName == "close_ui_builder")
            {
                return CloseUiBuilderWindow(changed);
            }
            if (actionName == "query_assets")
            {
                return QueryAssets(action);
            }
            if (actionName == "read_object")
            {
                return ReadObject(action);
            }
            if (actionName == "read_asset")
            {
                return ReadAsset(action);
            }
            if (actionName == "refresh_assets")
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                changed.Add("Assets");
                return "refresh_assets";
            }
            if (actionName == "save_scene")
            {
                var savedPath = SaveSceneForAction(action);
                changed.Add(savedPath);
                return "save_scene " + savedPath;
            }
            if (actionName == "create_folder")
            {
                var folderPath = EnsureAssetFolder(action.AssetPathForAction());
                changed.Add(folderPath);
                return "create_folder " + folderPath;
            }
            if (actionName == "create_scriptable_object_asset")
            {
                var asset = CreateScriptableObjectAsset(action, changed);
                return "create_scriptable_object_asset " + AssetDatabase.GetAssetPath(asset);
            }
            if (actionName == "select_object")
            {
                var pathValue = action.TargetPath();
                if (LooksLikeAssetPath(pathValue))
                {
                    var asset = RequireAsset(pathValue, action.targetType);
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                    changed.Add(AssetDatabase.GetAssetPath(asset));
                    return "select_object " + AssetDatabase.GetAssetPath(asset);
                }
                var go = FindObjectByPath(pathValue);
                if (go == null) throw new InvalidOperationException("Object not found: " + pathValue);
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                changed.Add(ObjectPath(go));
                return "select_object " + ObjectPath(go);
            }
            if (actionName == "ping_asset")
            {
                var asset = RequireAsset(action.AssetPathForAction(), action.targetType);
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                changed.Add(AssetDatabase.GetAssetPath(asset));
                return "ping_asset " + AssetDatabase.GetAssetPath(asset);
            }
            if (actionName == "create_gameobject")
            {
                var pathValue = action.TargetPath();
                if (string.IsNullOrWhiteSpace(pathValue)) throw new InvalidOperationException("create_gameobject requires path/objectPath/name.");
                var go = EnsureObjectPath(pathValue, changed);
                Selection.activeGameObject = go;
                return "create_gameobject " + ObjectPath(go);
            }
            if (actionName == "destroy_gameobject")
            {
                var go = RequireObject(action.ObjectPathForAction(), "destroy_gameobject target object");
                var destroyedPath = ObjectPath(go);
                Undo.DestroyObjectImmediate(go);
                changed.Add(destroyedPath);
                return "destroy_gameobject " + destroyedPath;
            }
            if (actionName == "set_parent")
            {
                var go = RequireObject(action.ObjectPathForAction(), "set_parent target object");
                var parent = string.IsNullOrWhiteSpace(action.parent) ? null : RequireObject(action.parent, "set_parent parent");
                Undo.SetTransformParent(go.transform, parent == null ? null : parent.transform, "Alepou set parent");
                changed.Add(ObjectPath(go));
                return "set_parent " + go.name;
            }
            if (actionName == "set_transform")
            {
                var go = RequireObject(action.ObjectPathForAction(), "set_transform object");
                ApplyTransform(go.transform, action);
                changed.Add(ObjectPath(go));
                return "set_transform " + ObjectPath(go);
            }
            if (actionName == "add_component")
            {
                var go = RequireObject(action.ObjectPathForAction(), "add_component object");
                var type = ResolveComponentType(action.component);
                if (type == null) throw new InvalidOperationException("Component type not found: " + action.component);
                var existing = go.GetComponent(type);
                if (existing != null && !action.allowDuplicate)
                {
                    changed.Add(ObjectPath(go));
                    return "add_component existing " + type.Name + " on " + ObjectPath(go);
                }
                Undo.AddComponent(go, type);
                changed.Add(ObjectPath(go));
                return "add_component " + type.Name + " on " + ObjectPath(go);
            }
            if (actionName == "remove_component")
            {
                var component = RequireComponent(action.ObjectPathForAction(), action.component);
                var removed = component.GetType().Name + " on " + ObjectPath(component.gameObject);
                Undo.DestroyObjectImmediate(component);
                changed.Add(action.ObjectPathForAction());
                return "remove_component " + removed;
            }
            if (actionName == "assign_reference")
            {
                var component = RequireComponent(action.ObjectPathForAction(), action.component);
                AssignReference(component, action.field, ResolveReferenceTarget(action));
                changed.Add(ObjectPath(component.gameObject));
                return "assign_reference " + action.field + " on " + ObjectPath(component.gameObject);
            }
            if (actionName == "assign_asset_reference")
            {
                var component = RequireComponent(action.ObjectPathForAction(), action.component);
                var asset = RequireAsset(action.AssetPathForAction(), action.targetType);
                AssignReference(component, action.field, asset);
                changed.Add(ObjectPath(component.gameObject));
                changed.Add(AssetDatabase.GetAssetPath(asset));
                return "assign_asset_reference " + action.field + " on " + ObjectPath(component.gameObject);
            }
            if (actionName == "set_property")
            {
                var component = RequireComponent(action.ObjectPathForAction(), action.component);
                SetSerializedProperty(component, action.field, action, "Alepou set property");
                changed.Add(ObjectPath(component.gameObject));
                return "set_property " + action.field + " on " + ObjectPath(component.gameObject);
            }
            if (actionName == "set_asset_property")
            {
                var asset = RequireAsset(action.AssetPathForAction(), action.type);
                SetSerializedProperty(asset, action.field, action, "Alepou set asset property");
                changed.Add(AssetDatabase.GetAssetPath(asset));
                return "set_asset_property " + action.field + " on " + AssetDatabase.GetAssetPath(asset);
            }
            if (actionName == "move_asset")
            {
                var moved = MoveAssetForAction(action);
                changed.Add(moved);
                return "move_asset " + moved;
            }
            if (actionName == "rename_asset")
            {
                var renamed = RenameAssetForAction(action);
                changed.Add(renamed);
                return "rename_asset " + renamed;
            }
            if (actionName == "delete_asset")
            {
                var deleted = DeleteAssetForAction(action);
                changed.Add(deleted);
                return "delete_asset " + deleted;
            }
            if (actionName == "set_player_setting")
            {
                var setting = SetPlayerSetting(action);
                changed.Add("ProjectSettings/ProjectSettings.asset");
                return "set_player_setting " + setting;
            }
            if (actionName == "set_build_scenes")
            {
                var count = SetBuildScenes(action);
                changed.Add("ProjectSettings/EditorBuildSettings.asset");
                return "set_build_scenes " + count + " scene(s)";
            }
            if (AlepouUnityBridgeAuthoring.Supports(actionName))
            {
                return AlepouUnityBridgeAuthoring.Apply(
                    new AlepouUnityBridgeAuthoring.AuthoringRequest
                    {
                        action = actionName,
                        path = action.path,
                        objectPath = action.ObjectPathForAction(),
                        parent = action.parent,
                        newName = action.newName,
                        assetPath = action.AssetPathForAction(),
                        childPath = action.childPath,
                        component = action.component,
                        field = action.field,
                        value = action.value,
                        values = action.values,
                        shader = action.shader,
                        shaderAssetPath = action.shaderAssetPath,
                        shaderName = action.shaderName,
                        template = action.template,
                        propertyName = action.propertyName,
                        valueType = action.valueType,
                        textureAssetPath = action.textureAssetPath,
                        menuPath = action.menuPath,
                        lightType = action.lightType,
                        lightShadows = action.lightShadows,
                        intensity = action.intensity,
                        range = action.range,
                        spotAngle = action.spotAngle,
                        frameRate = action.frameRate,
                        loopTime = action.loopTime,
                        setColor = action.setColor,
                        setIntensity = action.setIntensity,
                        setPosition = action.setPosition,
                        setRotationEuler = action.setRotationEuler,
                        setLocalPosition = action.setLocalPosition,
                        setLocalRotationEuler = action.setLocalRotationEuler,
                        setLocalScale = action.setLocalScale,
                        color = action.color,
                        vector = action.vector,
                        position = ToAuthoringVector(action.position),
                        rotationEuler = ToAuthoringVector(action.rotationEuler),
                        localPosition = ToAuthoringVector(action.localPosition),
                        localRotationEuler = ToAuthoringVector(action.localRotationEuler),
                        localScale = ToAuthoringVector(action.localScale),
                        curves = action.curves,
                        objectEdits = action.objectEdits
                    },
                    changed);
            }
            throw new InvalidOperationException("Unsupported action: " + actionName);
        }

        private static AlepouUnityBridgeAuthoring.Vector3Request ToAuthoringVector(Vector3Data value)
        {
            return value == null
                ? null
                : new AlepouUnityBridgeAuthoring.Vector3Request { x = value.x, y = value.y, z = value.z };
        }

        private CommandResult RejectCommand(string file, BridgeCommand command, string raw, ApprovalDecision decision, string requestHash)
        {
            var result = new CommandResult
            {
                commandId = command.commandId,
                status = "rejected",
                message = "Rejected in Unity Bridge window.",
                processedAt = DateTimeOffset.Now.ToString("o")
            };
            DecorateResult(result, decision, requestHash, command);
            MoveCommand(file, "rejected", result, raw);
            ExportSnapshot(false);
            lastMessage = "Rejected command " + command.titleOrId;
            return result;
        }

        private void MoveCommand(string sourceFile, string bucket, CommandResult result, string rawCommand)
        {
            var stem = Path.GetFileNameWithoutExtension(sourceFile);
            var dir = CommandsDir(bucket);
            Directory.CreateDirectory(dir);
            if (File.Exists(Path.Combine(dir, stem + ".result.json")) || File.Exists(Path.Combine(dir, stem + ".command.json")))
            {
                stem += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            WriteText(Path.Combine("commands", bucket, stem + ".result.json"), JsonUtility.ToJson(result, true));
            WriteText(Path.Combine("commands", bucket, stem + ".command.json"), rawCommand);
            File.Delete(sourceFile);
        }

        private BridgeCommand ParseCommand(string raw, string file)
        {
            try
            {
                var command = JsonUtility.FromJson<BridgeCommand>(raw);
                if (command == null) throw new InvalidOperationException("Empty command.");
                if (string.IsNullOrWhiteSpace(command.commandId)) command.commandId = Path.GetFileNameWithoutExtension(file);
                return command;
            }
            catch
            {
                return new BridgeCommand { commandId = Path.GetFileNameWithoutExtension(file), title = "Invalid command JSON", summary = "Unity could not parse this command." };
            }
        }

        private GameObject FindObjectByPath(string pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue)) return null;
            var parts = pathValue.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                var current = root.transform;
                for (var i = 1; i < parts.Length; i++)
                {
                    current = current.Cast<Transform>().FirstOrDefault(child => child.name == parts[i]);
                    if (current == null) break;
                }
                if (current != null) return current.gameObject;
            }
            return null;
        }

        private GameObject RequireObject(string pathValue, string label)
        {
            var go = FindObjectByPath(pathValue);
            if (go == null) throw new InvalidOperationException(label + " not found: " + pathValue);
            return go;
        }

        private GameObject EnsureObjectPath(string pathValue, List<string> changed)
        {
            var clean = (pathValue ?? "").Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(clean)) throw new InvalidOperationException("GameObject path is empty.");
            var existing = FindObjectByPath(clean);
            if (existing != null) return existing;

            var parts = clean.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            GameObject current = null;
            var currentPath = "";
            for (var i = 0; i < parts.Length; i++)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : currentPath + "/" + parts[i];
                var found = FindObjectByPath(currentPath);
                if (found != null)
                {
                    current = found;
                    continue;
                }
                var created = new GameObject(parts[i]);
                Undo.RegisterCreatedObjectUndo(created, "Alepou create GameObject");
                if (current != null)
                {
                    Undo.SetTransformParent(created.transform, current.transform, "Alepou parent GameObject");
                    created.transform.localPosition = Vector3.zero;
                    created.transform.localRotation = Quaternion.identity;
                    created.transform.localScale = Vector3.one;
                }
                current = created;
                changed.Add(ObjectPath(created));
            }
            return current;
        }

        private string SaveSceneForAction(BridgeAction action)
        {
            var rawPath = !string.IsNullOrWhiteSpace(action.scenePath) ? action.scenePath : action.path;
            var scene = SceneManager.GetActiveScene();
            var saveAsPath = "";
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                var scenePath = NormalizeScenePath(rawPath);
                var loaded = EditorSceneManager.GetSceneByPath(scenePath);
                if (loaded.IsValid() && loaded.isLoaded)
                {
                    scene = loaded;
                }
                else if (string.IsNullOrWhiteSpace(scene.path))
                {
                    saveAsPath = scenePath;
                }
                else
                {
                    throw new InvalidOperationException("Scene is not loaded: " + scenePath);
                }
            }
            if (!scene.IsValid()) throw new InvalidOperationException("No valid scene to save.");
            if (string.IsNullOrWhiteSpace(scene.path) && string.IsNullOrWhiteSpace(saveAsPath))
            {
                throw new InvalidOperationException("Active scene has no path; provide scenePath.");
            }
            var ok = string.IsNullOrWhiteSpace(saveAsPath)
                ? EditorSceneManager.SaveScene(scene)
                : EditorSceneManager.SaveScene(scene, saveAsPath);
            if (!ok) throw new InvalidOperationException("Unity failed to save scene.");
            return string.IsNullOrWhiteSpace(saveAsPath) ? scene.path : saveAsPath;
        }

        private static bool LooksLikeAssetPath(string rawPath)
        {
            var value = (rawPath ?? "").Trim().Replace('\\', '/');
            return value == "Assets" || value.StartsWith("Assets/", StringComparison.Ordinal);
        }

        private void ApplyTransform(Transform transform, BridgeAction action)
        {
            Undo.RecordObject(transform, "Alepou set transform");
            if (action.position != null) transform.position = action.position.ToVector3();
            if (action.rotationEuler != null) transform.rotation = Quaternion.Euler(action.rotationEuler.ToVector3());
            if (action.localPosition != null) transform.localPosition = action.localPosition.ToVector3();
            if (action.localRotationEuler != null) transform.localRotation = Quaternion.Euler(action.localRotationEuler.ToVector3());
            if (action.localScale != null) transform.localScale = action.localScale.ToVector3();
            EditorUtility.SetDirty(transform);
        }

        private Component RequireComponent(string objectPath, string componentName)
        {
            var go = RequireObject(objectPath, "component host object");
            var type = ResolveComponentType(componentName);
            if (type == null) throw new InvalidOperationException("Component type not found: " + componentName);
            var component = go.GetComponent(type);
            if (component == null) throw new InvalidOperationException("Component " + componentName + " not found on " + objectPath);
            return component;
        }

        private static Type ResolveComponentType(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return null;
            var wanted = componentName.Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type)) continue;
                    if (type.Name == wanted || type.FullName == wanted) return type;
                }
            }
            return null;
        }

        private UnityEngine.Object CreateScriptableObjectAsset(BridgeAction action, List<string> changed)
        {
            var assetPath = NormalizeAssetPath(action.AssetPathForAction(), "assetPath", true);
            var typeName = !string.IsNullOrWhiteSpace(action.type) ? action.type : action.targetType;
            var type = ResolveScriptableObjectType(typeName);
            if (type == null) throw new InvalidOperationException("ScriptableObject type not found: " + typeName);

            var existing = AssetDatabase.LoadAssetAtPath(assetPath, type);
            if (existing != null)
            {
                changed.Add(assetPath);
                Selection.activeObject = existing;
                return existing;
            }

            var conflicting = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (conflicting != null)
            {
                throw new InvalidOperationException("Asset already exists at " + assetPath + " with type " + conflicting.GetType().Name);
            }

            EnsureAssetFolder(ParentAssetFolder(assetPath));
            var asset = ScriptableObject.CreateInstance(type);
            if (asset == null) throw new InvalidOperationException("Could not create ScriptableObject: " + type.FullName);
            asset.name = Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            Selection.activeObject = asset;
            changed.Add(assetPath);
            return asset;
        }

        private string EnsureAssetFolder(string rawFolderPath)
        {
            var folderPath = NormalizeAssetPath(rawFolderPath, "folderPath", false);
            if (AssetDatabase.IsValidFolder(folderPath)) return folderPath;

            var parts = folderPath.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets") throw new InvalidOperationException("Folder path must be under Assets/: " + rawFolderPath);
            var current = "Assets";
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("Could not create folder: " + next);
                }
                current = next;
            }
            AssetDatabase.SaveAssets();
            return folderPath;
        }

        private static string NormalizeAssetPath(string rawPath, string label, bool requireAssetFile)
        {
            var value = (rawPath ?? "").Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(label + " is required.");
            if (value.StartsWith("/", StringComparison.Ordinal) || value.Contains(":")) throw new InvalidOperationException(label + " must be a project-relative Assets/... path: " + rawPath);
            var parts = value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "Assets") throw new InvalidOperationException(label + " must start with Assets/: " + rawPath);
            if (parts.Any(part => part == "." || part == "..")) throw new InvalidOperationException(label + " cannot contain . or .. segments: " + rawPath);
            var normalized = string.Join("/", parts);
            if (requireAssetFile && !normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(label + " must end with .asset: " + rawPath);
            return normalized;
        }

        private static string ParentAssetFolder(string assetPath)
        {
            var index = assetPath.LastIndexOf('/');
            return index > 0 ? assetPath.Substring(0, index) : "Assets";
        }

        private static Type ResolveScriptableObjectType(string typeName)
        {
            var type = ResolveUnityObjectType(typeName);
            if (type == null || type.IsAbstract || !typeof(ScriptableObject).IsAssignableFrom(type)) return null;
            return type;
        }

        private static Type ResolveUnityObjectType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var wanted = typeName.Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(UnityEngine.Object).IsAssignableFrom(type)) continue;
                    if (type.Name == wanted || type.FullName == wanted) return type;
                }
            }
            return null;
        }

        private UnityEngine.Object RequireAsset(string rawAssetPath, string expectedTypeName = null)
        {
            var assetPath = NormalizeAssetPath(rawAssetPath, "assetPath", false);
            Type expectedType = null;
            if (!string.IsNullOrWhiteSpace(expectedTypeName))
            {
                expectedType = ResolveUnityObjectType(expectedTypeName);
                if (expectedType == null) throw new InvalidOperationException("Asset type not found: " + expectedTypeName);
            }
            var asset = expectedType == null
                ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath)
                : AssetDatabase.LoadAssetAtPath(assetPath, expectedType);
            if (asset == null) throw new InvalidOperationException("Asset not found: " + assetPath);
            return asset;
        }

        private string MoveAssetForAction(BridgeAction action)
        {
            var sourcePath = NormalizeAssetPath(action.AssetPathForAction(), "assetPath", false);
            EnsureNotRootAssets(sourcePath, "move_asset");
            if (AssetDatabase.IsValidFolder(sourcePath) && !action.allowFolder)
            {
                throw new InvalidOperationException("move_asset on folders requires allowFolder: true.");
            }
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath) == null && !AssetDatabase.IsValidFolder(sourcePath))
            {
                throw new InvalidOperationException("Asset not found: " + sourcePath);
            }
            var targetPath = NormalizeAssetPath(action.TargetAssetPathForAction(), "targetAssetPath", false);
            EnsureAssetFolder(ParentAssetFolder(targetPath));
            var error = AssetDatabase.MoveAsset(sourcePath, targetPath);
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
            AssetDatabase.SaveAssets();
            return targetPath;
        }

        private string RenameAssetForAction(BridgeAction action)
        {
            var sourcePath = NormalizeAssetPath(action.AssetPathForAction(), "assetPath", false);
            EnsureNotRootAssets(sourcePath, "rename_asset");
            if (AssetDatabase.IsValidFolder(sourcePath) && !action.allowFolder)
            {
                throw new InvalidOperationException("rename_asset on folders requires allowFolder: true.");
            }
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath) == null && !AssetDatabase.IsValidFolder(sourcePath))
            {
                throw new InvalidOperationException("Asset not found: " + sourcePath);
            }
            var newName = (action.newName ?? action.value ?? action.target ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("rename_asset requires newName.");
            if (newName.Contains("/") || newName.Contains("\\") || newName.Contains(":")) throw new InvalidOperationException("newName must be a filename, not a path.");
            newName = Path.GetFileNameWithoutExtension(newName);
            var guid = AssetDatabase.AssetPathToGUID(sourcePath);
            var error = AssetDatabase.RenameAsset(sourcePath, newName);
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
            AssetDatabase.SaveAssets();
            return AssetDatabase.GUIDToAssetPath(guid);
        }

        private string DeleteAssetForAction(BridgeAction action)
        {
            var sourcePath = NormalizeAssetPath(action.AssetPathForAction(), "assetPath", false);
            EnsureNotRootAssets(sourcePath, "delete_asset");
            var isFolder = AssetDatabase.IsValidFolder(sourcePath);
            if (isFolder && !action.allowFolder) throw new InvalidOperationException("delete_asset on folders requires allowFolder: true.");
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath) == null && !isFolder)
            {
                throw new InvalidOperationException("Asset not found: " + sourcePath);
            }
            if (!AssetDatabase.DeleteAsset(sourcePath)) throw new InvalidOperationException("Unity failed to delete asset: " + sourcePath);
            AssetDatabase.SaveAssets();
            return sourcePath;
        }

        private string SetPlayerSetting(BridgeAction action)
        {
            var setting = (action.setting ?? action.field ?? "").Trim();
            if (string.IsNullOrWhiteSpace(setting)) throw new InvalidOperationException("set_player_setting requires setting or field.");
            var raw = action.value ?? "";
            switch (NormalizeSettingName(setting))
            {
                case "productname":
                    PlayerSettings.productName = raw;
                    break;
                case "companyname":
                    PlayerSettings.companyName = raw;
                    break;
                case "bundleversion":
                case "version":
                    PlayerSettings.bundleVersion = raw;
                    break;
                case "applicationidentifier":
                case "bundleidentifier":
                    var group = ParseBuildTargetGroup(action.buildTargetGroup ?? action.target ?? action.targetType);
                    PlayerSettings.SetApplicationIdentifier(group, raw);
                    AssetDatabase.SaveAssets();
                    return "applicationIdentifier[" + group + "]=" + raw;
                default:
                    throw new InvalidOperationException("Unsupported PlayerSettings field: " + setting);
            }
            AssetDatabase.SaveAssets();
            return setting + "=" + raw;
        }

        private int SetBuildScenes(BridgeAction action)
        {
            var scenePaths = action.PathListForAction();
            if (scenePaths == null) throw new InvalidOperationException("set_build_scenes requires paths, scenes, or values.");
            var scenes = scenePaths
                .Select(NormalizeScenePath)
                .Select(path =>
                {
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) throw new InvalidOperationException("Scene asset not found: " + path);
                    return new EditorBuildSettingsScene(path, true);
                })
                .ToArray();
            EditorBuildSettings.scenes = scenes;
            AssetDatabase.SaveAssets();
            return scenes.Length;
        }

        private static void EnsureNotRootAssets(string assetPath, string actionName)
        {
            if (string.Equals(assetPath, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(actionName + " cannot target the project Assets root.");
            }
        }

        private static string NormalizeScenePath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath, "scenePath", false);
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("scenePath must end with .unity: " + rawPath);
            return path;
        }

        private static string NormalizeSettingName(string setting)
        {
            return new string((setting ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static BuildTargetGroup ParseBuildTargetGroup(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return EditorUserBuildSettings.selectedBuildTargetGroup;
            if (Enum.TryParse(raw.Trim(), true, out BuildTargetGroup group)) return group;
            throw new InvalidOperationException("Unknown BuildTargetGroup: " + raw);
        }

        private void AssignReference(Component component, string field, UnityEngine.Object target)
        {
            if (string.IsNullOrWhiteSpace(field)) throw new InvalidOperationException("assign_reference requires field.");
            if (target == null) throw new InvalidOperationException("assign_reference target could not be resolved.");
            Undo.RecordObject(component, "Alepou assign reference");
            var so = new SerializedObject(component);
            var prop = so.FindProperty(field);
            if (prop == null) throw new InvalidOperationException("Serialized field not found: " + field);
            if (prop.propertyType != SerializedPropertyType.ObjectReference) throw new InvalidOperationException("Field is not an object reference: " + field);
            prop.objectReferenceValue = target;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
        }

        private UnityEngine.Object ResolveReferenceTarget(BridgeAction action)
        {
            if (!string.IsNullOrWhiteSpace(action.assetPath))
            {
                return RequireAsset(action.assetPath, action.targetType);
            }
            var targetPath = action.ReferenceTargetPath();
            var go = RequireObject(targetPath, "reference target");
            var targetType = (action.targetType ?? "").Trim();
            var targetComponent = (action.targetComponent ?? "").Trim();
            if (string.Equals(targetType, "GameObject", StringComparison.OrdinalIgnoreCase)) return go;
            if (string.Equals(targetType, "Transform", StringComparison.OrdinalIgnoreCase)) return go.transform;
            if (!string.IsNullOrWhiteSpace(targetComponent))
            {
                var type = ResolveComponentType(targetComponent);
                if (type == null) throw new InvalidOperationException("Target component type not found: " + targetComponent);
                var component = go.GetComponent(type);
                if (component == null) throw new InvalidOperationException("Target component " + targetComponent + " not found on " + targetPath);
                return component;
            }
            return go;
        }

        private void SetSerializedProperty(UnityEngine.Object target, string field, BridgeAction action, string undoLabel)
        {
            if (target == null) throw new InvalidOperationException("set_property target is missing.");
            if (string.IsNullOrWhiteSpace(field)) throw new InvalidOperationException("set_property requires field.");
            Undo.RecordObject(target, undoLabel);
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) throw new InvalidOperationException("Serialized field not found: " + field);
            SetSerializedPropertyValue(prop, action);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            if (!string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(target)))
            {
                AssetDatabase.SaveAssets();
            }
        }

        private void SetSerializedPropertyValue(SerializedProperty prop, BridgeAction action)
        {
            if (prop.propertyType == SerializedPropertyType.Generic && prop.isArray && action.values != null)
            {
                prop.arraySize = action.values.Length;
                for (var i = 0; i < action.values.Length; i++)
                {
                    SetScalarSerializedProperty(prop.GetArrayElementAtIndex(i), action.values[i]);
                }
                return;
            }
            if (action.values != null)
            {
                throw new InvalidOperationException("values can only be used with array or list serialized fields: " + prop.propertyPath);
            }
            SetScalarSerializedProperty(prop, action.value ?? "");
        }

        private void SetScalarSerializedProperty(SerializedProperty prop, string raw)
        {
            raw = raw ?? "";
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(raw, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = bool.Parse(raw);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(raw, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = raw;
                    break;
                case SerializedPropertyType.Enum:
                    var names = prop.enumDisplayNames;
                    var index = Array.FindIndex(names, n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase));
                    prop.enumValueIndex = index >= 0 ? index : int.Parse(raw, CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new InvalidOperationException("set_property does not support " + prop.propertyType + " yet.");
            }
        }

        private void WriteText(string relativePath, string contents)
        {
            var full = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(outputPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var temp = full + ".tmp";
            File.WriteAllText(temp, contents ?? "", Encoding.UTF8);
            if (File.Exists(full)) File.Delete(full);
            File.Move(temp, full);
        }

        private void WriteTextIfChanged(string relativePath, string contents)
        {
            var full = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(outputPath, relativePath);
            if (File.Exists(full) && File.ReadAllText(full, Encoding.UTF8) == (contents ?? "")) return;
            WriteText(relativePath, contents);
        }

        internal static string ObjectPath(GameObject go)
        {
            if (go == null) return null;
            var parts = new List<string>();
            var current = go.transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string GlobalId(UnityEngine.Object obj)
        {
            try
            {
                return GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string ScriptGuid(Component component)
        {
            if (!(component is MonoBehaviour mb)) return "";
            var script = MonoScript.FromMonoBehaviour(mb);
            if (script == null) return "";
            return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
        }

        private static string SerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue.ToString();
                case SerializedPropertyType.Float: return property.floatValue.ToString("R");
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                case SerializedPropertyType.ObjectReference:
                    return DescribeObjectReference(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return property.intValue.ToString();
                case SerializedPropertyType.Enum: return property.enumValueIndex >= 0 && property.enumDisplayNames.Length > property.enumValueIndex ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString();
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.ArraySize: return property.intValue.ToString();
                case SerializedPropertyType.Character: return property.intValue.ToString();
                case SerializedPropertyType.AnimationCurve: return "(animation curve)";
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return property.quaternionValue.eulerAngles.ToString();
                default: return "(" + property.propertyType + ")";
            }
        }

        private static string DescribeObjectReference(UnityEngine.Object obj)
        {
            if (obj == null) return "(null)";
            var basic = obj.name + " [" + obj.GetType().Name + "]";
            if (obj is GameObject go)
            {
                return basic + " path=" + ObjectPath(go) + " globalId=" + GlobalId(go);
            }
            if (obj is Component component)
            {
                return basic + " objectPath=" + ObjectPath(component.gameObject) + " component=" + component.GetType().Name + " globalId=" + GlobalId(component);
            }
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                return basic + " assetPath=" + assetPath + " guid=" + AssetDatabase.AssetPathToGUID(assetPath);
            }
            return basic + " instanceId=" + obj.GetInstanceID();
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        [Serializable] private sealed class EditorWindowCaptureMetadata
        {
            public int schemaVersion;
            public string target;
            public string status;
            public string message;
            public string requestedAt;
            public string capturedAt;
            public string captureApi;
            public string windowType;
            public string windowTitle;
            public int sourceWidthPoints;
            public int sourceHeightPoints;
            public float pixelsPerPoint;
            public int sourceWidthPixels;
            public int sourceHeightPixels;
            public bool capturedAtSourceResolution;
            public int maxLongEdgePixels;
            public int outputWidthPixels;
            public int outputHeightPixels;
            public bool downscaledAfterCapture;
            public bool focusChanged;
            public string previousWindowType;
            public bool focusRestored;
            public string imageFile;
            public string metadataFile;
        }
        [Serializable] private sealed class BridgeState { public string bridgeVersion; public bool enabled; public bool autoExport; public int autoExportIntervalSeconds; public bool watchCommands; public string lastExportedAt; public string unityVersion; public string projectName; public string projectRoot; public string planUnityPath; public bool playMode; public bool isCompiling; public bool isUpdating; }
        [Serializable] private sealed class ActiveSceneSnapshot { public string path; public string name; public bool isLoaded; public bool isDirty; public int rootCount; }
        [Serializable] private sealed class SceneSnapshot { public string path; public string name; public bool isLoaded; public bool isDirty; public bool isActive; }
        [Serializable] private sealed class OpenScenesSnapshot { public SceneSnapshot[] scenes; }
        [Serializable] private sealed class HierarchySnapshot { public string scene; public string exportedAt; public int maxExportedDepth; public int nodeCount; public HierarchyNode[] nodes; }
        [Serializable] private sealed class HierarchyNode { public string name; public string path; public string parentPath; public int depth; public bool activeSelf; public bool activeInHierarchy; public string tag; public string layer; public int instanceId; public string globalObjectId; public string prefabSource; public ComponentInfo[] components; public int childCount; public bool childrenTruncated; }
        [Serializable] private sealed class ComponentInfo { public string type; public string enabled; public bool missing; public string scriptGuid; }
        [Serializable] private sealed class SelectionSnapshot { public SelectedObjectInfo[] selectedObjects; }
        [Serializable] private sealed class SelectedObjectInfo { public string type; public string name; public string path; public int instanceId; public string globalObjectId; }
        [Serializable] private sealed class AssetIndex { public string exportedAt; public int listCap; public AssetCounts counts; public string[] scenes; public string[] prefabs; public string[] scriptableObjects; public bool scenesTruncated; public bool prefabsTruncated; public bool scriptableObjectsTruncated; public FolderCount[] folders; public string note; }
        [Serializable] private sealed class AssetCounts { public int scenes; public int prefabs; public int scriptableObjects; public int materials; public int scripts; public int textures; public int audioClips; public int animationClips; public int shaders; }
        [Serializable] private sealed class FolderCount { public string folder; public int assetCount; }
        [Serializable] private sealed class AssetQueryHit { public string path; public string type; public string guid; }
        [Serializable] private sealed class AssetQueryResult { public string filter; public int count; public int returned; public bool truncated; public string exportedAt; public AssetQueryHit[] results; }
        [Serializable] private sealed class ObjectReadResult { public string path; public bool found; public string exportedAt; public InspectorComponent[] components; }
        [Serializable] private sealed class AssetReadResult { public string path; public string type; public bool found; public string exportedAt; public InspectorComponent[] components; }
        [Serializable] private sealed class InspectorSnapshot { public string selectedObject; public InspectorComponent[] components; }
        [Serializable] private sealed class SelectedSubtreeInspectorSnapshot { public string selectedObject; public string exportedAt; public int maxExportedDepth; public int objectCount; public SelectedSubtreeObject[] objects; }
        [Serializable] private sealed class SelectedSubtreeObject { public string name; public string path; public string parentPath; public int depth; public bool activeSelf; public bool activeInHierarchy; public string tag; public string layer; public int instanceId; public string globalObjectId; public string prefabSource; public int childCount; public bool childrenTruncated; public InspectorComponent[] components; }
        [Serializable] private sealed class InspectorComponent { public string type; public string enabled; public string scriptGuid; public InspectorProperty[] properties; }
        [Serializable] private sealed class InspectorProperty { public string path; public string displayName; public string type; public string value; }
        [Serializable] private sealed class BuildSettingsSnapshot { public string activeBuildTarget; public string selectedBuildTargetGroup; public BuildSceneInfo[] scenesInBuild; }
        [Serializable] private sealed class BuildSceneInfo { public string path; public bool enabled; public int index; }
        [Serializable] private sealed class PackagesSnapshot { public string manifestPath; public string lockPath; public string manifestJson; public string lockJson; }
        [Serializable] private sealed class CompileStatusSnapshot { public string checkedAt; public bool isCompiling; public bool isUpdating; public bool hasCompileErrors; public int capturedErrorCount; public int capturedWarningCount; public CompileDiagnostic[] compileErrors; }
        [Serializable] private sealed class CompileDiagnostic { public string time; public string level; public string message; public string stackTrace; public string activeScene; }
        [Serializable] private sealed class CommandResult { public int schemaVersion; public string commandId; public string workflowId; public string sessionId; public bool wakeOnTerminal; public string status; public string message; public string processedAt; public string approvalProfile; public string approvalRule; public string grantId; public string requestHash; public string[] actionsApplied; public string[] objectsChanged; }
        [Serializable] private sealed class CommandResultLog { public string generatedAt; public int pendingCommandCount; public CommandResultEntry[] recentResults; }
        [Serializable] private sealed class CommandResultEntry { public string bucket; public string resultFile; public string commandFile; public string modifiedAt; public string commandId; public string workflowId; public string sessionId; public bool wakeOnTerminal; public string status; public string message; public string processedAt; public string approvalProfile; public string approvalRule; public string grantId; public string requestHash; public string[] actionsApplied; public string[] objectsChanged; }
        [Serializable] private sealed class ApprovalAuditEntry { public string decidedAt; public string commandId; public string commandFile; public string phase; public bool allowed; public string rule; public string reason; public string profile; public string grantId; public bool sessionBound; public string requestHash; public int actionCount; public string[] actions; }

        [Serializable]
        private sealed class Vector3Data
        {
            public float x;
            public float y;
            public float z;

            public Vector3 ToVector3()
            {
                return new Vector3(x, y, z);
            }
        }

        [Serializable]
        private sealed class BridgeCommand
        {
            public int schemaVersion;
            public string commandId;
            public string workflowId;
            public string grantId;
            public string sessionId;
            public bool wakeOnTerminal;
            public string title;
            public string summary;
            public string action;
            public string path;
            public string objectPath;
            public string @object;
            public string gameObject;
            public string target;
            public string parent;
            public string component;
            public string targetComponent;
            public string targetType;
            public string type;
            public string assetPath;
            public string folderPath;
            public string targetAssetPath;
            public string destinationPath;
            public string scenePath;
            public string setting;
            public string newName;
            public string buildTargetGroup;
            public string field;
            public string value;
            public string[] values;
            public string[] paths;
            public string[] scenes;
            public string childPath;
            public string shader;
            public string shaderAssetPath;
            public string shaderName;
            public string template;
            public string propertyName;
            public string valueType;
            public string textureAssetPath;
            public string menuPath;
            public string lightType;
            public string lightShadows;
            public float intensity;
            public float range;
            public float spotAngle;
            public float frameRate;
            public bool loopTime;
            public bool setColor;
            public bool setIntensity;
            public bool setPosition;
            public bool setRotationEuler;
            public bool setLocalPosition;
            public bool setLocalRotationEuler;
            public bool setLocalScale;
            public AlepouUnityBridgeAuthoring.ColorRequest color;
            public AlepouUnityBridgeAuthoring.Vector4Request vector;
            public AlepouUnityBridgeAuthoring.CurveRequest[] curves;
            public AlepouUnityBridgeAuthoring.ObjectEditRequest[] objectEdits;
            public bool allowDuplicate;
            public bool allowFolder;
            public Vector3Data position;
            public Vector3Data rotationEuler;
            public Vector3Data localPosition;
            public Vector3Data localRotationEuler;
            public Vector3Data localScale;
            public BridgeAction[] actions;

            public string titleOrId => string.IsNullOrWhiteSpace(title) ? commandId : title;

            public BridgeAction[] Actions()
            {
                if (actions != null && actions.Length > 0) return actions;
                if (string.IsNullOrWhiteSpace(action)) return Array.Empty<BridgeAction>();
                return new[]
                {
                    new BridgeAction
                    {
                        action = action,
                        path = path,
                        objectPath = objectPath,
                        @object = @object,
                        gameObject = gameObject,
                        target = target,
                        parent = parent,
                        component = component,
                        targetComponent = targetComponent,
                        targetType = targetType,
                        type = type,
                        assetPath = assetPath,
                        folderPath = folderPath,
                        targetAssetPath = targetAssetPath,
                        destinationPath = destinationPath,
                        scenePath = scenePath,
                        setting = setting,
                        newName = newName,
                        buildTargetGroup = buildTargetGroup,
                        field = field,
                        value = value,
                        values = values,
                        paths = paths,
                        scenes = scenes,
                        childPath = childPath,
                        shader = shader,
                        shaderAssetPath = shaderAssetPath,
                        shaderName = shaderName,
                        template = template,
                        propertyName = propertyName,
                        valueType = valueType,
                        textureAssetPath = textureAssetPath,
                        menuPath = menuPath,
                        lightType = lightType,
                        lightShadows = lightShadows,
                        intensity = intensity,
                        range = range,
                        spotAngle = spotAngle,
                        frameRate = frameRate,
                        loopTime = loopTime,
                        setColor = setColor,
                        setIntensity = setIntensity,
                        setPosition = setPosition,
                        setRotationEuler = setRotationEuler,
                        setLocalPosition = setLocalPosition,
                        setLocalRotationEuler = setLocalRotationEuler,
                        setLocalScale = setLocalScale,
                        color = color,
                        vector = vector,
                        curves = curves,
                        objectEdits = objectEdits,
                        allowDuplicate = allowDuplicate,
                        allowFolder = allowFolder,
                        position = position,
                        rotationEuler = rotationEuler,
                        localPosition = localPosition,
                        localRotationEuler = localRotationEuler,
                        localScale = localScale
                    }
                };
            }
        }

        [Serializable]
        private sealed class BridgeAction
        {
            public string action;
            public string path;
            public string objectPath;
            public string @object;
            public string gameObject;
            public string target;
            public string parent;
            public string component;
            public string targetComponent;
            public string targetType;
            public string type;
            public string assetPath;
            public string folderPath;
            public string targetAssetPath;
            public string destinationPath;
            public string scenePath;
            public string setting;
            public string newName;
            public string buildTargetGroup;
            public string field;
            public string value;
            public string[] values;
            public string[] paths;
            public string[] scenes;
            public string childPath;
            public string shader;
            public string shaderAssetPath;
            public string shaderName;
            public string template;
            public string propertyName;
            public string valueType;
            public string textureAssetPath;
            public string menuPath;
            public string lightType;
            public string lightShadows;
            public float intensity;
            public float range;
            public float spotAngle;
            public float frameRate;
            public bool loopTime;
            public bool setColor;
            public bool setIntensity;
            public bool setPosition;
            public bool setRotationEuler;
            public bool setLocalPosition;
            public bool setLocalRotationEuler;
            public bool setLocalScale;
            public AlepouUnityBridgeAuthoring.ColorRequest color;
            public AlepouUnityBridgeAuthoring.Vector4Request vector;
            public AlepouUnityBridgeAuthoring.CurveRequest[] curves;
            public AlepouUnityBridgeAuthoring.ObjectEditRequest[] objectEdits;
            public bool allowDuplicate;
            public bool allowFolder;
            public Vector3Data position;
            public Vector3Data rotationEuler;
            public Vector3Data localPosition;
            public Vector3Data localRotationEuler;
            public Vector3Data localScale;

            public string ActionName()
            {
                return (action ?? "").Trim().ToLowerInvariant();
            }

            public string TargetPath()
            {
                if (!string.IsNullOrWhiteSpace(objectPath)) return objectPath;
                if (!string.IsNullOrWhiteSpace(@object)) return @object;
                if (!string.IsNullOrWhiteSpace(gameObject)) return gameObject;
                if (!string.IsNullOrWhiteSpace(path)) return path;
                return target;
            }

            public string ObjectPathForAction()
            {
                if (!string.IsNullOrWhiteSpace(objectPath)) return objectPath;
                if (!string.IsNullOrWhiteSpace(@object)) return @object;
                if (!string.IsNullOrWhiteSpace(gameObject)) return gameObject;
                return path;
            }

            public string ReferenceTargetPath()
            {
                return target;
            }

            public string AssetPathForAction()
            {
                if (!string.IsNullOrWhiteSpace(assetPath)) return assetPath;
                if (!string.IsNullOrWhiteSpace(folderPath)) return folderPath;
                if (!string.IsNullOrWhiteSpace(path)) return path;
                return target;
            }

            public string TargetAssetPathForAction()
            {
                if (!string.IsNullOrWhiteSpace(targetAssetPath)) return targetAssetPath;
                if (!string.IsNullOrWhiteSpace(destinationPath)) return destinationPath;
                if (!string.IsNullOrWhiteSpace(target)) return target;
                return path;
            }

            public string[] PathListForAction()
            {
                if (paths != null) return paths;
                if (scenes != null) return scenes;
                return values;
            }
        }
    }
}
