# Changelog

All notable changes to this package will be documented in this file.

## [2.37.0] - 2026-07-19

### Changed (context efficiency)
- **`scene/hierarchy` is dense by default** — per-node fields carrying their default value are omitted, and an absent field means the default: `active` (true), `tag` (Untagged), `layer` (Default), `position` (origin; Unity's approximate `Vector3 ==` treats float noise as origin), the universal `Transform` component entry, and `childCount` when a complete `children` array already implies it. Non-default information is always emitted. Measured **43% smaller** on a representative 221-node hierarchy (42.3KB → 24.4KB); scenes with many empty organizer objects cut more. **Behavior change:** consumers that require the old always-present per-node shape pass `verbose:true` (the server companion 2.35.0 declares the parameter).

## [2.36.0] - 2026-07-18

### Added (per-action / per-agent Undo)
- **`undo/last` — revert the most recent undoable MCP action as a whole.** Each write action now reverts cleanly as one step (a whole create/edit/boolean, not one internal operation). With `agentId`, targets that agent's most recent action. Honest about Unity's LINEAR undo: if newer actions are stacked on top of the target, `undo/last` refuses to cascade and lists exactly which actions would also be reverted unless `force:true` is passed.
- **`undo/history` is now a real per-agent action log** — returns recent MCP actions (newest first, optional `agentId`/`count` filters) with per-agent attribution, target object, an `undoable` flag, and the current Unity undo group — instead of only the current group name.

### Changed (multi-agent queue — each action is now independently revertable)
- **The queue wraps every WRITE action in its own named, collapsed Undo group** (`MCPRequestQueue`). Previously the recorded undo group used a `GetCurrentGroup()` before/after diff that missed most real edits (e.g. `RegisterCreatedObjectUndo` doesn't advance the group), so per-action undo was effectively unavailable. Now each agent's action is a clean, named entry in Unity's Undo history and a precise revert target for `undo/last`.
- **Undoability is detected from the actual undo stack**, not guessed: an action is only marked undoable if it genuinely registered an undo op (measured via the internal `Undo.GetRecords`), so reads that slip past the read classifier — and `execute-code`, whose temp-host churn registers incidental undo — never become misleading `undo/last` targets. Reads, undo/redo ops, empty groups and failures stay non-undoable. Fails open if the internal API is ever unavailable.

## [2.35.0] - 2026-07-18

### Added (ProBuilder integration — `com.unity.probuilder`)
- **Full ProBuilder command surface** (`MCPProBuilderCommands`, 14 handlers under the `probuilder/*` routes) — create parametric shapes and edit real, still-editable ProBuilder meshes through ProBuilder's public API:
  - `create-shape` — cube, plane, cylinder, prism, stair, cone, door, pipe, arch, sphere (icosphere), torus, with per-shape parameters (sides, steps, thickness, angle, subdivisions, …).
  - Geometry edits — `extrude-faces` (face/individual/vertex-normal), `bevel-edges`, `subdivide`, `delete-faces`, `translate-faces`, `flip-normals`.
  - Materials — `set-face-material` (per-face submesh assignment).
  - Boolean CSG — `boolean` (union / subtract / intersect); the result is a new editable ProBuilder object.
  - `combine` (merge ProBuilder meshes), `probuilderize` (convert a plain mesh to ProBuilder), `center-pivot`, `export-mesh` (bake to a `.asset`, guarded by the shared overwrite check).
- **Tolerated-when-missing integration** — the whole surface is gated on `PROBUILDER_INSTALLED` (asmdef `versionDefines` on `com.unity.probuilder >= 4.0.0`); when ProBuilder is absent every handler returns a clear "not installed" message instead of failing to compile, matching the existing UMA pattern. New `probuilder` capability category in `MCPSettingsManager`.
- Every mutation registers Undo (`Undo.RegisterCompleteObjectUndo` / `RegisterCreatedObjectUndo`), so ProBuilder edits compose with the multi-agent queue's per-action undo tracking. `export-mesh` uses `outputPath` (distinct from the `path`/`instanceId` used to resolve the source object) and confines writes under `Assets/` via `MCPAssetSafety`.

## [2.34.0] - 2026-07-18

### Fixed (data safety — several handlers could silently destroy user assets)
- **`script/create` and `script/update` no longer write outside the project or destroy source files** — the project root was computed with `dataPath.Replace("/Assets","")`, which strips **every** `/Assets` occurrence, so a project under a path containing `/Assets` resolved to the wrong root and writes landed outside the project (the import then silently no-oped). Root is now `Path.GetDirectoryName(Application.dataPath)`. `update` with missing **or empty** content used to truncate the target script to zero bytes — empty content is now rejected. `create` no longer silently overwrites an existing script. All paths are canonicalized and confined under `Assets/`/`Packages/` (traversal and absolute-path escapes rejected).
- **Asset creators no longer silently overwrite existing assets** — `AssetDatabase.CreateAsset` on an existing path destroys the asset and every reference to it (the GUID is reused). ScriptableObject, Terrain (`create`), Animator Controller, Animation Clip, Material, and `asset/import` now refuse to replace an existing asset unless `overwrite: true` is passed. The overwrite check tests the canonical on-disk path **and** the AssetDatabase, so a non-canonical spelling (`Assets//X`, `Assets/./X`) or a not-yet-imported file can't slip past it. New shared `MCPAssetSafety` helper centralizes root resolution, path confinement, and the overwrite guard.
- **Queue double-execution** — a request whose 30s sync waiter already gave up (`TimedOut`) stayed in the queue and still executed later, so a client retry ran a non-idempotent action (create, package add, import) twice. Timed-out tickets are now dropped before execution (race-safe under the queue lock).
- **`component/set-property` reported success without applying the value** — Color/Vector2/3/4/Rect branches silently did nothing when the value wasn't an object (e.g. an agent passing `"1,0,0,1"` or an array); they now return a clear error.
- **`asmdef` remove-references removed the wrong reference** — a substring `Contains` match removed `Unity.InputSystem.ForUI` when asked to remove `Unity.InputSystem` (breaking compilation while reporting success); now an exact name/GUID match, and a no-match is reported rather than churning a recompile.

### Fixed (ShaderGraph — all four asset-corruption bugs in [#18](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/issues/18))
- ShaderGraph create/add-node/connect/disconnect/remove-node now go through ShaderGraph's real `GraphData` model (new `MCPShaderGraphApi` reflection wrapper over `GraphData` / `MultiJson` / `FileUtilities`) instead of regex/JSON string surgery, so the serialized `.shadergraph` is **always** a valid, importable asset:
  - `create` built an identical invalid 809-byte graph for every template (no target, dangling `m_OutputNode`, failed import); it now builds a real URP Lit/Unlit graph with its default surface/vertex blocks (or a valid target-less graph for `blank`), and refuses to silently downgrade an explicit URP template to blank when URP isn't installed.
  - `add-node` produced empty-slot nodes that broke import and dropped the requested position; nodes now get their real slots and the position is honored (`positionX`/`positionY` or `x`/`y`).
  - `disconnect` matched by output slot only (dropping sibling edges) and blanked surviving multi-line edges to `m_Id:""`; it now removes exactly the tuple-matched edges, survivors untouched.
  - `remove-node` had the same edge-blanking corruption; it now uses `GraphData.RemoveNode`.
  - `connect` validates slot compatibility and refuses cycles instead of blindly inserting edge JSON.
  - Fail-closed: if the ShaderGraph API can't be resolved (version drift), these handlers return a clear error instead of corrupting anything.

### Changed
- **`overwrite: true` opt-in** on the asset-creator handlers is the escape hatch for the new overwrite guards (see the companion `unity-mcp-server` 2.32.0 schema additions). `editor/state` and `project/info` now report the project path via the corrected root helper.

## [2.33.0] - 2026-07-18

### Fixed
- **`/api/context` always returned HTTP 500** — `GetContextResponse` reads `EditorPrefs` (main-thread-only) but ran on the HTTP ThreadPool thread, so every Project Context call threw; both context routes now run through `ExecuteOnMainThread` like every other synchronous route. Root cause + fix per PR [#17](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/17) by @rcasaleiro.
- **`GetRegisteredRoutes` drift** — the hand-maintained route list had drifted to ~150 of 321 routes with several wrong names (e.g. `editor/undo` vs the real `undo/perform`), silently breaking the server's dynamic tool discovery. The list is now **generated from the dispatch switch** into `MCPBridgeServer.Routes.g.cs` by `tools~/generate-routes.mjs`; a CI workflow fails on drift.
- **`editor/execute-code` returned numbers as strings** — result serialization ToString'd every reflected property and list element (`42` became `"42"`, nested objects flattened to type names). New depth-capped recursive serializer preserves primitive types, recurses dicts/lists/anonymous objects (depth 4, 1000-item cap), and renders `UnityEngine.Object` graphs compactly instead of exploding their property trees.
- **Action history truncated 64-bit ids** — `MCPActionRecord.TargetInstanceId` was `int` but Unity 6.5 EntityIds are 64-bit opaque strings; the field is now a string end to end (record, persistence DTO, history window, select-target). Old persisted entries lose only this field on first load.
- **Error responses leaked exception stack traces to the wire** — traces now go to the editor log only.
- **macOS: ExecuteCode could not find Roslyn** — added `Contents/Resources/Scripting` to the assembly search paths, per PR [#19](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/19) by @JetNik.

### Added
- **Browser CSRF / DNS-rebinding guard** — the bridge can execute arbitrary editor code but accepted any local HTTP request; requests with a non-loopback `Host` or any non-loopback `Origin` (browser pages always attach one on cross-origin fetches) are now rejected with 403 before touching editor state. Local tools (no Origin header) are unaffected.
- **Capability handshake (plugin half)** — `/api/ping` advertises a monotonic `protocolVersion` (1) and `pluginVersion` (from PackageInfo); unknown routes return HTTP 404 on the legacy path so servers can distinguish "feature missing" from "call failed" and degrade gracefully across version drift. Pairs with `unity-mcp-server` 2.31.0. Re-implementation of PR [#20](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/20) by @D3vCrow.

## [2.32.0] - 2026-06-02

### Added
- **`screenshot/editor-window` command** — `MCPScreenshotCommands.CaptureEditorWindow` captures any EditorWindow (Inspector, Project, Console, custom windows) to a PNG via the Win32 `PrintWindow` API (`PW_RENDERFULLCONTENT`), occlusion-proof (the window renders itself offscreen — no raise or focus-steal). Docked windows are captured by PrintWindowing the main window and cropping the panel rect; floating windows by resolving their own HWND (exact title match) and capturing the whole window. Defaults to `Assets/Screenshots/`, honours any user-chosen `.png` path; bounds dimensions against `SystemInfo.maxTextureSize`, all GDI handles + the `Texture2D` released in `try/finally`. **Windows editor only** (`#if UNITY_EDITOR_WIN`) — returns a clear unsupported-platform error on macOS/Linux (no `PrintWindow` equivalent); use `screenshot/scene` / `screenshot/game` (camera-based) there. Companion to the `unity-mcp-server` 2.30.0 change.

### Changed
- **Welcome window reworked into a modular, themed system** — the single-file `Editor/MCPWelcomeWindow.cs` is replaced by `1-Scripts/Editor/WelcomeWindow/` (own assembly `UnityMCP.Editor.Welcome`, namespace `UnityMCP.Editor.Welcome`): a USS theme, Welcome + Studio tabs, auto-open on first load with per-project detection, a config-driven content seam (custom sections / buttons, cross-sell entries via `welcome.gen.json`), a devlog fetcher, and bundled icons.

## [2.31.2] - 2026-05-21

### Changed
- **Settings panel grouped into labelled sections** — the Dashboard's *Settings* foldout now has three bold sub-headers (**General**, **Port**, **Multiplayer Play Mode (MPPM)**) instead of an unlabelled flat list. The *Start on Virtual Players* toggle is now under the explicit **MPPM** header so its scope is clear, and it was moved below the Port settings. UI-only change, no behaviour difference.

## [2.31.1] - 2026-05-21

### Fixed
- **MPPM scenario commands now work on MPPM 2.0 (Unity 6)** — the 2.31.0 Unity 6 port resolved the scenario types under the wrong names. In MPPM 2.0 the scenario "config" ScriptableObject was renamed `OrchestratedScenario` (from `ScenarioConfig`) and the status struct `ScenarioStatusData` (from `ScenarioStatus`); `MCPScenarioCommands` now resolves both. `create_scenario` no longer requires the removed `RemoteInstanceDescription` type (remote instances were dropped in MPPM 2.0), and `list_scenarios` reads instance counts from `OrchestratedScenario`'s fields. All MPPM tools verified end-to-end on Unity 6000.5.0b8 + MPPM 2.0.2.

## [2.31.0] - 2026-05-21

### Added
- **MPPM Virtual Player management** — new commands `mppm/list-players`, `mppm/activate-player`, `mppm/deactivate-player` to list and activate/deactivate Multiplayer Play Mode virtual players by 1-based index.
- **`scenario/create`** — create an MPPM `ScenarioConfig` asset programmatically (one Main Editor instance + N Virtual Editor instances with configurable Host/Client/Server roles).

### Changed
- **MPPM scenario commands now work on Unity 6** — `MCPScenarioCommands` resolves the MPPM scenario types from both the legacy package assembly (`Unity.Multiplayer.PlayMode.Scenarios.Editor`, pre-Unity-6) and the built-in `UnityEditor.MultiplayerModule` introduced in Unity 6; previously all `mppm/*` commands returned "MPPM is not installed" on Unity 6. `scenario/start` / `scenario/stop` also enter/exit Play mode so virtual-player launch hooks fire.

## [2.30.0] - 2026-05-21

### Changed
- **MCP settings are now scoped per project / per instance** — `EditorPrefs` is global to the machine, so settings were previously shared across every Unity project and instance (e.g. one project's manual port leaked to all others). `MCPSettingsManager` now namespaces keys into two tiers: **instance-scoped** (`Port`, `UseManualPort`, `AutoStart` — keyed by project path, unique per main Editor / ParrelSync clone / MPPM virtual player) and **project-scoped** (`StartOnVirtualPlayers`, project context, action-history and category settings — keyed by `PlayerSettings.productGUID`, shared by a project and its clones / virtual players). Existing settings are migrated to the new keys automatically on first load.

## [2.29.1] - 2026-05-21

### Fixed
- **MPPM Virtual Player detection on Unity 6** — `MCPScenarioCommands.IsVirtualPlayer()` (the gate behind the 2.29.0 "Start on Virtual Players" setting) only resolved the pre-Unity-6 type `Unity.Multiplayer.Playmode.CurrentPlayer`. On Unity 6 that API moved to `Unity.Multiplayer.PlayMode.CurrentPlayer` in the built-in `UnityEngine.MultiplayerModule`, so detection always returned false and the gate never engaged. It now resolves both locations (Unity 6 first, pre-6 fallback). Verified live on Unity 6000.5 with MPPM.

## [2.29.0] - 2026-05-21

### Added
- **"Start on Virtual Players" setting** — new MCP settings toggle controlling whether the bridge auto-starts on Multiplayer Play Mode (MPPM) virtual players. Previously every virtual player launched its own MCP bridge, which is usually unwanted noise. Default is **on** (behaviour unchanged); turn it off so only the main Editor runs a bridge. Virtual players are detected via `Unity.Multiplayer.Playmode.CurrentPlayer.IsMainEditor`; manual start on a virtual player still works. Addresses [unity-mcp-server#21](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/21).

## [2.28.1] - 2026-05-21

### Fixed
- **Manual (fixed) port not reclaimed after a domain reload** — with a manual port configured, `MCPBridgeServer.Start()` bound the port directly and gave up permanently on the first failure. Right after a domain reload the port can be briefly unbindable while the previous listener's socket is released; auto-port mode already survived this (it probes and falls back) but manual mode had neither probe nor retry. `Start()` now retries the same manual port up to 10 times on a 0.5s delay before giving up. Addresses [unity-mcp-server#10](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/10).

## [2.28.0] - 2026-05-21

### Added
- **Unity 6.5 (6000.5) compatibility** — The plugin compiles and runs on Unity 6.5. The InstanceID APIs deprecated as compile errors in 6.5 (`Object.GetInstanceID`, `EditorUtility.InstanceIDToObject`, `SerializedProperty.objectReferenceInstanceIDValue`, `AssetPreview.IsLoadingAssetPreview(int)`) are now routed through a version-gated `MCPObjectId` shim — it uses `EntityId` with `EntityId.ToULong`/`FromULong` on 6.5 and the classic APIs on 2021.3–6.4. Fixes [#14](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/issues/14) and [unity-mcp-server#24](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/24).

### Changed
- **`instanceId` is now a string** — Unity 6.5 entity ids are 64-bit values that exceed JavaScript's safe-integer range (2^53), so as JSON numbers they were rounded crossing the Node MCP server and object-by-`instanceId` resolution failed. The JSON `instanceId` field is now a decimal string on every Unity version (opaque, lossless). Requires `unity-mcp-server` ≥ 2.28.3.

## [2.27.2] - 2026-05-21

### Fixed
- **Roslyn assemblies not found on macOS** — `MCPEditorCommands.TryLoadRoslyn()` assumed the Windows/Linux `Data/` editor layout; on macOS the assemblies live inside `Unity.app/Contents/`, so `unity_execute_code` always failed with "Roslyn is not available". The lookup now detects the `.app` bundle and adds `Unity.app/Contents` as a data root, plus `Tools/ScriptUpdater`. Contributed by [@dougfy](https://github.com/dougfy) in [#13](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/13).

## [2.27.1] - 2026-05-21

### Fixed
- **UPM install compile failure (`CS0103` cascade)** — `MCPPrefsCommands`, `MCPConstraintCommands` and `MCPProfilerCommands` shipped `.cs.meta` files with hand-typed placeholder GUIDs. Under a UPM git install (`Library/PackageCache/`), Unity 6 silently skipped indexing those scripts, cascading into `CS0103` errors. The three GUIDs were regenerated with proper random values. Fixes [#11](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/issues/11). Contributed by [@BadranRaza](https://github.com/BadranRaza) in [#12](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/12).

## [2.27.0] - 2026-04-22

### Fixed
- **Path-based lookup for inactive GameObjects** — `MCPGameObjectCommands.FindGameObject` now passes `FindObjectsInactive.Include` to `FindObjectsByType<GameObject>`. Every tool routed through path-based lookup (`prefab_info`, `set_active`, `info`, `delete`, `set_transform`, `reparent`, etc.) now works correctly on inactive GameObjects, whereas they previously failed with "GameObject not found". Fixes [unity-mcp-server#16](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/16). Contributed by [@BadranRaza](https://github.com/BadranRaza) in [#8](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/8).
- **Prefab-instance detection on scene instances** — `MCPPrefabCommands.GetPrefabInfo` now uses `PrefabUtility.IsPartOfPrefabInstance` instead of `PrefabUtility.GetPrefabInstanceStatus == NotAPrefab`. This eliminates known false-negative cases (non-root children, instances with missing nested assets) where scene GameObjects that are valid prefab instances were reported as "not a prefab instance". Contributed by [@BadranRaza](https://github.com/BadranRaza) in [#8](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/8).
- **Bridge server started in AssetImportWorker subprocesses** — Unity spawns batch-mode `AssetImportWorker` subprocesses for parallel asset import, and these were running the plugin's `[InitializeOnLoad]` constructor and claiming ports in the 7890-7899 range on top of the main Editor. A single user with a few projects open could easily exhaust the range, blocking legitimate editor instances. `MCPBridgeServer` now early-returns when `Application.isBatchMode` is true.
- **Infinite retry loop on port exhaustion** — When no port was available, `MCPInstanceRegistry.FindAvailablePort()` returned `PortRangeStart` (7890) by default; `MCPBridgeServer.Start()` then retried via `EditorApplication.delayCall`, hit the same default, and looped forever, spamming `Failed to start on port 7890`. `FindAvailablePort()` now returns `-1` when nothing is free, and `Start()` gives up cleanly. Fixes [unity-mcp-server#10](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/10).

### Changed
- **Declared minimum Unity version corrected** — `unityRelease` bumped from `0f1` to `18f1`. The plugin has been using `Object.FindObjectsByType` (introduced in Unity 2021.3.18) for several releases, so the declared minimum was inaccurate. No effective support window change.

## [2.26.0] - 2026-04-02

### Added
- **SpriteAtlas management** — 7 new HTTP endpoints for Unity SpriteAtlas workflow (contributed by [@zaferdace](https://github.com/zaferdace)):
  - `spriteatlas/create` — Create a new SpriteAtlas asset
  - `spriteatlas/info` — Get SpriteAtlas details (packed sprites, packing/texture settings)
  - `spriteatlas/add` — Add sprites or folders to a SpriteAtlas
  - `spriteatlas/remove` — Remove entries from a SpriteAtlas
  - `spriteatlas/settings` — Configure packing, texture, and platform-specific settings
  - `spriteatlas/delete` — Delete a SpriteAtlas asset
  - `spriteatlas/list` — List all SpriteAtlases in the project
- New `MCPSpriteAtlasCommands.cs` — Dedicated SpriteAtlas command handler
- **Self-test system overhaul** — Probes for all 43 command modules (18 new categories), robust test runner with domain reload resume and timeout handling

### Fixed
- **Unity 2023+ / Unity 6 compatibility** — Resolved 43 `CS0618` deprecation warnings across the codebase
- **Self-test conditional compilation** — UMA probe wrapped in `#if UMA_INSTALLED`, Scenario probe handles missing MPPM package gracefully

## [2.25.0] - 2026-03-25

### Added
- **UMA (Unity Multipurpose Avatar) integration** — 13 new HTTP endpoints for the complete UMA asset pipeline:
  - `uma/inspect-fbx` — Inspect FBX meshes for UMA compatibility
  - `uma/create-slot` — Create SlotDataAsset from mesh data
  - `uma/create-overlay` — Create OverlayDataAsset with texture assignments
  - `uma/create-wardrobe-recipe` — Create WardrobeRecipe combining slots and overlays
  - `uma/create-wardrobe-from-fbx` — Atomic FBX-to-wardrobe pipeline (inspect → slot → overlay → recipe in one call)
  - `uma/wardrobe-equip` — Equip/unequip wardrobe items on DynamicCharacterAvatar
  - `uma/list-global-library` — Browse the UMA Global Library contents
  - `uma/list-wardrobe-slots` — List available wardrobe slots
  - `uma/list-uma-materials` — List UMA-compatible materials
  - `uma/get-project-config` — Get UMA project configuration
  - `uma/verify-recipe` — Validate a WardrobeRecipe for missing references
  - `uma/rebuild-global-library` — Force rebuild the Global Library index
  - `uma/register-assets` — Register Slot/Overlay/Recipe assets in the Global Library
- New `MCPUMACommands.cs` — Dedicated UMA command handler with conditional compilation (`UMA_INSTALLED`)
- UMA routes wired into `MCPBridgeServer.cs`

## [2.24.0] - 2026-03-25

### Added
- **Unity Test Runner integration** — Run and manage tests directly from AI assistants
  - `testing/run-tests` — Start EditMode/PlayMode test runs, returns job ID for async polling
  - `testing/get-job` — Poll test job status and results (passed/failed/skipped counts, duration)
  - `testing/list-tests` — Discover available tests with names, categories, and run state
  - Async job-based pattern with deferred execution on Unity main thread
  - Supports filtering by test name, category, assembly, or group
- **Compilation error tracking via CompilationPipeline** — Dedicated error buffer independent of console log
  - `CompilationPipeline.assemblyCompilationFinished` captures errors/warnings per assembly
  - `CompilationPipeline.compilationStarted` auto-clears buffer on new compilation cycle
  - Thread-safe with lock-based synchronization
  - Not affected by console `Clear()` or Play Mode log flooding
  - Returns file, line, column, message, severity, assembly, and timestamp
  - Supports filtering by severity (`error`, `warning`, `all`) and count limit
  - Includes `isCompiling` flag in response
- **HTTP route `compilation/errors`** — New endpoint on the bridge server for the MCP server's `unity_get_compilation_errors` tool

### Fixed
- **Unity 2021.3 LTS compilation compatibility** — Replaced `string.Contains(string, StringComparison)` with `IndexOf` for .NET Standard 2.0 compatibility
- **Operator precedence bug** — Fixed `!IndexOf >= 0` (CS0023) to `IndexOf < 0` in test name filtering

## [2.9.1] - 2026-02-26

### Changed
- **MCP connector renamed to `unity-mcp`** for better Cowork discovery (technical name only)
  - AnkleBreaker branding preserved in all user-facing UI (menu, dashboard, logs, tooltips)
  - Menu item remains: `Window > AB Unity MCP`
  - Log prefix remains: `[AB-UMCP]`
- Updated README with clear two-part installation instructions and Cowork setup guide
- Added Project Context to dashboard documentation

## [2.9.0] - 2026-02-26

### Added
- Project Context System — auto-inject project documentation to AI agents
- MCPContextManager for file discovery and template generation
- Context endpoints on HTTP bridge (direct read-only, bypasses queue)
- Context UI foldout in dashboard window

## [2.8.0] - 2026-02-25

### Added
- Multi-agent async request queue with fair round-robin scheduling
- Agent session tracking and action logging
- Read batching (up to 5/frame) and write serialization (1/frame)
- Queue management API endpoints
- Dashboard with live queue monitoring and agent sessions
- Self-test system for verifying all 21 categories
- Toolbar status element with server controls

## [1.0.0] - 2026-02-25

### Added
- Initial release
- HTTP bridge server on localhost:7890
- Scene management (open, save, create, hierarchy)
- GameObject operations (create, delete, inspect, transform)
- Component management (add, remove, get/set properties)
- Asset management (list, import, delete, prefabs, materials)
- Script operations (create, read, update)
- Build system (multi-platform builds)
- Console log access
- Play mode control
- Editor state monitoring
- Project info retrieval
- Menu item execution
- MiniJson serializer (zero dependencies)
