# Unity MCP Bridge Plugin

A Unity Editor plugin that enables AI assistants (Claude, etc.) to control Unity Editor via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io). Part of the [Unity MCP](https://github.com/AnkleBreaker-Studio) toolchain by AnkleBreaker Studio.

## What It Does

This package runs a lightweight HTTP server inside the Unity Editor on `localhost:7890`. The companion [unity-mcp-server](https://github.com/AnkleBreaker-Studio/unity-mcp-server) connects to it, exposing **145+ tools** to AI assistants across **21 feature categories**.

**Core Capabilities:**

- **Scene Management** — Open, save, create scenes; browse full hierarchy tree
- **GameObjects** — Create (primitives or empty), delete, inspect, set transforms (world/local)
- **Components** — Add/remove components, get/set any serialized property
- **Assets** — List, import, delete assets; create prefabs and materials; assign materials
- **Scripts** — Create, read, update C# scripts
- **Builds** — Trigger multi-platform builds (Windows, macOS, Linux, Android, iOS, WebGL)
- **Console** — Read errors/warnings/logs, clear console
- **Play Mode** — Play, pause, stop
- **Editor** — Execute menu items, run arbitrary C# code, check editor state, get project info

**Extended Capabilities:**

- **Animation** — List clips, get clip info, list Animator controllers and parameters, set Animator properties, play animations
- **Prefab (Advanced)** — Open/close prefab editing mode, check prefab status, get overrides, apply/revert changes
- **Physics** — Raycasts, sphere/box casts, overlap tests, get/set physics settings (gravity, layers, collision matrix)
- **Lighting** — Manage lights, configure environment lighting/skybox, bake lightmaps, list/manage reflection probes
- **Audio** — Manage AudioSources, AudioListeners, AudioMixers, play/stop clips, adjust mixer parameters
- **Tags & Layers** — List tags and layers, add/remove tags, assign tags/layers to GameObjects
- **Selection** — Get/set editor selection, find objects by name/tag/component
- **Input Actions** — List action maps and actions, inspect bindings (Input System package)
- **Assembly Definitions** — List, inspect, create, update .asmdef files

**Profiling & Debugging:**

- **Profiler** — Start/stop profiler, get stats, take deep profiles, save profiler data
- **Frame Debugger** — Enable/disable frame debugger, get draw call list and details, get render target info
- **Memory Profiler** — Memory breakdown by asset type, top memory consumers, take memory snapshots (with `com.unity.memoryprofiler` package)

**Shader & Visual Tools (conditional on packages):**

- **Shader Graph** — List, inspect, create, open Shader Graphs; inspect shader properties; list Sub Graphs and VFX Graphs (requires `com.unity.shadergraph` / `com.unity.visualeffectgraph`)
- **Amplify Shader Editor** — List, inspect, open Amplify shaders and functions (requires Amplify Shader Editor asset)

**Infrastructure:**

- **Multi-Agent Support** — Multiple AI agents can connect simultaneously with session tracking, action logging, and queued execution
- **Dashboard** — Built-in Editor window (`Window > MCP Dashboard`) showing server status, category toggles, agent sessions, and update checker
- **Settings** — Configurable port, auto-start, and per-category enable/disable via EditorPrefs
- **Update Checker** — Automatic GitHub release checking with in-dashboard notification

## Installation via Unity Package Manager

1. Open Unity > **Window** > **Package Manager**
2. Click the **+** button > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git
   ```
4. Click **Add**

Unity will download and install the package. You should see in the Console:
```
[MCP Bridge] Server started on port 7890
```

### Verify

Open a browser and visit: `http://127.0.0.1:7890/api/ping`

You should see JSON with your Unity version and project name.

## Companion: MCP Server

This plugin is one half of the system. You also need the **Node.js MCP Server** that connects Claude to this bridge:

👉 [unity-mcp-server](https://github.com/AnkleBreaker-Studio/unity-mcp-server)

## Dashboard

Open **Window > MCP Dashboard** to access:

- Server status with live indicator (green = running, red = stopped)
- Start / Stop / Restart controls
- Per-category feature toggles (enable/disable any of the 21 categories)
- Port and auto-start settings
- Active agent session monitoring
- Version display with update checker

## Requirements

- Unity 2021.3 LTS or newer (tested on 2022.3 LTS and Unity 6)
- .NET Standard 2.1 or .NET Framework

### Optional Packages

Some features activate automatically when their corresponding packages are detected:

| Package / Asset | Features Unlocked |
|----------------|-------------------|
| `com.unity.memoryprofiler` | Memory snapshots via MemoryProfiler API |
| `com.unity.shadergraph` | Shader Graph create, inspect, open |
| `com.unity.visualeffectgraph` | VFX Graph listing and opening |
| `com.unity.inputsystem` | Input Action maps and bindings inspection |
| Amplify Shader Editor (Asset Store) | Amplify shader listing, inspection, opening |

## Configuration

Configuration is managed through the MCP Dashboard (`Window > MCP Dashboard > Settings`):

- **Port** — HTTP server port (default: `7890`)
- **Auto-Start** — Automatically start the bridge when Unity opens (default: `true`)
- **Category Toggles** — Enable/disable any of the 21 feature categories

Settings are stored in `EditorPrefs` and persist across sessions.

## Security

- The server **only** binds to `127.0.0.1` (localhost) — it is not accessible from the network
- No authentication is required since it's local-only
- All operations support Unity's Undo system
- Multi-agent requests are queued to prevent conflicts

## Support the Project

If Unity MCP helps your workflow, consider supporting its development! Your support helps fund new features, bug fixes, documentation, and more open-source game dev tools.

<a href="https://github.com/sponsors/AnkleBreaker-Studio">
  <img src="https://img.shields.io/badge/Sponsor-GitHub%20Sponsors-ea4aaa?logo=github&style=for-the-badge" alt="GitHub Sponsors" />
</a>
<a href="https://www.patreon.com/AnkleBreakerStudio">
  <img src="https://img.shields.io/badge/Support-Patreon-f96854?logo=patreon&style=for-the-badge" alt="Patreon" />
</a>

**Sponsor tiers include priority feature requests** — your ideas get bumped up the roadmap! Check out the tiers on [GitHub Sponsors](https://github.com/sponsors/AnkleBreaker-Studio) or [Patreon](https://www.patreon.com/AnkleBreakerStudio).

## License

MIT with Attribution Requirement — see [LICENSE](LICENSE)

Any product built with Unity MCP must display **"Made with AnkleBreaker MCP"** (or "Powered by AnkleBreaker MCP") with the logo. Personal/educational use is exempt.
