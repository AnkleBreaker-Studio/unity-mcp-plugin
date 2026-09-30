using System;
using System.Collections.Generic;

namespace UnityMCP.Editor
{
    internal static class MCPCommandPolicy
    {
        // A route name cannot prove absence of side effects; unknown commands stay serialized as writes.
        private static readonly HashSet<string> ReadOnlyRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_meta/routes", "agents/list", "agents/log",
            "amplify/get-connections", "amplify/get-node-types", "amplify/get-nodes", "amplify/info",
            "amplify/list", "amplify/list-functions", "amplify/status",
            "animation/get-blend-tree", "animation/get-curve-keyframes", "animation/get-events",
            "asmdef/info", "asmdef/list", "asset/list", "audio/info",
            "compilation/errors", "component/get-properties", "component/get-referenceable",
            "console/log", "constraint/info", "debugger/events", "editor/state", "editorprefs/get",
            "gameobject/info", "input/info", "lighting/info", "lod/info", "mppm/list-players",
            "navigation/info", "packages/info", "packages/list", "particle/info", "ping", "playerprefs/get",
            "prefab-asset/get-properties", "prefab/info", "probuilder/info",
            "profiler/analyze", "profiler/frame-data", "profiler/memory", "profiler/memory-breakdown",
            "profiler/memory-status", "profiler/memory-top-assets", "profiler/stats", "project/info",
            "scenario/info", "scenario/list", "scenario/status", "scene/hierarchy", "scene/info", "sceneview/info",
            "scriptableobject/info", "scriptableobject/list-types",
            "search/assets", "search/by-component", "search/by-layer", "search/by-name", "search/by-shader",
            "search/by-tag", "search/missing-references", "search/scene-stats",
            "selection/find-by-type", "selection/get",
            "shadergraph/get-edges", "shadergraph/get-node-types", "shadergraph/get-nodes", "shadergraph/get-properties",
            "shadergraph/info", "shadergraph/list", "shadergraph/list-shaders", "shadergraph/list-subgraphs",
            "shadergraph/list-vfx", "shadergraph/status", "spriteatlas/info", "spriteatlas/list", "taglayer/info",
            "terrain/get-height", "terrain/get-heights-region", "terrain/get-steepness", "terrain/get-tree-instances",
            "terrain/info", "terrain/list", "testing/get-job", "testing/list-tests", "texture/info", "ui/info",
            "uma/get-project-config", "uma/list-global-library", "uma/list-uma-materials", "uma/list-wardrobe-slots",
        };

        internal static bool IsReadOnly(string actionName) => actionName != null && ReadOnlyRoutes.Contains(actionName);
    }
}
