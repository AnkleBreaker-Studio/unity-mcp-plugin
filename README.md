<p align="center">
  <img src="Documentation~/workflow.svg" alt="AnkleBreaker Unity MCP: AI assistants route independent requests to multiple Unity projects with fair editor queues" width="1200" />
</p>

# AnkleBreaker Unity MCP Plugin

**The Unity side of a workflow built for multiple projects, multiple agents and multiplayer iteration.** This UPM package runs the editor bridge used by the companion [AnkleBreaker MCP server](https://github.com/AnkleBreaker-Studio/unity-mcp-server).

[Install](#installation) · [Dashboard](#dashboard-and-monitoring) · [Architecture](#how-it-works) · [Validation](#compatibility-and-validation) · [Changelog](CHANGELOG.md)

| 338 registered editor routes | Fair scheduling between agents | Multiplayer scenario tools |
|:---:|:---:|:---:|
| Scenes through optional integrations | FIFO per agent, grouped reads | MPPM controls and clone discovery |

The server also provides Hub and connection tools. Route counts describe the checked-in dispatcher; optional integrations require their own packages.

## Installation

1. In Unity, open **Window → Package Manager → Add package from git URL**.
2. Add `https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git`.
3. Open **Window → AB Unity MCP → Dashboard** and check the bridge status.
4. Install and configure the [Node MCP server](https://github.com/AnkleBreaker-Studio/unity-mcp-server#get-started).
5. From your MCP client, call `unity_list_instances`, select your project, then call `unity_editor_state`.

The default port is 7890; multiple editors can claim different ports. Discover the current project and port instead of assuming it remains the same after a restart. Browser navigation to the internal HTTP bridge is not the supported verification flow.

### Requirements

- Unity **2021.3 or newer** is the declared compatibility floor.
- uGUI and Unity Test Framework are declared UPM dependencies because editor command classes compile against their APIs. The manifest uses minimum versions compatible with older supported editors; Unity 6.6 resolves its built-in versions.
- The companion Node server and an MCP client are needed for AI-driven operations.

Server and plugin versions advance independently. Queue and legacy synchronous paths remain available; version numbers do not need to match.

On the modernization branch, protocol 2 adds protected submission retries and session-scoped polling. An updated server can recover the original ticket when its acknowledgement is lost. Older clients keep using their existing endpoints; their retry behavior does not change. [Protocol and compatibility details](https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/Development-Unity66-Modernization/docs/queue-protocol.md).

## What you can do

| Author | Inspect and verify | Coordinate |
|---|---|---|
| Scenes, objects, components and references | Scene hierarchy and search | Multiple editor projects |
| Prefabs, materials and ScriptableObjects | Console and compilation diagnostics | Per-agent queues and history |
| Animation, terrain, particles and audio | EditMode / PlayMode test jobs | MPPM scenarios and players |
| UI, lighting, navigation and builds | Screenshots, profiling and memory | ParrelSync clone discovery |
| Scripts and editor code | Named undo for supported writes | Project-specific context files |

Optional integrations include ProBuilder, UMA, Amplify Shader Editor, Shader Graph, VFX Graph, Input System, Memory Profiler and Multiplayer Play Mode. Discover their tools through `unity_list_advanced_tools`; the plugin reports unavailable packages or disabled categories instead of requiring every integration in every project.

<details>
<summary><strong>See a scene built with Unity MCP</strong></summary>

<p align="center"><img src="docs/unity-mcp-showcase-village.gif" alt="Existing Unity MCP demonstration showing a village with terrain and houses" width="800" /></p>

Other existing demonstrations: [brick breaker](docs/unity-mcp-showcase-brickbreaker.gif) and [castle](docs/unity-mcp-showcase-castle.gif).

</details>

## Dashboard and monitoring

Open **Window → AB Unity MCP → Dashboard** for bridge state, start/stop controls, category switches, auto-start and port settings, agent sessions and update information.

From your assistant, use `unity_queue_info`, `unity_agents_list` and `unity_agent_log` to inspect work. Action history records attribution and supported undo groups. New ticket fields separate monotonic `queueWaitMs` from `processingTimeMs`; existing `executionTimeMs` keeps its original total-response-time meaning.

The dashboard distinguishes pending and running requests. Agent sessions separate returned command errors (`commandErrors`), exceptions (`failedRequests`) and deadlines (`timedOutRequests`), alongside average wait and processing times. `completedRequests` counts all terminal tickets; `queuedRequests` counts outstanding queued and executing tickets. Timing stops when a ticket finishes or expires.

Recognized command errors retain the existing `Completed` ticket status and raw result, with additive `commandFailed` and `commandError` fields. The dashboard and history display **Command error**. Deferred callbacks and timeouts enter history once; duplicate callbacks cannot add records. History insertion runs on the editor thread through a bounded buffer, with pending/drop counts in `unity_queue_info`. Optional persistence includes the new outcome flag and remains compatible with older history files.

Sessions with outstanding work stay visible even after five minutes. Sessions without work expire after 30 minutes of inactivity; at most 256 inactive sessions are retained, oldest first. Active/busy sessions are preserved. `unity_queue_info` reports the retention policy and eviction count. Returning after eviction starts fresh session statistics; the separate action history keeps its own limits. [Scheduling and measured retention behavior →](https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/Development-Unity66-Modernization/docs/queue-monitoring.md)

Ticket completion is atomic: duplicate or late callbacks cannot replace a terminal result or count it twice. Legacy calls expire after 30 seconds; work still waiting is removed. Deferred execution expires after 120 seconds of processing when the editor runs cleanup. A timeout cannot cancel work that already started and does not prove that no changes occurred. Results remain available for polling for 60 seconds, or 30 seconds after timeout, until periodic cleanup.

The editor registry receives a heartbeat approximately every 30 seconds while the editor update loop runs. A long compile can pause that loop. The server uses a staleness allowance and live identity checks during discovery; the heartbeat is a signal, not proof that an operation completed.

## How it works

1. The MCP server sends a command with agent identity to the local bridge.
2. The bridge returns a queue ticket. Legacy calls wait on a ticket internally.
3. `EditorApplication.update` processes one write or up to five explicitly classified reads, visiting agent queues fairly. Unknown routes use the write path.
4. Commands execute on Unity's main thread; deferred Unity APIs complete through callbacks.
5. The MCP server polls the original ticket and returns text or images to the client.

Pending tickets are indexed by ID, avoiding a scan across every agent queue on each status request. Supported synchronous writes get individual named undo groups. Deferred commands do not collapse undo groups across intervening work from other agents.

Each editor has its own listener, queue and project identity. The server handles request isolation and per-call routing. The plugin handles editor scheduling, history, optional integrations and reload lifecycle. Heavy Unity operations still occupy the main thread; queueing does not make arbitrary editor work non-blocking.

### Local access and undo

The bridge binds to loopback and checks incoming browser/host metadata. It is intended for the companion server on the same machine. Local code-execution tools have the authority of the editor process, so use trusted MCP clients. Queueing orders requests; it does not resolve conflicting edits by different agents. Undo applies to commands that register supported Unity undo operations, not every possible command or filesystem change.

## Compatibility and validation

Current modernization work has passed a focused batch run on **Unity 6000.6.2f1**: package compilation, object identity round-trips, agent ordering, read batching, duplicate/late deferred callbacks, result retention, 50 synchronous requests, real 30-second timeout races, dashboard state and polling at queue depths up to 10,000. Dashboard checks inspect its UI Toolkit labels in batch mode; they do not certify visual layout. Old Unity versions and other optional integrations remain part of the wider validation work.

MPPM 3.0 live validation covers native scenario creation/selection, actual Host/Client roles, separate agent selections for the main and virtual editor, and a shared script recompilation followed by more simultaneous commands. Discovery adds parent project and virtual-player IDs without changing ParrelSync fields. Role assignment requires the project's Multiplayer Roles setting; the plugin reports that setting without changing it. [Multiplayer workflow and test limits →](https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/Development-Unity66-Modernization/docs/multiplayer.md)

Live validation also covers 24 overlapping commands to two editors, four Play Mode reload configurations, and an actual script reload that loses a result without replaying the command. Unity 6.6 builds now default to Checked managed diagnostics for Development and Release otherwise; optional `managedCodeVariant` overrides this for one build, with the project setting restored even on failure. Five Windows Mono builds verified the compiled defines. See the server's [build guide](https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/Development-Unity66-Modernization/docs/builds.md) and [validation record](https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/Development-Unity66-Modernization/docs/modernization.md).

To reproduce on Windows with an installed editor and a disposable project directory:

```powershell
./tools~/validate-unity.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.6.2f1/Editor/Unity.exe' -ProjectPath 'C:/UnityMcpValidation/Unity66'
```

The runner launches Unity hidden in batch mode, refuses an unmarked existing project and writes `Library/UnityMcpValidation.json` plus `validation.log`. Development tooling under `tools~` is ignored by Unity's package importer.

Add `-Suite Health` to validate session retention, read/write scheduling and idle queue allocations. This writes `Library/UnityMcpQueueHealthValidation.json`. The measured empty queue loop has zero allocation events after warmup; this is not a zero-allocation claim for the whole plugin or editor.

Add `-Suite Monitoring` to verify command-result classification, callback history, old/new persistence, history-buffer capacity, dashboard state and a real create/undo cycle. This writes `Library/UnityMcpMonitoringValidation.json`. Batch-mode UI checks do not certify the interactive layout.

The route list is generated from the dispatcher:

```sh
node tools~/generate-routes.mjs --check
```

The server repository contains the MCP protocol, backwards-compatibility and concurrent routing tests. See its architecture and modernization guides for the full evidence and remaining scope.

## Support and license

[Report an issue](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/issues) with Unity, plugin and server versions and the affected operation. Support development through [GitHub Sponsors](https://github.com/sponsors/AnkleBreaker-Studio) or [Patreon](https://www.patreon.com/AnkleBreakerStudio).

Distributed under the **AnkleBreaker Open License v1.0**. See [LICENSE](LICENSE) for attribution requirements and restrictions on reselling the tool. AI client/model costs are separate.
