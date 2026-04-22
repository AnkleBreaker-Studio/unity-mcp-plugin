# Changelog

All notable changes to this package will be documented in this file.

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
