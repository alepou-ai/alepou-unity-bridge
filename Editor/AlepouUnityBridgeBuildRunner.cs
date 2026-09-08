using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Alepou.UnityBridge
{
    internal static class AlepouUnityBridgeBuildRunner
    {
        private const int SchemaVersion = 1;
        private const int ArtifactSchemaVersion = 1;
        private const int MaxScenes = 256;
        private const int MaxOptions = 32;
        private const int MaxSummaryMessages = 50;
        private const int MaxMessageLength = 2000;
        private const int MaxLogMessageLength = 8000;

        private static readonly HashSet<string> AllowedBuildOptions = new HashSet<string>(
            new[]
            {
                "None",
                "Development",
                "AllowDebugging",
                "ConnectWithProfiler",
                "EnableDeepProfilingSupport",
                "CompressWithLz4",
                "CompressWithLz4HC",
                "StrictMode",
                "DetailedBuildReport",
                "CleanBuildCache",
                "NoUniqueIdentifier"
            },
            StringComparer.OrdinalIgnoreCase);

        private static string outputPath;
        private static BuildJobState activeState;

        internal static void Initialize(string bridgeOutputPath)
        {
            if (!string.Equals(outputPath, bridgeOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = bridgeOutputPath;
                activeState = null;
            }
            EnsureFolders();
            RecoverInterruptedJobs();
            WriteSummary();
        }

        internal static BuildJobPublicSnapshot Start(
            BuildJobRequest rawRequest,
            string approvalRule,
            string approvalGrantId,
            bool compileHasErrors)
        {
            EnsureReady();
            if (activeState != null && !IsTerminal(activeState.status))
            {
                if (string.Equals(activeState.buildJobId, rawRequest == null ? null : rawRequest.buildJobId, StringComparison.Ordinal))
                {
                    return ToSnapshot(activeState, RunningStatePath(activeState.buildJobId));
                }
                throw new InvalidOperationException("Another Alepou build job is already active: " + activeState.buildJobId);
            }

            var request = ValidateAndNormalize(rawRequest);
            if (FindResultPath(request.buildJobId) != null)
            {
                throw new InvalidOperationException("Duplicate buildJobId was not started: " + request.buildJobId);
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity must be import/compile idle before a build starts.");
            }
            if (compileHasErrors)
            {
                throw new InvalidOperationException("Unity compiler errors are present; the build was not started.");
            }
            ValidateRequiredTest(request.requiredTestRunId);

            var now = UtcNow();
            activeState = new BuildJobState
            {
                schemaVersion = SchemaVersion,
                buildJobId = request.buildJobId,
                status = "queued",
                message = "Unity build job queued.",
                target = request.target.ToString(),
                targetGroup = request.targetGroup.ToString(),
                scenes = request.scenes,
                options = request.optionNames,
                outputProjectPath = request.outputProjectPath,
                outputAbsolutePath = request.outputAbsolutePath,
                artifactKind = request.artifactKind,
                requiredTestRunId = request.requiredTestRunId,
                approvalRule = approvalRule,
                approvalGrantId = approvalGrantId,
                progressStage = "queued",
                progressPercent = 0.0,
                createdAt = now,
                updatedAt = now,
                warnings = new List<BuildMessageSummary>(),
                errors = new List<BuildMessageSummary>()
            };
            SaveState();

            try
            {
                var artifactRoot = ArtifactAbsolutePath(
                    request.outputAbsolutePath,
                    request.artifactKind);
                if (File.Exists(artifactRoot) || Directory.Exists(artifactRoot))
                {
                    throw new InvalidOperationException(
                        "Build output already exists. Use a new buildJobId or a new Builds/Alepou output path.");
                }
                Directory.CreateDirectory(
                    string.Equals(request.artifactKind, "directory", StringComparison.Ordinal)
                        ? request.outputAbsolutePath
                        : Path.GetDirectoryName(request.outputAbsolutePath));

                activeState.status = "running";
                activeState.message = "Unity BuildPipeline is running.";
                activeState.progressStage = "building";
                activeState.progressPercent = 0.1;
                activeState.startedAt = UtcNow();
                SaveState();

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = request.scenes,
                    locationPathName = request.outputAbsolutePath,
                    target = request.target,
                    options = request.options
                });
                return CompleteFromReport(report);
            }
            catch (Exception ex)
            {
                if (activeState == null) throw;
                activeState.errors.Add(new BuildMessageSummary
                {
                    type = "Exception",
                    message = Truncate(ex.Message, MaxMessageLength)
                });
                return Complete(
                    "failed",
                    "Unity build failed before a report was available: " + Truncate(ex.Message, MaxMessageLength),
                    null,
                    null);
            }
        }

        internal static BuildJobPublicSnapshot Snapshot(string buildJobId = null)
        {
            EnsureFolders();
            if (activeState != null &&
                (string.IsNullOrWhiteSpace(buildJobId) || string.Equals(activeState.buildJobId, buildJobId, StringComparison.Ordinal)))
            {
                return ToSnapshot(activeState, RunningStatePath(activeState.buildJobId));
            }

            if (!string.IsNullOrWhiteSpace(buildJobId))
            {
                var resultPath = FindResultPath(buildJobId);
                if (!string.IsNullOrWhiteSpace(resultPath))
                {
                    try
                    {
                        var result = JsonUtility.FromJson<BuildJobResult>(File.ReadAllText(resultPath));
                        return ToSnapshot(result, resultPath);
                    }
                    catch { }
                }
            }

            return new BuildJobPublicSnapshot
            {
                active = false,
                status = "idle",
                progressStage = "idle"
            };
        }

        private static BuildJobPublicSnapshot CompleteFromReport(BuildReport report)
        {
            if (activeState == null) return Snapshot();
            if (report == null)
            {
                return Complete("failed", "Unity BuildPipeline returned no build report.", null, null);
            }

            WriteBuildLog(report, FullLogPath(activeState.buildJobId));
            CollectReportMessages(report);
            activeState.durationSeconds = report.summary.totalTime.TotalSeconds;
            activeState.warningCount = Convert.ToInt32(report.summary.totalWarnings);
            activeState.errorCount = Convert.ToInt32(report.summary.totalErrors);
            activeState.reportedOutputPath = NormalizeReportedOutput(report.summary.outputPath);

            var resultName = report.summary.result.ToString();
            if (string.Equals(resultName, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return Complete("cancelled", "Unity BuildPipeline reported a cancelled build.", report, null);
            }
            if (!string.Equals(resultName, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return Complete(
                    "failed",
                    "Unity BuildPipeline reported " + resultName + " with " +
                    activeState.errorCount.ToString(CultureInfo.InvariantCulture) + " error(s).",
                    report,
                    null);
            }

            var artifact = CreateArtifactManifest();
            return Complete(
                "completed",
                "Build completed with " + activeState.warningCount.ToString(CultureInfo.InvariantCulture) +
                " warning(s). Artifact SHA-256: " + artifact.sha256 + ".",
                report,
                artifact);
        }

        private static BuildJobPublicSnapshot Complete(
            string status,
            string message,
            BuildReport report,
            BuildArtifactManifest artifact)
        {
            if (activeState == null) return Snapshot();
            var artifactManifestPath = artifact == null ? null : ArtifactManifestPath(activeState.buildJobId);
            if (artifact != null)
            {
                WriteAtomic(artifactManifestPath, JsonUtility.ToJson(artifact, true));
            }

            activeState.status = status;
            activeState.message = message;
            activeState.progressStage = status;
            activeState.progressPercent = IsTerminal(status) ? 1.0 : activeState.progressPercent;
            activeState.artifactManifestFile = Relative(artifactManifestPath);
            activeState.updatedAt = UtcNow();
            if (report != null && activeState.durationSeconds <= 0)
            {
                activeState.durationSeconds = report.summary.totalTime.TotalSeconds;
            }

            var result = new BuildJobResult
            {
                schemaVersion = SchemaVersion,
                buildJobId = activeState.buildJobId,
                status = status,
                message = message,
                target = activeState.target,
                targetGroup = activeState.targetGroup,
                scenes = activeState.scenes,
                options = activeState.options,
                outputProjectPath = activeState.outputProjectPath,
                reportedOutputPath = activeState.reportedOutputPath,
                artifactKind = activeState.artifactKind,
                requiredTestRunId = activeState.requiredTestRunId,
                approvalRule = activeState.approvalRule,
                approvalGrantId = activeState.approvalGrantId,
                durationSeconds = activeState.durationSeconds,
                warningCount = activeState.warningCount,
                errorCount = activeState.errorCount,
                warnings = activeState.warnings == null
                    ? Array.Empty<BuildMessageSummary>()
                    : activeState.warnings.Take(MaxSummaryMessages).ToArray(),
                errors = activeState.errors == null
                    ? Array.Empty<BuildMessageSummary>()
                    : activeState.errors.Take(MaxSummaryMessages).ToArray(),
                fullLogFile = File.Exists(FullLogPath(activeState.buildJobId))
                    ? Relative(FullLogPath(activeState.buildJobId))
                    : null,
                artifactManifestFile = activeState.artifactManifestFile,
                startedAt = activeState.startedAt,
                finishedAt = UtcNow()
            };
            var bucket = status == "completed" ? "completed" : status == "cancelled" ? "cancelled" : "failed";
            var resultPath = Path.Combine(Dir(bucket), SafeId(activeState.buildJobId) + ".result.json");
            WriteAtomic(resultPath, JsonUtility.ToJson(result, true));
            DeleteIfExists(RunningStatePath(activeState.buildJobId));
            activeState = null;
            var snapshot = ToSnapshot(result, resultPath);
            WriteSummary(snapshot);
            return snapshot;
        }

        private static BuildArtifactManifest CreateArtifactManifest()
        {
            if (activeState == null) throw new InvalidOperationException("No build job is active.");
            var artifactPath = ArtifactAbsolutePath(
                activeState.outputAbsolutePath,
                activeState.artifactKind);
            var directory = !string.Equals(activeState.artifactKind, "file", StringComparison.Ordinal);
            if (directory && !Directory.Exists(artifactPath))
            {
                throw new InvalidOperationException("Successful build did not produce the expected output directory.");
            }
            if (!directory && !File.Exists(artifactPath))
            {
                throw new InvalidOperationException("Successful build did not produce the expected output file.");
            }

            var size = directory
                ? Directory.EnumerateFiles(artifactPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : new FileInfo(artifactPath).Length;
            var hash = directory ? HashDirectory(artifactPath) : HashFile(artifactPath);
            return new BuildArtifactManifest
            {
                schemaVersion = ArtifactSchemaVersion,
                contract = "alepou.unity-build-artifact/v1",
                buildJobId = activeState.buildJobId,
                target = activeState.target,
                targetGroup = activeState.targetGroup,
                artifactKind = activeState.artifactKind,
                projectRelativePath = RelativeToProject(artifactPath),
                absolutePath = artifactPath.Replace("\\", "/"),
                sizeBytes = size,
                hashAlgorithm = "sha256",
                sha256 = hash,
                sceneCount = activeState.scenes == null ? 0 : activeState.scenes.Length,
                options = activeState.options,
                durationSeconds = activeState.durationSeconds,
                warningCount = activeState.warningCount,
                errorCount = activeState.errorCount,
                createdAt = UtcNow()
            };
        }

        private static NormalizedBuildRequest ValidateAndNormalize(BuildJobRequest request)
        {
            if (request == null) throw new InvalidOperationException("build_player requires a request.");
            var id = SafeId(request.buildJobId);
            var target = ParseTarget(request.target);
            var targetGroup = BuildPipeline.GetBuildTargetGroup(target);
            if (target == BuildTarget.NoTarget || targetGroup == BuildTargetGroup.Unknown)
            {
                throw new InvalidOperationException("build_player target is not a buildable Unity target.");
            }
            if (!BuildPipeline.IsBuildTargetSupported(targetGroup, target))
            {
                throw new InvalidOperationException(
                    "Unity build support is not installed for target " + target + " (" + targetGroup + ").");
            }

            var scenes = NormalizeScenes(request.scenes);
            var optionNames = NormalizeOptions(request.options);
            var options = ParseOptions(optionNames);
            var artifactKind = ArtifactKind(target);
            var output = NormalizeOutputPath(id, target, artifactKind, request.outputPath);
            ValidateTargetExtension(target, output.projectPath, artifactKind);
            return new NormalizedBuildRequest
            {
                buildJobId = id,
                target = target,
                targetGroup = targetGroup,
                scenes = scenes,
                optionNames = optionNames,
                options = options,
                outputProjectPath = output.projectPath,
                outputAbsolutePath = output.absolutePath,
                artifactKind = artifactKind,
                requiredTestRunId = string.IsNullOrWhiteSpace(request.requiredTestRunId)
                    ? null
                    : SafeId(request.requiredTestRunId)
            };
        }

        private static BuildTarget ParseTarget(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return EditorUserBuildSettings.activeBuildTarget;
            BuildTarget target;
            if (!Enum.TryParse(raw.Trim(), true, out target))
            {
                throw new InvalidOperationException("Unknown Unity BuildTarget: " + raw);
            }
            return target;
        }

        private static string[] NormalizeScenes(string[] requested)
        {
            var values = requested == null || requested.Length == 0
                ? EditorBuildSettings.scenes.Where(scene => scene != null && scene.enabled).Select(scene => scene.path)
                : requested.AsEnumerable();
            var scenes = values
                .Where(scene => !string.IsNullOrWhiteSpace(scene))
                .Select(scene => scene.Trim().Replace("\\", "/"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (scenes.Length == 0) throw new InvalidOperationException("build_player requires at least one enabled scene.");
            if (scenes.Length > MaxScenes) throw new InvalidOperationException("build_player exceeds the " + MaxScenes + "-scene limit.");
            foreach (var scene in scenes)
            {
                if (!scene.StartsWith("Assets/", StringComparison.Ordinal) ||
                    !scene.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                    scene.Contains("..") ||
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null)
                {
                    throw new InvalidOperationException("Invalid or missing build scene: " + scene);
                }
            }
            return scenes;
        }

        private static string[] NormalizeOptions(string[] requested)
        {
            var options = (requested ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (options.Length > MaxOptions) throw new InvalidOperationException("build_player exceeds the build-option limit.");
            foreach (var option in options)
            {
                if (!AllowedBuildOptions.Contains(option))
                {
                    throw new InvalidOperationException(
                        "Unsupported or unsafe BuildOptions value: " + option +
                        ". AutoRunPlayer, ShowBuiltPlayer, patching, and external-project options are intentionally blocked.");
                }
                BuildOptions parsed;
                if (!Enum.TryParse(option, true, out parsed))
                {
                    throw new InvalidOperationException("BuildOptions value is unavailable in this Unity version: " + option);
                }
            }
            return options;
        }

        private static BuildOptions ParseOptions(IEnumerable<string> names)
        {
            var result = BuildOptions.None;
            foreach (var name in names)
            {
                BuildOptions parsed;
                if (Enum.TryParse(name, true, out parsed)) result |= parsed;
            }
            return result;
        }

        private static OutputPath NormalizeOutputPath(
            string buildJobId,
            BuildTarget target,
            string artifactKind,
            string requested)
        {
            var projectRoot = ProjectRoot();
            var buildRoot = Path.GetFullPath(Path.Combine(projectRoot, "Builds", "Alepou"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var relative = string.IsNullOrWhiteSpace(requested)
                ? DefaultOutputPath(buildJobId, target, artifactKind)
                : requested.Trim().Replace("\\", "/").TrimStart('/');
            if (Path.IsPathRooted(relative) || relative.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new InvalidOperationException("Build outputPath must be a safe project-relative path.");
            }
            var full = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(buildRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Build outputPath must remain under Builds/Alepou/.");
            }
            return new OutputPath
            {
                projectPath = RelativeToProject(full),
                absolutePath = full
            };
        }

        private static string DefaultOutputPath(string buildJobId, BuildTarget target, string artifactKind)
        {
            var folder = "Builds/Alepou/" + buildJobId + "/";
            if (string.Equals(artifactKind, "directory", StringComparison.Ordinal))
            {
                return folder + SafeFileName(PlayerSettings.productName) + "-" + target;
            }
            var extension = TargetExtension(target);
            return folder + SafeFileName(PlayerSettings.productName) + extension;
        }

        private static string ArtifactKind(BuildTarget target)
        {
            var name = target.ToString();
            if (name == "Android") return "file";
            if (name.StartsWith("StandaloneWindows", StringComparison.Ordinal) ||
                name.StartsWith("StandaloneLinux", StringComparison.Ordinal))
            {
                return "bundle";
            }
            return "directory";
        }

        private static string ArtifactAbsolutePath(string outputAbsolutePath, string artifactKind)
        {
            return string.Equals(artifactKind, "bundle", StringComparison.Ordinal)
                ? Path.GetDirectoryName(outputAbsolutePath)
                : outputAbsolutePath;
        }

        private static string TargetExtension(BuildTarget target)
        {
            var name = target.ToString();
            if (name == "Android") return EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk";
            if (name.StartsWith("StandaloneWindows", StringComparison.Ordinal)) return ".exe";
            if (name.StartsWith("StandaloneLinux", StringComparison.Ordinal)) return ".x86_64";
            if (name == "WSAPlayer") return "";
            return "";
        }

        private static void ValidateTargetExtension(BuildTarget target, string projectPath, string artifactKind)
        {
            if (string.Equals(artifactKind, "directory", StringComparison.Ordinal)) return;
            var expected = TargetExtension(target);
            if (!string.IsNullOrWhiteSpace(expected) &&
                !projectPath.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Build outputPath for " + target + " must end with " + expected + ".");
            }
        }

        private static void ValidateRequiredTest(string testRunId)
        {
            if (string.IsNullOrWhiteSpace(testRunId)) return;
            var test = AlepouUnityBridgeTestRunner.Snapshot(testRunId);
            if (!string.Equals(test.status, "completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Required Unity test run is not a clean completed result: " + testRunId + " (" + test.status + ").");
            }
        }

        private static void CollectReportMessages(BuildReport report)
        {
            if (activeState == null || report == null || report.steps == null) return;
            foreach (var step in report.steps)
            {
                if (step.messages == null) continue;
                foreach (var message in step.messages)
                {
                    var summary = new BuildMessageSummary
                    {
                        type = message.type.ToString(),
                        message = Truncate(message.content, MaxMessageLength)
                    };
                    if (message.type == LogType.Warning && activeState.warnings.Count < MaxSummaryMessages)
                    {
                        activeState.warnings.Add(summary);
                    }
                    else if ((message.type == LogType.Error ||
                              message.type == LogType.Exception ||
                              message.type == LogType.Assert) &&
                             activeState.errors.Count < MaxSummaryMessages)
                    {
                        activeState.errors.Add(summary);
                    }
                }
            }
        }

        private static void WriteBuildLog(BuildReport report, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var writer = new StreamWriter(file, false, new UTF8Encoding(false)))
            {
                var stepIndex = 0;
                foreach (var step in report.steps ?? Array.Empty<BuildStep>())
                {
                    if (step.messages != null)
                    {
                        foreach (var message in step.messages)
                        {
                            writer.WriteLine(JsonUtility.ToJson(new BuildLogEntry
                            {
                                stepIndex = stepIndex,
                                stepName = Truncate(step.name, 512),
                                type = message.type.ToString(),
                                message = Truncate(message.content, MaxLogMessageLength)
                            }));
                        }
                    }
                    stepIndex++;
                }
            }
        }

        private static string NormalizeReportedOutput(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var full = Path.GetFullPath(raw);
                return IsUnderProject(full) ? RelativeToProject(full) : full.Replace("\\", "/");
            }
            catch
            {
                return Truncate(raw, 1000);
            }
        }

        private static string HashFile(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                return Hex(sha.ComputeHash(stream));
            }
        }

        private static string HashDirectory(string directory)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    var relative = file.Substring(directory.TrimEnd('\\', '/').Length)
                        .TrimStart('\\', '/')
                        .Replace("\\", "/");
                    var info = new FileInfo(file);
                    var record = relative + "\0" +
                                 info.Length.ToString(CultureInfo.InvariantCulture) + "\0" +
                                 HashFile(file) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(record);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Hex(sha.Hash);
            }
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
        }

        private static string SafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(value) ? "AlepouBuild" : value.Trim())
                .Select(ch => invalid.Contains(ch) ? '-' : ch)
                .ToArray();
            var result = new string(chars).Trim(' ', '.');
            return string.IsNullOrWhiteSpace(result) ? "AlepouBuild" : result;
        }

        private static void RecoverInterruptedJobs()
        {
            if (activeState != null || string.IsNullOrWhiteSpace(outputPath)) return;
            foreach (var statePath in Directory.GetFiles(Dir("running"), "*.state.json"))
            {
                BuildJobState state = null;
                try { state = JsonUtility.FromJson<BuildJobState>(File.ReadAllText(statePath)); } catch { }
                var id = state == null || string.IsNullOrWhiteSpace(state.buildJobId)
                    ? Path.GetFileName(statePath).Replace(".state.json", "")
                    : state.buildJobId;
                var result = new BuildJobResult
                {
                    schemaVersion = SchemaVersion,
                    buildJobId = id,
                    status = "failed",
                    message = "Build job was interrupted by an editor/domain reload or process exit; Unity cannot resume BuildPipeline in place.",
                    target = state == null ? null : state.target,
                    targetGroup = state == null ? null : state.targetGroup,
                    scenes = state == null ? null : state.scenes,
                    options = state == null ? null : state.options,
                    outputProjectPath = state == null ? null : state.outputProjectPath,
                    artifactKind = state == null ? null : state.artifactKind,
                    requiredTestRunId = state == null ? null : state.requiredTestRunId,
                    approvalRule = state == null ? null : state.approvalRule,
                    approvalGrantId = state == null ? null : state.approvalGrantId,
                    errors = new[]
                    {
                        new BuildMessageSummary
                        {
                            type = "Interrupted",
                            message = "No success artifact manifest was produced."
                        }
                    },
                    finishedAt = UtcNow()
                };
                WriteAtomic(Path.Combine(Dir("failed"), SafeId(id) + ".result.json"), JsonUtility.ToJson(result, true));
                DeleteIfExists(statePath);
            }
        }

        private static void EnsureReady()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("Unity Bridge build runner is not initialized.");
            EnsureFolders();
            RecoverInterruptedJobs();
        }

        private static void EnsureFolders()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            foreach (var name in new[] { "running", "completed", "failed", "cancelled", "logs", "artifacts" })
            {
                Directory.CreateDirectory(Dir(name));
            }
        }

        private static string Dir(string name)
        {
            return Path.Combine(outputPath ?? "", "build-jobs", name);
        }

        private static string RunningStatePath(string buildJobId)
        {
            return Path.Combine(Dir("running"), SafeId(buildJobId) + ".state.json");
        }

        private static string FullLogPath(string buildJobId)
        {
            return Path.Combine(Dir("logs"), SafeId(buildJobId) + ".jsonl");
        }

        private static string ArtifactManifestPath(string buildJobId)
        {
            return Path.Combine(Dir("artifacts"), SafeId(buildJobId) + ".artifact.json");
        }

        private static string FindResultPath(string buildJobId)
        {
            var id = SafeId(buildJobId);
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

        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static bool IsUnderProject(string full)
        {
            var root = Path.GetFullPath(ProjectRoot())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return Path.GetFullPath(full).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string RelativeToProject(string full)
        {
            var root = Path.GetFullPath(ProjectRoot())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return Path.GetFullPath(full).Substring(root.Length).Replace("\\", "/");
        }

        private static string SafeId(string value)
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > 120) throw new InvalidOperationException("buildJobId is invalid.");
            if (trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')))
            {
                throw new InvalidOperationException("buildJobId contains unsafe characters.");
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
            var root = Path.GetFullPath(outputPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).Replace("\\", "/")
                : full.Replace("\\", "/");
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString("o");
        }

        private static void DeleteIfExists(string file)
        {
            if (File.Exists(file)) File.Delete(file);
        }

        private static void SaveState()
        {
            if (activeState == null) return;
            activeState.updatedAt = UtcNow();
            WriteAtomic(RunningStatePath(activeState.buildJobId), JsonUtility.ToJson(activeState, true));
            WriteSummary(ToSnapshot(activeState, RunningStatePath(activeState.buildJobId)));
        }

        private static void WriteSummary(BuildJobPublicSnapshot snapshot = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            WriteAtomic(
                Path.Combine(outputPath, "build-job-status.json"),
                JsonUtility.ToJson(snapshot ?? Snapshot(), true));
        }

        private static BuildJobPublicSnapshot ToSnapshot(BuildJobState state, string statePath)
        {
            return new BuildJobPublicSnapshot
            {
                active = !IsTerminal(state.status),
                buildJobId = state.buildJobId,
                status = state.status,
                message = state.message,
                target = state.target,
                targetGroup = state.targetGroup,
                outputProjectPath = state.outputProjectPath,
                progressStage = state.progressStage,
                progressPercent = state.progressPercent,
                warningCount = state.warningCount,
                errorCount = state.errorCount,
                requiredTestRunId = state.requiredTestRunId,
                artifactManifestFile = state.artifactManifestFile,
                resultFile = Relative(statePath),
                startedAt = state.startedAt,
                updatedAt = state.updatedAt
            };
        }

        private static BuildJobPublicSnapshot ToSnapshot(BuildJobResult result, string resultPath)
        {
            return new BuildJobPublicSnapshot
            {
                active = false,
                buildJobId = result.buildJobId,
                status = result.status,
                message = result.message,
                target = result.target,
                targetGroup = result.targetGroup,
                outputProjectPath = result.outputProjectPath,
                progressStage = result.status,
                progressPercent = 1.0,
                warningCount = result.warningCount,
                errorCount = result.errorCount,
                requiredTestRunId = result.requiredTestRunId,
                artifactManifestFile = result.artifactManifestFile,
                resultFile = Relative(resultPath),
                startedAt = result.startedAt,
                updatedAt = result.finishedAt
            };
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
        private sealed class NormalizedBuildRequest
        {
            public string buildJobId;
            public BuildTarget target;
            public BuildTargetGroup targetGroup;
            public string[] scenes;
            public string[] optionNames;
            public BuildOptions options;
            public string outputProjectPath;
            public string outputAbsolutePath;
            public string artifactKind;
            public string requiredTestRunId;
        }

        [Serializable]
        private sealed class OutputPath
        {
            public string projectPath;
            public string absolutePath;
        }

        [Serializable]
        private sealed class BuildJobState
        {
            public int schemaVersion;
            public string buildJobId;
            public string status;
            public string message;
            public string target;
            public string targetGroup;
            public string[] scenes;
            public string[] options;
            public string outputProjectPath;
            public string outputAbsolutePath;
            public string reportedOutputPath;
            public string artifactKind;
            public string requiredTestRunId;
            public string approvalRule;
            public string approvalGrantId;
            public string progressStage;
            public double progressPercent;
            public double durationSeconds;
            public int warningCount;
            public int errorCount;
            public List<BuildMessageSummary> warnings;
            public List<BuildMessageSummary> errors;
            public string artifactManifestFile;
            public string createdAt;
            public string startedAt;
            public string updatedAt;
        }

        [Serializable]
        private sealed class BuildJobResult
        {
            public int schemaVersion;
            public string buildJobId;
            public string status;
            public string message;
            public string target;
            public string targetGroup;
            public string[] scenes;
            public string[] options;
            public string outputProjectPath;
            public string reportedOutputPath;
            public string artifactKind;
            public string requiredTestRunId;
            public string approvalRule;
            public string approvalGrantId;
            public double durationSeconds;
            public int warningCount;
            public int errorCount;
            public BuildMessageSummary[] warnings;
            public BuildMessageSummary[] errors;
            public string fullLogFile;
            public string artifactManifestFile;
            public string startedAt;
            public string finishedAt;
        }

        [Serializable]
        private sealed class BuildLogEntry
        {
            public int stepIndex;
            public string stepName;
            public string type;
            public string message;
        }
    }

    [Serializable]
    internal sealed class BuildJobRequest
    {
        public string buildJobId;
        public string target;
        public string[] scenes;
        public string[] options;
        public string outputPath;
        public string requiredTestRunId;
    }

    [Serializable]
    internal sealed class BuildJobPublicSnapshot
    {
        public bool active;
        public string buildJobId;
        public string status;
        public string message;
        public string target;
        public string targetGroup;
        public string outputProjectPath;
        public string progressStage;
        public double progressPercent;
        public int warningCount;
        public int errorCount;
        public string requiredTestRunId;
        public string artifactManifestFile;
        public string resultFile;
        public string startedAt;
        public string updatedAt;
    }

    [Serializable]
    internal sealed class BuildArtifactManifest
    {
        public int schemaVersion;
        public string contract;
        public string buildJobId;
        public string target;
        public string targetGroup;
        public string artifactKind;
        public string projectRelativePath;
        public string absolutePath;
        public long sizeBytes;
        public string hashAlgorithm;
        public string sha256;
        public int sceneCount;
        public string[] options;
        public double durationSeconds;
        public int warningCount;
        public int errorCount;
        public string createdAt;
    }

    [Serializable]
    internal sealed class BuildMessageSummary
    {
        public string type;
        public string message;
    }
}
