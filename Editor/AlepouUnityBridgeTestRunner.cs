using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Alepou.UnityBridge
{
    internal static class AlepouUnityBridgeTestRunner
    {
        private const int SchemaVersion = 1;
        private const int MaxFilterValues = 50;
        private const int MaxFilterLength = 256;
        private const int MaxFailureSummaries = 50;
        private const int MaxMessageLength = 2000;
        private const int MaxStackLength = 4000;
        private const int MaxOutputLength = 4000;
        private const double CancellationCallbackGraceSeconds = 5.0;
        private const double SceneRestoreRetrySeconds = 0.25;
        private const int MaxSceneRestoreAttempts = 3;

        private static string outputPath;
        private static TestRunnerApi api;
        private static TestCallbacks callbacks;
        private static bool callbacksRegistered;
        private static TestRunState activeState;

        internal static void Initialize(string bridgeOutputPath)
        {
            if (!string.Equals(outputPath, bridgeOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = bridgeOutputPath;
                activeState = null;
            }
            EnsureFolders();
            EnsureCallbacks();
            LoadRunningState();
            TryRestorePendingScenes();
            WriteSummary();
        }

        internal static void Tick()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            EnsureFolders();
            EnsureCallbacks();
            if (activeState == null) LoadRunningState();
            TryRestorePendingScenes();
            if (activeState != null &&
                activeState.cancelRequested &&
                string.Equals(activeState.status, "cancelling", StringComparison.Ordinal) &&
                SecondsSince(activeState.cancelRequestedAt) >= CancellationCallbackGraceSeconds)
            {
                CompleteWithoutResult(
                    "cancelled",
                    "Cancellation was accepted, but Unity Test Framework did not emit a terminal callback within " +
                    CancellationCallbackGraceSeconds.ToString("0", CultureInfo.InvariantCulture) +
                    " seconds. Framework cleanup may still be settling.");
            }
        }

        internal static TestRunPublicSnapshot Start(TestRunRequest rawRequest, string approvalRule, string approvalGrantId)
        {
            EnsureReady();
            if (activeState != null && !IsTerminal(activeState.status))
            {
                if (string.Equals(activeState.testRunId, rawRequest == null ? null : rawRequest.testRunId, StringComparison.Ordinal))
                {
                    return ToSnapshot(activeState, RunningStatePath(activeState.testRunId));
                }
                throw new InvalidOperationException("Another Alepou test run is already active: " + activeState.testRunId);
            }

            var request = ValidateAndNormalize(rawRequest);
            if (FindResultPath(request.testRunId) != null)
            {
                throw new InvalidOperationException("Duplicate testRunId was not started: " + request.testRunId);
            }

            var sceneContext = PrepareActiveSceneForTestRun();

            var now = UtcNow();
            activeState = new TestRunState
            {
                schemaVersion = SchemaVersion,
                testRunId = request.testRunId,
                status = "queued",
                message = "Unity Test Framework run queued.",
                mode = request.mode,
                assemblyNames = request.assemblyNames,
                namespaceNames = request.namespaceNames,
                categoryNames = request.categoryNames,
                fixtureNames = request.fixtureNames,
                testNames = request.testNames,
                approvalRule = approvalRule,
                approvalGrantId = approvalGrantId,
                originalScenePath = sceneContext.path,
                originalSceneSavedBeforeRun = sceneContext.savedBeforeRun,
                createdAt = now,
                updatedAt = now,
                failureSummaries = new List<TestFailureSummary>()
            };
            SaveState();

            try
            {
                var filter = new Filter
                {
                    testMode = ParseMode(request.mode),
                    assemblyNames = request.assemblyNames,
                    categoryNames = request.categoryNames.Select(ExactRegex).ToArray(),
                    groupNames = BuildGroupFilter(request.namespaceNames, request.fixtureNames),
                    testNames = request.testNames
                };
                var settings = new ExecutionSettings(filter)
                {
                    runSynchronously = false
                };
                activeState.runGuid = api.Execute(settings);
                activeState.status = "running";
                activeState.startedAt = UtcNow();
                activeState.message = "Unity Test Framework run started.";
                SaveState();
                return ToSnapshot(activeState, RunningStatePath(activeState.testRunId));
            }
            catch (Exception ex)
            {
                CompleteWithoutResult("failed", "Could not start Unity Test Framework: " + ex.Message);
                return Snapshot(request.testRunId);
            }
        }

        internal static TestRunPublicSnapshot Snapshot(string testRunId = null)
        {
            EnsureFolders();
            if (activeState != null &&
                (string.IsNullOrWhiteSpace(testRunId) || string.Equals(activeState.testRunId, testRunId, StringComparison.Ordinal)))
            {
                return ToSnapshot(activeState, RunningStatePath(activeState.testRunId));
            }

            if (!string.IsNullOrWhiteSpace(testRunId))
            {
                var pendingRestore = ReadPendingSceneRestore(testRunId);
                if (pendingRestore != null)
                {
                    return new TestRunPublicSnapshot
                    {
                        active = true,
                        testRunId = pendingRestore.testRunId,
                        status = "restoring-scene",
                        message = "Unity tests finished; restoring the original active scene before the workflow continues.",
                        originalScenePath = pendingRestore.scenePath,
                        sceneRestoreStatus = "pending",
                        sceneRestoreFile = Relative(SceneRestoreResultPath(pendingRestore.testRunId)),
                        updatedAt = pendingRestore.updatedAt
                    };
                }

                var restoreResult = ReadSceneRestoreResult(testRunId);
                if (restoreResult != null && string.Equals(restoreResult.status, "failed", StringComparison.Ordinal))
                {
                    return new TestRunPublicSnapshot
                    {
                        active = false,
                        testRunId = restoreResult.testRunId,
                        status = "failed",
                        message = restoreResult.message,
                        originalScenePath = restoreResult.scenePath,
                        sceneRestoreStatus = restoreResult.status,
                        sceneRestoreFile = Relative(SceneRestoreResultPath(restoreResult.testRunId)),
                        updatedAt = restoreResult.finishedAt
                    };
                }

                var resultPath = FindResultPath(testRunId);
                if (!string.IsNullOrWhiteSpace(resultPath))
                {
                    try
                    {
                        var result = JsonUtility.FromJson<TestRunResult>(File.ReadAllText(resultPath));
                        return ToSnapshot(result, resultPath);
                    }
                    catch { }
                }
            }

            return new TestRunPublicSnapshot
            {
                active = false,
                status = "idle"
            };
        }

        internal static bool Cancel(string testRunId, string reason)
        {
            EnsureFolders();
            if (activeState == null || IsTerminal(activeState.status)) return false;
            if (!string.IsNullOrWhiteSpace(testRunId) &&
                !string.Equals(activeState.testRunId, testRunId, StringComparison.Ordinal))
            {
                return false;
            }

            activeState.cancelRequested = true;
            activeState.cancelRequestedAt = UtcNow();
            activeState.status = "cancelling";
            activeState.message = string.IsNullOrWhiteSpace(reason) ? "Test cancellation requested." : reason;
            SaveState();
            if (!string.IsNullOrWhiteSpace(activeState.runGuid) && TryCancelTestRun(activeState.runGuid))
            {
                return true;
            }

            CompleteWithoutResult("cancelled", activeState.message + " The Unity test job was no longer active.");
            return true;
        }

        private static void EnsureReady()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("Unity Bridge test runner is not initialized.");
            EnsureFolders();
            EnsureCallbacks();
            LoadRunningState();
        }

        private static void EnsureCallbacks()
        {
            if (callbacksRegistered) return;
            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            callbacks = new TestCallbacks();
            api.RegisterCallbacks(callbacks, 100);
            callbacksRegistered = true;
        }

        private static void LoadRunningState()
        {
            if (activeState != null || string.IsNullOrWhiteSpace(outputPath)) return;
            var files = Directory.GetFiles(Dir("running"), "*.state.json")
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToArray();
            if (files.Length == 0) return;
            try
            {
                activeState = JsonUtility.FromJson<TestRunState>(File.ReadAllText(files[0]));
                if (activeState == null || string.IsNullOrWhiteSpace(activeState.testRunId))
                {
                    throw new InvalidOperationException("Persisted test-run state is invalid.");
                }
                if (activeState.failureSummaries == null) activeState.failureSummaries = new List<TestFailureSummary>();
                activeState.updatedAt = UtcNow();
                activeState.message = "Test run resumed after Unity reload.";
                SaveState();
            }
            catch (Exception ex)
            {
                var orphan = files[0];
                var id = Path.GetFileName(orphan).Replace(".state.json", "");
                var failed = new TestRunResult
                {
                    schemaVersion = SchemaVersion,
                    testRunId = id,
                    status = "failed",
                    message = "Could not resume persisted test run: " + ex.Message,
                    finishedAt = UtcNow()
                };
                WriteAtomic(Path.Combine(Dir("failed"), SafeId(id) + ".result.json"), JsonUtility.ToJson(failed, true));
                File.Delete(orphan);
                activeState = null;
                WriteSummary();
            }
        }

        private static TestRunRequest ValidateAndNormalize(TestRunRequest request)
        {
            if (request == null) throw new InvalidOperationException("run_tests requires a request.");
            var normalized = new TestRunRequest
            {
                testRunId = SafeId(request.testRunId),
                mode = NormalizeMode(request.mode),
                assemblyNames = NormalizeValues(request.assemblyNames, "assemblyNames"),
                namespaceNames = NormalizeValues(request.namespaceNames, "namespaceNames"),
                categoryNames = NormalizeValues(request.categoryNames, "categoryNames"),
                fixtureNames = NormalizeValues(request.fixtureNames, "fixtureNames"),
                testNames = NormalizeValues(request.testNames, "testNames")
            };
            return normalized;
        }

        private static TestSceneContext PrepareActiveSceneForTestRun()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return new TestSceneContext();
            var scenePath = scene.path ?? "";
            if (!scene.isDirty)
            {
                return new TestSceneContext { path = scenePath, savedBeforeRun = false };
            }
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                throw new InvalidOperationException(
                    "run_tests cannot start while the active scene is dirty and untitled. Save the scene to an asset path first; the Bridge will then save it automatically before future test runs.");
            }
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException(
                    "run_tests could not save the dirty active scene before launching Unity Test Framework: " + scenePath);
            }
            return new TestSceneContext { path = scenePath, savedBeforeRun = true };
        }

        private static string[] NormalizeValues(string[] values, string field)
        {
            var normalized = (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalized.Length > MaxFilterValues) throw new InvalidOperationException(field + " exceeds the " + MaxFilterValues + "-value limit.");
            if (normalized.Any(value => value.Length > MaxFilterLength || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0))
            {
                throw new InvalidOperationException(field + " contains an invalid value.");
            }
            return normalized;
        }

        private static string NormalizeMode(string mode)
        {
            if (string.Equals(mode, "EditMode", StringComparison.OrdinalIgnoreCase)) return "EditMode";
            if (string.Equals(mode, "PlayMode", StringComparison.OrdinalIgnoreCase)) return "PlayMode";
            throw new InvalidOperationException("run_tests mode must be EditMode or PlayMode.");
        }

        private static TestMode ParseMode(string mode)
        {
            return string.Equals(mode, "PlayMode", StringComparison.Ordinal) ? TestMode.PlayMode : TestMode.EditMode;
        }

        private static string ExactRegex(string value)
        {
            return "^" + Regex.Escape(value) + "$";
        }

        private static string[] BuildGroupFilter(string[] namespaceNames, string[] fixtureNames)
        {
            var namespaces = namespaceNames ?? Array.Empty<string>();
            var fixtures = fixtureNames ?? Array.Empty<string>();
            if (namespaces.Length == 0 && fixtures.Length == 0) return Array.Empty<string>();

            var builder = new StringBuilder("^");
            if (namespaces.Length > 0)
            {
                builder.Append("(?=(?:");
                builder.Append(string.Join("|", namespaces.Select(Regex.Escape).ToArray()));
                builder.Append(")(?:\\.|$))");
            }
            if (fixtures.Length > 0)
            {
                builder.Append("(?=(?:");
                builder.Append(string.Join("|", fixtures.Select(Regex.Escape).ToArray()));
                builder.Append(")(?:\\.|$))");
            }
            builder.Append(".*");
            return new[] { builder.ToString() };
        }

        private static void OnRunStarted(ITestAdaptor testsToRun)
        {
            if (activeState == null) return;
            activeState.status = "running";
            activeState.discoveredCount = testsToRun == null ? 0 : testsToRun.TestCaseCount;
            if (string.IsNullOrWhiteSpace(activeState.startedAt)) activeState.startedAt = UtcNow();
            activeState.message = "Loaded a test tree with " +
                                  activeState.discoveredCount.ToString(CultureInfo.InvariantCulture) +
                                  " case(s); executing the filtered selection.";
            SaveState();
        }

        private static void OnTestStarted(ITestAdaptor test)
        {
            if (activeState == null || test == null || test.IsSuite) return;
            activeState.currentTestName = Truncate(test.FullName, MaxFilterLength * 2);
            activeState.message = "Running test: " + activeState.currentTestName;
            SaveState();
        }

        private static void OnTestFinished(ITestResultAdaptor result)
        {
            if (activeState == null || result == null || result.Test == null || result.Test.IsSuite) return;
            activeState.executedCount++;
            activeState.currentTestName = null;
            var status = result.TestStatus.ToString();
            if (string.Equals(status, "Passed", StringComparison.OrdinalIgnoreCase)) activeState.passedCount++;
            else if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)) activeState.failedCount++;
            else if (string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase)) activeState.skippedCount++;
            else activeState.inconclusiveCount++;

            var evidence = new TestCaseEvidence
            {
                fullName = Truncate(result.FullName, MaxFilterLength * 2),
                status = result.ResultState,
                durationSeconds = result.Duration,
                message = Truncate(result.Message, MaxMessageLength),
                stackTrace = Truncate(result.StackTrace, MaxStackLength),
                output = Truncate(result.Output, MaxOutputLength)
            };
            AppendJsonLine(FullLogPath(activeState.testRunId), JsonUtility.ToJson(evidence));
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                activeState.failureSummaries.Count < MaxFailureSummaries)
            {
                activeState.failureSummaries.Add(new TestFailureSummary
                {
                    fullName = evidence.fullName,
                    resultState = evidence.status,
                    message = evidence.message,
                    stackTrace = evidence.stackTrace
                });
            }
            activeState.message = "Completed " + activeState.executedCount.ToString(CultureInfo.InvariantCulture) +
                                  " selected test(s).";
            SaveState();
        }

        private static void OnRunFinished(ITestResultAdaptor result)
        {
            if (activeState == null) return;
            var xmlPath = XmlLogPath(activeState.testRunId);
            try
            {
                if (result != null) SaveNUnitXml(result, xmlPath);
            }
            catch { }

            if (result != null)
            {
                activeState.passedCount = result.PassCount;
                activeState.failedCount = result.FailCount;
                activeState.skippedCount = result.SkipCount;
                activeState.inconclusiveCount = result.InconclusiveCount;
                activeState.durationSeconds = result.Duration;
            }
            var total = activeState.passedCount + activeState.failedCount + activeState.skippedCount + activeState.inconclusiveCount;
            activeState.totalCount = total;
            var cancelled = activeState.cancelRequested ||
                            (result != null && result.ResultState != null &&
                             result.ResultState.IndexOf("Cancelled", StringComparison.OrdinalIgnoreCase) >= 0);
            activeState.cancellationConfirmed = result != null && result.ResultState != null &&
                                                result.ResultState.IndexOf("Cancelled", StringComparison.OrdinalIgnoreCase) >= 0;
            var status = cancelled ? "cancelled" : activeState.failedCount > 0 || total == 0 ? "failed" : "completed";
            var message = cancelled
                ? activeState.cancellationConfirmed
                    ? "Unity Test Framework confirmed that the test run was cancelled."
                    : "Unity test run finished after a cancellation request."
                : total == 0
                    ? "Unity Test Framework matched no tests; refusing a false-green result."
                    : activeState.failedCount > 0
                        ? activeState.failedCount.ToString(CultureInfo.InvariantCulture) + " test(s) failed."
                        : activeState.passedCount.ToString(CultureInfo.InvariantCulture) + " test(s) passed.";
            Complete(status, message, xmlPath);
        }

        private static void CompleteWithoutResult(string status, string message)
        {
            Complete(status, message, null);
        }

        private static void Complete(string status, string message, string xmlPath)
        {
            if (activeState == null) return;
            var sceneRestoreFile = QueueSceneRestore(activeState);
            var result = new TestRunResult
            {
                schemaVersion = SchemaVersion,
                testRunId = activeState.testRunId,
                runGuid = activeState.runGuid,
                status = status,
                message = message,
                mode = activeState.mode,
                assemblyNames = activeState.assemblyNames,
                namespaceNames = activeState.namespaceNames,
                categoryNames = activeState.categoryNames,
                fixtureNames = activeState.fixtureNames,
                testNames = activeState.testNames,
                approvalRule = activeState.approvalRule,
                approvalGrantId = activeState.approvalGrantId,
                originalScenePath = activeState.originalScenePath,
                originalSceneSavedBeforeRun = activeState.originalSceneSavedBeforeRun,
                sceneRestoreFile = sceneRestoreFile,
                discoveredCount = activeState.discoveredCount,
                totalCount = activeState.totalCount,
                executedCount = activeState.executedCount,
                passedCount = activeState.passedCount,
                failedCount = activeState.failedCount,
                skippedCount = activeState.skippedCount,
                inconclusiveCount = activeState.inconclusiveCount,
                durationSeconds = activeState.durationSeconds,
                cancellationConfirmed = activeState.cancellationConfirmed,
                failureSummaries = activeState.failureSummaries == null
                    ? Array.Empty<TestFailureSummary>()
                    : activeState.failureSummaries.ToArray(),
                fullLogFile = Relative(FullLogPath(activeState.testRunId)),
                nunitXmlFile = string.IsNullOrWhiteSpace(xmlPath) ? null : Relative(xmlPath),
                startedAt = activeState.startedAt,
                finishedAt = UtcNow()
            };
            var bucket = status == "completed" ? "completed" : status == "cancelled" ? "cancelled" : "failed";
            var resultPath = Path.Combine(Dir(bucket), SafeId(activeState.testRunId) + ".result.json");
            WriteAtomic(resultPath, JsonUtility.ToJson(result, true));
            DeleteIfExists(RunningStatePath(activeState.testRunId));
            activeState = null;
            WriteSummary(Snapshot(result.testRunId));
        }

        private static string QueueSceneRestore(TestRunState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.originalScenePath)) return null;
            var pending = new SceneRestoreState
            {
                schemaVersion = SchemaVersion,
                testRunId = state.testRunId,
                scenePath = state.originalScenePath,
                status = "pending",
                message = "Waiting to restore the original active scene after Unity Test Framework cleanup.",
                attemptCount = 0,
                requestedAt = UtcNow(),
                updatedAt = UtcNow()
            };
            DeleteIfExists(SceneRestoreResultPath(state.testRunId));
            WriteAtomic(SceneRestoreStatePath(state.testRunId), JsonUtility.ToJson(pending, true));
            EditorApplication.delayCall -= TryRestorePendingScenes;
            EditorApplication.delayCall += TryRestorePendingScenes;
            return Relative(SceneRestoreResultPath(state.testRunId));
        }

        private static void TryRestorePendingScenes()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            EnsureFolders();
            foreach (var file in Directory.GetFiles(Dir("scene-restores"), "*.state.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                SceneRestoreState pending;
                try { pending = JsonUtility.FromJson<SceneRestoreState>(File.ReadAllText(file)); }
                catch { pending = null; }
                if (pending == null || string.IsNullOrWhiteSpace(pending.testRunId) || string.IsNullOrWhiteSpace(pending.scenePath))
                {
                    DeleteIfExists(file);
                    continue;
                }
                var lastAttempt = string.IsNullOrWhiteSpace(pending.lastAttemptAt)
                    ? pending.requestedAt
                    : pending.lastAttemptAt;
                if (SecondsSince(lastAttempt) < SceneRestoreRetrySeconds) continue;

                try
                {
                    var activeScene = SceneManager.GetActiveScene();
                    var alreadyActive = activeScene.IsValid() &&
                                        string.Equals(activeScene.path, pending.scenePath, StringComparison.OrdinalIgnoreCase);
                    if (!alreadyActive)
                    {
                        var restored = EditorSceneManager.OpenScene(pending.scenePath, OpenSceneMode.Single);
                        if (!restored.IsValid() ||
                            !string.Equals(restored.path, pending.scenePath, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Unity did not activate the requested scene.");
                        }
                    }
                    FinishSceneRestore(
                        pending,
                        "restored",
                        alreadyActive
                            ? "The original scene was already active after the test run."
                            : "Restored the original active scene after the test run.");
                }
                catch (Exception ex)
                {
                    pending.attemptCount++;
                    pending.lastAttemptAt = UtcNow();
                    pending.updatedAt = pending.lastAttemptAt;
                    pending.message = "Scene restore attempt " + pending.attemptCount.ToString(CultureInfo.InvariantCulture) +
                                      " failed: " + ex.Message;
                    if (pending.attemptCount >= MaxSceneRestoreAttempts)
                    {
                        FinishSceneRestore(
                            pending,
                            "failed",
                            "Unity tests finished, but the Bridge could not restore the original scene after " +
                            MaxSceneRestoreAttempts.ToString(CultureInfo.InvariantCulture) + " attempts: " + ex.Message);
                    }
                    else
                    {
                        WriteAtomic(file, JsonUtility.ToJson(pending, true));
                        EditorApplication.delayCall -= TryRestorePendingScenes;
                        EditorApplication.delayCall += TryRestorePendingScenes;
                    }
                }
            }
        }

        private static void FinishSceneRestore(SceneRestoreState pending, string status, string message)
        {
            var evidence = new SceneRestoreResult
            {
                schemaVersion = SchemaVersion,
                testRunId = pending.testRunId,
                scenePath = pending.scenePath,
                status = status,
                message = message,
                attemptCount = pending.attemptCount,
                requestedAt = pending.requestedAt,
                finishedAt = UtcNow()
            };
            WriteAtomic(SceneRestoreResultPath(pending.testRunId), JsonUtility.ToJson(evidence, true));
            DeleteIfExists(SceneRestoreStatePath(pending.testRunId));
            WriteSummary(Snapshot(pending.testRunId));
        }

        private static SceneRestoreState ReadPendingSceneRestore(string testRunId)
        {
            var file = SceneRestoreStatePath(testRunId);
            if (!File.Exists(file)) return null;
            try { return JsonUtility.FromJson<SceneRestoreState>(File.ReadAllText(file)); }
            catch { return null; }
        }

        private static SceneRestoreResult ReadSceneRestoreResult(string testRunId)
        {
            var file = SceneRestoreResultPath(testRunId);
            if (!File.Exists(file)) return null;
            try { return JsonUtility.FromJson<SceneRestoreResult>(File.ReadAllText(file)); }
            catch { return null; }
        }

        private static void SaveState()
        {
            if (activeState == null) return;
            activeState.updatedAt = UtcNow();
            WriteAtomic(RunningStatePath(activeState.testRunId), JsonUtility.ToJson(activeState, true));
            WriteSummary(ToSnapshot(activeState, RunningStatePath(activeState.testRunId)));
        }

        private static void WriteSummary(TestRunPublicSnapshot snapshot = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            WriteAtomic(
                Path.Combine(outputPath, "test-run-status.json"),
                JsonUtility.ToJson(snapshot ?? Snapshot(), true));
        }

        private static TestRunPublicSnapshot ToSnapshot(TestRunState state, string statePath)
        {
            return new TestRunPublicSnapshot
            {
                active = !IsTerminal(state.status),
                testRunId = state.testRunId,
                status = state.status,
                message = state.message,
                mode = state.mode,
                currentTestName = state.currentTestName,
                discoveredCount = state.discoveredCount,
                totalCount = state.totalCount,
                executedCount = state.executedCount,
                passedCount = state.passedCount,
                failedCount = state.failedCount,
                skippedCount = state.skippedCount,
                inconclusiveCount = state.inconclusiveCount,
                cancelRequested = state.cancelRequested,
                cancellationConfirmed = state.cancellationConfirmed,
                resultFile = Relative(statePath),
                originalScenePath = state.originalScenePath,
                originalSceneSavedBeforeRun = state.originalSceneSavedBeforeRun,
                sceneRestoreStatus = string.IsNullOrWhiteSpace(state.originalScenePath) ? "not-required" : "waiting-for-tests",
                sceneRestoreFile = string.IsNullOrWhiteSpace(state.originalScenePath)
                    ? null
                    : Relative(SceneRestoreResultPath(state.testRunId)),
                startedAt = state.startedAt,
                updatedAt = state.updatedAt
            };
        }

        private static TestRunPublicSnapshot ToSnapshot(TestRunResult result, string resultPath)
        {
            return new TestRunPublicSnapshot
            {
                active = false,
                testRunId = result.testRunId,
                status = result.status,
                message = result.message,
                mode = result.mode,
                discoveredCount = result.discoveredCount,
                totalCount = result.totalCount,
                executedCount = result.executedCount,
                passedCount = result.passedCount,
                failedCount = result.failedCount,
                skippedCount = result.skippedCount,
                inconclusiveCount = result.inconclusiveCount,
                cancellationConfirmed = result.cancellationConfirmed,
                resultFile = Relative(resultPath),
                originalScenePath = result.originalScenePath,
                originalSceneSavedBeforeRun = result.originalSceneSavedBeforeRun,
                sceneRestoreStatus = string.IsNullOrWhiteSpace(result.sceneRestoreFile) ? "not-required" : "restored",
                sceneRestoreFile = result.sceneRestoreFile,
                startedAt = result.startedAt,
                updatedAt = result.finishedAt
            };
        }

        private static string FindResultPath(string testRunId)
        {
            var id = SafeId(testRunId);
            foreach (var bucket in new[] { "completed", "failed", "cancelled" })
            {
                var path = Path.Combine(Dir(bucket), id + ".result.json");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static bool IsTerminal(string status)
        {
            return string.Equals(status, "completed", StringComparison.Ordinal) ||
                   string.Equals(status, "failed", StringComparison.Ordinal) ||
                   string.Equals(status, "cancelled", StringComparison.Ordinal);
        }

        private static double SecondsSince(string raw)
        {
            DateTimeOffset value;
            return DateTimeOffset.TryParse(raw, out value)
                ? Math.Max(0, (DateTimeOffset.UtcNow - value).TotalSeconds)
                : double.PositiveInfinity;
        }

        private static bool TryCancelTestRun(string runGuid)
        {
            try
            {
                // CancelTestRun is internal in Test Framework 1.1.33 and public in
                // newer versions. Reflection preserves Unity 2021.3 compatibility.
                var method = typeof(TestRunnerApi).GetMethod(
                    "CancelTestRun",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string) },
                    null);
                if (method == null) return false;
                var result = method.Invoke(null, new object[] { runGuid });
                return result is bool && (bool)result;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveNUnitXml(ITestResultAdaptor result, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var document = new XmlDocument();
            var declaration = document.CreateXmlDeclaration("1.0", "utf-8", null);
            document.AppendChild(declaration);
            var run = document.CreateElement("test-run");
            var total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
            run.SetAttribute("id", "2");
            run.SetAttribute("testcasecount", total.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("result", result.ResultState ?? "");
            run.SetAttribute("total", total.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("passed", result.PassCount.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("failed", result.FailCount.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("inconclusive", result.InconclusiveCount.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("skipped", result.SkipCount.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("asserts", result.AssertCount.ToString(CultureInfo.InvariantCulture));
            run.SetAttribute("start-time", result.StartTime.ToUniversalTime().ToString("u"));
            run.SetAttribute("end-time", result.EndTime.ToUniversalTime().ToString("u"));
            run.SetAttribute("duration", result.Duration.ToString(CultureInfo.InvariantCulture));
            var resultDocument = new XmlDocument();
            resultDocument.LoadXml(result.ToXml().OuterXml);
            run.AppendChild(document.ImportNode(resultDocument.DocumentElement, true));
            document.AppendChild(run);
            using (var writer = XmlWriter.Create(file, new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false)
            }))
            {
                document.Save(writer);
            }
        }

        private static void EnsureFolders()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            foreach (var name in new[] { "running", "completed", "failed", "cancelled", "logs", "scene-restores" })
            {
                Directory.CreateDirectory(Dir(name));
            }
        }

        private static string Dir(string name)
        {
            return Path.Combine(outputPath ?? "", "test-runs", name);
        }

        private static string RunningStatePath(string testRunId)
        {
            return Path.Combine(Dir("running"), SafeId(testRunId) + ".state.json");
        }

        private static string FullLogPath(string testRunId)
        {
            return Path.Combine(Dir("logs"), SafeId(testRunId) + ".jsonl");
        }

        private static string XmlLogPath(string testRunId)
        {
            return Path.Combine(Dir("logs"), SafeId(testRunId) + ".xml");
        }

        private static string SceneRestoreStatePath(string testRunId)
        {
            return Path.Combine(Dir("scene-restores"), SafeId(testRunId) + ".state.json");
        }

        private static string SceneRestoreResultPath(string testRunId)
        {
            return Path.Combine(Dir("scene-restores"), SafeId(testRunId) + ".result.json");
        }

        private static string SafeId(string value)
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > 120) throw new InvalidOperationException("testRunId is invalid.");
            if (trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')))
            {
                throw new InvalidOperationException("testRunId contains unsafe characters.");
            }
            return trimmed;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length <= max ? value : value.Substring(0, max) + "\n...[truncated]";
        }

        private static string Relative(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(outputPath)) return null;
            var root = Path.GetFullPath(outputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).Replace("\\", "/")
                : full.Replace("\\", "/");
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString("o");
        }

        private static void AppendJsonLine(string file, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.AppendAllText(file, json + "\n", new UTF8Encoding(false));
        }

        private static void DeleteIfExists(string file)
        {
            if (File.Exists(file)) File.Delete(file);
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

        private sealed class TestCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { OnRunStarted(testsToRun); }
            public void RunFinished(ITestResultAdaptor result) { OnRunFinished(result); }
            public void TestStarted(ITestAdaptor test) { OnTestStarted(test); }
            public void TestFinished(ITestResultAdaptor result) { OnTestFinished(result); }
        }

        [Serializable]
        private sealed class TestRunState
        {
            public int schemaVersion;
            public string testRunId;
            public string runGuid;
            public string status;
            public string message;
            public string mode;
            public string[] assemblyNames;
            public string[] namespaceNames;
            public string[] categoryNames;
            public string[] fixtureNames;
            public string[] testNames;
            public string approvalRule;
            public string approvalGrantId;
            public string originalScenePath;
            public bool originalSceneSavedBeforeRun;
            public int discoveredCount;
            public int totalCount;
            public int executedCount;
            public int passedCount;
            public int failedCount;
            public int skippedCount;
            public int inconclusiveCount;
            public double durationSeconds;
            public string currentTestName;
            public bool cancelRequested;
            public string cancelRequestedAt;
            public bool cancellationConfirmed;
            public List<TestFailureSummary> failureSummaries;
            public string createdAt;
            public string startedAt;
            public string updatedAt;
        }

        [Serializable]
        private sealed class TestRunResult
        {
            public int schemaVersion;
            public string testRunId;
            public string runGuid;
            public string status;
            public string message;
            public string mode;
            public string[] assemblyNames;
            public string[] namespaceNames;
            public string[] categoryNames;
            public string[] fixtureNames;
            public string[] testNames;
            public string approvalRule;
            public string approvalGrantId;
            public string originalScenePath;
            public bool originalSceneSavedBeforeRun;
            public string sceneRestoreFile;
            public int discoveredCount;
            public int totalCount;
            public int executedCount;
            public int passedCount;
            public int failedCount;
            public int skippedCount;
            public int inconclusiveCount;
            public double durationSeconds;
            public bool cancellationConfirmed;
            public TestFailureSummary[] failureSummaries;
            public string fullLogFile;
            public string nunitXmlFile;
            public string startedAt;
            public string finishedAt;
        }

        [Serializable]
        private sealed class TestSceneContext
        {
            public string path;
            public bool savedBeforeRun;
        }

        [Serializable]
        private sealed class SceneRestoreState
        {
            public int schemaVersion;
            public string testRunId;
            public string scenePath;
            public string status;
            public string message;
            public int attemptCount;
            public string requestedAt;
            public string lastAttemptAt;
            public string updatedAt;
        }

        [Serializable]
        private sealed class SceneRestoreResult
        {
            public int schemaVersion;
            public string testRunId;
            public string scenePath;
            public string status;
            public string message;
            public int attemptCount;
            public string requestedAt;
            public string finishedAt;
        }

        [Serializable]
        private sealed class TestCaseEvidence
        {
            public string fullName;
            public string status;
            public double durationSeconds;
            public string message;
            public string stackTrace;
            public string output;
        }
    }

    [Serializable]
    internal sealed class TestRunRequest
    {
        public string testRunId;
        public string mode;
        public string[] assemblyNames;
        public string[] namespaceNames;
        public string[] categoryNames;
        public string[] fixtureNames;
        public string[] testNames;
    }

    [Serializable]
    internal sealed class TestRunPublicSnapshot
    {
        public bool active;
        public string testRunId;
        public string status;
        public string message;
        public string mode;
        public string currentTestName;
        public int discoveredCount;
        public int totalCount;
        public int executedCount;
        public int passedCount;
        public int failedCount;
        public int skippedCount;
        public int inconclusiveCount;
        public bool cancelRequested;
        public bool cancellationConfirmed;
        public string resultFile;
        public string originalScenePath;
        public bool originalSceneSavedBeforeRun;
        public string sceneRestoreStatus;
        public string sceneRestoreFile;
        public string startedAt;
        public string updatedAt;
    }

    [Serializable]
    internal sealed class TestFailureSummary
    {
        public string fullName;
        public string resultState;
        public string message;
        public string stackTrace;
    }
}
