# AnkleBreaker Unity MCP — Plugin

<p align="center">
  <strong>Give AI agents direct control of the Unity Editor</strong><br>
  <em>Built for <a href="https://claude.ai">Claude Cowork</a> multi-agent workflows</em>
</p>

<p align="center">
  <a href="https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/releases"><img alt="Version" src="https://img.shields.io/badge/version-2.11.0-blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-green"></a>
  <a href="https://unity.com/releases/editor/archive"><img alt="Unity" src="https://img.shields.io/badge/Unity-2021.3%2B-black"></a>
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

This package runs a lightweight HTTP bridge inside the Unity Editor on `localhost:7890`. The companion [Unity MCP Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server) connects to it, exposing **150+ tools** to AI agents across **23 feature categories**.

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
| Amplify Shader Editor (Asset Store) | Amplify shader listing, inspection, opening |

### Multi-Agent Infrastructure

| Feature | Description |
|---------|-------------|
| **Request Queue** | Ticket-based async queue with fair round-robin scheduling |
| **Agent Sessions** | Per-agent identity tracking, action logging, queue stats |
| **Read Batching** | Read-only operations batched (up to 5/frame) for throughput |
| **Write Serialization** | Write operations serialized (1/frame) for safety |
| **Dashboard** | Built-in Editor window showing queue state, agent sessions, categories |
| **30s Timeout** | Queue timeout handles long operations like compilation |
| **Project Context** | Auto-injected project documentation for agents |

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

### Verify

Open a browser and visit: `http://127.0.0.1:7890/api/ping`

You should see JSON with your Unity version and project name.

---

## You Also Need: The MCP Server

This plugin is one half of the system. You also need the **Node.js MCP Server**:

> **[AnkleBreaker Unity MCP — Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server)**

The server is what Claude (or Claude Cowork) actually talks to via MCP protocol. The server then communicates with this plugin's HTTP bridge.

> **Note:** AI agents should **never call the HTTP bridge directly**. The bridge is an internal layer between the MCP server and Unity. Agents must use the `unity_*` MCP tools provided by the server connector, which handle multi-agent queuing, agent tracking, and safety mechanisms automatically.

```
Claude Cowork Agents ←→ MCP Server (Node.js) ←→ This Plugin (HTTP bridge in Unity)
       ↕                       ↕
  Multiple agents        Unity Hub CLI
  working in parallel
```

---

## Dashboard

Open **Window > AB Unity MCP** to access:

- Server status with live indicator (green = running, red = stopped)
- Start / Stop / Restart controls
- **Request Queue** — live view of pending tickets, active agents, per-agent queue depths
- **Agent Sessions** — connected agents with action counts, queue stats, average response time
- **Project Context** — configure auto-injected project documentation
- Per-category feature toggles (enable/disable any of the 21 categories)
- Port and auto-start settings
- Version display with update checker

---

## Configuration

Configuration is managed through the Dashboard (**Window > AB Unity MCP**):

| Setting | Default | Description |
|---------|---------|-------------|
| **Port** | `7890` | HTTP server port |
| **Auto-Start** | `true` | Start the bridge when Unity opens |
| **Category Toggles** | All enabled | Enable/disable any of the 21 feature categories |
| **Project Context** | Enabled | Auto-inject project docs to agents on first tool call |

Settings are stored in `EditorPrefs` and persist across sessions.

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
| Amplify Shader Editor (Asset Store) | Amplify shader listing, inspection, opening |

---

## Security

- The server **only** binds to `127.0.0.1` (localhost) — not accessible from the network
- No authentication required (local-only by design)
- All operations support Unity's Undo system
- Multi-agent requests are queued with fair scheduling to prevent conflicts

---

## Contributing

Contributions are welcome! This is an open-source project by [AnkleBreaker Consulting](https://anklebreaker-consulting.com) & [AnkleBreaker Studio](https://anklebreaker-studio.com).

1. Fork the repo
2. Create a feature branch
3. Make your changes
4. Submit a pull request

Please also check out the companion server repo: [Unity MCP — Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server)

---

## Changelog

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
