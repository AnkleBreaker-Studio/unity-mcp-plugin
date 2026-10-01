using System;
using System.Collections.Generic;
using System.Text;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Structured record of a single action performed by an agent via MCP.
    /// Captures timing, target objects, parameters, status, and undo group
    /// for display in the Action History UI.
    /// </summary>
    [Serializable]
    public class MCPActionRecord
    {
        public long     Id              { get; set; }
        public DateTime Timestamp       { get; set; }
        public string   AgentId         { get; set; }
        public string   ActionName      { get; set; }
        public string   Category        { get; set; }
        public string   Status          { get; set; } // Completed, Failed, TimedOut
        public long     ExecutionTimeMs { get; set; }
        public string   ErrorMessage    { get; set; }
        public bool     CommandFailed   { get; set; }
        public string DisplayStatus => CommandFailed ? "Command error" : Status;

        // Target object tracking. String because Unity 6.5 EntityIds are 64-bit
        // values carried as opaque decimal strings on the wire (see MCPObjectId) —
        // an int here silently truncated them. null/empty = no target. Old int-typed
        // persisted entries: JsonUtility parses the scalar-type mismatch to "".
        public string TargetInstanceId { get; set; }
        public string TargetPath       { get; set; }
        public string TargetType       { get; set; } // GameObject, Component, Asset, Script, Scene, etc.

        // Key parameters (extracted from request)
        public Dictionary<string, string> Parameters { get; set; }

        // Undo support
        public int UndoGroup { get; set; } = -1; // -1 = no undo available
        public string UndoSessionId { get; set; }
        public string UndoSignature { get; set; }

        /// <summary>
        /// Extract the category from an action name path (e.g. "gameobject/create" → "gameobject").
        /// </summary>
        public static string ExtractCategory(string actionName)
        {
            if (string.IsNullOrEmpty(actionName)) return "unknown";
            int slash = actionName.IndexOf('/');
            return slash > 0 ? actionName.Substring(0, slash).ToLower() : actionName.ToLower();
        }

        /// <summary>
        /// Extract a human-readable command from an action name (e.g. "gameobject/create" → "create").
        /// </summary>
        public static string ExtractCommand(string actionName)
        {
            if (string.IsNullOrEmpty(actionName)) return "unknown";
            int slash = actionName.LastIndexOf('/');
            return slash >= 0 && slash < actionName.Length - 1
                ? actionName.Substring(slash + 1)
                : actionName;
        }

        /// <summary>
        /// Try to extract target object info from a result dictionary.
        /// Many MCP handlers return { instanceId, path, name } etc.
        /// </summary>
        public void ExtractTargetFromResult(object result)
        {
            if (!(result is Dictionary<string, object> dict)) return;

            // Instance ID — stored verbatim as a string (lossless for 64-bit EntityIds)
            if (dict.TryGetValue("instanceId", out var idObj) && idObj != null)
            {
                string id = idObj.ToString();
                if (!string.IsNullOrEmpty(id))
                    TargetInstanceId = id;
            }

            // Path
            if (dict.TryGetValue("path", out var pathObj) && pathObj != null)
                TargetPath = pathObj.ToString();
            else if (dict.TryGetValue("gameObjectPath", out var goPath) && goPath != null)
                TargetPath = goPath.ToString();
            else if (dict.TryGetValue("hierarchyPath", out var hPath) && hPath != null)
                TargetPath = hPath.ToString();

            // Name (fallback for path)
            if (string.IsNullOrEmpty(TargetPath) && dict.TryGetValue("name", out var nameObj) && nameObj != null)
                TargetPath = nameObj.ToString();

            // Determine target type from category
            if (string.IsNullOrEmpty(TargetType))
                TargetType = InferTargetType(Category);
        }

        // Queue callbacks may run off the editor thread. Copy small scalar metadata without retaining the result graph.
        internal void CaptureTargetFromResult(object result)
        {
            if (!(result is Dictionary<string, object> values)) return;
            var comparer = values.Comparer;
            if (!ReferenceEquals(comparer, EqualityComparer<string>.Default)
                && !ReferenceEquals(comparer, StringComparer.Ordinal)
                && !ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase)) return;
            TargetInstanceId = TargetValue(values, "instanceId");
            TargetPath = TargetValue(values, "path") ?? TargetValue(values, "gameObjectPath") ?? TargetValue(values, "hierarchyPath");
            if (string.IsNullOrEmpty(TargetPath)) TargetPath = TargetValue(values, "name");
            TargetType = InferTargetType(Category);
        }

        private static string TargetValue(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var value) || value == null) return null;
            string text = value as string;
            if (text == null && (value.GetType().IsPrimitive || value is decimal))
                text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return text == null || text.Length <= 4096 ? text : text.Substring(0, 4096);
        }

        private static string InferTargetType(string category)
        {
            switch (category)
            {
                case "gameobject": return "GameObject";
                case "component":  return "Component";
                case "asset":      return "Asset";
                case "script":     return "Script";
                case "scene":      return "Scene";
                case "prefab":     return "Prefab";
                case "material":
                case "renderer":   return "Material";
                case "animation":  return "Animation";
                case "audio":      return "Audio";
                case "lighting":   return "Light";
                case "physics":    return "Physics";
                default:           return "";
            }
        }

        /// <summary>
        /// Format as a human-readable string for clipboard copy.
        /// </summary>
        public string ToCopyString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Action: {ActionName}");
            sb.AppendLine($"Agent: {AgentId}");
            sb.AppendLine($"Time: {Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Status: {DisplayStatus}");
            sb.AppendLine($"Duration: {ExecutionTimeMs}ms");

            if (!string.IsNullOrEmpty(TargetPath))
                sb.AppendLine($"Target: {TargetPath}");
            if (!string.IsNullOrEmpty(TargetInstanceId))
                sb.AppendLine($"InstanceId: {TargetInstanceId}");
            if (!string.IsNullOrEmpty(ErrorMessage))
                sb.AppendLine($"Error: {ErrorMessage}");

            if (Parameters != null && Parameters.Count > 0)
            {
                sb.AppendLine("Parameters:");
                foreach (var kvp in Parameters)
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Serialize to dictionary for JSON persistence.
        /// </summary>
        public Dictionary<string, object> ToDict()
        {
            var dict = new Dictionary<string, object>
            {
                { "id",               Id },
                { "timestamp",        Timestamp.ToString("O") },
                { "agentId",          AgentId ?? "" },
                { "actionName",       ActionName ?? "" },
                { "category",         Category ?? "" },
                { "status",           Status ?? "" },
                { "commandFailed",    CommandFailed },
                { "executionTimeMs",  ExecutionTimeMs },
                { "errorMessage",     ErrorMessage ?? "" },
                { "targetInstanceId", TargetInstanceId ?? "" },
                { "targetPath",       TargetPath ?? "" },
                { "targetType",       TargetType ?? "" },
                { "undoGroup",        UndoGroup },
            };

            if (Parameters != null && Parameters.Count > 0)
            {
                var paramDict = new Dictionary<string, object>();
                foreach (var kvp in Parameters)
                    paramDict[kvp.Key] = kvp.Value;
                dict["parameters"] = paramDict;
            }

            return dict;
        }
    }
}
