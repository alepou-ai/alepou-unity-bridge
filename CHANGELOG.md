# Changelog

All notable changes to Alepou Unity Bridge are documented here.

## 0.18.0 - 2026-08-30

- Add pull-only `capture_view` support for the live UI Builder editor window.
- Capture UI Builder through Unity's own editor-window render path at the full
  native window size, then downscale the complete frame to a 1920-pixel long-edge
  cap. This avoids Unity treating the output dimensions as a lower-left crop.
- Record the resolved window, point/native-pixel/output dimensions, post-capture
  downscale state, capture API, and bounded focus handoff in
  `ui-builder-view.json` beside `ui-builder-view.png`.
- Report closed-window and unsupported-Unity failures explicitly while preserving
  existing Scene/Game capture behavior.
- Add delegated `open_ui_builder` and `close_ui_builder` actions so an audited
  command can open, capture, and close the workspace without arbitrary menu access.
- Refuse to close UI Builder when it reports unsaved changes, preventing an
  automation-blocking Unity save prompt.
- Reject blank editor-window frames and require a post-open editor update before
  capture, preventing a newly opened UI Builder from being reported as evidence
  before Unity has painted it.
- Allow `open_ui_builder` to load an exact `Assets/.../*.uxml` VisualTreeAsset
  through Unity's public asset-opening path, with bounded path/type validation
  and refusal to switch documents while UI Builder has unsaved changes.
- Add auto-approved `frame_ui_builder_document` using UI Builder's own
  `FitViewport(VisualElement)` path targeted at the largest rendered element in
  the loaded document, so fixed-width content that overflows UI Builder's narrower
  root wrapper or artboard can be framed without modifying UXML/USS.

## 0.17.0 - 2026-08-21

- Discover the local Alepou agent through its authenticated loopback rendezvous.
- Match the canonical Unity project and list only its live Alepou sessions.
- Add a refreshable project/session picker and deliberate **Bind + Trust This
  Alepou Session** action while preserving the manual session-ID fallback.
- Make Trusted Development durable until explicitly revoked or rebound, without
  arbitrary command/action counters.
- Restore the menu route to the Bridge window: the opener moved to **Tools >
  Alepou > Unity Bridge > Open Bridge Window** so it is no longer swallowed by
  the emergency-stop submenu sharing its path.
- Dock the Bridge window beside the Inspector on first open instead of leaving
  it floating.
- Open a real Bridge window from the menu instead of silently focusing the
  service's hidden headless executor instance, which never appeared on screen.

## 0.16.0 - 2026-08-13

- Add an independent watchdog that distinguishes a stopped Unity process from a
  live process whose Editor main loop is blocked by a modal operation.
- Make Unity test runs save named dirty scenes, reject dirty untitled scenes
  without opening a modal, and restore the original scene before workflows
  advance.
- Allow matching session-bound Trusted Development workflows to enter, inspect,
  and exit Play Mode without repeated approval clicks.
- Fix the EditorApplication namespace regression found during live 0.16 testing.

## 0.15.0 - 2026-08-02

- Keep the command processor active whenever the Unity project is open, independently of the Bridge Editor window.
- Export a fresh processor heartbeat and bounded compile, workflow, test, build, approval, and session state.
- Add human-created Manual, Reversible Workspace, Trusted Development, and Custom Expert authority profiles.
- Add durable workflow barriers for import, compilation, focused tests, builds, Play Mode transitions, checkpoints, and runtime capture.
- Add structured EditMode and PlayMode test filtering with NUnit XML and per-test JSONL evidence.
- Add validated build jobs with collision-free output and SHA-256 artifact manifests.
- Add typed prefab, material, animation, lighting, scene, asset, and multi-object authoring commands.
- Add intent-aware Alepou result notifications that do not interrupt an already active owning session.
- Document the optional, separately authorized Alepou Meta Quest device adapter.

This is the first release from the dedicated package-root repository. Earlier Bridge development history is preserved in this repository.
