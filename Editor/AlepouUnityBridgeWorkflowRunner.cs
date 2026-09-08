using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Alepou.UnityBridge
{
    internal static class AlepouUnityBridgeWorkflowRunner
    {
        private const int WorkflowSchemaVersion = 1;
        private const int DefaultWorkflowTimeoutSeconds = 1800;
        private const int DefaultStepTimeoutSeconds = 300;
        private const double IdleStableSeconds = 0.5;

        private static string outputPath;
        private static AlepouUnityBridgeWindow bridge;
        private static WorkflowManifest activeManifest;
        private static WorkflowState activeState;
        private static string activeManifestPath;
        private static double nextStatusWriteAt;

        internal static void Initialize(string bridgeOutputPath, AlepouUnityBridgeWindow bridgeWindow)
        {
            if (!string.Equals(outputPath, bridgeOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                activeManifest = null;
                activeState = null;
                activeManifestPath = null;
            }
            outputPath = bridgeOutputPath;
            bridge = bridgeWindow;
            EnsureFolders();
            LoadActiveWorkflow();
            WriteSummary(true);
        }

        internal static void Tick()
        {
            if (bridge == null || string.IsNullOrWhiteSpace(outputPath)) return;
            EnsureFolders();
            if (activeManifest == null || activeState == null) LoadActiveWorkflow();
            if (activeManifest == null || activeState == null) ClaimNextWorkflow();
            if (activeManifest != null && activeState != null) AdvanceActiveWorkflow();
            if (EditorApplication.timeSinceStartup >= nextStatusWriteAt)
            {
                nextStatusWriteAt = EditorApplication.timeSinceStartup + 2.0;
                WriteSummary(false);
            }
        }

        internal static WorkflowPublicSnapshot Snapshot()
        {
            EnsureFolders();
            return new WorkflowPublicSnapshot
            {
                schemaVersion = WorkflowSchemaVersion,
                active = activeState != null,
                workflowId = activeState == null ? null : activeState.workflowId,
                sessionId = activeState == null ? null : activeState.sessionId,
                title = activeManifest == null ? null : activeManifest.title,
                status = activeState == null ? "idle" : activeState.status,
                currentStepIndex = activeState == null ? -1 : activeState.currentStepIndex,
                currentStepId = activeState == null ? null : activeState.currentStepId,
                currentStepType = activeState == null ? null : activeState.currentStepType,
                stepPhase = activeState == null ? null : activeState.stepPhase,
                message = activeState == null ? null : activeState.message,
                childCommandId = activeState == null ? null : activeState.childCommandId,
                testRunId = activeState == null ? null : activeState.testRunId,
                testResultFile = activeState == null ? null : activeState.testResultFile,
                buildJobId = activeState == null ? null : activeState.buildJobId,
                buildResultFile = activeState == null ? null : activeState.buildResultFile,
                playModeOwned = activeState != null && activeState.playModeOwned,
                playModeStateChange = activeState == null ? null : activeState.lastPlayModeStateChange,
                runtimeCaptureId = activeState == null ? null : activeState.runtimeCaptureId,
                runtimeCaptureResultFile = activeState == null ? null : activeState.runtimeCaptureResultFile,
                waitingForHuman = activeState != null && string.Equals(activeState.status, "waiting-for-human", StringComparison.Ordinal),
                pendingCount = CountJson(Dir("pending")),
                cancelRequested = activeState != null && CancelRequested(activeState.workflowId),
                startedAt = activeState == null ? null : activeState.startedAt,
                stepStartedAt = activeState == null ? null : activeState.stepStartedAt,
                updatedAt = activeState == null ? null : activeState.updatedAt
            };
        }

        internal static string SuggestedSessionBinding()
        {
            EnsureFolders();
            if (activeState != null && !string.IsNullOrWhiteSpace(activeState.sessionId))
            {
                return activeState.sessionId;
            }

            var files = new List<string>();
            foreach (var bucket in new[] { "completed", "failed", "cancelled" })
            {
                var dir = Dir(bucket);
                if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*.result.json"));
            }
            foreach (var file in files.OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var result = JsonUtility.FromJson<WorkflowResult>(File.ReadAllText(file));
                    if (result != null && !string.IsNullOrWhiteSpace(result.sessionId)) return result.sessionId;
                }
                catch
                {
                    // Ignore malformed or concurrently moving evidence and keep looking.
                }
            }
            return null;
        }

        internal static bool ApproveCurrentGate()
        {
            if (activeState == null || !string.Equals(activeState.status, "waiting-for-human", StringComparison.Ordinal)) return false;
            activeState.gateDecision = "approved";
            RecordPlayModeDecision("approved");
            activeState.message = "Human gate approved in Unity.";
            SaveState();
            return true;
        }

        internal static bool RejectCurrentGate()
        {
            if (activeState == null || !string.Equals(activeState.status, "waiting-for-human", StringComparison.Ordinal)) return false;
            activeState.gateDecision = "rejected";
            RecordPlayModeDecision("rejected");
            activeState.message = "Human gate rejected in Unity.";
            SaveState();
            return true;
        }

        private static void RecordPlayModeDecision(string decision)
        {
            if (activeState == null) return;
            if (string.Equals(activeState.currentStepType, "enter_play_mode", StringComparison.Ordinal))
            {
                activeState.enterPlayModeDecision = decision;
                activeState.enterPlayModeDecisionAt = UtcNow();
            }
            else if (string.Equals(activeState.currentStepType, "exit_play_mode", StringComparison.Ordinal))
            {
                activeState.exitPlayModeDecision = decision;
                activeState.exitPlayModeDecisionAt = UtcNow();
            }
        }

        internal static bool CancelActive(string reason)
        {
            if (activeState == null) return false;
            if (RuntimeSessionNeedsStop())
            {
                EditorApplication.isPlaying = false;
            }
            if (string.Equals(activeState.currentStepType, "run_tests", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(activeState.testRunId))
            {
                activeState.testCancellationRequested = true;
                CancelActiveTestRun(reason);
                SetWaitingState(
                    "cancelling",
                    "cancelling-tests",
                    string.IsNullOrWhiteSpace(reason)
                        ? "Waiting for the Unity Test Framework cancellation result."
                        : reason);
                return true;
            }
            Finish("cancelled", string.IsNullOrWhiteSpace(reason) ? "Workflow cancelled." : reason);
            return true;
        }

        internal static void NotifyPlayModeStateChanged(PlayModeStateChange change)
        {
            if (activeState == null) return;
            activeState.lastPlayModeStateChange = change.ToString();
            activeState.lastPlayModeStateChangeAt = UtcNow();
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                activeState.enteredPlayModeAt = activeState.lastPlayModeStateChangeAt;
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                activeState.enteredEditModeAt = activeState.lastPlayModeStateChangeAt;
            }
            SaveState();
        }

        private static void LoadActiveWorkflow()
        {
            activeManifest = null;
            activeState = null;
            activeManifestPath = null;
            var stateFiles = Directory.GetFiles(Dir("running"), "*.state.json")
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToArray();
            if (stateFiles.Length == 0) return;

            var stateFile = stateFiles[0];
            try
            {
                var state = JsonUtility.FromJson<WorkflowState>(File.ReadAllText(stateFile));
                if (state == null || string.IsNullOrWhiteSpace(state.workflowId)) throw new InvalidOperationException("Running state is invalid.");
                var manifestPath = Path.Combine(Dir("running"), SafeId(state.workflowId) + ".manifest.json");
                var manifest = JsonUtility.FromJson<WorkflowManifest>(File.ReadAllText(manifestPath));
                ValidateManifest(manifest);
                activeState = state;
                activeManifest = manifest;
                activeManifestPath = manifestPath;
                if (string.IsNullOrWhiteSpace(activeState.sessionId)) activeState.sessionId = activeManifest.sessionId;
                if (activeManifest.wakeOnTerminal) activeState.wakeOnTerminal = true;
                activeState.updatedAt = UtcNow();
                activeState.message = "Workflow resumed after Unity reload.";
                SaveState();
            }
            catch (Exception ex)
            {
                FailOrphanedState(stateFile, ex.Message);
            }
        }

        private static void ClaimNextWorkflow()
        {
            var files = Directory.GetFiles(Dir("pending"), "*.json")
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToArray();
            if (files.Length == 0) return;

            var pendingFile = files[0];
            string raw = "";
            WorkflowManifest manifest = null;
            try
            {
                raw = File.ReadAllText(pendingFile);
                manifest = JsonUtility.FromJson<WorkflowManifest>(raw);
                ValidateManifest(manifest);
                if (HasFinishedWorkflow(manifest.workflowId)) throw new InvalidOperationException("Duplicate workflowId was not started: " + manifest.workflowId);

                var id = SafeId(manifest.workflowId);
                var runningManifest = Path.Combine(Dir("running"), id + ".manifest.json");
                if (File.Exists(runningManifest)) throw new InvalidOperationException("Workflow is already running: " + manifest.workflowId);
                File.Move(pendingFile, runningManifest);

                activeManifest = manifest;
                activeManifestPath = runningManifest;
                FreezeCommandInputs(manifest);
                var now = UtcNow();
                activeState = new WorkflowState
                {
                    schemaVersion = WorkflowSchemaVersion,
                    workflowId = manifest.workflowId,
                    sessionId = manifest.sessionId,
                    wakeOnTerminal = manifest.wakeOnTerminal,
                    manifestHash = Hash(raw),
                    status = "running",
                    currentStepIndex = 0,
                    startedAt = now,
                    updatedAt = now,
                    workflowDeadlineAt = DateTimeOffset.UtcNow
                        .AddSeconds(ClampTimeout(manifest.timeoutSeconds, DefaultWorkflowTimeoutSeconds, 30, 86400))
                        .ToString("o"),
                    message = "Workflow claimed."
                };
                BeginCurrentStep();
                WriteSummary(true);
            }
            catch (Exception ex)
            {
                FailPendingManifest(pendingFile, raw: raw, message: ex.Message);
                if (!string.IsNullOrWhiteSpace(activeManifestPath) && File.Exists(activeManifestPath))
                {
                    var failedManifest = Path.Combine(Dir("failed"), Path.GetFileName(activeManifestPath));
                    if (File.Exists(failedManifest)) failedManifest = Path.Combine(Dir("failed"), Path.GetFileNameWithoutExtension(activeManifestPath) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                    File.Move(activeManifestPath, failedManifest);
                }
                DeleteDirectoryIfExists(manifest == null ? null : FrozenInputsDir(manifest.workflowId));
                activeManifest = null;
                activeManifestPath = null;
                activeState = null;
            }
        }

        private static void FreezeCommandInputs(WorkflowManifest manifest)
        {
            var frozenDir = FrozenInputsDir(manifest.workflowId);
            Directory.CreateDirectory(frozenDir);
            for (var i = 0; i < manifest.steps.Length; i++)
            {
                var step = manifest.steps[i];
                if (!string.Equals(Normalize(step.type), "command", StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(step.commandFile)) throw new InvalidOperationException("Command step " + StepName(step, i) + " requires commandFile.");
                var source = ResolveWorkflowInput(step.commandFile);
                var raw = File.ReadAllText(source);
                var envelope = JsonUtility.FromJson<CommandEnvelope>(raw);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.commandId)) throw new InvalidOperationException("Command input has no commandId: " + step.commandFile);
                if (!string.IsNullOrWhiteSpace(step.commandId) && !string.Equals(step.commandId, envelope.commandId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Command step commandId does not match its frozen input.");
                }
                if (!string.IsNullOrWhiteSpace(manifest.grantId) && !string.Equals(manifest.grantId, envelope.grantId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Frozen child command does not match the workflow grantId.");
                }
                if (!string.IsNullOrWhiteSpace(manifest.sessionId) && !string.Equals(manifest.sessionId, envelope.sessionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Frozen child command does not match the workflow sessionId.");
                }
                if (FindCommandResult(envelope.commandId) != null || ChildCommandExists(envelope.commandId))
                {
                    throw new InvalidOperationException("Child commandId already exists and cannot be reused: " + envelope.commandId);
                }
                WriteAtomic(
                    Path.Combine(frozenDir, i.ToString("D3", CultureInfo.InvariantCulture) + ".command.json"),
                    BindCommandToWorkflow(raw, envelope, manifest.workflowId));
            }
        }

        private static string BindCommandToWorkflow(string raw, CommandEnvelope envelope, string workflowId)
        {
            if (!string.IsNullOrWhiteSpace(envelope.workflowId))
            {
                if (!string.Equals(envelope.workflowId, workflowId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Frozen child command workflowId does not match its workflow manifest.");
                }
                return raw;
            }
            var objectStart = (raw ?? "").IndexOf('{');
            if (objectStart < 0) throw new InvalidOperationException("Frozen child command is not a JSON object.");
            return raw.Insert(objectStart + 1, "\n  \"workflowId\": \"" + SafeId(workflowId) + "\",");
        }

        private static void AdvanceActiveWorkflow()
        {
            if (!string.IsNullOrWhiteSpace(activeState.pendingTerminalStatus))
            {
                AdvanceTerminalPlayModeExit();
                return;
            }
            if (activeState.testCancellationRequested || CancelRequested(activeState.workflowId))
            {
                if (AdvanceCancellation()) return;
                Finish("cancelled", "Workflow cancellation was requested.");
                return;
            }
            if (DeadlinePassed(activeState.workflowDeadlineAt))
            {
                CancelActiveTestRun("Workflow timeout expired.");
                Finish("failed", "Workflow timeout expired.");
                return;
            }
            if (activeManifest.steps == null || activeState.currentStepIndex >= activeManifest.steps.Length)
            {
                Finish("completed", "Workflow completed.");
                return;
            }
            if (DeadlinePassed(activeState.stepDeadlineAt))
            {
                CancelActiveTestRun("Workflow step timeout expired.");
                Finish("failed", "Step timeout expired: " + activeState.currentStepId);
                return;
            }

            var step = activeManifest.steps[activeState.currentStepIndex];
            var type = Normalize(step.type);
            switch (type)
            {
                case "checkpoint":
                    CompleteCurrentStep("Checkpoint reached.");
                    break;
                case "command":
                    AdvanceCommandStep(step);
                    break;
                case "wait_editor_idle":
                case "wait_compile_idle":
                    AdvanceIdleStep();
                    break;
                case "require_compile_clean":
                    AdvanceCompileCleanStep();
                    break;
                case "run_tests":
                    AdvanceRunTestsStep(step);
                    break;
                case "build_player":
                    AdvanceBuildPlayerStep(step);
                    break;
                case "enter_play_mode":
                    AdvanceEnterPlayModeStep();
                    break;
                case "runtime_capture":
                    AdvanceRuntimeCaptureStep(step);
                    break;
                case "exit_play_mode":
                    AdvanceExitPlayModeStep();
                    break;
                case "human_gate":
                    AdvanceHumanGate(step);
                    break;
                default:
                    Finish("failed", "Unsupported workflow step type: " + step.type);
                    break;
            }
        }

        private static void AdvanceCommandStep(WorkflowStep step)
        {
            var frozen = FrozenCommandPath(activeManifest.workflowId, activeState.currentStepIndex);
            var raw = File.ReadAllText(frozen);
            var envelope = JsonUtility.FromJson<CommandEnvelope>(raw);
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.commandId))
            {
                Finish("failed", "Frozen command input is invalid.");
                return;
            }
            activeState.childCommandId = envelope.commandId;
            var result = FindCommandResult(envelope.commandId);
            if (result != null)
            {
                activeState.childResultFile = result.relativePath;
                activeState.lastChildCommandId = envelope.commandId;
                activeState.lastChildResultFile = result.relativePath;
                if (string.Equals(result.status, "applied", StringComparison.OrdinalIgnoreCase))
                {
                    CompleteCurrentStep("Child command applied: " + envelope.commandId);
                }
                else
                {
                    Finish("failed", "Child command " + envelope.commandId + " ended as " + result.status + ": " + result.message);
                }
                return;
            }

            if (!ChildCommandExists(envelope.commandId))
            {
                activeState.stepPhase = "submitting-command";
                activeState.status = "running";
                activeState.message = "Submitting child command " + envelope.commandId + ".";
                SaveState();
                var target = Path.Combine(
                    outputPath,
                    "commands",
                    "pending",
                    SafeId(activeManifest.workflowId) + "-" +
                    activeState.currentStepIndex.ToString("D3", CultureInfo.InvariantCulture) + "-" +
                    SafeId(envelope.commandId) + ".json");
                if (!File.Exists(target)) WriteAtomic(target, raw);
            }
            SetWaitingState("waiting-command", "waiting-command-result", "Waiting for child command result: " + envelope.commandId);
        }

        private static void AdvanceIdleStep()
        {
            if (!ReachedStableIdle()) return;
            CompleteCurrentStep("Unity reached a stable idle barrier.");
        }

        private static void AdvanceCompileCleanStep()
        {
            if (!ReachedStableIdle()) return;
            if (bridge.CompileHasErrors())
            {
                Finish("failed", "Compile-clean barrier failed; Unity compiler errors are present.");
                return;
            }
            CompleteCurrentStep("Compile-clean barrier passed.");
        }

        private static void AdvanceRunTestsStep(WorkflowStep step)
        {
            if (string.IsNullOrWhiteSpace(activeState.testRunId))
            {
                activeState.testRunId = string.IsNullOrWhiteSpace(step.testRunId)
                    ? SafeId(activeState.workflowId) + "-" +
                      activeState.currentStepIndex.ToString("D3", CultureInfo.InvariantCulture) + "-" +
                      "tests"
                    : SafeId(step.testRunId);
                SaveState();
            }

            var testRun = AlepouUnityBridgeTestRunner.Snapshot(activeState.testRunId);
            if (string.Equals(testRun.status, "idle", StringComparison.Ordinal))
            {
                if (string.Equals(activeState.gateDecision, "rejected", StringComparison.Ordinal))
                {
                    Finish("cancelled", "Test run approval was rejected in Unity.");
                    return;
                }

                ApprovalDecision decision;
                if (string.Equals(activeState.gateDecision, "approved", StringComparison.Ordinal))
                {
                    decision = AlepouUnityBridgeApprovalPolicy.ManualApproval();
                }
                else
                {
                    decision = AlepouUnityBridgeApprovalPolicy.Evaluate(
                        activeManifest.grantId,
                        activeManifest.sessionId,
                        new[] { "run_tests" },
                        false);
                    if (!decision.allowed)
                    {
                        SetWaitingState(
                            "waiting-for-human",
                            "test-approval",
                            "Approve this Unity test run, enable session-bound Trusted Development, or enable a Custom Expert grant containing run_tests. " + decision.reason);
                        return;
                    }
                    if (string.Equals(decision.profile, AlepouUnityBridgeApprovalPolicy.TrustedDevelopmentProfile, StringComparison.Ordinal) &&
                        !HasFocusedTestFilter(step))
                    {
                        SetWaitingState(
                            "waiting-for-human",
                            "test-approval",
                            "Trusted Development only auto-approves filtered Unity test runs. Add an assembly, namespace, category, fixture, or exact test filter; otherwise approve this whole-suite run directly or use an exact Custom Expert grant.");
                        return;
                    }
                    decision = AlepouUnityBridgeApprovalPolicy.Evaluate(
                        activeManifest.grantId,
                        activeManifest.sessionId,
                        new[] { "run_tests" },
                        true);
                    if (!decision.allowed)
                    {
                        SetWaitingState("waiting-for-human", "test-approval", decision.reason);
                        return;
                    }
                }

                activeState.status = "running";
                activeState.stepPhase = "starting-tests";
                activeState.message = "Starting Unity Test Framework run " + activeState.testRunId + ".";
                SaveState();
                try
                {
                    testRun = AlepouUnityBridgeTestRunner.Start(
                        new TestRunRequest
                        {
                            testRunId = activeState.testRunId,
                            mode = step.mode,
                            assemblyNames = step.assemblyNames,
                            namespaceNames = step.namespaceNames,
                            categoryNames = step.categoryNames,
                            fixtureNames = step.fixtureNames,
                            testNames = step.testNames
                        },
                        decision.rule,
                        decision.grantId);
                }
                catch (Exception ex)
                {
                    Finish("failed", "Could not start test run: " + ex.Message);
                    return;
                }
            }

            activeState.testResultFile = testRun.resultFile;
            if (string.Equals(testRun.status, "completed", StringComparison.Ordinal))
            {
                activeState.lastTestRunId = activeState.testRunId;
                activeState.lastTestResultFile = testRun.resultFile;
                CompleteCurrentStep(
                    "Tests passed: " + testRun.passedCount.ToString(CultureInfo.InvariantCulture) +
                    " passed, " + testRun.skippedCount.ToString(CultureInfo.InvariantCulture) + " skipped.");
                return;
            }
            if (string.Equals(testRun.status, "failed", StringComparison.Ordinal))
            {
                Finish("failed", "Test barrier failed: " + testRun.message);
                return;
            }
            if (string.Equals(testRun.status, "cancelled", StringComparison.Ordinal))
            {
                Finish("cancelled", "Test barrier was cancelled: " + testRun.message);
                return;
            }

            SetWaitingState(
                "waiting-tests",
                string.Equals(testRun.status, "cancelling", StringComparison.Ordinal) ? "cancelling-tests" : "running-tests",
                string.IsNullOrWhiteSpace(testRun.message) ? "Waiting for Unity Test Framework." : testRun.message);
        }

        private static bool HasFocusedTestFilter(WorkflowStep step)
        {
            return new[]
            {
                step.assemblyNames,
                step.namespaceNames,
                step.categoryNames,
                step.fixtureNames,
                step.testNames
            }.Any(values => values != null && values.Any(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static void CancelActiveTestRun(string reason)
        {
            if (activeState == null || !string.Equals(activeState.currentStepType, "run_tests", StringComparison.Ordinal)) return;
            if (string.IsNullOrWhiteSpace(activeState.testRunId)) return;
            AlepouUnityBridgeTestRunner.Cancel(activeState.testRunId, reason);
        }

        private static void AdvanceBuildPlayerStep(WorkflowStep step)
        {
            if (string.IsNullOrWhiteSpace(activeState.buildJobId))
            {
                activeState.buildJobId = string.IsNullOrWhiteSpace(step.buildJobId)
                    ? SafeId(activeState.workflowId) + "-" +
                      activeState.currentStepIndex.ToString("D3", CultureInfo.InvariantCulture) + "-build"
                    : SafeId(step.buildJobId);
                SaveState();
            }

            var buildJob = AlepouUnityBridgeBuildRunner.Snapshot(activeState.buildJobId);
            if (string.Equals(buildJob.status, "idle", StringComparison.Ordinal))
            {
                if (!ReachedStableIdle()) return;
                if (bridge.CompileHasErrors())
                {
                    Finish("failed", "Build preflight failed; Unity compiler errors are present.");
                    return;
                }
                if (string.Equals(activeState.gateDecision, "rejected", StringComparison.Ordinal))
                {
                    Finish("cancelled", "Build approval was rejected in Unity.");
                    return;
                }

                var requiredTestRunId = string.IsNullOrWhiteSpace(step.requiredTestRunId)
                    ? step.requireTestsPassed ? activeState.lastTestRunId : null
                    : step.requiredTestRunId;
                if (step.requireTestsPassed && string.IsNullOrWhiteSpace(requiredTestRunId))
                {
                    Finish("failed", "Build requires a prior clean test result, but this workflow has no completed test run.");
                    return;
                }

                ApprovalDecision decision;
                if (string.Equals(activeState.gateDecision, "approved", StringComparison.Ordinal))
                {
                    decision = AlepouUnityBridgeApprovalPolicy.ManualApproval();
                }
                else
                {
                    decision = AlepouUnityBridgeApprovalPolicy.Evaluate(
                        activeManifest.grantId,
                        activeManifest.sessionId,
                        new[] { "build_player" },
                        false);
                    if (!decision.allowed)
                    {
                        SetWaitingState(
                            "waiting-for-human",
                            "build-approval",
                            "Approve this Unity build or enable a Custom Expert grant containing build_player. " + decision.reason);
                        return;
                    }
                    decision = AlepouUnityBridgeApprovalPolicy.Evaluate(
                        activeManifest.grantId,
                        activeManifest.sessionId,
                        new[] { "build_player" },
                        true);
                    if (!decision.allowed)
                    {
                        SetWaitingState("waiting-for-human", "build-approval", decision.reason);
                        return;
                    }
                }

                activeState.status = "running";
                activeState.stepPhase = "starting-build";
                activeState.message = "Starting Unity build job " + activeState.buildJobId + ".";
                SaveState();
                try
                {
                    buildJob = AlepouUnityBridgeBuildRunner.Start(
                        new BuildJobRequest
                        {
                            buildJobId = activeState.buildJobId,
                            target = step.target,
                            scenes = step.scenes,
                            options = step.options,
                            outputPath = step.outputPath,
                            requiredTestRunId = requiredTestRunId
                        },
                        decision.rule,
                        decision.grantId,
                        bridge.CompileHasErrors());
                }
                catch (Exception ex)
                {
                    Finish("failed", "Could not start build job: " + ex.Message);
                    return;
                }
            }

            activeState.buildResultFile = buildJob.resultFile;
            if (string.Equals(buildJob.status, "completed", StringComparison.Ordinal))
            {
                activeState.lastBuildJobId = activeState.buildJobId;
                activeState.lastBuildResultFile = buildJob.resultFile;
                activeState.lastArtifactManifestFile = buildJob.artifactManifestFile;
                if (DeadlinePassed(activeState.workflowDeadlineAt) || DeadlinePassed(activeState.stepDeadlineAt))
                {
                    Finish("failed", "Build completed, but its workflow or step deadline expired before the barrier returned.");
                    return;
                }
                CompleteCurrentStep(
                    "Build completed for " + buildJob.target + ". Artifact manifest: " +
                    buildJob.artifactManifestFile + ".");
                return;
            }
            if (string.Equals(buildJob.status, "failed", StringComparison.Ordinal))
            {
                Finish("failed", "Build barrier failed: " + buildJob.message);
                return;
            }
            if (string.Equals(buildJob.status, "cancelled", StringComparison.Ordinal))
            {
                Finish("cancelled", "Build barrier was cancelled: " + buildJob.message);
                return;
            }

            SetWaitingState(
                "waiting-build",
                string.IsNullOrWhiteSpace(buildJob.progressStage) ? "building" : buildJob.progressStage,
                string.IsNullOrWhiteSpace(buildJob.message) ? "Waiting for Unity BuildPipeline." : buildJob.message);
        }

        private static void AdvanceEnterPlayModeStep()
        {
            if (string.Equals(activeState.gateDecision, "rejected", StringComparison.Ordinal))
            {
                Finish("cancelled", "Enter Play Mode approval was rejected in Unity.");
                return;
            }
            if (!EnsurePlayModeAuthorization(
                    "enter_play_mode",
                    "enter-play-mode-approval",
                    "Enter Play Mode will execute this project's runtime code. Approve this exact step in the Unity Bridge window, or enable session-bound Trusted Development for this workflow."))
            {
                return;
            }

            if (!activeState.playModeRequestIssued)
            {
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    Finish("failed", "Unity was already entering or running Play Mode; this workflow will not claim ownership of an existing runtime session.");
                    return;
                }
                if (!ReachedStableIdle()) return;
                if (bridge.CompileHasErrors())
                {
                    Finish("failed", "Enter Play Mode preflight failed; Unity compiler errors are present.");
                    return;
                }

                activeState.playModeRequestIssued = true;
                activeState.playModeTransitionTarget = "playing";
                activeState.playModeRequestedAt = UtcNow();
                activeState.consoleDeltaSinceAt = activeState.playModeRequestedAt;
                activeState.lastPlayModeStateChange = null;
                activeState.lastPlayModeStateChangeAt = null;
                activeState.status = "waiting-play-mode";
                activeState.stepPhase = "requesting-enter-play-mode";
                activeState.message = "Enter Play Mode requested; waiting for Unity's EnteredPlayMode transition.";
                SaveState();
                EditorApplication.isPlaying = true;
                return;
            }

            if (EditorApplication.isPlaying &&
                EditorApplication.isPlayingOrWillChangePlaymode &&
                ObservedPlayModeChange("EnteredPlayMode"))
            {
                activeState.playModeOwned = true;
                activeState.playModeTransitionTarget = null;
                CompleteCurrentStep("Unity reported EnteredPlayMode; the workflow now owns this runtime session.");
                return;
            }
            if (!EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                ObservedPlayModeChange("EnteredEditMode"))
            {
                Finish("failed", "Unity returned to Edit Mode before the Enter Play Mode barrier completed.");
                return;
            }
            SetWaitingState(
                "waiting-play-mode",
                "waiting-entered-play-mode",
                "Waiting for Unity's EnteredPlayMode transition; command submission alone is not success.");
        }

        private static void AdvanceRuntimeCaptureStep(WorkflowStep step)
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Finish("failed", "runtime_capture requires Unity to be stably in Play Mode.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(activeState.runtimeCaptureResultFile))
            {
                CompleteCurrentStep("Runtime capture already completed: " + activeState.runtimeCaptureResultFile + ".");
                return;
            }

            activeState.runtimeCaptureId = string.IsNullOrWhiteSpace(step.captureId)
                ? SafeId(activeState.workflowId) + "-" +
                  activeState.currentStepIndex.ToString("D3", CultureInfo.InvariantCulture) + "-runtime"
                : SafeId(step.captureId);
            activeState.status = "running";
            activeState.stepPhase = "capturing-runtime";
            activeState.message = "Capturing bounded Game-view, console delta, and targeted runtime probes.";
            SaveState();

            RuntimeCaptureResult capture;
            try
            {
                capture = bridge.CaptureRuntime(new RuntimeCaptureRequest
                {
                    workflowId = activeState.workflowId,
                    captureId = activeState.runtimeCaptureId,
                    consoleSinceAt = activeState.consoleDeltaSinceAt ?? activeState.stepStartedAt,
                    maxConsoleEntries = step.maxConsoleEntries,
                    probes = step.probes
                });
            }
            catch (Exception ex)
            {
                Finish("failed", "Runtime capture could not be written: " + ex.Message);
                return;
            }

            activeState.runtimeCaptureResultFile = capture.resultFile;
            activeState.lastRuntimeCaptureId = capture.captureId;
            activeState.lastRuntimeCaptureResultFile = capture.resultFile;
            if (!string.Equals(capture.status, "completed", StringComparison.Ordinal))
            {
                Finish("failed", "Runtime capture failed: " + capture.message);
                return;
            }
            activeState.consoleDeltaSinceAt = capture.capturedAt;
            CompleteCurrentStep("Runtime capture completed: " + capture.resultFile + ".");
        }

        private static void AdvanceExitPlayModeStep()
        {
            if (string.Equals(activeState.gateDecision, "rejected", StringComparison.Ordinal))
            {
                Finish("cancelled", "Exit Play Mode approval was rejected in Unity.");
                return;
            }
            if (!EnsurePlayModeAuthorization(
                    "exit_play_mode",
                    "exit-play-mode-approval",
                    "Exit Play Mode discards transient runtime state. Approve this exact step in the Unity Bridge window, or enable session-bound Trusted Development for this workflow."))
            {
                return;
            }

            if (!activeState.playModeExitRequestIssued)
            {
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    activeState.playModeOwned = false;
                    CompleteCurrentStep("Unity was already stably in Edit Mode.");
                    return;
                }
                activeState.playModeExitRequestIssued = true;
                activeState.playModeTransitionTarget = "edit-mode";
                activeState.playModeRequestedAt = UtcNow();
                activeState.lastPlayModeStateChange = null;
                activeState.lastPlayModeStateChangeAt = null;
                activeState.status = "waiting-play-mode";
                activeState.stepPhase = "requesting-exit-play-mode";
                activeState.message = "Exit Play Mode requested; waiting for Unity's EnteredEditMode transition.";
                SaveState();
                EditorApplication.isPlaying = false;
                return;
            }

            if (!EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                ObservedPlayModeChange("EnteredEditMode"))
            {
                activeState.playModeOwned = false;
                activeState.playModeTransitionTarget = null;
                CompleteCurrentStep("Unity reported EnteredEditMode; transient runtime state has stopped.");
                return;
            }
            SetWaitingState(
                "waiting-play-mode",
                "waiting-entered-edit-mode",
                "Waiting for Unity's EnteredEditMode transition; exit submission alone is not success.");
        }

        private static bool EnsurePlayModeAuthorization(string action, string waitingPhase, string manualMessage)
        {
            if (string.Equals(activeState.gateDecision, "approved", StringComparison.Ordinal) ||
                string.Equals(activeState.gateDecision, "delegated", StringComparison.Ordinal))
            {
                return true;
            }

            var decision = AlepouUnityBridgeApprovalPolicy.Evaluate(
                activeManifest.grantId,
                activeManifest.sessionId,
                new[] { action },
                false);
            if (!decision.allowed)
            {
                SetWaitingState(
                    "waiting-for-human",
                    waitingPhase,
                    manualMessage + " " + decision.reason);
                return false;
            }

            decision = AlepouUnityBridgeApprovalPolicy.Evaluate(
                activeManifest.grantId,
                activeManifest.sessionId,
                new[] { action },
                true);
            if (!decision.allowed)
            {
                SetWaitingState("waiting-for-human", waitingPhase, manualMessage + " " + decision.reason);
                return false;
            }

            activeState.gateDecision = "delegated";
            RecordPlayModeDecision("delegated");
            activeState.message = action + " authorized by the active session-bound Trusted Development grant.";
            SaveState();
            return true;
        }

        private static bool ObservedPlayModeChange(string expected)
        {
            if (!string.Equals(activeState.lastPlayModeStateChange, expected, StringComparison.Ordinal)) return false;
            DateTimeOffset changedAt;
            DateTimeOffset requestedAt;
            return DateTimeOffset.TryParse(activeState.lastPlayModeStateChangeAt, out changedAt) &&
                   DateTimeOffset.TryParse(activeState.playModeRequestedAt, out requestedAt) &&
                   changedAt >= requestedAt;
        }

        private static void AdvanceTerminalPlayModeExit()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (!activeState.terminalExitRequestIssued)
                {
                    activeState.terminalExitRequestIssued = true;
                    activeState.stepPhase = "stopping-play-mode";
                    activeState.status = "cancelling";
                    activeState.message = "Stopping the workflow-owned Play Mode session before finalizing " +
                                          activeState.pendingTerminalStatus + ".";
                    SaveState();
                    EditorApplication.isPlaying = false;
                }
                return;
            }
            activeState.playModeOwned = false;
            var status = activeState.pendingTerminalStatus;
            var message = activeState.pendingTerminalMessage;
            activeState.pendingTerminalStatus = null;
            activeState.pendingTerminalMessage = null;
            Finish(status, message);
        }

        private static bool AdvanceCancellation()
        {
            if (activeState == null ||
                !string.Equals(activeState.currentStepType, "run_tests", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(activeState.testRunId))
            {
                return false;
            }

            var testRun = AlepouUnityBridgeTestRunner.Snapshot(activeState.testRunId);
            if (string.Equals(testRun.status, "idle", StringComparison.Ordinal)) return false;
            if (string.Equals(testRun.status, "completed", StringComparison.Ordinal) ||
                string.Equals(testRun.status, "failed", StringComparison.Ordinal) ||
                string.Equals(testRun.status, "cancelled", StringComparison.Ordinal))
            {
                activeState.testResultFile = testRun.resultFile;
                activeState.lastTestRunId = activeState.testRunId;
                activeState.lastTestResultFile = testRun.resultFile;
                Finish("cancelled", "Workflow cancellation was requested. Test run ended as " + testRun.status + ".");
                return true;
            }

            if (!activeState.testCancellationRequested)
            {
                activeState.testCancellationRequested = true;
                AlepouUnityBridgeTestRunner.Cancel(activeState.testRunId, "Workflow cancellation was requested.");
                SaveState();
            }
            SetWaitingState(
                "cancelling",
                "cancelling-tests",
                "Waiting for the Unity Test Framework cancellation result.");
            return true;
        }

        private static bool ReachedStableIdle()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                activeState.idleSinceAt = null;
                SetWaitingState(
                    "waiting-editor",
                    EditorApplication.isCompiling ? "waiting-compile" : "waiting-import",
                    EditorApplication.isCompiling ? "Waiting for Unity compilation." : "Waiting for Unity asset import.");
                return false;
            }
            DateTimeOffset idleSince;
            if (!DateTimeOffset.TryParse(activeState.idleSinceAt, out idleSince))
            {
                activeState.idleSinceAt = UtcNow();
                SetWaitingState("waiting-editor", "stabilizing-idle", "Unity is idle; waiting for a stable barrier.");
                return false;
            }
            return (DateTimeOffset.UtcNow - idleSince).TotalSeconds >= IdleStableSeconds;
        }

        private static void AdvanceHumanGate(WorkflowStep step)
        {
            if (string.Equals(activeState.gateDecision, "approved", StringComparison.Ordinal))
            {
                CompleteCurrentStep("Human gate approved.");
                return;
            }
            if (string.Equals(activeState.gateDecision, "rejected", StringComparison.Ordinal))
            {
                Finish("cancelled", "Human gate rejected.");
                return;
            }
            SetWaitingState(
                "waiting-for-human",
                "human-gate",
                string.IsNullOrWhiteSpace(step.message) ? "Workflow is waiting for a human gate." : step.message);
        }

        private static void BeginCurrentStep()
        {
            if (activeState.currentStepIndex >= activeManifest.steps.Length)
            {
                Finish("completed", "Workflow completed.");
                return;
            }
            var step = activeManifest.steps[activeState.currentStepIndex];
            var now = DateTimeOffset.UtcNow;
            activeState.currentStepId = StepName(step, activeState.currentStepIndex);
            activeState.currentStepType = Normalize(step.type);
            activeState.stepPhase = "starting";
            activeState.status = "running";
            activeState.stepStartedAt = now.ToString("o");
            activeState.stepDeadlineAt = now
                .AddSeconds(ClampTimeout(step.timeoutSeconds, DefaultStepTimeoutSeconds, 5, 86400))
                .ToString("o");
            activeState.idleSinceAt = null;
            activeState.childCommandId = null;
            activeState.childResultFile = null;
            activeState.testRunId = null;
            activeState.testResultFile = null;
            activeState.testCancellationRequested = false;
            activeState.buildJobId = null;
            activeState.buildResultFile = null;
            activeState.playModeRequestIssued = false;
            activeState.playModeExitRequestIssued = false;
            activeState.playModeTransitionTarget = null;
            activeState.playModeRequestedAt = null;
            activeState.runtimeCaptureId = null;
            activeState.runtimeCaptureResultFile = null;
            activeState.gateDecision = null;
            activeState.message = "Starting step " + activeState.currentStepId + ".";
            SaveState();
        }

        private static void CompleteCurrentStep(string message)
        {
            activeState.completedStepCount++;
            activeState.currentStepIndex++;
            activeState.message = message;
            SaveState();
            BeginCurrentStep();
        }

        private static void Finish(string status, string message)
        {
            if (RuntimeSessionNeedsStop())
            {
                if (string.IsNullOrWhiteSpace(activeState.pendingTerminalStatus))
                {
                    activeState.pendingTerminalStatus = status;
                    activeState.pendingTerminalMessage = message;
                    activeState.terminalExitRequestIssued = false;
                    SaveState();
                }
                AdvanceTerminalPlayModeExit();
                return;
            }
            var finishedAt = UtcNow();
            var result = new WorkflowResult
            {
                schemaVersion = WorkflowSchemaVersion,
                workflowId = activeState.workflowId,
                sessionId = activeState.sessionId,
                wakeOnTerminal = activeState.wakeOnTerminal,
                title = activeManifest == null ? null : activeManifest.title,
                status = status,
                message = message,
                manifestHash = activeState.manifestHash,
                startedAt = activeState.startedAt,
                finishedAt = finishedAt,
                completedStepCount = activeState.completedStepCount,
                totalStepCount = activeManifest == null || activeManifest.steps == null ? 0 : activeManifest.steps.Length,
                finalStepIndex = activeState.currentStepIndex,
                finalStepId = activeState.currentStepId,
                finalStepType = activeState.currentStepType,
                childCommandId = activeState.lastChildCommandId ?? activeState.childCommandId,
                childResultFile = activeState.lastChildResultFile ?? activeState.childResultFile,
                testRunId = activeState.lastTestRunId ?? activeState.testRunId,
                testResultFile = activeState.lastTestResultFile ?? activeState.testResultFile,
                buildJobId = activeState.lastBuildJobId ?? activeState.buildJobId,
                buildResultFile = activeState.lastBuildResultFile ?? activeState.buildResultFile,
                artifactManifestFile = activeState.lastArtifactManifestFile,
                runtimeCaptureId = activeState.lastRuntimeCaptureId ?? activeState.runtimeCaptureId,
                runtimeCaptureResultFile = activeState.lastRuntimeCaptureResultFile ?? activeState.runtimeCaptureResultFile,
                enterPlayModeDecision = activeState.enterPlayModeDecision,
                enterPlayModeDecisionAt = activeState.enterPlayModeDecisionAt,
                enteredPlayModeAt = activeState.enteredPlayModeAt,
                exitPlayModeDecision = activeState.exitPlayModeDecision,
                exitPlayModeDecisionAt = activeState.exitPlayModeDecisionAt,
                enteredEditModeAt = activeState.enteredEditModeAt,
                playModeStoppedBeforeFinalize =
                    (!string.IsNullOrWhiteSpace(activeState.enteredPlayModeAt) ||
                     activeState.playModeRequestIssued ||
                     activeState.playModeExitRequestIssued) &&
                    !EditorApplication.isPlaying &&
                    !EditorApplication.isPlayingOrWillChangePlaymode
            };
            var bucket = status == "completed" ? "completed" : status == "cancelled" ? "cancelled" : "failed";
            var id = SafeId(activeState.workflowId);
            WriteAtomic(Path.Combine(Dir(bucket), id + ".result.json"), JsonUtility.ToJson(result, true));
            if (!string.IsNullOrWhiteSpace(activeManifestPath) && File.Exists(activeManifestPath))
            {
                var destination = Path.Combine(Dir(bucket), id + ".manifest.json");
                if (File.Exists(destination)) destination = Path.Combine(Dir(bucket), id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".manifest.json");
                File.Move(activeManifestPath, destination);
            }
            var frozenInputs = FrozenInputsDir(activeState.workflowId);
            if (Directory.Exists(frozenInputs))
            {
                var frozenDestination = Path.Combine(Dir(bucket), id + ".inputs");
                if (Directory.Exists(frozenDestination)) frozenDestination = Path.Combine(Dir(bucket), id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".inputs");
                Directory.Move(frozenInputs, frozenDestination);
            }
            DeleteIfExists(StatePath(activeState.workflowId));
            DeleteIfExists(Path.Combine(Dir("cancel"), id + ".json"));
            activeManifest = null;
            activeState = null;
            activeManifestPath = null;
            WriteSummary(true);
        }

        private static bool RuntimeSessionNeedsStop()
        {
            if (activeState == null) return false;
            var workflowRequestedRuntime =
                activeState.playModeOwned ||
                activeState.playModeRequestIssued ||
                activeState.playModeExitRequestIssued ||
                string.Equals(activeState.playModeTransitionTarget, "playing", StringComparison.Ordinal);
            return workflowRequestedRuntime &&
                   (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode);
        }

        private static void SaveState()
        {
            if (activeState == null) return;
            activeState.updatedAt = UtcNow();
            WriteAtomic(StatePath(activeState.workflowId), JsonUtility.ToJson(activeState, true));
            WriteSummary(true);
        }

        private static void SetWaitingState(string status, string phase, string message)
        {
            if (string.Equals(activeState.status, status, StringComparison.Ordinal) &&
                string.Equals(activeState.stepPhase, phase, StringComparison.Ordinal) &&
                string.Equals(activeState.message, message, StringComparison.Ordinal))
            {
                return;
            }
            activeState.status = status;
            activeState.stepPhase = phase;
            activeState.message = message;
            SaveState();
        }

        private static void WriteSummary(bool force)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            var snapshot = Snapshot();
            WriteAtomic(Path.Combine(outputPath, "workflow-status.json"), JsonUtility.ToJson(snapshot, true));
        }

        private static void ValidateManifest(WorkflowManifest manifest)
        {
            if (manifest == null) throw new InvalidOperationException("Workflow manifest is empty.");
            if (manifest.schemaVersion != WorkflowSchemaVersion) throw new InvalidOperationException("Unsupported workflow schemaVersion.");
            if (string.IsNullOrWhiteSpace(manifest.workflowId)) throw new InvalidOperationException("workflowId is required.");
            if (manifest.wakeOnTerminal && string.IsNullOrWhiteSpace(manifest.sessionId))
            {
                throw new InvalidOperationException("wakeOnTerminal requires the owning Alepou sessionId.");
            }
            SafeId(manifest.workflowId);
            if (manifest.steps == null || manifest.steps.Length == 0) throw new InvalidOperationException("Workflow requires at least one step.");
            if (manifest.steps.Length > 100) throw new InvalidOperationException("Workflow exceeds the 100-step limit.");
            for (var i = 0; i < manifest.steps.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(manifest.steps[i].type)) throw new InvalidOperationException("Workflow step " + i + " has no type.");
            }
        }

        private static string ResolveWorkflowInput(string relative)
        {
            var root = Path.GetFullPath(Dir("inputs")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalized = (relative ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, normalized));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Workflow commandFile escapes workflows/inputs.");
            if (!File.Exists(full)) throw new FileNotFoundException("Workflow command input not found.", relative);
            return full;
        }

        private static CommandResultReference FindCommandResult(string commandId)
        {
            foreach (var bucket in new[] { "applied", "failed", "rejected" })
            {
                var dir = Path.Combine(outputPath, "commands", bucket);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.result.json"))
                {
                    try
                    {
                        var result = JsonUtility.FromJson<CommandResultEnvelope>(File.ReadAllText(file));
                        if (result != null && string.Equals(result.commandId, commandId, StringComparison.Ordinal))
                        {
                            return new CommandResultReference
                            {
                                status = result.status,
                                message = result.message,
                                relativePath = Relative(file)
                            };
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static bool ChildCommandExists(string commandId)
        {
            foreach (var bucket in new[] { "pending", "processing" })
            {
                var dir = Path.Combine(outputPath, "commands", bucket);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var envelope = JsonUtility.FromJson<CommandEnvelope>(File.ReadAllText(file));
                        if (envelope != null && string.Equals(envelope.commandId, commandId, StringComparison.Ordinal)) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        private static bool HasFinishedWorkflow(string workflowId)
        {
            var id = SafeId(workflowId);
            return new[] { "completed", "failed", "cancelled" }
                .Any(bucket => File.Exists(Path.Combine(Dir(bucket), id + ".result.json")));
        }

        private static bool CancelRequested(string workflowId)
        {
            return File.Exists(Path.Combine(Dir("cancel"), SafeId(workflowId) + ".json"));
        }

        private static void FailPendingManifest(string pendingFile, string raw, string message)
        {
            var id = Path.GetFileNameWithoutExtension(pendingFile);
            WorkflowManifest parsed = null;
            try { parsed = JsonUtility.FromJson<WorkflowManifest>(raw ?? ""); } catch { }
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.workflowId))
            {
                try { id = SafeId(parsed.workflowId); } catch { }
            }
            var result = new WorkflowResult
            {
                schemaVersion = WorkflowSchemaVersion,
                workflowId = parsed == null ? id : parsed.workflowId,
                sessionId = parsed == null ? null : parsed.sessionId,
                wakeOnTerminal = parsed != null && parsed.wakeOnTerminal,
                title = parsed == null ? null : parsed.title,
                status = "failed",
                message = message,
                finishedAt = UtcNow(),
                totalStepCount = parsed == null || parsed.steps == null ? 0 : parsed.steps.Length
            };
            WriteAtomic(Path.Combine(Dir("failed"), id + ".result.json"), JsonUtility.ToJson(result, true));
            if (File.Exists(pendingFile))
            {
                var destination = Path.Combine(Dir("failed"), id + ".manifest.json");
                if (File.Exists(destination)) destination = Path.Combine(Dir("failed"), id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".manifest.json");
                File.Move(pendingFile, destination);
            }
        }

        private static void FailOrphanedState(string stateFile, string message)
        {
            var id = Path.GetFileName(stateFile).Replace(".state.json", "");
            WorkflowState parsed = null;
            try { parsed = JsonUtility.FromJson<WorkflowState>(File.ReadAllText(stateFile)); } catch { }
            var result = new WorkflowResult
            {
                schemaVersion = WorkflowSchemaVersion,
                workflowId = parsed == null || string.IsNullOrWhiteSpace(parsed.workflowId) ? id : parsed.workflowId,
                sessionId = parsed == null ? null : parsed.sessionId,
                wakeOnTerminal = parsed != null && parsed.wakeOnTerminal,
                status = "failed",
                message = "Could not resume persisted workflow: " + message,
                finishedAt = UtcNow()
            };
            WriteAtomic(Path.Combine(Dir("failed"), id + "-recovery.result.json"), JsonUtility.ToJson(result, true));
            DeleteIfExists(stateFile);
        }

        private static void EnsureFolders()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            foreach (var name in new[] { "pending", "running", "inputs", "cancel", "completed", "failed", "cancelled", "archive" })
            {
                Directory.CreateDirectory(Dir(name));
            }
        }

        private static string Dir(string name)
        {
            return Path.Combine(outputPath ?? "", "workflows", name);
        }

        private static string FrozenInputsDir(string workflowId)
        {
            return Path.Combine(Dir("running"), SafeId(workflowId) + ".inputs");
        }

        private static string FrozenCommandPath(string workflowId, int stepIndex)
        {
            return Path.Combine(FrozenInputsDir(workflowId), stepIndex.ToString("D3", CultureInfo.InvariantCulture) + ".command.json");
        }

        private static string StatePath(string workflowId)
        {
            return Path.Combine(Dir("running"), SafeId(workflowId) + ".state.json");
        }

        private static string StepName(WorkflowStep step, int index)
        {
            return string.IsNullOrWhiteSpace(step.stepId) ? "step-" + (index + 1).ToString(CultureInfo.InvariantCulture) : step.stepId.Trim();
        }

        private static string SafeId(string value)
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > 120) throw new InvalidOperationException("Workflow/command id is invalid.");
            if (trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')))
            {
                throw new InvalidOperationException("Workflow/command id contains unsafe characters.");
            }
            return trimmed;
        }

        private static int ClampTimeout(int requested, int fallback, int min, int max)
        {
            var value = requested > 0 ? requested : fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private static bool DeadlinePassed(string raw)
        {
            DateTimeOffset deadline;
            return DateTimeOffset.TryParse(raw, out deadline) && DateTimeOffset.UtcNow >= deadline;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        }

        private static string Hash(string raw)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw ?? ""))).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string Relative(string file)
        {
            var root = Path.GetFullPath(outputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).Replace("\\", "/")
                : full.Replace("\\", "/");
        }

        private static string TryRead(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file) : ""; }
            catch { return ""; }
        }

        private static int CountJson(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0; }
            catch { return 0; }
        }

        private static void DeleteIfExists(string file)
        {
            if (File.Exists(file)) File.Delete(file);
        }

        private static void DeleteDirectoryIfExists(string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString("o");
        }

        private static void WriteAtomic(string file, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, contents ?? "", new UTF8Encoding(false));
            if (!File.Exists(file))
            {
                File.Move(temp, file);
                return;
            }
            try { File.Replace(temp, file, null); }
            catch
            {
                File.Delete(file);
                File.Move(temp, file);
            }
        }

        [Serializable]
        private sealed class WorkflowManifest
        {
            public int schemaVersion;
            public string workflowId;
            public string title;
            public string summary;
            public string grantId;
            public string sessionId;
            public bool wakeOnTerminal;
            public int timeoutSeconds;
            public WorkflowStep[] steps;
        }

        [Serializable]
        private sealed class WorkflowStep
        {
            public string stepId;
            public string type;
            public string title;
            public string message;
            public int timeoutSeconds;
            public string commandFile;
            public string commandId;
            public string testRunId;
            public string mode;
            public string[] assemblyNames;
            public string[] namespaceNames;
            public string[] categoryNames;
            public string[] fixtureNames;
            public string[] testNames;
            public string buildJobId;
            public string target;
            public string[] scenes;
            public string[] options;
            public string outputPath;
            public string requiredTestRunId;
            public bool requireTestsPassed;
            public string captureId;
            public int maxConsoleEntries;
            public RuntimeProbeRequest[] probes;
        }

        [Serializable]
        private sealed class WorkflowState
        {
            public int schemaVersion;
            public string workflowId;
            public string sessionId;
            public bool wakeOnTerminal;
            public string manifestHash;
            public string status;
            public int currentStepIndex;
            public int completedStepCount;
            public string currentStepId;
            public string currentStepType;
            public string stepPhase;
            public string message;
            public string startedAt;
            public string updatedAt;
            public string workflowDeadlineAt;
            public string stepStartedAt;
            public string stepDeadlineAt;
            public string idleSinceAt;
            public string childCommandId;
            public string childResultFile;
            public string lastChildCommandId;
            public string lastChildResultFile;
            public string testRunId;
            public string testResultFile;
            public string lastTestRunId;
            public string lastTestResultFile;
            public bool testCancellationRequested;
            public string buildJobId;
            public string buildResultFile;
            public string lastBuildJobId;
            public string lastBuildResultFile;
            public string lastArtifactManifestFile;
            public bool playModeOwned;
            public bool playModeRequestIssued;
            public bool playModeExitRequestIssued;
            public string playModeTransitionTarget;
            public string playModeRequestedAt;
            public string lastPlayModeStateChange;
            public string lastPlayModeStateChangeAt;
            public string consoleDeltaSinceAt;
            public string runtimeCaptureId;
            public string runtimeCaptureResultFile;
            public string lastRuntimeCaptureId;
            public string lastRuntimeCaptureResultFile;
            public string enterPlayModeDecision;
            public string enterPlayModeDecisionAt;
            public string enteredPlayModeAt;
            public string exitPlayModeDecision;
            public string exitPlayModeDecisionAt;
            public string enteredEditModeAt;
            public string pendingTerminalStatus;
            public string pendingTerminalMessage;
            public bool terminalExitRequestIssued;
            public string gateDecision;
        }

        [Serializable]
        private sealed class WorkflowResult
        {
            public int schemaVersion;
            public string workflowId;
            public string sessionId;
            public bool wakeOnTerminal;
            public string title;
            public string status;
            public string message;
            public string manifestHash;
            public string startedAt;
            public string finishedAt;
            public int completedStepCount;
            public int totalStepCount;
            public int finalStepIndex;
            public string finalStepId;
            public string finalStepType;
            public string childCommandId;
            public string childResultFile;
            public string testRunId;
            public string testResultFile;
            public string buildJobId;
            public string buildResultFile;
            public string artifactManifestFile;
            public string runtimeCaptureId;
            public string runtimeCaptureResultFile;
            public string enterPlayModeDecision;
            public string enterPlayModeDecisionAt;
            public string enteredPlayModeAt;
            public string exitPlayModeDecision;
            public string exitPlayModeDecisionAt;
            public string enteredEditModeAt;
            public bool playModeStoppedBeforeFinalize;
        }

        [Serializable]
        private sealed class CommandEnvelope
        {
            public string commandId;
            public string workflowId;
            public string grantId;
            public string sessionId;
        }

        [Serializable]
        private sealed class CommandResultEnvelope
        {
            public string commandId;
            public string status;
            public string message;
        }

        private sealed class CommandResultReference
        {
            public string status;
            public string message;
            public string relativePath;
        }
    }

    [Serializable]
    internal sealed class WorkflowPublicSnapshot
    {
        public int schemaVersion;
        public bool active;
        public string workflowId;
        public string sessionId;
        public string title;
        public string status;
        public int currentStepIndex;
        public string currentStepId;
        public string currentStepType;
        public string stepPhase;
        public string message;
        public string childCommandId;
        public string testRunId;
        public string testResultFile;
        public string buildJobId;
        public string buildResultFile;
        public bool playModeOwned;
        public string playModeStateChange;
        public string runtimeCaptureId;
        public string runtimeCaptureResultFile;
        public bool waitingForHuman;
        public int pendingCount;
        public bool cancelRequested;
        public string startedAt;
        public string stepStartedAt;
        public string updatedAt;
    }
}
