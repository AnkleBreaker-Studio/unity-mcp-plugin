using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace UnityMCP.Editor
{
    public static class MCPAssetCommands
    {
        public static object List(Dictionary<string, object> args)
        {
            string folder = args.ContainsKey("folder") ? args["folder"].ToString() : "Assets";
            string typeFilter = args.ContainsKey("type") ? args["type"].ToString() : null;
            string search = args.ContainsKey("search") ? args["search"].ToString() : null;
            bool recursive = !args.ContainsKey("recursive") || Convert.ToBoolean(args["recursive"]);
            int maxResults = 500;
            if (args.TryGetValue("maxResults", out var limit)
                && (limit == null || !int.TryParse(limit.ToString(), out maxResults)))
                return new { error = "maxResults must be an integer from 1 to 10000" };
            if (maxResults < 1 || maxResults > 10000)
                return new { error = "maxResults must be an integer from 1 to 10000" };

            string searchQuery = "";
            if (!string.IsNullOrEmpty(search))
                searchQuery = search;
            if (!string.IsNullOrEmpty(typeFilter))
                searchQuery += $" t:{typeFilter}";

            string[] guids;
            if (!string.IsNullOrEmpty(searchQuery))
            {
                string[] searchFolders = recursive ? new[] { folder } : new[] { folder };
                guids = AssetDatabase.FindAssets(searchQuery.Trim(), searchFolders);
            }
            else
            {
                guids = AssetDatabase.FindAssets("", new[] { folder });
            }

            var assets = new List<Dictionary<string, object>>();
            int totalCount = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // If not recursive, only include direct children
                if (!recursive)
                {
                    string parentDir = Path.GetDirectoryName(path).Replace("\\", "/");
                    if (parentDir != folder) continue;
                }

                totalCount++;
                // Count matches without querying types for assets that will not be returned.
                if (assets.Count >= maxResults) continue;
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                assets.Add(new Dictionary<string, object>
                {
                    { "path", path },
                    { "name", Path.GetFileName(path) },
                    { "type", assetType?.Name ?? "Unknown" },
                    { "guid", guid },
                    { "isFolder", AssetDatabase.IsValidFolder(path) },
                });
            }

            return new Dictionary<string, object>
            {
                { "folder", folder },
                { "count", assets.Count },
                { "totalCount", totalCount },
                { "maxResults", maxResults },
                { "truncated", totalCount > assets.Count },
                { "assets", assets },
            };
        }

        public static object Import(Dictionary<string, object> args)
        {
            string source = args.ContainsKey("sourcePath") ? args["sourcePath"].ToString() : "";
            string dest = args.ContainsKey("destinationPath") ? args["destinationPath"].ToString() : "";

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
                return new { error = "sourcePath and destinationPath are required" };

            if (!File.Exists(source))
                return new { error = $"Source file not found: {source}" };

            // Confine the destination under the project (correct root, no traversal escape).
            if (!MCPAssetSafety.TryResolveProjectPath(dest, out string fullDest, out string pathError))
                return new { error = pathError };

            // Don't silently overwrite an existing project asset unless asked.
            var overwriteError = MCPAssetSafety.OverwriteGuard(dest, args);
            if (overwriteError != null)
                return overwriteError;

            string destDir = Path.GetDirectoryName(fullDest);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(source, fullDest, true);
            AssetDatabase.ImportAsset(MCPAssetSafety.ToAssetDatabasePath(dest));

            return new { success = true, importedPath = dest };
        }

        public static object Delete(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            // Confine to the project like every other asset writer — a traversal/absolute path
            // reached AssetDatabase.DeleteAsset raw before this.
            if (!MCPAssetSafety.TryResolveProjectPath(path, out _, out var pathError))
                return new { error = pathError };
            string assetPath = MCPAssetSafety.ToAssetDatabasePath(path);

            // Existence check that holds on the whole supported range (AssetPathExists is 2023.2+,
            // this package targets 2021.3): a real asset loads, a folder answers IsValidFolder.
            bool exists = AssetDatabase.IsValidFolder(assetPath)
                || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null;
            if (!exists)
                return new { error = $"Asset not found: {assetPath}" };

            // DeleteAsset on a FOLDER is silently recursive: one truncated path
            // ('Assets/Art/Materials' instead of the .mat) takes the whole tree. Asset deletion
            // also registers nothing on the undo stack, so undo/last cannot bring it back —
            // require an explicit opt-in and disclose the blast radius first.
            bool isFolder = AssetDatabase.IsValidFolder(assetPath);
            if (isFolder)
            {
                var contained = AssetDatabase.FindAssets("", new[] { assetPath });
                if (!(args.ContainsKey("recursive") && Convert.ToBoolean(args["recursive"])))
                    return new
                    {
                        error = $"'{assetPath}' is a FOLDER containing {contained.Length} asset(s). Deleting it removes them all and is not undoable. Pass recursive:true to confirm.",
                        requiresRecursive = true,
                        isFolder = true,
                        assetCount = contained.Length,
                    };
            }

            // Default to the OS trash so a mistake stays recoverable; permanent:true keeps the
            // old hard-delete behaviour for callers that really mean it.
            bool permanent = args.ContainsKey("permanent") && Convert.ToBoolean(args["permanent"]);
            bool deleted = permanent
                ? AssetDatabase.DeleteAsset(assetPath)
                : AssetDatabase.MoveAssetToTrash(assetPath);

            return new
            {
                success = deleted,
                path = assetPath,
                isFolder,
                recoverable = !permanent,
                method = permanent ? "deleted permanently" : "moved to OS trash",
            };
        }

        public static object CreatePrefab(Dictionary<string, object> args)
        {
            string goPath = args.ContainsKey("gameObjectPath") ? args["gameObjectPath"].ToString() : "";
            string savePath = args.ContainsKey("savePath") ? args["savePath"].ToString() : "";

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            if (string.IsNullOrEmpty(savePath))
                return new { error = "savePath is required" };

            // Same confinement + clobber guard CreateMaterial already uses below. Without it,
            // saving over an existing prefab kept the .meta GUID, so every scene reference
            // silently re-bound to the new asset instead of erroring.
            if (!MCPAssetSafety.TryResolveProjectPath(savePath, out _, out var prefabPathError))
                return new { error = prefabPathError };
            var prefabOverwrite = MCPAssetSafety.OverwriteGuard(savePath, args);
            if (prefabOverwrite != null) return prefabOverwrite;

            // Ensure directory exists
            string dir = Path.GetDirectoryName(savePath)?.Replace('\\', '/');
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

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, savePath);
            return new Dictionary<string, object>
            {
                { "success", prefab != null },
                { "path", savePath },
                { "name", prefab?.name },
            };
        }

        public static object InstantiatePrefab(Dictionary<string, object> args)
        {
            string prefabPath = args.ContainsKey("prefabPath") ? args["prefabPath"].ToString() : "";
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required" };

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found at {prefabPath}" };

            // Resolve the parent before instantiating (the shared lookup also finds inactive objects),
            // so an unresolved parent is an error and leaves nothing in the scene.
            string parentPath = args.ContainsKey("parent") ? args["parent"]?.ToString() : null;
            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parent = MCPGameObjectCommands.FindGameObject(new Dictionary<string, object> { { "path", parentPath } });
                if (parent == null)
                    return new { error = $"Parent GameObject '{parentPath}' not found" };
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return new { error = "Failed to instantiate prefab" };

            if (args.ContainsKey("name"))
                instance.name = args["name"].ToString();

            if (args.ContainsKey("position"))
                instance.transform.position = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);

            if (args.ContainsKey("rotation"))
                instance.transform.eulerAngles = MCPGameObjectCommands.DictToVector3(args["rotation"] as Dictionary<string, object>);

            if (parent != null) instance.transform.SetParent(parent.transform);

            Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", instance.name },
                { "instanceId", MCPObjectId.Get(instance) },
                { "position", MCPGameObjectCommands.Vector3ToDict(instance.transform.position) },
                { "parent", parent != null ? MCPGameObjectCommands.GetHierarchyPath(parent) : "root" },
            };
        }

        public static object CreateMaterial(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string shaderName = args.ContainsKey("shader") ? args["shader"].ToString() : "Standard";

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var shader = Shader.Find(shaderName);
            if (shader == null) return new { error = $"Shader '{shaderName}' not found" };

            Dictionary<string, object> properties = null;
            if (args.ContainsKey("properties") && args["properties"] != null)
            {
                properties = args["properties"] as Dictionary<string, object>;
                if (properties == null)
                    return new { error = "properties must be an object of shader property names to values" };
            }

            // Don't reset an existing tuned material back to defaults.
            var materialOverwrite = MCPAssetSafety.OverwriteGuard(path, args);
            if (materialOverwrite != null)
                return materialOverwrite;

            var material = new Material(shader);

            if (args.ContainsKey("color"))
            {
                var cd = args["color"] as Dictionary<string, object>;
                if (cd != null)
                {
                    material.color = new Color(
                        Convert.ToSingle(cd.GetValueOrDefault("r", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("g", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("b", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("a", 1f))
                    );
                }
            }

            var appliedProperties = new List<string>();
            var ignoredProperties = new List<Dictionary<string, object>>();
            if (properties != null)
            {
                foreach (var entry in properties)
                {
                    string reason = ApplyShaderProperty(material, entry.Key, entry.Value);
                    if (reason == null)
                        appliedProperties.Add(entry.Key);
                    else
                        ignoredProperties.Add(new Dictionary<string, object> { { "name", entry.Key }, { "reason", reason } });
                }
            }

            // Ensure directory exists (normalize backslashes from Path.GetDirectoryName on Windows)
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
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

            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "shader", shaderName },
            };
            if (properties != null)
            {
                result["appliedProperties"] = appliedProperties;
                result["ignoredProperties"] = ignoredProperties;
            }
            return result;
        }

        // Sets one shader property through the type the shader declares for it. Returns null on
        // success, otherwise why the value was not applied so the caller can report it.
        private static string ApplyShaderProperty(Material material, string name, object value)
        {
            int index = material.shader.FindPropertyIndex(name);
            if (index < 0)
                return $"shader '{material.shader.name}' has no property '{name}'";

            var propertyType = material.shader.GetPropertyType(index);
            try
            {
                switch (propertyType)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (!TryGetNumber(value, out float number))
                            return $"{propertyType} property expects a number";
                        material.SetFloat(name, number);
                        return null;
                    case ShaderPropertyType.Int:
                        if (!TryGetNumber(value, out float integer) || integer != Mathf.Round(integer)
                            || integer < int.MinValue || (double)integer > int.MaxValue)
                            return "Int property expects an integer";
                        material.SetInteger(name, (int)integer);
                        return null;
                    case ShaderPropertyType.Color:
                        if (!(value is Dictionary<string, object> color))
                            return "Color property expects an object {r, g, b, a}";
                        material.SetColor(name, new Color(
                            MCPArgs.GetFloat(color, "r", 1f), MCPArgs.GetFloat(color, "g", 1f),
                            MCPArgs.GetFloat(color, "b", 1f), MCPArgs.GetFloat(color, "a", 1f)));
                        return null;
                    case ShaderPropertyType.Vector:
                        if (!(value is Dictionary<string, object> vector))
                            return "Vector property expects an object {x, y, z, w}";
                        material.SetVector(name, new Vector4(
                            MCPArgs.GetFloat(vector, "x", 0f), MCPArgs.GetFloat(vector, "y", 0f),
                            MCPArgs.GetFloat(vector, "z", 0f), MCPArgs.GetFloat(vector, "w", 0f)));
                        return null;
                    case ShaderPropertyType.Texture:
                        if (value == null)
                        {
                            material.SetTexture(name, null);
                            return null;
                        }
                        var texture = value is string texturePath ? AssetDatabase.LoadAssetAtPath<Texture>(texturePath) : null;
                        if (texture == null)
                            return $"Texture property expects the asset path of a texture (got '{value}')";
                        material.SetTexture(name, texture);
                        return null;
                    default:
                        return $"{propertyType} properties are not supported";
                }
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        private static bool TryGetNumber(object value, out float number)
        {
            switch (value)
            {
                case double d: number = (float)d; return true;
                case float f: number = f; return true;
                case long l: number = l; return true;
                case int i: number = i; return true;
                // Shader toggles are Float properties, so accept booleans as 1/0.
                case bool flag: number = flag ? 1f : 0f; return true;
                case string s: return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                default: number = 0f; return false;
            }
        }
    }
}
