# Unity MCP Bridge Plugin

A Unity Editor plugin that enables AI assistants (Claude, etc.) to control Unity Editor via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io). Part of the [Unity MCP](https://github.com/AnkleBreaker-Studio) toolchain by AnkleBreaker Studio.

## What It Does

This package runs a lightweight HTTP server inside the Unity Editor on `localhost:7890`. The companion [unity-mcp-server](https://github.com/AnkleBreaker-Studio/unity-mcp-server) connects to it, exposing 30+ tools to AI assistants.

**Capabilities:**
- **Scene Management** — Open, save, create scenes; browse full hierarchy
- **GameObjects** — Create, delete, inspect, transform any object
- **Components** — Add/remove components, read/write any serialized property
- **Assets** — List, import, delete, create prefabs and materials
- **Scripts** — Create, read, update C# scripts in your project
- **Builds** — Trigger multi-platform builds
- **Console** — Read errors/warnings/logs from the Unity Console
- **Play Mode** — Start, pause, stop play mode
- **Editor** — Execute menu items, check editor state, get project info

## Installation via Unity Package Manager

1. Open Unity → **Window** → **Package Manager**
2. Click the **+** button → **Add package from git URL...**
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

## Requirements

- Unity 2021.3 LTS or newer (tested on 2022.3 LTS and Unity 6)
- .NET Standard 2.1 or .NET Framework

## Configuration

The bridge listens on `127.0.0.1:7890` by default. To change the port, edit `MCPBridgeServer.cs` line 20:

```csharp
private static readonly int Port = 7890;
```

## Security

- The server **only** binds to `127.0.0.1` (localhost) — it is not accessible from the network
- No authentication is required since it's local-only
- All operations support Unity's Undo system

## License

MIT — see [LICENSE](LICENSE)
