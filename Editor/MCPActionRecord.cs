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

        // Target object tracking
        public int    TargetInstanceId { get; set; } // 0 = no target
        public string TargetPath       { get; set; }
        public string TargetType       { get; set; } // GameObject, Component, Asset, Script, Scene, etc.

        // Key parameters (extracted from request)
        public Dictionary<string, string> Parameters { get; set; }

        // Undo support
        public int UndoGroup { get; set; } = -1; // -1 = no undo available

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

            // Instance ID
            if (dict.TryGetValue("instanceId", out var idObj))
            {
                if (idObj is int intId)
                    TargetInstanceId = intId;
                else if (idObj is long longId)
                    TargetInstanceId = (int)longId;
                else if (int.TryParse(idObj?.ToString(), out int parsed))
                    TargetInstanceId = parsed;
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
            sb.AppendLine($"Status: {Status}");
            sb.AppendLine($"Duration: {ExecutionTimeMs}ms");

            if (!string.IsNullOrEmpty(TargetPath))
                sb.AppendLine($"Target: {TargetPath}");
            if (TargetInstanceId != 0)
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
                { "executionTimeMs",  ExecutionTimeMs },
                { "errorMessage",     ErrorMessage ?? "" },
                { "targetInstanceId", TargetInstanceId },
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
