namespace UnityMCP.Editor
{
    /// <summary>
    /// Capability + version signal for the server↔plugin handshake (drift resilience).
    ///
    /// The Node MCP server and this plugin ship on separate release trains. Before the
    /// handshake neither exchanged a version, so a call to a route an older peer lacked
    /// failed with no graceful fallback and no "why". The plugin now advertises
    /// <see cref="ProtocolVersion"/> and <see cref="PluginVersion"/> in its /api/ping
    /// response; the server gates negotiated features on the protocol version and degrades
    /// gracefully (warn once, fall back) when the peer is too old.
    ///
    /// Pattern ported (not copied) from Unity ML-Agents' UnityRLCapabilities.
    /// Keep <see cref="ProtocolVersion"/> in sync with the server's PROTOCOL_VERSION
    /// (src/capabilities.js) and <see cref="PluginVersion"/> in sync with package.json.
    /// Ref: knowledge/research/2026-07-12-vet-unity-ml-agents.md
    /// </summary>
    public static class MCPCapabilities
    {
        /// <summary>
        /// Monotonic wire-protocol version. Bump when adding a capability the server may
        /// need to feature-detect (e.g. a new negotiated route). Current negotiated
        /// features by minimum protocol: batch-wire (component/batch-wire) → 1.
        /// </summary>
        public const int ProtocolVersion = 1;

        /// <summary>Plugin package version. Keep synced with package.json.</summary>
        public const string PluginVersion = "2.27.0";
    }
}
