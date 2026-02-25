using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing Assembly Definition (.asmdef) and Assembly Definition Reference (.asmref) files.
    /// Works by manipulating the JSON format directly.
    /// </summary>
    public static class MCPAssemblyDefCommands
    {
        // ─── Create Assembly Definition ───

        /// <summary>
        /// Create a new .asmdef file with the given settings.
        /// </summary>
        public static object CreateAssemblyDef(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (e.g. 'Assets/Scripts/Runtime/MyGame.Runtime.asmdef')" };

            if (!path.EndsWith(".asmdef"))
                path += ".asmdef";

            string asmName = args.ContainsKey("name") ? args["name"].ToString()
                : Path.GetFileNameWithoutExtension(path);

            // Ensure directory exists
            EnsureDirectoryExists(path);

            // Check if file already exists
            if (File.Exists(path))
                return new { error = $"Assembly definition already exists at '{path}'. Use asmdef/info to inspect or asmdef/update to modify it." };

            // Build the asmdef JSON
            var asmdef = new Dictionary<string, object>
            {
                { "name", asmName },
                { "rootNamespace", args.ContainsKey("rootNamespace") ? args["rootNamespace"].ToString() : "" },
                { "references", BuildStringList(args, "references") },
                { "includePlatforms", BuildStringList(args, "includePlatforms") },
                { "excludePlatforms", BuildStringList(args, "excludePlatforms") },
                { "allowUnsafeCode", GetBool(args, "allowUnsafeCode", false) },
                { "overrideReferences", GetBool(args, "overrideReferences", false) },
                { "precompiledReferences", BuildStringList(args, "precompiledReferences") },
                { "autoReferenced", GetBool(args, "autoReferenced", true) },
                { "defineConstraints", BuildStringList(args, "defineConstraints") },
                { "versionDefines", new List<object>() },
                { "noEngineReferences", GetBool(args, "noEngineReferences", false) }
            };

            string json = FormatAsmdefJson(asmdef);
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);

            return new
            {
                success = true,
                path = path,
                name = asmName,
                message = $"Assembly definition '{asmName}' created at '{path}'"
            };
        }

        // ─── Get Assembly Definition Info ───

        /// <summary>
        /// Read and return the contents of an .asmdef file.
        /// </summary>
        public static object GetAssemblyDefInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!File.Exists(path))
                return new { error = $"File not found: '{path}'" };

            string json = File.ReadAllText(path);
            var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (asmdef == null)
                return new { error = "Failed to parse assembly definition JSON" };

            // Add the file path for context
            asmdef["_filePath"] = path;

            return asmdef;
        }

        // ─── List Assembly Definitions ───

        /// <summary>
        /// List all .asmdef files in the project, optionally filtered by folder.
        /// </summary>
        public static object ListAssemblyDefs(Dictionary<string, object> args)
        {
            string folder = args.ContainsKey("folder") ? args["folder"].ToString() : "Assets";
            bool includePackages = GetBool(args, "includePackages", false);

            var results = new List<object>();

            // Search in Assets
            string[] guids = AssetDatabase.FindAssets("t:asmdef", new[] { folder });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                try
                {
                    string json = File.ReadAllText(assetPath);
                    var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
                    if (asmdef != null)
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "path", assetPath },
                            { "name", asmdef.ContainsKey("name") ? asmdef["name"] : "" },
                            { "rootNamespace", asmdef.ContainsKey("rootNamespace") ? asmdef["rootNamespace"] : "" },
                            { "referenceCount", asmdef.ContainsKey("references") && asmdef["references"] is List<object> refs ? refs.Count : 0 },
                            { "platforms", GetPlatformSummary(asmdef) }
                        });
                    }
                }
                catch { /* skip unreadable files */ }
            }

            // Optionally include Packages
            if (includePackages)
            {
                string[] pkgGuids = AssetDatabase.FindAssets("t:asmdef", new[] { "Packages" });
                foreach (string guid in pkgGuids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    try
                    {
                        string json = File.ReadAllText(assetPath);
                        var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
                        if (asmdef != null)
                        {
                            results.Add(new Dictionary<string, object>
                            {
                                { "path", assetPath },
                                { "name", asmdef.ContainsKey("name") ? asmdef["name"] : "" },
                                { "rootNamespace", asmdef.ContainsKey("rootNamespace") ? asmdef["rootNamespace"] : "" },
                                { "referenceCount", asmdef.ContainsKey("references") && asmdef["references"] is List<object> refs ? refs.Count : 0 },
                                { "platforms", GetPlatformSummary(asmdef) }
                            });
                        }
                    }
                    catch { /* skip unreadable files */ }
                }
            }

            return new { assemblies = results, count = results.Count };
        }

        // ─── Add References ───

        /// <summary>
        /// Add assembly references to an existing .asmdef file.
        /// Supports both assembly names (e.g. "Unity.TextMeshPro") and GUID references (e.g. "GUID:6055be8ebefd69e48b49212b09b47b2f").
        /// </summary>
        public static object AddReferences(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!File.Exists(path))
                return new { error = $"File not found: '{path}'" };

            var newRefs = BuildStringList(args, "references");
            if (newRefs.Count == 0)
                return new { error = "references array is required and must not be empty" };

            string json = File.ReadAllText(path);
            var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (asmdef == null)
                return new { error = "Failed to parse assembly definition JSON" };

            var existingRefs = asmdef.ContainsKey("references") && asmdef["references"] is List<object> list
                ? list.Select(r => r.ToString()).ToList()
                : new List<string>();

            var added = new List<string>();
            var skipped = new List<string>();

            foreach (string newRef in newRefs)
            {
                // Resolve assembly name to GUID if it's not already a GUID ref
                string resolvedRef = ResolveAssemblyReference(newRef);

                if (existingRefs.Contains(resolvedRef) || existingRefs.Contains(newRef))
                {
                    skipped.Add(newRef);
                }
                else
                {
                    existingRefs.Add(resolvedRef);
                    added.Add(resolvedRef);
                }
            }

            asmdef["references"] = existingRefs.Cast<object>().ToList();

            json = FormatAsmdefJson(asmdef);
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);

            return new
            {
                success = true,
                path = path,
                added = added,
                skipped = skipped,
                totalReferences = existingRefs.Count
            };
        }

        // ─── Remove References ───

        /// <summary>
        /// Remove assembly references from an existing .asmdef file.
        /// </summary>
        public static object RemoveReferences(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!File.Exists(path))
                return new { error = $"File not found: '{path}'" };

            var refsToRemove = BuildStringList(args, "references");
            if (refsToRemove.Count == 0)
                return new { error = "references array is required and must not be empty" };

            string json = File.ReadAllText(path);
            var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (asmdef == null)
                return new { error = "Failed to parse assembly definition JSON" };

            var existingRefs = asmdef.ContainsKey("references") && asmdef["references"] is List<object> list
                ? list.Select(r => r.ToString()).ToList()
                : new List<string>();

            var removed = new List<string>();
            foreach (string refToRemove in refsToRemove)
            {
                // Try to match by name or GUID
                string match = existingRefs.FirstOrDefault(r =>
                    r.Equals(refToRemove, StringComparison.OrdinalIgnoreCase) ||
                    r.Contains(refToRemove));

                if (match != null)
                {
                    existingRefs.Remove(match);
                    removed.Add(match);
                }
            }

            asmdef["references"] = existingRefs.Cast<object>().ToList();

            json = FormatAsmdefJson(asmdef);
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);

            return new
            {
                success = true,
                path = path,
                removed = removed,
                totalReferences = existingRefs.Count
            };
        }

        // ─── Set Platforms ───

        /// <summary>
        /// Set the platform include/exclude lists for an assembly definition.
        /// </summary>
        public static object SetPlatforms(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!File.Exists(path))
                return new { error = $"File not found: '{path}'" };

            string json = File.ReadAllText(path);
            var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (asmdef == null)
                return new { error = "Failed to parse assembly definition JSON" };

            if (args.ContainsKey("includePlatforms"))
                asmdef["includePlatforms"] = BuildStringList(args, "includePlatforms").Cast<object>().ToList();

            if (args.ContainsKey("excludePlatforms"))
                asmdef["excludePlatforms"] = BuildStringList(args, "excludePlatforms").Cast<object>().ToList();

            json = FormatAsmdefJson(asmdef);
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);

            return new
            {
                success = true,
                path = path,
                includePlatforms = asmdef.ContainsKey("includePlatforms") ? asmdef["includePlatforms"] : new List<object>(),
                excludePlatforms = asmdef.ContainsKey("excludePlatforms") ? asmdef["excludePlatforms"] : new List<object>()
            };
        }

        // ─── Update Settings ───

        /// <summary>
        /// Update various settings on an assembly definition (rootNamespace, allowUnsafeCode, etc.).
        /// </summary>
        public static object UpdateSettings(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!File.Exists(path))
                return new { error = $"File not found: '{path}'" };

            string json = File.ReadAllText(path);
            var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (asmdef == null)
                return new { error = "Failed to parse assembly definition JSON" };

            var updated = new List<string>();

            // String properties
            string[] stringProps = { "name", "rootNamespace" };
            foreach (string prop in stringProps)
            {
                if (args.ContainsKey(prop))
                {
                    asmdef[prop] = args[prop].ToString();
                    updated.Add(prop);
                }
            }

            // Bool properties
            string[] boolProps = { "allowUnsafeCode", "overrideReferences", "autoReferenced", "noEngineReferences" };
            foreach (string prop in boolProps)
            {
                if (args.ContainsKey(prop))
                {
                    asmdef[prop] = GetBool(args, prop, false);
                    updated.Add(prop);
                }
            }

            // List properties
            string[] listProps = { "defineConstraints", "precompiledReferences" };
            foreach (string prop in listProps)
            {
                if (args.ContainsKey(prop))
                {
                    asmdef[prop] = BuildStringList(args, prop).Cast<object>().ToList();
                    updated.Add(prop);
                }
            }

            // Version defines
            if (args.ContainsKey("versionDefines") && args["versionDefines"] is List<object> vdList)
            {
                asmdef["versionDefines"] = vdList;
                updated.Add("versionDefines");
            }

            if (updated.Count == 0)
                return new { error = "No settings to update. Provide at least one of: name, rootNamespace, allowUnsafeCode, overrideReferences, autoReferenced, noEngineReferences, defineConstraints, precompiledReferences, versionDefines" };

            json = FormatAsmdefJson(asmdef);
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);

            return new
            {
                success = true,
                path = path,
                updatedProperties = updated,
                message = $"Updated {updated.Count} properties on '{Path.GetFileNameWithoutExtension(path)}'"
            };
        }

        // ─── Create Assembly Definition Reference ───

        /// <summary>
        /// Create a .asmref file that references an existing assembly definition.
        /// Useful for splitting code across folders while keeping it in the same assembly.
        /// </summary>
        public static object CreateAssemblyRef(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (e.g. 'Assets/Plugins/MyPlugin/MyGame.Runtime.asmref')" };

            if (!path.EndsWith(".asmref"))
                path += ".asmref";

            string targetAssembly = args.ContainsKey("reference") ? args["reference"].ToString() : "";
            if (string.IsNullOrEmpty(targetAssembly))
                return new { error = "reference is required (name of the assembly definition to reference, e.g. 'MyGame.Runtime')" };

            EnsureDirectoryExists(path);

            if (File.Exists(path))
                return new { error = $"Assembly reference already exists at '{path}'" };

            // Resolve to GUID format
            string resolvedRef = ResolveAssemblyReference(targetAssembly);

            var asmref = new Dictionary<string, object>
            {
                { "reference", resolvedRef }
            };

            string json = MiniJson.Serialize(asmref);
            // Pretty-print manually
            json = json.Replace("{", "{\n    ").Replace("}", "\n}");

            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);

            return new
            {
                success = true,
                path = path,
                reference = resolvedRef,
                message = $"Assembly reference created at '{path}' pointing to '{targetAssembly}'"
            };
        }

        // ─── Helpers ───

        private static void EnsureDirectoryExists(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
        }

        private static List<string> BuildStringList(Dictionary<string, object> args, string key)
        {
            if (!args.ContainsKey(key)) return new List<string>();
            if (args[key] is List<object> list)
                return list.Select(item => item.ToString()).ToList();
            if (args[key] is string s && !string.IsNullOrEmpty(s))
                return new List<string> { s };
            return new List<string>();
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (!args.ContainsKey(key)) return defaultValue;
            var val = args[key];
            if (val is bool b) return b;
            if (val is string s) return s.ToLowerInvariant() == "true";
            return defaultValue;
        }

        /// <summary>
        /// Try to resolve an assembly name to its GUID reference format.
        /// If found, returns "GUID:xxx". If not, returns the name as-is (Unity also supports name refs).
        /// </summary>
        private static string ResolveAssemblyReference(string nameOrGuid)
        {
            // Already a GUID reference
            if (nameOrGuid.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                return nameOrGuid;

            // Search for the .asmdef by name
            string[] guids = AssetDatabase.FindAssets("t:asmdef");
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                try
                {
                    string json = File.ReadAllText(assetPath);
                    var asmdef = MiniJson.Deserialize(json) as Dictionary<string, object>;
                    if (asmdef != null && asmdef.ContainsKey("name") &&
                        asmdef["name"].ToString().Equals(nameOrGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        return "GUID:" + guid;
                    }
                }
                catch { /* skip */ }
            }

            // Fallback: return name as-is (Unity supports name references too)
            return nameOrGuid;
        }

        private static string GetPlatformSummary(Dictionary<string, object> asmdef)
        {
            var include = asmdef.ContainsKey("includePlatforms") && asmdef["includePlatforms"] is List<object> inc
                ? inc : new List<object>();
            var exclude = asmdef.ContainsKey("excludePlatforms") && asmdef["excludePlatforms"] is List<object> exc
                ? exc : new List<object>();

            if (include.Count > 0)
                return "Include: " + string.Join(", ", include);
            if (exclude.Count > 0)
                return "Exclude: " + string.Join(", ", exclude);
            return "All Platforms";
        }

        /// <summary>
        /// Format the asmdef dictionary as nicely indented JSON matching Unity's convention.
        /// </summary>
        private static string FormatAsmdefJson(Dictionary<string, object> asmdef)
        {
            // MiniJson.Serialize produces compact JSON; we'll use it and reformat
            string compact = MiniJson.Serialize(asmdef);

            // Simple pretty-printer for asmdef (they're always flat objects with array values)
            var sb = new System.Text.StringBuilder();
            int indent = 0;
            bool inString = false;
            bool lastWasNewline = false;

            for (int i = 0; i < compact.Length; i++)
            {
                char c = compact[i];

                if (c == '"' && (i == 0 || compact[i - 1] != '\\'))
                    inString = !inString;

                if (inString)
                {
                    sb.Append(c);
                    lastWasNewline = false;
                    continue;
                }

                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        // Check if next meaningful char is } or ] (empty container)
                        int peek = i + 1;
                        while (peek < compact.Length && compact[peek] == ' ') peek++;
                        if (peek < compact.Length && (compact[peek] == '}' || compact[peek] == ']'))
                        {
                            // Empty container — keep on same line
                        }
                        else
                        {
                            indent++;
                            sb.Append('\n');
                            sb.Append(new string(' ', indent * 2));
                            lastWasNewline = true;
                        }
                        break;
                    case '}':
                    case ']':
                        indent = Math.Max(0, indent - 1);
                        if (!lastWasNewline)
                        {
                            sb.Append('\n');
                            sb.Append(new string(' ', indent * 2));
                        }
                        else
                        {
                            // Remove extra indent if we just added a newline
                            int extraSpaces = (indent + 1) * 2;
                            if (extraSpaces > 0 && sb.Length >= extraSpaces)
                            {
                                string tail = sb.ToString(sb.Length - extraSpaces, extraSpaces);
                                if (tail == new string(' ', extraSpaces) && sb.Length >= 2)
                                    sb.Remove(sb.Length - 2, 2);
                            }
                        }
                        sb.Append(c);
                        lastWasNewline = false;
                        break;
                    case ',':
                        sb.Append(c);
                        sb.Append('\n');
                        sb.Append(new string(' ', Math.Max(0, indent) * 2));
                        lastWasNewline = true;
                        break;
                    case ':':
                        sb.Append(": ");
                        lastWasNewline = false;
                        break;
                    default:
                        sb.Append(c);
                        lastWasNewline = false;
                        break;
                }
            }

            return sb.ToString() + "\n";
        }
    }
}
