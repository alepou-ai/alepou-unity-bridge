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
    internal static class AlepouUnityBridgeApprovalPolicy
    {
        internal const string ManualProfile = "manual-writes";
        internal const string ReversibleProfile = "reversible-workspace";
        internal const string TrustedDevelopmentProfile = "trusted-development";
        internal const string CustomProfile = "custom";

        private const int GrantSchemaVersion = 2;
        private const string GrantKeyPrefix = "Alepou.UnityBridge.ApprovalGrant.";
        private const string PausedKeyPrefix = "Alepou.UnityBridge.ApprovalPaused.";

        private static readonly string[] ObservationActions =
        {
            "export_snapshot",
            "capture_view",
            "frame_ui_builder_document",
            "query_assets",
            "read_object",
            "read_asset"
        };

        // These are bounded editor/workspace operations that an owner may delegate.
        // The presets below decide which of these capabilities are grouped together.
        // Destructive, general project settings, and device capabilities
        // intentionally do not appear here at all.
        private static readonly string[] DelegatableActions =
        {
            "open_ui_builder",
            "close_ui_builder",
            "select_object",
            "ping_asset",
            "refresh_assets",
            "save_scene",
            "create_gameobject",
            "set_parent",
            "set_transform",
            "add_component",
            "assign_reference",
            "set_property",
            "create_folder",
            "create_scriptable_object_asset",
            "set_asset_property",
            "assign_asset_reference",
            "move_asset",
            "rename_asset",
            "run_tests",
            "build_player",
            "create_prefab",
            "instantiate_prefab",
            "edit_prefab",
            "create_material",
            "set_material_property",
            "create_shader_asset",
            "create_animation_clip",
            "create_light",
            "bake_lighting",
            "batch_edit_objects",
            "execute_menu_item"
        };

        // Play Mode executes project runtime code and is therefore not exposed in
        // the customizable or reversible action lists. It is available only in
        // the session-bound Trusted Development preset and only through the
        // durable workflow runner.
        private static readonly string[] TrustedDevelopmentOnlyActions =
        {
            "enter_play_mode",
            "exit_play_mode"
        };

        private static readonly string[] ReversiblePresetActions =
        {
            "open_ui_builder",
            "close_ui_builder",
            "select_object",
            "ping_asset",
            "save_scene",
            "create_gameobject",
            "set_parent",
            "set_transform",
            "add_component",
            "assign_reference",
            "set_property",
            "create_folder",
            "create_scriptable_object_asset",
            "set_asset_property",
            "assign_asset_reference",
            "move_asset",
            "rename_asset",
            "create_prefab",
            "instantiate_prefab",
            "create_material",
            "create_animation_clip",
            "create_light",
            "batch_edit_objects"
        };

        private static readonly string[] TrustedDevelopmentPresetActions =
        {
            "open_ui_builder",
            "close_ui_builder",
            "select_object",
            "ping_asset",
            "refresh_assets",
            "save_scene",
            "create_gameobject",
            "set_parent",
            "set_transform",
            "add_component",
            "assign_reference",
            "set_property",
            "create_folder",
            "create_scriptable_object_asset",
            "set_asset_property",
            "assign_asset_reference",
            "move_asset",
            "rename_asset",
            "run_tests",
            "create_prefab",
            "instantiate_prefab",
            "edit_prefab",
            "create_material",
            "set_material_property",
            "create_animation_clip",
            "create_light",
            "batch_edit_objects",
            "enter_play_mode",
            "exit_play_mode"
        };

        private static readonly HashSet<string> ObservationSet = new HashSet<string>(ObservationActions, StringComparer.Ordinal);
        private static readonly HashSet<string> DelegatableSet = new HashSet<string>(
            DelegatableActions.Concat(TrustedDevelopmentOnlyActions),
            StringComparer.Ordinal);
        private static readonly HashSet<string> HumanOnlySet = new HashSet<string>(new[]
        {
            "destroy_gameobject",
            "remove_component",
            "delete_asset",
            "set_player_setting",
            "set_build_scenes",
            "clear_baked_lighting",
            "device_install",
            "device_launch",
            "device_stop",
            "device_uninstall",
            "device_clear_data",
            "device_reboot",
            "device_shell",
            "device_push",
            "device_pull"
        }, StringComparer.Ordinal);

        internal static string[] CustomizableActions
        {
            get { return DelegatableActions.ToArray(); }
        }

        internal static string[] ReversibleActions
        {
            get { return ReversiblePresetActions.ToArray(); }
        }

        internal static string[] TrustedDevelopmentActions
        {
            get { return TrustedDevelopmentPresetActions.ToArray(); }
        }

        internal static string ProjectIdentity
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath).FullName;
                var canonical = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Application.platform == RuntimePlatform.WindowsEditor) canonical = canonical.ToUpperInvariant();
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                        .Replace("-", "")
                        .ToLowerInvariant();
                }
            }
        }

        internal static bool AutomaticProcessingEnabled
        {
            get { return !EditorPrefs.GetBool(PausedKey(), false); }
        }

        internal static ApprovalPolicySnapshot Snapshot()
        {
            var grant = LoadGrant();
            var now = DateTimeOffset.UtcNow;
            var active = grant != null && grant.enabled && !IsExpired(grant, now);
            var durableTrusted = active && IsDurableTrustedGrant(grant);
            return new ApprovalPolicySnapshot
            {
                profile = active ? grant.profile : ManualProfile,
                grantActive = active,
                grantId = active ? grant.grantId : null,
                durable = durableTrusted,
                expiresAt = active && !durableTrusted ? grant.expiresAt : null,
                sessionBinding = active ? grant.sessionBinding : null,
                sessionBound = active && !string.IsNullOrWhiteSpace(grant.sessionBinding),
                maxCommands = active ? grant.maxCommands : 0,
                usedCommands = active ? grant.usedCommands : 0,
                remainingCommands = durableTrusted ? -1 : active ? Math.Max(0, grant.maxCommands - grant.usedCommands) : 0,
                maxActions = active ? grant.maxActions : 0,
                usedActions = active ? grant.usedActions : 0,
                remainingActions = durableTrusted ? -1 : active ? Math.Max(0, grant.maxActions - grant.usedActions) : 0,
                allowedActions = active ? (grant.allowedActions ?? Array.Empty<string>()) : Array.Empty<string>(),
                automaticProcessingPaused = !AutomaticProcessingEnabled,
                riskSummary = active ? RiskSummary(grant.allowedActions) : "Observation commands only; every mutation requires a human click."
            };
        }

        internal static ApprovalDecision Evaluate(string commandGrantId, string commandSessionId, IEnumerable<string> rawActions, bool consume)
        {
            var actions = (rawActions ?? Array.Empty<string>())
                .Select(NormalizeAction)
                .Where(action => !string.IsNullOrWhiteSpace(action))
                .ToArray();
            if (actions.Length == 0)
            {
                return ApprovalDecision.Deny("empty-command", "The command contains no actions.", ManualProfile, null);
            }
            if (!AutomaticProcessingEnabled)
            {
                return ApprovalDecision.Deny("emergency-stop", "Automatic processing is stopped locally in Unity.", ManualProfile, null);
            }

            var nonObservation = actions.Where(action => !ObservationSet.Contains(action)).ToArray();
            if (nonObservation.Length == 0)
            {
                return ApprovalDecision.Allow("default-observation", "All actions are bounded observation commands.", ManualProfile, null);
            }

            var protectedAction = nonObservation.FirstOrDefault(action => HumanOnlySet.Contains(action) || !DelegatableSet.Contains(action));
            if (!string.IsNullOrWhiteSpace(protectedAction))
            {
                return ApprovalDecision.Deny(
                    "human-only-action",
                    protectedAction + " requires a direct human approval or a future separately scoped capability.",
                    ManualProfile,
                    null);
            }

            var grant = LoadGrant();
            if (grant == null || !grant.enabled)
            {
                return ApprovalDecision.Deny("no-active-grant", "No delegated write grant is active for this Unity project.", ManualProfile, null);
            }
            if (IsExpired(grant, DateTimeOffset.UtcNow))
            {
                return ApprovalDecision.Deny("grant-expired", "The delegated write grant has expired.", grant.profile, grant.grantId);
            }
            if (!string.Equals(commandGrantId, grant.grantId, StringComparison.Ordinal))
            {
                return ApprovalDecision.Deny("grant-id-mismatch", "The command does not reference the active local grant.", grant.profile, grant.grantId);
            }
            if (!string.IsNullOrWhiteSpace(grant.sessionBinding) &&
                !string.Equals(commandSessionId, grant.sessionBinding, StringComparison.Ordinal))
            {
                return ApprovalDecision.Deny("session-mismatch", "The command does not match the grant's Alepou session binding.", grant.profile, grant.grantId);
            }

            var allowed = new HashSet<string>(grant.allowedActions ?? Array.Empty<string>(), StringComparer.Ordinal);
            var deniedAction = nonObservation.FirstOrDefault(action => !allowed.Contains(action));
            if (!string.IsNullOrWhiteSpace(deniedAction))
            {
                return ApprovalDecision.Deny("action-not-delegated", deniedAction + " is not included in the active grant.", grant.profile, grant.grantId);
            }
            var durableTrusted = IsDurableTrustedGrant(grant);
            if (!durableTrusted && grant.usedCommands + 1 > grant.maxCommands)
            {
                return ApprovalDecision.Deny("command-budget-exhausted", "The grant's command budget is exhausted.", grant.profile, grant.grantId);
            }
            if (!durableTrusted && grant.usedActions + actions.Length > grant.maxActions)
            {
                return ApprovalDecision.Deny("action-budget-exhausted", "The grant's action budget is exhausted.", grant.profile, grant.grantId);
            }

            if (consume && !durableTrusted)
            {
                grant.usedCommands++;
                grant.usedActions += actions.Length;
                SaveGrant(grant);
            }
            return ApprovalDecision.Allow(
                durableTrusted
                    ? (consume ? "durable-trusted-development" : "durable-trusted-development-preview")
                    : (consume ? "delegated-grant-consumed" : "delegated-grant-preview"),
                "Allowed by the active " + grant.profile + " grant.",
                grant.profile,
                grant.grantId);
        }

        internal static ApprovalPolicySnapshot EnableReversibleGrant(int durationMinutes, int maxCommands, int maxActions, string sessionBinding)
        {
            return EnableGrant(ReversibleProfile, ReversiblePresetActions, durationMinutes, maxCommands, maxActions, sessionBinding);
        }

        internal static ApprovalPolicySnapshot EnableTrustedDevelopmentGrant(string sessionBinding)
        {
            var normalizedBinding = NormalizeSessionBinding(sessionBinding);
            if (string.IsNullOrWhiteSpace(normalizedBinding))
            {
                throw new InvalidOperationException("Trusted Development must be bound to the owning Alepou session.");
            }
            return EnableGrant(
                TrustedDevelopmentProfile,
                TrustedDevelopmentPresetActions,
                0,
                0,
                0,
                normalizedBinding,
                true);
        }

        internal static ApprovalPolicySnapshot EnableCustomGrant(IEnumerable<string> actions, int durationMinutes, int maxCommands, int maxActions, string sessionBinding)
        {
            var customizable = new HashSet<string>(DelegatableActions, StringComparer.Ordinal);
            var bounded = (actions ?? Array.Empty<string>())
                .Select(NormalizeAction)
                .Where(action => customizable.Contains(action))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (bounded.Length == 0) throw new InvalidOperationException("Select at least one delegatable action.");
            return EnableGrant(CustomProfile, bounded, durationMinutes, maxCommands, maxActions, sessionBinding);
        }

        internal static void RevokeGrant()
        {
            EditorPrefs.DeleteKey(GrantKey());
        }

        internal static void EmergencyStop()
        {
            EditorPrefs.SetBool(PausedKey(), true);
            RevokeGrant();
        }

        internal static void ResumeObservationProcessing()
        {
            EditorPrefs.SetBool(PausedKey(), false);
        }

        internal static ApprovalDecision ManualApproval()
        {
            return ApprovalDecision.Allow(
                "manual-editor-approval",
                "Approved directly by a human in the Unity Bridge window.",
                ManualProfile,
                null);
        }

        internal static ApprovalDecision ManualRejection()
        {
            return ApprovalDecision.Deny(
                "manual-editor-rejection",
                "Rejected directly by a human in the Unity Bridge window.",
                ManualProfile,
                null);
        }

        internal static string RiskSummary(IEnumerable<string> actions)
        {
            var set = new HashSet<string>((actions ?? Array.Empty<string>()).Select(NormalizeAction), StringComparer.Ordinal);
            var risks = new List<string>();
            if (set.Contains("open_ui_builder") || set.Contains("close_ui_builder")) risks.Add("may open, focus, or close the UI Builder editor window; close refuses while UI Builder reports unsaved changes");
            if (set.Contains("refresh_assets")) risks.Add("may import assets, compile and execute project Editor code with your Unity user privileges, and reload the Unity domain");
            if (set.Contains("run_tests")) risks.Add("may execute EditMode or PlayMode project test code and change transient editor/runtime state");
            if (set.Contains("enter_play_mode") || set.Contains("exit_play_mode")) risks.Add("may start and stop project runtime code through a bounded workflow and capture transient runtime state");
            if (set.Contains("build_player")) risks.Add("may execute Unity build hooks and project code, use substantial CPU/disk space, and write a new artifact under Builds/Alepou");
            if (set.Contains("create_shader_asset")) risks.Add("writes a fixed-template shader source file and imports it through Unity");
            if (set.Contains("bake_lighting")) risks.Add("may run a long synchronous lighting bake and write generated lighting data");
            if (set.Contains("execute_menu_item")) risks.Add("may invoke only the Bridge's fixed Assets/Refresh or File/Save menu allowlist");
            if (set.Contains("edit_prefab") || set.Contains("set_material_property")) risks.Add("may persist edits to existing prefab or material assets");
            if (set.Contains("save_scene")) risks.Add("may persist current scene changes to disk");
            if (set.Any(action => action.Contains("asset") || action == "create_folder" || action == "move_asset" || action == "rename_asset" ||
                                  action == "create_prefab" || action == "edit_prefab" || action == "create_material" ||
                                  action == "set_material_property" || action == "create_animation_clip"))
            {
                risks.Add("may create or reorganize project assets");
            }
            if (set.Any(action => action.Contains("gameobject") || action == "set_parent" || action == "set_transform" || action == "add_component" ||
                                  action == "assign_reference" || action == "set_property" || action == "instantiate_prefab" ||
                                  action == "create_light" || action == "batch_edit_objects"))
            {
                risks.Add("may change the open scene and Inspector values");
            }
            if (risks.Count == 0) return "Bounded editor selection/utility operations only.";
            return "This grant " + string.Join("; ", risks) + ". Destructive operations, general project settings, baked-light clearing, and device actions remain human-only.";
        }

        private static ApprovalPolicySnapshot EnableGrant(
            string profile,
            IEnumerable<string> actions,
            int durationMinutes,
            int maxCommands,
            int maxActions,
            string sessionBinding,
            bool durable = false)
        {
            var now = DateTimeOffset.UtcNow;
            var grant = new ApprovalGrantRecord
            {
                schemaVersion = GrantSchemaVersion,
                enabled = true,
                projectIdentity = ProjectIdentity,
                grantId = Guid.NewGuid().ToString("N"),
                profile = profile,
                createdAt = now.ToString("o"),
                durable = durable,
                expiresAt = durable ? null : now.AddMinutes(Math.Max(5, Math.Min(480, durationMinutes))).ToString("o"),
                sessionBinding = NormalizeSessionBinding(sessionBinding),
                maxCommands = durable ? 0 : Math.Max(1, Math.Min(200, maxCommands)),
                usedCommands = 0,
                maxActions = durable ? 0 : Math.Max(1, Math.Min(1000, maxActions)),
                usedActions = 0,
                allowedActions = actions
                    .Select(NormalizeAction)
                    .Where(action => DelegatableSet.Contains(action))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
            SaveGrant(grant);
            ResumeObservationProcessing();
            return Snapshot();
        }

        private static string NormalizeAction(string action)
        {
            return string.IsNullOrWhiteSpace(action) ? "" : action.Trim().ToLowerInvariant();
        }

        private static string NormalizeSessionBinding(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length <= 128 ? trimmed : trimmed.Substring(0, 128);
        }

        private static ApprovalGrantRecord LoadGrant()
        {
            var raw = EditorPrefs.GetString(GrantKey(), "");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var grant = JsonUtility.FromJson<ApprovalGrantRecord>(raw);
                if (grant == null || (grant.schemaVersion != 1 && grant.schemaVersion != GrantSchemaVersion)) return null;
                if (!string.Equals(grant.projectIdentity, ProjectIdentity, StringComparison.Ordinal)) return null;
                if (grant.schemaVersion == 1)
                {
                    grant.schemaVersion = GrantSchemaVersion;
                    grant.durable = false;
                }
                return grant;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveGrant(ApprovalGrantRecord grant)
        {
            EditorPrefs.SetString(GrantKey(), JsonUtility.ToJson(grant));
        }

        private static bool IsExpired(ApprovalGrantRecord grant, DateTimeOffset now)
        {
            if (IsDurableTrustedGrant(grant)) return false;
            DateTimeOffset expiresAt;
            return !DateTimeOffset.TryParse(grant.expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiresAt)
                || expiresAt <= now;
        }

        private static bool IsDurableTrustedGrant(ApprovalGrantRecord grant)
        {
            return grant != null
                && grant.durable
                && string.Equals(grant.profile, TrustedDevelopmentProfile, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(grant.sessionBinding);
        }

        private static string GrantKey()
        {
            return GrantKeyPrefix + ProjectIdentity;
        }

        private static string PausedKey()
        {
            return PausedKeyPrefix + ProjectIdentity;
        }

        [Serializable]
        private sealed class ApprovalGrantRecord
        {
            public int schemaVersion;
            public bool enabled;
            public string projectIdentity;
            public string grantId;
            public string profile;
            public string createdAt;
            public bool durable;
            public string expiresAt;
            public string sessionBinding;
            public int maxCommands;
            public int usedCommands;
            public int maxActions;
            public int usedActions;
            public string[] allowedActions;
        }
    }

    internal sealed class ApprovalPolicySnapshot
    {
        public string profile;
        public bool grantActive;
        public string grantId;
        public bool durable;
        public string expiresAt;
        public string sessionBinding;
        public bool sessionBound;
        public int maxCommands;
        public int usedCommands;
        public int remainingCommands;
        public int maxActions;
        public int usedActions;
        public int remainingActions;
        public string[] allowedActions;
        public bool automaticProcessingPaused;
        public string riskSummary;
    }

    internal sealed class ApprovalDecision
    {
        public bool allowed;
        public string rule;
        public string reason;
        public string profile;
        public string grantId;

        internal static ApprovalDecision Allow(string rule, string reason, string profile, string grantId)
        {
            return new ApprovalDecision { allowed = true, rule = rule, reason = reason, profile = profile, grantId = grantId };
        }

        internal static ApprovalDecision Deny(string rule, string reason, string profile, string grantId)
        {
            return new ApprovalDecision { allowed = false, rule = rule, reason = reason, profile = profile, grantId = grantId };
        }
    }
}
