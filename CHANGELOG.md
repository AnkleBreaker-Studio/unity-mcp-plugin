# Changelog

All notable changes to this package will be documented in this file.

## [2.9.1] - 2026-02-26

### Changed
- **Renamed to "Unity MCP"** — cleaner name for better Cowork connector discovery
  - Menu item: `Window > Unity MCP` (was `Window > AB Unity MCP`)
  - Dashboard header: "Unity MCP" (was "AnkleBreaker Unity MCP")
  - Log prefix: `[Unity MCP]` (was `[AB-UMCP]`)
  - All UI strings and error messages updated
- Updated README with clear two-part installation instructions
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
