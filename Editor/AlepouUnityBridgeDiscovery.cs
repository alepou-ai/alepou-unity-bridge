using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;

namespace Alepou.UnityBridge
{
    internal static class AlepouUnityBridgeDiscovery
    {
        private const int MaximumResponseCharacters = 1_000_000;
        private const int RequestTimeoutMilliseconds = 2_000;

        internal static AlepouUnityBindingDiscoveryResult DiscoverCurrentProject()
        {
            try
            {
                var record = ReadAgentDiscovery();
                var catalogue = FetchCatalogue(record);
                var currentRoot = ComparablePath(CurrentProjectRoot());
                AlepouUnityBindingProject project = null;
                foreach (var candidate in catalogue.projects ?? Array.Empty<AlepouUnityBindingProject>())
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.path)) continue;
                    if (!string.Equals(ComparablePath(candidate.path), currentRoot, PathComparison())) continue;
                    project = candidate;
                    break;
                }

                if (project == null)
                {
                    return AlepouUnityBindingDiscoveryResult.Failure(
                        "Alepou is running, but this exact Unity project is not registered there. " +
                        "Add or open this project root in Alepou, then refresh: " + CurrentProjectRoot());
                }

                var sessions = Array.FindAll(
                    project.sessions ?? Array.Empty<AlepouUnityBindingSession>(),
                    session => session != null
                        && string.Equals(session.status, "running", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(session.sessionId));
                var message = sessions.Length == 0
                    ? "Project matched, but it has no running Alepou session. Start or open a session for this project, then refresh."
                    : "Matched Alepou project " + Display(project.name, project.projectId) +
                      " with " + sessions.Length + " running session" + (sessions.Length == 1 ? "." : "s.");
                return AlepouUnityBindingDiscoveryResult.Success(project, sessions, catalogue.generatedAt, message);
            }
            catch (Exception ex)
            {
                return AlepouUnityBindingDiscoveryResult.Failure(Explain(ex));
            }
        }

        internal static string SessionLabel(AlepouUnityBindingSession session)
        {
            if (session == null) return "Unknown Alepou session";
            var primary = Display(session.label, session.launchMode);
            var health = Display(session.healthState, session.status);
            var activity = RelativeTime(session.lastActivityAt);
            var shortId = string.IsNullOrWhiteSpace(session.sessionId)
                ? "unknown"
                : session.sessionId.Substring(0, Math.Min(12, session.sessionId.Length));
            return primary + " | " + health + " | " + activity + " | " + shortId;
        }

        private static AgentDiscoveryRecord ReadAgentDiscovery()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var file = Path.Combine(home, ".alepou", "agent.json");
            if (!File.Exists(file))
            {
                throw new InvalidOperationException(
                    "Alepou discovery was not found. Start the rebuilt Alepou app, then click Refresh Alepou Sessions.");
            }
            var raw = File.ReadAllText(file, Encoding.UTF8);
            if (raw.Length > 64_000) throw new InvalidOperationException("Alepou discovery file is unexpectedly large.");
            var record = JsonUtility.FromJson<AgentDiscoveryRecord>(raw);
            if (record == null || record.schemaVersion != 1)
            {
                throw new InvalidOperationException("Alepou discovery uses an unsupported schema. Restart the current Alepou app.");
            }

            Uri baseUri;
            if (!Uri.TryCreate(record.baseUrl, UriKind.Absolute, out baseUri)
                || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !baseUri.IsLoopback
                || !string.IsNullOrEmpty(baseUri.UserInfo))
            {
                throw new InvalidOperationException("Alepou discovery did not contain a safe loopback HTTP address.");
            }
            var token = string.IsNullOrWhiteSpace(record.integrationToken)
                ? record.launchToken
                : record.integrationToken;
            token = string.IsNullOrWhiteSpace(token) ? "" : token.Trim();
            if (token.Length < 32 || token.Length > 256)
            {
                throw new InvalidOperationException("Alepou discovery did not contain a valid integration token.");
            }
            record.baseUri = baseUri;
            record.token = token;
            return record;
        }

        private static BindingCatalogue FetchCatalogue(AgentDiscoveryRecord record)
        {
            var endpoint = new Uri(record.baseUri, "/api/integrations/unity/bindings");
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.Accept = "application/json";
            request.KeepAlive = false;
            request.Proxy = null;
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.Headers["x-alepou-integration-token"] = record.token;

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException("Alepou returned HTTP " + (int)response.StatusCode + ".");
                }
                if (response.ContentLength > MaximumResponseCharacters)
                {
                    throw new InvalidOperationException("Alepou's binding catalogue exceeded the size limit.");
                }
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = ReadBounded(reader);
                    var catalogue = JsonUtility.FromJson<BindingCatalogue>(json);
                    if (catalogue == null || catalogue.schemaVersion != 1 || !catalogue.ok)
                    {
                        throw new InvalidOperationException("Alepou returned an invalid binding catalogue.");
                    }
                    return catalogue;
                }
            }
        }

        private static string ReadBounded(TextReader reader)
        {
            var builder = new StringBuilder();
            var buffer = new char[4_096];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (builder.Length + read > MaximumResponseCharacters)
                {
                    throw new InvalidOperationException("Alepou's binding catalogue exceeded the size limit.");
                }
                builder.Append(buffer, 0, read);
            }
            return builder.ToString();
        }

        private static string Explain(Exception exception)
        {
            var web = exception as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            return "Alepou rejected the local integration token. Restart Alepou so ~/.alepou/agent.json is refreshed, then try again.";
                        }
                    }
                }
                return "Could not reach the local Alepou app. Confirm it is running, then try Refresh Alepou Sessions again. " + web.Message;
            }
            return exception.Message;
        }

        private static string CurrentProjectRoot()
        {
            var assets = Application.dataPath;
            var parent = string.IsNullOrWhiteSpace(assets) ? null : Directory.GetParent(assets);
            if (parent == null) throw new InvalidOperationException("Unity project root could not be resolved.");
            return Path.GetFullPath(parent.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ComparablePath(string value)
        {
            var full = Path.GetFullPath(value ?? "")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Application.platform == RuntimePlatform.WindowsEditor ? full.ToUpperInvariant() : full;
        }

        private static StringComparison PathComparison()
        {
            return Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        private static string Display(string preferred, string fallback)
        {
            return !string.IsNullOrWhiteSpace(preferred)
                ? preferred.Trim()
                : !string.IsNullOrWhiteSpace(fallback) ? fallback.Trim() : "Unnamed";
        }

        private static string RelativeTime(string raw)
        {
            DateTimeOffset value;
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
            {
                return "activity unknown";
            }
            var age = DateTimeOffset.UtcNow - value;
            if (age.TotalSeconds < 60) return "active now";
            if (age.TotalMinutes < 60) return Math.Max(1, (int)age.TotalMinutes) + "m ago";
            if (age.TotalHours < 24) return Math.Max(1, (int)age.TotalHours) + "h ago";
            return Math.Max(1, (int)age.TotalDays) + "d ago";
        }

        [Serializable]
        private sealed class AgentDiscoveryRecord
        {
            public int schemaVersion;
            public string baseUrl;
            public string launchToken;
            public string integrationToken;
            [NonSerialized] public Uri baseUri;
            [NonSerialized] public string token;
        }

        [Serializable]
        private sealed class BindingCatalogue
        {
            public int schemaVersion;
            public bool ok;
            public AlepouUnityBindingProject[] projects;
            public string generatedAt;
        }
    }

    [Serializable]
    internal sealed class AlepouUnityBindingProject
    {
        public string projectId;
        public string name;
        public string path;
        public AlepouUnityBindingProjectState unity;
        public AlepouUnityBindingSession[] sessions;
    }

    [Serializable]
    internal sealed class AlepouUnityBindingProjectState
    {
        public bool active;
        public bool detected;
        public bool enabled;
    }

    [Serializable]
    internal sealed class AlepouUnityBindingSession
    {
        public string sessionId;
        public string label;
        public string launchMode;
        public string backendKind;
        public string status;
        public string healthState;
        public string startedAt;
        public string lastActivityAt;
    }

    internal sealed class AlepouUnityBindingDiscoveryResult
    {
        internal bool ok;
        internal string message;
        internal string projectId;
        internal string projectName;
        internal string projectPath;
        internal string generatedAt;
        internal AlepouUnityBindingSession[] sessions = Array.Empty<AlepouUnityBindingSession>();

        internal static AlepouUnityBindingDiscoveryResult Success(
            AlepouUnityBindingProject project,
            AlepouUnityBindingSession[] sessions,
            string generatedAt,
            string message)
        {
            return new AlepouUnityBindingDiscoveryResult
            {
                ok = true,
                message = message,
                projectId = project.projectId,
                projectName = project.name,
                projectPath = project.path,
                generatedAt = generatedAt,
                sessions = sessions ?? Array.Empty<AlepouUnityBindingSession>()
            };
        }

        internal static AlepouUnityBindingDiscoveryResult Failure(string message)
        {
            return new AlepouUnityBindingDiscoveryResult
            {
                ok = false,
                message = string.IsNullOrWhiteSpace(message) ? "Alepou session discovery failed." : message
            };
        }
    }
}
