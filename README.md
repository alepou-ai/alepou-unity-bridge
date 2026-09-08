# Alepou Unity Bridge

Alepou Unity Bridge is the official Unity integration for [Alepou](https://alepou.ai). It gives AI coding sessions a durable, inspectable way to understand and work through the Unity Editor: compact state is exported into `plan/unity/`, typed commands are applied through Unity APIs, long compile/test/build workflows survive domain reloads, and every mutation stays inside a local approval policy you control.

The package is free and open source under Apache 2.0. Alepou is the recommended control plane, but the Unity-side file contract remains local and inspectable rather than hidden behind a hosted service.

> **Beta:** use version-pinned installs, keep the project under version control, and read the [authority boundary](#safety) before delegating compilation or mutation work.

The current release is **0.18.0**. Install the immutable version-pinned package URL below rather than tracking `main`.

## Exported State

Clicking **Export Snapshot** writes:

- `bridge-health.json` - a continuously refreshed processor heartbeat, current wait reason, active command, and pending/processing counts.
- `bridge-watchdog.json` - an independent process heartbeat and last editor-loop tick, used to identify a live Unity process whose main loop is blocked.
- `scene-hierarchy.json` - flat scene hierarchy nodes with GameObject paths, active state, tags/layers, prefab source, and attached component names.
- `selection.json` - currently selected GameObjects.
- `inspector-snapshot.json` - detailed serialized fields for the active selected GameObject.
- `selected-subtree-inspector.json` - detailed serialized fields for the selected GameObject and its children, bounded by depth/object count.
- `compile-status.json` - Unity import/compile state and likely compiler errors captured by the bridge.
- `console-summary.md` and `console.jsonl` - captured Console output.
- `command-results.md` and `command-results.json` - recent applied, failed, and rejected command results.
- `workflow-status.json`, `test-run-status.json`, and `build-job-status.json` - bounded durable job progress.
- `build-settings.json`, `packages.json`, and `diagnostics.md`.
- Pull-only UI Builder evidence, when requested: `ui-builder-view.png` and `ui-builder-view.json`.
- `plan/unity/recipes/` - generated workflow recipes for script generation, compile checkpoints, command batches, and prototype scaffolds.

Object reference fields in inspector exports include readable object paths/global IDs when the reference points at a scene GameObject or Component.

## Install

### Version-pinned Git install (recommended)

1. Open **Window > Package Manager**.
2. Open the **+** menu and choose **Install package from git URL**.
3. Enter the version-pinned package-root URL:

   ```text
   https://github.com/alepou-ai/alepou-unity-bridge.git#v0.18.0
   ```

4. The Bridge service starts whenever the Unity project is open; its Editor window is not required for command processing.
5. Open **Tools > Alepou > Unity Bridge > Open Bridge Window**. With Alepou running, the window automatically matches the exact Unity project and lists its live Alepou sessions; choose one and click **Bind + Trust This Alepou Session** when you want unattended project work.
6. Confirm the output path, normally `<UnityProject>/plan/unity`, and verify `bridge-health.json` reports a fresh heartbeat, `processorActive: true`, and `bridgeVersion: "0.18.0"`.

The tag keeps the Unity project on a known Bridge build. Do not install production projects from an unpinned `main` URL.

### Local package development

Clone this repository, then choose **Add package from disk** in Unity Package Manager and select its root `package.json`. Local edits are visible after Unity refreshes the package and completes any domain reload.

### Migrate an existing Alepou-checkout install

Older projects may reference `file:<Alepou checkout>/unity-bridge/com.alepou.unity-bridge` in `Packages/manifest.json`.

1. Save project work and wait until `bridge-health.json` shows no active command, workflow, test, build, compile, or Play Mode transition. Revoke an active grant first if you do not want it to survive the update.
2. In Unity Package Manager, remove the local package and install the version-pinned Git URL above. Alternatively, replace only the `com.alepou.unity-bridge` dependency value in `Packages/manifest.json` while Unity is closed.
3. Allow package resolution and the Unity domain reload to finish.
4. Confirm `plan/unity/bridge-health.json` has a fresh heartbeat, `processorActive: true`, and `bridgeVersion: "0.18.0"`.
5. Open **Tools > Alepou > Unity Bridge > Open Bridge Window** and verify the output path and policy. Existing project-local evidence under `plan/unity/` is not removed by changing the package source.
6. Run the smoke test below before delegating writes, tests, builds, or device work.

## Smoke Test

After installing:

1. Confirm `plan/unity/bridge-health.json` reports a fresh heartbeat with `processorActive: true`, even while the Bridge window is closed.
2. Open **Tools > Alepou > Unity Bridge > Open Bridge Window** and confirm the bridge output path is `<UnityProject>/plan/unity`.
3. Click **Export Snapshot**.
4. Confirm these files exist:
   - `plan/unity/status.md`
   - `plan/unity/capabilities.json`
   - `plan/unity/compile-status.json`
   - `plan/unity/command-results.md`
   - `plan/unity/recipes/prototype-game-loop.md`
5. Create a command file under `plan/unity/commands/pending/smoke-snapshot.json`:

```json
{
  "schemaVersion": 1,
  "commandId": "cmd-smoke-snapshot",
  "title": "Smoke test snapshot",
  "actions": [
    { "action": "export_snapshot" }
  ]
}
```

6. The default observation policy should claim and apply this command without the Bridge window being open.
7. Confirm the command briefly moves through `commands/processing/`, then lands in `commands/applied/`, and `command-results.md` includes `cmd-smoke-snapshot`.

## MVP Commands

Command files go in:

```text
plan/unity/commands/pending/
```

Supported action names in this MVP:

- `export_snapshot`
- `capture_view`
- `frame_ui_builder_document`
- `open_ui_builder`
- `close_ui_builder`
- `refresh_assets`
- `save_scene`
- `select_object`
- `ping_asset`
- `create_gameobject`
- `destroy_gameobject`
- `set_parent`
- `set_transform`
- `add_component`
- `remove_component`
- `assign_reference`
- `set_property`
- `create_folder`
- `create_scriptable_object_asset`
- `set_asset_property`
- `assign_asset_reference`
- `move_asset`
- `rename_asset`
- `delete_asset`
- `set_player_setting`
- `set_build_scenes`
- `create_prefab`
- `instantiate_prefab`
- `edit_prefab`
- `create_material`
- `set_material_property`
- `create_shader_asset`
- `create_animation_clip`
- `create_light`
- `bake_lighting`
- `clear_baked_lighting`
- `batch_edit_objects`
- `execute_menu_item`

The background service watches pending commands whenever the Unity Editor is open. It atomically claims each command into `commands/processing/` before execution and never replays a command left there by a Unity/domain reload. A repeated `commandId` is recorded as failed rather than executed again.

The optional Bridge window shows pending commands. Approving a command applies it and writes a result file under `commands/applied/` or `commands/failed/`; rejecting it writes a result under `commands/rejected/`.

## Pull-only View Capture

`capture_view` accepts `scene`, `game`, `both`, or `ui-builder` as its `target`. Scene and Game behavior is unchanged. UI Builder capture is observational and does not open, resize, edit, or save the window:

```json
{
  "schemaVersion": 1,
  "commandId": "cmd-capture-ui-builder-001",
  "title": "Capture the live UI Builder workspace",
  "actions": [
    { "action": "capture_view", "target": "ui-builder" }
  ]
}
```

Open **Window > UI Toolkit > UI Builder** and load the intended UXML document before submitting the command. Unity's editor-window capture helper only accepts the focused window, so the Bridge briefly focuses UI Builder for the capture and immediately restores the previously focused Unity window. The tab may flash to the foreground for one frame. The Bridge always captures the complete editor window into a native-size render texture first, then downsizes that complete frame only when its long edge exceeds 1920 pixels. This prevents a smaller output texture from being interpreted by Unity as a lower-left crop. `ui-builder-view.json` records point and native pixel dimensions, the final output dimensions, whether post-capture downscaling occurred, API availability, and focus handoff. If UI Builder is closed or the Unity version lacks the helper, the command fails explicitly and still writes bounded failure metadata. Game-view and headset evidence remain the acceptance views for the running UI.

For unattended evidence, use four bounded steps: submit `open_ui_builder` with the optional exact UXML path, wait for its terminal result and at least one editor update, submit `frame_ui_builder_document`, wait for its terminal result and one editor update, submit `capture_view` with target `ui-builder`, then submit `close_ui_builder`:

```json
{
  "action": "open_ui_builder",
  "assetPath": "Assets/UI/PalmMenu.uxml"
}
```

```json
{
  "action": "frame_ui_builder_document"
}
```

The path must identify an imported `VisualTreeAsset` under `Assets/` and end in `.uxml`; absolute paths, traversal, folders, missing assets, and other asset types are rejected. Framing feature-detects Unity's installed `BuilderViewport.FitViewport(VisualElement)` path and targets the largest rendered element inside the loaded `documentRootElement`, which handles fixed-width content that can overflow UI Builder's narrower root wrapper. It changes only transient editor zoom/pan, requires a loaded UXML document, and fails explicitly on unsupported Unity versions. Open, frame, and capture remain separate commands so UI Toolkit can repaint before evidence capture. The Bridge detects and rejects a blank capture instead of publishing false evidence. These are exact typed window/asset actions, not arbitrary editor menu access. With no `assetPath`, opening preserves the original focus-or-create behavior. Document switches and closing both refuse while UI Builder reports unsaved changes, so the Bridge never creates or answers Unity's blocking save prompt. Closing remains idempotent when already closed. Framing and capture are bounded observations that run without a grant; Reversible Workspace, Trusted Development, or an exact Custom grant can authorize the open/close window actions. Adding the capability never widens an already-created grant.

## Delegated Automatic Approval

Automatic write authority is a human-created, project-local grant; the AI cannot approve or widen its own permissions.

- **Manual writes** is the default. Only bounded observation commands (`export_snapshot`, `capture_view`, `frame_ui_builder_document`, `query_assets`, `read_object`, `read_asset`) run automatically.
- **Reversible Workspace** is an explicit time-limited grant for the bounded editor/scene/asset actions shown in the Unity window. It deliberately excludes `refresh_assets`/compilation.
- **Trusted Development** is the recommended durable, session-bound opt-in for an active AI coding session. It adds refresh/import/compile, focused Unity test runs, persisted prefab/material editing, and bounded workflow-owned Play Mode entry/capture/exit to the reversible authoring actions. It stays active until explicitly revoked or rebound and has no arbitrary expiry or command/action counter.
- **Custom Expert Grant** lets the owner select exact delegatable actions, expiry, command/action budgets, and an optional Alepou session binding.

The authoritative grant is stored in Unity `EditorPrefs`, keyed to the canonical project identity—not under the model-writable `plan/unity/` folder. A delegated command includes the active opaque `grantId` and, when configured, the matching `sessionId`:

```json
{
  "schemaVersion": 1,
  "commandId": "cmd-wire-player",
  "grantId": "copy-from-bridge-health",
  "sessionId": "only-when-the-grant-is-session-bound",
  "actions": [
    { "action": "create_gameobject", "path": "Player" },
    { "action": "set_transform", "objectPath": "Player", "localScale": { "x": 1, "y": 1, "z": 1 } }
  ]
}
```

Unity verifies the project, grant id, session binding, and exact action set after atomically claiming the command. Reversible Workspace and Custom Expert additionally enforce their selected expiry and budgets; Trusted Development remains durable. Every result records the approval rule, grant id, and frozen request hash; bounded decisions are also written to `approval-audit.jsonl`.

`refresh_assets` can import files, compile and execute project Editor code with the user’s Unity privileges, and reload the Unity domain. It therefore requires a human approval, a matching Trusted Development grant, or an exact Custom Expert grant. Trusted Development also includes `run_tests`, `edit_prefab`, `set_material_property`, `enter_play_mode`, and `exit_play_mode` because these are part of the normal trusted edit/compile/test/inspect loop. Play Mode automation is accepted only from a durable workflow carrying the exact active Trusted Development grant and matching Alepou session; Reversible Workspace and Custom Expert grants cannot authorize it. Builds, fixed-template shader import, lighting bake, and allowlisted menu execution remain separately selected Custom Expert capabilities. Destructive operations, PlayerSettings/build configuration, baked-light clearing, and device actions remain human-only.

Trusted Development cannot be enabled without an Alepou session binding. The Bridge reads Alepou's loopback-only discovery record, authenticates with its rotating local integration token, matches the canonical Unity project root, and lists only live sessions for that project. Select a session and click **Bind + Trust This Alepou Session**; no copied session ID is required. A manual ID field remains as a fallback when discovery is unavailable. The AI still cannot create, switch, or revoke the trusted mode itself.

Use **Revoke Grant** in the window to return to manual writes. **Tools > Alepou > Unity Bridge > STOP Automatic Processing** is available even when the Bridge window is closed; it revokes the grant and stops observation claims too.

## Durable Workflows

Use a workflow when later work depends on an asynchronous child command, asset import, script compilation, a clean-compile assertion, or a human checkpoint. The Bridge persists the active step under `workflows/running/` and resumes it after an assembly/domain reload, so the model does not need to spend turns polling Unity.

The complete v1 contract and example are generated at `plan/unity/recipes/workflow-schema.md`. In short:

1. Put each normal child command under `plan/unity/workflows/inputs/`.
2. Put a workflow manifest under `plan/unity/workflows/pending/`.
3. The runner atomically claims the manifest, freezes referenced child inputs, and submits one deterministic child command at a time through the normal approval policy.
4. Read bounded progress from `workflow-status.json`.
5. Read final evidence under `workflows/completed/`, `workflows/failed/`, or `workflows/cancelled/`.

Supported v1 steps are `command`, `wait_editor_idle`, `wait_compile_idle`, `require_compile_clean`, `run_tests`, `build_player`, `enter_play_mode`, `runtime_capture`, `exit_play_mode`, `human_gate`, and `checkpoint`. Each workflow and step has a bounded timeout. Write any JSON object to `workflows/cancel/<workflowId>.json`, or use **Cancel Active Workflow** in the Unity window, to cancel it.

Quest device actions remain separate server-owned capabilities; they are never silently treated as generic Unity commands or authorized by a build grant.

## Two-way Alepou Liveness

Bridge 0.18.0 echoes a command or workflow manifest's optional `sessionId` and `wakeOnTerminal` intent into its immutable terminal result. The Alepou agent watches those result contracts independently of any open Manager page, persists a bounded deduplication cursor outside the Unity project, and creates one project notification for each new applied, failed, rejected, completed, cancelled, or waiting-at-human-gate event.

Use `sessionId` for ownership and notification routing. Add `"wakeOnTerminal": true` only when the current Planner turn will finish waiting for that exact command/workflow result and genuinely needs a new continuation turn. If either field is absent, Alepou still records the project notification but deliberately does not guess or start an AI turn.

A workflow runner stamps its frozen child commands with the parent `workflowId`. Those intermediate command results remain normal audit evidence but never wake Alepou. Only the parent terminal result is eligible, and only with explicit `wakeOnTerminal` intent; human gates remain notification-only.

Cancelled/rejected work and human gates are notification-only even if a malformed request asks for a wake. When the owning Planner or its Structured Codex provider already has an active turn, the notification is retained but the wake is retired rather than replayed into later work. Multiple terminal events therefore coalesce naturally: the first eligible event may start an idle continuation; later events arriving during it remain notifications.

A wake-up is only an attention signal. The AI must read the exact result path named by the notification before claiming success. A human-gate notification also does not approve the Unity action. Entering or leaving Play Mode proceeds automatically only with the workflow's matching session-bound Trusted Development grant; otherwise the transition remains a direct Unity-window gate.

## Structured Test Runs

`run_tests` uses the Unity Test Framework API and can run EditMode or PlayMode tests with assembly, namespace, category, fixture, and exact individual-test filters. Filter families compose with AND semantics. A filter that matches no tests fails rather than reporting a false green.

Before a test run starts, the Bridge checks the active scene itself so Unity Test Framework cannot raise a blocking save prompt. A dirty scene with an asset path is saved automatically. A dirty untitled scene fails fast with an instruction to save it first. The original active scene path is persisted with the durable test state and restored before the workflow advances; a failed restoration fails visibly.

Tests execute project code and are not part of Reversible Workspace. Trusted Development automatically approves only filtered runs containing at least one assembly, namespace, category, fixture, or exact-test filter. An unfiltered whole-suite run requires direct approval of the waiting workflow step or a Custom Expert grant that explicitly includes `run_tests`. The local grant still applies its project identity, session boundary when present, expiry, command budget, and action budget.

Read `plan/unity/recipes/structured-test-runs.md` for the request schema. Live bounded progress is written to `test-run-status.json`. Terminal results land under `test-runs/completed/`, `test-runs/failed/`, or `test-runs/cancelled/` with counts, duration, and capped failure summaries. Each result points to the full NUnit XML and a per-test JSONL evidence log under `test-runs/logs/`.

The parent workflow advances only after a clean pass. Failure, cancellation, timeout, or a zero-test match stops the workflow. Cancelling the workflow first requests cancellation from the Unity Test Framework.

## Structured Build Jobs

`build_player` runs a validated Unity `BuildPipeline` job as a workflow barrier. It requires import/compile idle, no captured compiler errors, at least one valid scene, installed support for the requested target, a safe option allowlist, and—when requested—a prior clean structured test result.

Builds execute project build hooks and can consume substantial CPU, time, and disk space. They require direct approval of the waiting step or a Custom Expert grant explicitly containing `build_player`; the Reversible Workspace preset never includes builds. Build output is confined to a collision-free path under `Builds/Alepou/`. Options that launch the player, reveal output, patch an existing application, or create an external project are blocked.

Read `plan/unity/recipes/structured-build-jobs.md` for the request schema. Live state is written to `build-job-status.json`; terminal results and the full report message log land under `build-jobs/`. Every successful build writes a versioned `alepou.unity-build-artifact/v1` manifest containing the exact artifact path, file/directory/standalone-bundle kind, size, SHA-256, target, scene count, options, duration, warnings, and errors.

The artifact manifest is a handoff contract, not device authority. A build grant never authorizes headset discovery, APK installation, application launch, device logs, captures, or performance tracing. A deployment adapter must receive a separately granted action, an exact device identity, and an exact artifact manifest.

## Meta Quest Device Adapter

Alepou's optional server-owned adapter can hand a verified Android artifact to Meta's separately installed `@meta-quest/metavr` toolchain. Enable **Meta Quest Tools** in the Alepou project settings, install the exact tested metavr package yourself after reviewing Meta's SDK terms, run `metavr --version` once to provision its verified platform binary, then open **Project tools > Quest > Device adapter**.

For one shared Windows installation:

```text
npm install --global @meta-quest/metavr@1.3.2
metavr --version
```

A project-local development dependency at the same exact version is also accepted. Alepou never runs `npx`, installs or updates metavr, or downloads its platform binary silently. Meta's optional Codex/Claude plugin adds Quest-specific knowledge and skills to newly started provider sessions; it is not required by the audited device adapter and does not inherit or widen Bridge/device authority.

The adapter supports only device discovery/info, installation of an exact `alepou.unity-build-artifact/v1` APK, launch/stop of the Unity project's exact Android application ID, bounded logs, screenshots, and bounded performance traces. It does not expose uninstall, clear-data, reboot, raw shell/ADB, arbitrary device files, or delete operations.

Manual approval is the default. A separate **delegated automatic approval** grant may name exact actions, one device ID, the Unity application ID, an optional Alepou session, an expiry, and a job budget. If installation is delegated, the grant also freezes the selected build manifest and its APK SHA-256, so a rebuilt or substituted package needs a new grant. This Alepou-owned grant is independent of Unity's EditorPrefs grant: neither grant can create, widen, or imply the other.

Audited requests use `plan/meta-quest/device-jobs/pending/` and terminal evidence lands under `completed/`, `failed/`, or `rejected/`. Requests without matching authority move to `waiting/` and produce an Alepou notification. Every terminal result records the authorization rule, pinned tool version, bounded output, and local evidence paths; an install result also freezes the artifact manifest path, size, and SHA-256.

## Play Mode and Runtime Capture

`enter_play_mode` and `exit_play_mode` are durable state-transition barriers. They run automatically only when the workflow carries the active Trusted Development grant id and its exact Alepou session binding; otherwise each transition requires a direct human click in the Unity Bridge window. Reversible Workspace and Custom Expert grants cannot authorize Play Mode. Entry advances only after Unity reports `EnteredPlayMode`; exit advances only after `EnteredEditMode`, so a submitted request is never reported as completed work.

`runtime_capture` is bounded observation while Unity is stably playing. It writes one Game-view image capped to a 1280-pixel long edge, 1-100 Play Mode Console entries since entry or the previous capture, and at most 16 explicitly named object/component probes. Conditions, stack traces, component counts, and serialized properties all have fixed caps. Missing cameras, objects, component types, or requested property paths fail the barrier rather than returning a false green.

Evidence lands under `runtime-captures/<workflowId>/<captureId>/` with `game-view.png` and `runtime-capture.json`; the latest result is also copied to `runtime-capture-status.json`. Read `plan/unity/recipes/runtime-play-mode.md` for the exact workflow schema.

When the workflow owns the runtime session, cancellation, rejection, failure, or timeout requests Edit Mode before the terminal workflow result is written. The Unity window shows **STOP Play Mode + Cancel Workflow**, and **Tools > Alepou > Unity Bridge > STOP Automatic Processing** remains available even when the window is closed.

## Deep Authoring

The bridge exposes typed prefab creation/instantiation/editing, material creation and typed property changes, two fixed shader templates, bounded animation clip curves, light creation and lighting bake/clear actions, and multi-object edits capped at 100 explicit objects. Read `plan/unity/recipes/deep-authoring.md` for the exact schemas.

These commands deliberately do not expose arbitrary reflection or source execution. Prefab edits can change transforms and one serialized field. Material values are limited to float, int, color, vector, or texture. Shader generation accepts only the bridge-owned `unlit-color` and `unlit-texture` templates. Animation clips are capped at 100 curves and 200 keys per curve.

Non-zero transform/color values are detected automatically. Intentional all-zero vectors or black/transparent colors require the matching presence flag (`setPosition`, `setRotationEuler`, `setLocalPosition`, `setLocalRotationEuler`, `setLocalScale`, or `setColor`) so Unity’s default nested-object deserialization can never silently zero omitted values.

`execute_menu_item` is a narrow capability, not a pass-through. Its complete allowlist is `Assets/Refresh`, `File/Save`, and `File/Save Project`; every other path fails closed. `bake_lighting` is a potentially long Custom Expert action. `clear_baked_lighting` always remains human-only.

To intentionally create a disabled light, provide `setIntensity: true` with `intensity: 0`; an omitted intensity defaults to 1.

Commands may contain an ordered `actions` array. Unity executes actions sequentially in one Undo group, stops on the first failure, writes a result report, and exports a fresh snapshot after successful application.

Prefer one complete approval batch per phase. After a compile checkpoint is clean, queue the independent asset, scene, project-setting, build-scene, save, and snapshot actions together instead of asking the user to approve one administrative command at a time.

`refresh_assets` calls Unity asset refresh so newly written scripts are imported and compilation can begin. Use it as a separate checkpoint before attaching newly generated MonoBehaviours.

Script files are still ordinary project files: an AI CLI can write `Assets/**/*.cs` directly through the filesystem. The Unity approval step is for importing/compiling those files through Unity and for writing an auditable bridge result/snapshot. Do not treat a script as usable in Unity until a `refresh_assets` command has been approved and `compile-status.json` is clean.

Asset commands are intentionally narrow. `create_scriptable_object_asset` can create a compiled `ScriptableObject` type under `Assets/**/*.asset`; `set_asset_property` can set simple scalar fields and string/int/float/bool/enum arrays by passing `values`; `assign_asset_reference` wires an asset into a serialized object reference field on a scene component.

Project maintenance commands are also narrow. `set_player_setting` supports whitelisted settings (`productName`, `companyName`, `bundleVersion`, and `applicationIdentifier` with an optional `buildTargetGroup`). `set_build_scenes` replaces EditorBuildSettings from `paths`, `scenes`, or `values`. Destructive asset folder operations require `allowFolder: true`.

Example:

```json
{
  "schemaVersion": 1,
  "commandId": "cmd-create-game-root",
  "title": "Create and wire GameRoot",
  "actions": [
    { "action": "create_gameobject", "path": "GameRoot/Managers" },
    {
      "action": "set_transform",
      "objectPath": "GameRoot/Managers",
      "localPosition": { "x": 0, "y": 0, "z": 0 },
      "localRotationEuler": { "x": 0, "y": 0, "z": 0 },
      "localScale": { "x": 1, "y": 1, "z": 1 }
    },
    { "action": "add_component", "objectPath": "GameRoot/Managers", "component": "LevelManager" },
    {
      "action": "assign_reference",
      "objectPath": "GameRoot/Managers",
      "component": "LevelManager",
      "field": "player",
      "target": "Player",
      "targetType": "GameObject"
    },
    { "action": "export_snapshot" }
  ]
}
```

ScriptableObject asset example:

```json
{
  "schemaVersion": 1,
  "commandId": "cmd-create-level-asset",
  "title": "Create level data asset",
  "actions": [
    { "action": "create_folder", "path": "Assets/Data/Levels" },
    { "action": "create_scriptable_object_asset", "assetPath": "Assets/Data/Levels/Level01.asset", "type": "LodeRunnerLevelAsset" },
    { "action": "set_asset_property", "assetPath": "Assets/Data/Levels/Level01.asset", "field": "levelName", "value": "Level 01" },
    { "action": "set_asset_property", "assetPath": "Assets/Data/Levels/Level01.asset", "field": "rows", "values": ["########################################", "#                                      #"] },
    { "action": "assign_asset_reference", "objectPath": "GameRoot/Managers", "component": "LevelManager", "field": "levelAsset", "assetPath": "Assets/Data/Levels/Level01.asset" },
    { "action": "export_snapshot" }
  ]
}
```

Maintenance batch example:

```json
{
  "schemaVersion": 1,
  "commandId": "cmd-project-maintenance",
  "title": "Save scene and update project settings",
  "actions": [
    { "action": "set_player_setting", "setting": "productName", "value": "LodeRunnerVR" },
    { "action": "set_build_scenes", "paths": ["Assets/Scenes/LodeRunnerGameplay.unity"] },
    { "action": "save_scene" },
    { "action": "export_snapshot" }
  ]
}
```

## Prototype Recipe Loop

For full prototype creation, use the generated recipe files under:

```text
plan/unity/recipes/
```

The standard loop is:

1. Read `status.md`, `capabilities.json`, `compile-status.json`, `command-results.md`, and targeted scene/inspector files.
2. Write C# scripts directly into `Assets/`.
3. Submit a small approved command batch with `refresh_assets` and `export_snapshot`.
4. After approval, re-read `compile-status.json` until Unity is not compiling or updating.
5. Fix compiler/console errors before scene wiring.
6. Submit a complete ordered batch for all independent asset, scene, setting, build-scene, save, and snapshot actions needed for the phase.
7. Inspect `command-results.md` plus the latest result JSON before claiming the Unity-side work applied.

## Auto Refresh

The always-running service supports:

- export on selection changes
- export on play mode changes when auto export or interval export is enabled
- interval export: off, 5, 15, 30, or 60 seconds

## Processor Health

`bridge-health.json` is updated every two seconds by Unity's editor update loop. `bridge-watchdog.json` is updated every two seconds by an independent .NET timer using cached state and ordinary file I/O only; its timer callback never calls Unity APIs. Alepou treats the bridge as connected only when the processor is expected to be active, liveness is fresh, and the editor loop is responsive. Old snapshot files remain useful evidence, but they no longer make a stopped editor look connected.

If a modal dialog or another blocking editor operation stops the main update loop for ten seconds, the watchdog remains fresh and reports `editor-loop-stalled` with the last editor-loop tick and a probable cause. Commands and workflows cannot progress until the main loop resumes; a human at the Unity machine must dismiss the modal or otherwise unblock the Editor.

The file also distinguishes an idle processor from one waiting for compilation/import or human approval, records whether the optional Bridge window is open, and reports pending, processing, active-command, active-workflow/step, approval-profile, grant-expiry, session-binding, and remaining-budget state.

## Safety

The package does not accept arbitrary C# inside command files. Observation commands can run automatically while Unity is idle. Selection, refresh/import, scene, asset, and test work require an active matching grant or a human click. Trusted Development deliberately permits project compilation/test execution, routine persisted authoring, and workflow-owned Play Mode inspection for one named Alepou session; builds, shader import, lighting bake, and the fixed menu allowlist still require exact Custom Expert capabilities. Destructive operations, general project settings, baked-light clearing, and device operations cannot be smuggled into a broad wildcard grant.

Delegation is an operational safety and audit boundary, not an OS sandbox against malicious Unity project code. Compiled Unity Editor code already runs with the user’s privileges and can access local APIs/files. Only delegate compilation or mutation work to projects and agents you trust; use Alepou/CLI sandboxing and version control as separate defenses.
