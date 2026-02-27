# AnkleBreaker Unity MCP — Plugin

<p align="center">
  <strong>Give AI agents direct control of the Unity Editor</strong><br>
  <em>Built for <a href="https://claude.ai">Claude Cowork</a> multi-agent workflows</em>
</p>

<p align="center">
  <a href="https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/releases"><img alt="Version" src="https://img.shields.io/badge/version-2.16.0-blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-green"></a>
  <a href="https://unity.com/releases/editor/archive"><img alt="Unity" src="https://img.shields.io/badge/Unity-2021.3%2B-black"></a>
  <a href="https://discord.gg/Q2XmedUctz"><img alt="Discord" src="https://img.shields.io/badge/Discord-Join%20Community-5865F2?logo=discord&logoColor=white"></a>
</p>

<p align="center">
  A project by <a href="https://anklebreaker-consulting.com"><strong>AnkleBreaker Consulting</strong></a> & <a href="https://anklebreaker-studio.com"><strong>AnkleBreaker Studio</strong></a>
</p>

---

## How This Is Different From Typical MCP

Most MCP integrations are designed for a **single AI assistant** talking to **one tool** in a simple request-response loop.

**AnkleBreaker Unity MCP is built for something different.** It's designed for [Claude Cowork](https://claude.ai), where **multiple AI agents work in parallel** on the same Unity project — one agent building the scene, another writing scripts, another tweaking materials — all at the same time.

This creates a unique challenge: **Unity is single-threaded.** You can't have five agents calling Unity APIs simultaneously. AnkleBreaker Unity MCP solves this with a **ticket-based async queue** that sits inside the Editor:

1. Each agent submits a request and gets a ticket back immediately
2. The queue processes requests fairly across agents (round-robin, no starvation)
3. Read operations are batched (up to 5 per frame), writes are serialized (1 per frame)
4. Agents poll their ticket status and get results when ready

This means you can tell Claude Cowork to *"set up the level lighting while also creating the player controller script and configuring the physics materials"* and it will dispatch parallel agents that coordinate through the queue automatically.

> **TL;DR:** This isn't just "Claude can click buttons in Unity." This is "a team of AI agents can build your game together."

---

## What It Does

This package runs a lightweight HTTP bridge inside the Unity Editor. Each instance auto-selects a port from the range `7890–7899` and registers itself in a shared file, so the MCP Server can discover and route to any running Unity Editor — even when multiple instances are open simultaneously (e.g. different projects, or [ParrelSync](https://github.com/VeriorPies/ParrelSync) clones for multiplayer testing). The companion [Unity MCP Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server) connects to it, exposing **259 tools** to AI agents across **24 feature categories**.

### Core Capabilities

| Category | What Agents Can Do |
|----------|-------------------|
| **Scenes** | Open, save, create scenes; browse full hierarchy tree |
| **GameObjects** | Create primitives or empties, delete, inspect, set transforms |
| **Components** | Add/remove components, get/set any serialized property, wire ObjectReferences between components |
| **Assets** | List, import, delete assets; create prefabs and materials |
| **Scripts** | Create, read, update C# scripts |
| **Builds** | Trigger multi-platform builds (Windows, macOS, Linux, Android, iOS, WebGL) |
| **Console** | Read errors/warnings/logs, clear console |
| **Play Mode** | Play, pause, stop |
| **Editor** | Execute menu items, run arbitrary C# code (Roslyn compiler), check state, get project info |

### Extended Capabilities

| Category | What Agents Can Do |
|----------|-------------------|
| **Animation** | List clips, get clip info, manage Animator controllers and parameters |
| **Prefab** | Open/close prefab editing, check status, get overrides, apply/revert |
| **Physics** | Raycasts, sphere/box casts, overlap tests, get/set physics settings |
| **Lighting** | Manage lights, environment, skybox, bake lightmaps, reflection probes |
| **Audio** | AudioSources, AudioListeners, AudioMixers, play/stop clips |
| **Tags & Layers** | List/add/remove tags, assign tags and layers |
| **Selection** | Get/set editor selection, find objects by name/tag/component |
| **Input Actions** | List action maps, actions, and bindings (Input System) |
| **Assembly Defs** | List, inspect, create, update .asmdef files |

### Profiling & Debugging

| Category | What Agents Can Do |
|----------|-------------------|
| **Profiler** | Start/stop profiler, get stats, deep profiles, save data |
| **Frame Debugger** | Enable/disable, draw call list and details, render targets |
| **Memory Profiler** | Memory breakdown by asset type, top consumers, snapshots |

### Shader & Visual Tools (conditional)

| Package Required | Features Unlocked |
|-----------------|-------------------|
| `com.unity.shadergraph` | Shader Graph create, inspect, open; Sub Graphs |
| `com.unity.visualeffectgraph` | VFX Graph listing and opening |
| Amplify Shader Editor (Asset Store) | Full Amplify Shader Editor integration — create, inspect, graph manipulation: add/remove/connect/disconnect/duplicate nodes, set properties, templates |

### Multi-Agent Infrastructure

| Feature | Description |
|---------|-------------|
| **Request Queue** | Ticket-based async queue with fair round-robin scheduling |
| **Agent Sessions** | Per-agent identity tracking, action logging, queue stats |
| **Read Batching** | Read-only operations batched (up to 5/frame) for throughput |
| **Write Serialization** | Write operations serialized (1/frame) for safety |
| **Dashboard** | Built-in Editor window showing queue state, agent sessions, recent actions, categories |
| **Action History** | Structured action log (last 500 entries) — filter by agent/category, click to select target objects, undo actions, copy details, optional disk persistence |
| **60s Timeout** | Queue timeout handles long operations like compilation |
| **Project Context** | Auto-injected project documentation for agents |
| **Multi-Instance** | Auto-port selection (7890–7899), shared instance registry, ParrelSync clone detection |

---

## Installation

### Via Unity Package Manager

1. Open Unity > **Window** > **Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git
   ```
4. Click **Add**

You should see in the Console:
```
[AB-UMCP] Server started on port 7890
```

The port number may vary (7890–7899) if other Unity instances are already running.

### Verify

Open a browser and visit: `http://127.0.0.1:<port>/api/ping` (replace `<port>` with the port shown in the Console).

You should see JSON with your Unity version and project name.

---

## You Also Need: The MCP Server

This plugin is one half of the system. You also need the **Node.js MCP Server**:

> **[AnkleBreaker Unity MCP — Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server)**

The server is what Claude (or Claude Cowork) actually talks to via MCP protocol. The server then communicates with this plugin's HTTP bridge.

> **Note:** AI agents should **never call the HTTP bridge directly**. The bridge is an internal layer between the MCP server and Unity. Agents must use the `unity_*` MCP tools provided by the server connector, which handle multi-agent queuing, agent tracking, and safety mechanisms automatically.

```
                                                  ┌─ Unity Instance A (port 7890)
Claude Cowork Agents ←→ MCP Server (Node.js) ←→───┤
       ↕                       ↕                   └─ Unity Instance B (port 7891)
  Multiple agents        Unity Hub CLI                        ↕
  working in parallel                              Shared Instance Registry
                                                   (%LOCALAPPDATA%/UnityMCP/instances.json)
```

---

## Dashboard

Open **Window > AB Unity MCP** to access:

- Server status with live indicator (green = running, red = stopped)
- Start / Stop / Restart controls
- **Request Queue** — live view of pending tickets, active agents, per-agent queue depths
- **Agent Sessions** — connected agents with action counts, queue stats, average response time
- **Recent Actions** — last 8 actions across all agents with status, timing, and category
- **Project Context** — configure auto-injected project documentation
- Per-category feature toggles (enable/disable any of the 24 categories)
- Port display (auto-selected or manual) with ParrelSync clone indicator
- Auto-start, manual port, and action history persistence settings
- Version display with update checker

### Action History Window

Open **Window > AB Unity MCP > Action History** for the full history view:

- Browse the last 500 MCP actions with timestamps, agent badges, and status indicators
- **Filter** by agent ID, action category, or free-text search
- **Click** a target object to select it in the Hierarchy and ping it
- **Double-click** to frame the object in the Scene view
- **Undo** any action via its captured Undo group
- **Copy** full action details to clipboard
- **Optional disk persistence** — toggle in settings to survive domain reloads

---

## Configuration

Configuration is managed through the Dashboard (**Window > AB Unity MCP**):

| Setting | Default | Description |
|---------|---------|-------------|
| **Use Manual Port** | `false` | When off, auto-selects from range 7890–7899. When on, uses the fixed port below |
| **Port** | `7890` | HTTP server port (only used when Use Manual Port is enabled) |
| **Auto-Start** | `true` | Start the bridge when Unity opens |
| **Category Toggles** | All enabled | Enable/disable any of the 24 feature categories |
| **Project Context** | Enabled | Auto-inject project docs to agents on first tool call |
| **Action History Persistence** | `false` | Save action history to `Library/MCPActionHistory.json` so it survives domain reloads |
| **Action History Max Entries** | `500` | Maximum actions stored in the ring buffer |

Settings are stored in `EditorPrefs` and persist across sessions.

---

## Multi-Instance Support

The plugin supports running **multiple Unity Editor instances simultaneously**. This is useful for working on several projects at the same time, or for multiplayer development with [ParrelSync](https://github.com/VeriorPies/ParrelSync) clones.

### How It Works

1. **Auto-port selection** — Each Unity instance picks the first available port from `7890–7899` on startup. No manual configuration needed.
2. **Shared registry** — Every running instance writes its metadata (port, project name, path, PID, Unity version) to a shared file at `%LOCALAPPDATA%/UnityMCP/instances.json` (macOS: `~/Library/Application Support/UnityMCP/instances.json`).
3. **Automatic cleanup** — When Unity stops, quits, or reloads assemblies, the instance unregisters itself. Stale entries from crashed processes are cleaned up on next startup.
4. **ParrelSync detection** — ParrelSync clones are automatically detected and labeled in the Dashboard and toolbar tooltip (e.g. "ParrelSync Clone #0").
5. **Discovery by MCP Server** — The companion MCP Server reads the registry, pings each instance to verify it's alive, and either auto-connects (single instance) or asks the user which project to target.

### Manual Port Override

If you need a fixed port (e.g. for firewall rules or custom tooling), enable **Use Manual Port** in the Dashboard settings. When enabled, the instance always binds to the configured port instead of auto-selecting.

---

## Requirements

- **Unity 2021.3 LTS** or newer (tested on 2022.3 LTS and Unity 6)
- .NET Standard 2.1 or .NET Framework
- Unity 6000+ (CoreCLR) fully supported — uses Roslyn compiler for `execute_code`

### Optional Packages

Some features activate automatically when their packages are detected:

| Package / Asset | Features Unlocked |
|----------------|-------------------|
| `com.unity.memoryprofiler` | Memory snapshots via MemoryProfiler API |
| `com.unity.shadergraph` | Shader Graph create, inspect, open |
| `com.unity.visualeffectgraph` | VFX Graph listing and opening |
| `com.unity.inputsystem` | Input Action maps and bindings |
| Amplify Shader Editor (Asset Store) | Full Amplify Shader Editor integration — create, inspect, graph manipulation: add/remove/connect/disconnect/duplicate nodes, set properties, templates |

---

## Security

- The server **only** binds to `127.0.0.1` (localhost) — not accessible from the network
- No authentication required (local-only by design)
- All operations support Unity's Undo system
- Multi-agent requests are queued with fair scheduling to prevent conflicts

---

## Community & Support

Join the **[AnkleBreaker Discord](https://discord.gg/Q2XmedUctz)** to connect with the Unity MCP community:

- **#mcp-help-support** — Get help with setup and usage
- **#mcp-bug-reports** — Report issues with full context
- **#mcp-feature-requests** — Suggest and vote on new features
- **#mcp-showcase** — Share your AI-powered Unity workflows
- **#mcp-contributions** — Discuss PRs and development

Community roles: **@MCP Community** for all members, **@MCP Contributor** for PR authors, **@MCP Helper** for active community helpers, **@MCP Beta Tester** for pre-release testers.

---

## Contributing

Contributions are welcome! This is an open-source project by [AnkleBreaker Consulting](https://anklebreaker-consulting.com) & [AnkleBreaker Studio](https://anklebreaker-studio.com).

1. Fork the repo
2. Create a feature branch
3. Make your changes
4. Submit a pull request
5. Join the [Discord](https://discord.gg/Q2XmedUctz) to discuss your contribution

Please also check out the companion server repo: [Unity MCP — Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server)

---

## Changelog

### v2.16.0

- **Action History** — New structured action logging system that records every MCP operation with full metadata: timestamp, agent ID, action name, category, target object, execution time, status, parameters, and Undo group. Ring buffer stores the last 500 actions (configurable). Includes:
  - **`MCPActionRecord`** — Structured data class for each action, with target object tracking (`TargetInstanceId`, `TargetPath`, `TargetType`) for interactive features.
  - **`MCPActionHistory`** — Static thread-safe manager with global ring buffer, filtering by agent/category/time, optional disk persistence to `Library/MCPActionHistory.json`, and `OnActionRecorded` event for UI refresh.
  - **`MCPActionHistoryWindow`** — Full standalone EditorWindow (`Window > AB Unity MCP > Action History`) with toolbar filters, scrollable action list, detail panel, and interactive features: click to select target in Hierarchy, double-click to frame in Scene view, undo via captured Undo group, copy to clipboard.
  - **Dashboard "Recent Actions" section** — Last 8 actions shown inline in the main Dashboard with "Open Full History" button.
  - **`MCPAgentSession` structured log** — Per-agent structured action records alongside existing string log (backward compatible with `unity_agent_log` tool).
  - **New settings** in `MCPSettingsManager`: `ActionHistoryPersistence` (default off) and `ActionHistoryMaxEntries` (default 500).
- **execute_code race condition fix** — Added `_executingTickets` dictionary in `MCPRequestQueue` to track in-flight tickets between dequeue and completion. Previously, tickets being executed (especially slow Roslyn compilations for `execute_code`) would return 404 "not found" because they were removed from `_agentQueues` but not yet in `_completedTickets`. The new tracking layer ensures `GetTicketStatus()` always finds the ticket regardless of execution state. Includes 120-second safety valve cleanup for stale executing tickets.
- **`GetQueueInfo` now reports executing count** — The queue info API returns `executingCount` alongside `totalPending` and `activeAgents` for full observability.
- Requires server v2.17.0+.

### v2.15.0

- **Multi-instance support** — Run multiple Unity Editors simultaneously, each on its own port. Auto-port selection from range 7890–7899, shared instance registry at `%LOCALAPPDATA%/UnityMCP/instances.json`, automatic cleanup on stop/quit/domain reload.
- **ParrelSync clone detection** — Automatically detects ParrelSync clones and displays clone index in the Dashboard status bar and toolbar tooltip.
- **New `MCPInstanceRegistry` class** — Manages port allocation, instance registration/unregistration, stale entry cleanup, and ParrelSync detection.
- **Dashboard UI updates** — Connection status shows active port with auto/manual indicator. Settings section adds "Use Manual Port" toggle with auto-select info. ParrelSync clone indicator shown below status bar.
- **Toolbar updates** — Tooltip and dropdown menu show active port, auto/manual mode, and ParrelSync clone info. Settings moved to submenu.
- Requires server v2.15.0+.

### v2.14.5

- **Non-Amplify project optimization** — `GetAmplifyAssembly()` now caches its result with a `_amplifyAssemblyChecked` flag, preventing repeated assembly scans in projects that don't have Amplify Shader Editor installed.

### v2.14.3

- **CloseAmplifyEditor save fix** — `CloseAmplifyEditor` now defaults to `save=true` (auto-saves before closing). When `save=false`, the graph is marked as not-dirty to prevent ASE's built-in unsaved changes dialog from appearing.
- **SaveAmplifyGraph smart path detection** — `SaveAmplifyGraph` now handles shaders that haven't been saved to disk yet. Auto-generates a save path from the shader name in the master node. Accepts an optional `path` parameter.

### v2.14.2

- **Reflection type initialization fix** — `GetOpenAmplifyWindow()` now ensures `_parentGraphType` and other reflection types are initialized before use, fixing "Object reference not set" errors when calling graph-dependent tools.

### v2.14.1

- **GetCurrentGraph rewrite** — Fixed graph retrieval to try properties first (`CurrentGraph`, `MainGraphInstance`, `CustomGraph`, `ParentGraph`), then fields (`m_mainGraphInstance`, `m_customGraph`), skipping null values. Fixes graph access failures when ASE loads shaders from disk.

### v2.14.0

- **14 new Amplify Shader Editor graph manipulation commands** — `AddAmplifyNode`, `RemoveAmplifyNode`, `ConnectAmplifyNodes`, `DisconnectAmplifyNodes`, `GetAmplifyNodeInfo`, `SetAmplifyNodeProperty`, `MoveAmplifyNode`, `SaveAmplifyGraph`, `CloseAmplifyEditor`, `CreateAmplifyFromTemplate`, `FocusAmplifyNode`, `GetAmplifyMasterNodeInfo`, `DisconnectAllAmplifyNode`, `DuplicateAmplifyNode`. Full graph manipulation: add/remove/connect/disconnect/duplicate nodes, set node properties via reflection, move nodes, save/close editor, create shaders from templates (surface, unlit, URP lit, transparent, post-process), inspect master node, and focus view on nodes.
- Amplify toolset expanded from 9 to 23 commands.
- Requires server v2.14.0+.

### v2.13.2

- **Prefab render stability fix** — `RenderPrefabPreview` now safely delegates to Unity's built-in `AssetPreview` system instead of instantiating prefabs at runtime. Complex prefabs with runtime scripts (NavMeshAgent, NetworkBehaviour, etc.) no longer crash the editor.

### v2.13.0

- **Graphics & visual intelligence** — New `MCPGraphicsCommands` class with 9 tools for visual inspection and graphical metadata. Asset preview thumbnails, Scene View capture, Game View capture, prefab rendering from configurable angles — all returned as base64 PNG for inline MCP image content blocks. Plus deep metadata: mesh geometry (vertices, triangles, UVs, blend shapes, bones), material shader properties (all property types, keywords, texture slots), texture analysis (format, compression, mipmaps, memory estimate), renderer settings (materials, bounds, shadows, sorting), and scene lighting summary.
- Routes: `graphics/asset-preview`, `graphics/scene-capture`, `graphics/game-capture`, `graphics/prefab-render`, `graphics/mesh-info`, `graphics/material-info`, `graphics/texture-info`, `graphics/renderer-info`, `graphics/lighting-summary`.

### v2.12.0

- **Prefab variant management** — 5 new tools for inspecting and managing prefab variant relationships. Get variant info and find all variants of a base, compare variant overrides to base, apply overrides to base, revert overrides, and transfer overrides between variants.
- Routes: `prefab-asset/variant-info`, `prefab-asset/compare-variant`, `prefab-asset/apply-variant-override`, `prefab-asset/revert-variant-override`, `prefab-asset/transfer-variant-overrides`.

### v2.11.0

- **Direct prefab asset editing** — New `MCPPrefabAssetCommands` class with 8 tools for editing prefab assets directly on disk, without instantiating into a scene. Browse hierarchy, read/write properties, add/remove components, wire ObjectReference properties, add/remove child GameObjects — all via atomic load-modify-save operations using `PrefabUtility.LoadPrefabContents`.
- Routes: `prefab-asset/hierarchy`, `prefab-asset/get-properties`, `prefab-asset/set-property`, `prefab-asset/add-component`, `prefab-asset/remove-component`, `prefab-asset/set-reference`, `prefab-asset/add-gameobject`, `prefab-asset/remove-gameobject`.
- Reuses `MCPComponentCommands` helpers (now `internal static`): `GetSerializedValue`, `SetSerializedValue`, `ResolveObjectReference`, `FindType`.

### v2.10.6

- **Roslyn compiler for `execute_code`** — Replaced CodeDom/mcs with Microsoft.CodeAnalysis.CSharp (Roslyn). This fixes Windows path-length errors and .NET Standard facade conflicts on Unity 6000+ (CoreCLR). Complex code using Dictionary, LINQ, scene queries, and full Unity API now compiles reliably.
- **ObjectReference wiring via `set_property`** — The `set_property` command now resolves ObjectReference values from JSON objects (`{"instanceId": ...}`, `{"assetPath": ...}`, `{"gameObject": ..., "componentType": ...}`), plain asset paths, scene hierarchy paths, or GameObject names.
- **New: `set_reference` tool** — Dedicated tool for wiring ObjectReference properties between components, GameObjects, and assets. Supports resolution by asset path, scene object name/path, component type, or instance ID. Auto-searches all components when `componentType` is omitted.
- **New: `batch_wire` tool** — Wire multiple ObjectReference properties in a single call. Inherits parent-level `path` and `componentType` into each reference entry for convenience.
- **New: `get_referenceable` tool** — Discover what objects can be assigned to an ObjectReference property. Returns matching scene objects and assets filtered by the property's expected type.

### v2.9.1

- Multi-agent async queue system with ticket-based scheduling
- Dashboard GUI with live queue state, agent sessions, and category toggles
- Desktop Extension (.mcpb) packaging support

---

## License

MIT License — Copyright (c) 2026 [AnkleBreaker Consulting](https://anklebreaker-consulting.com) & [AnkleBreaker Studio](https://anklebreaker-studio.com). All rights reserved.

See [LICENSE](LICENSE) for the full text.
