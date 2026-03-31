using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace UnityMCP.Editor
{
    public static class MCPSpriteAtlasCommands
    {
        // ─── Create ───

        public static object CreateSpriteAtlas(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                return Error("path is required (e.g. 'Assets/Atlases/MyAtlas.spriteatlas')");

            if (!path.StartsWith("Assets/"))
                return Error("path must start with 'Assets/'");

            if (!path.EndsWith(".spriteatlas"))
                path += ".spriteatlas";

            if (AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path) != null)
                return Error("SpriteAtlas already exists at '" + path + "'");

            EnsureFolder(System.IO.Path.GetDirectoryName(path));

            var atlas = new SpriteAtlas();
            bool includeInBuild = GetBool(args, "includeInBuild", true);
            atlas.SetIncludeInBuild(includeInBuild);

            AssetDatabase.CreateAsset(atlas, path);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "includeInBuild", includeInBuild }
            };
        }

        // ─── Info ───

        public static object GetSpriteAtlasInfo(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                return Error("path is required");

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null)
                return Error("SpriteAtlas not found at '" + path + "'");

            var packables = atlas.GetPackables();
            var packableList = new List<Dictionary<string, object>>();
            foreach (var p in packables)
            {
                if (p == null) continue;
                packableList.Add(new Dictionary<string, object>
                {
                    { "name", p.name },
                    { "type", p.GetType().Name },
                    { "assetPath", AssetDatabase.GetAssetPath(p) }
                });
            }

            var texSettings = atlas.GetTextureSettings();
            var packSettings = atlas.GetPackingSettings();

            return new Dictionary<string, object>
            {
                { "path", path },
                { "name", atlas.name },
                { "spriteCount", atlas.spriteCount },
                { "isVariant", atlas.isVariant },
                { "includeInBuild", atlas.IsIncludeInBuild() },
                { "packables", packableList },
                { "textureSettings", new Dictionary<string, object>
                    {
                        { "readable", texSettings.readable },
                        { "generateMipMaps", texSettings.generateMipMaps },
                        { "sRGB", texSettings.sRGB },
                        { "filterMode", texSettings.filterMode.ToString() },
                    }
                },
                { "packingSettings", new Dictionary<string, object>
                    {
                        { "blockOffset", packSettings.blockOffset },
                        { "padding", packSettings.padding },
                        { "enableRotation", packSettings.enableRotation },
                        { "enableTightPacking", packSettings.enableTightPacking },
                        { "enableAlphaDilation", packSettings.enableAlphaDilation },
                    }
                }
            };
        }

        // ─── Add ───

        public static object AddToSpriteAtlas(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                return Error("path is required");

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null)
                return Error("SpriteAtlas not found at '" + path + "'");

            var paths = GetStringList(args, "assetPaths", "assetPath");
            if (paths.Count == 0)
                return Error("assetPaths or assetPath is required");

            var objects = new List<UnityEngine.Object>();
            var added = new List<string>();
            var notFound = new List<string>();
            var invalidType = new List<string>();

            foreach (var p in paths)
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p);
                if (obj == null)
                {
                    notFound.Add(p);
                    continue;
                }
                if (!(obj is Texture2D || obj is Sprite || obj is DefaultAsset))
                {
                    invalidType.Add(p + " (" + obj.GetType().Name + ")");
                    continue;
                }
                objects.Add(obj);
                added.Add(p);
            }

            if (objects.Count > 0)
            {
                atlas.Add(objects.ToArray());
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssets();
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "added", added },
                { "notFound", notFound },
                { "invalidType", invalidType },
                { "totalPackables", atlas.GetPackables().Length }
            };
        }

        // ─── Remove ───

        public static object RemoveFromSpriteAtlas(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                return Error("path is required");

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null)
                return Error("SpriteAtlas not found at '" + path + "'");

            var paths = GetStringList(args, "assetPaths", "assetPath");
            if (paths.Count == 0)
                return Error("assetPaths or assetPath is required");

            var currentPackables = atlas.GetPackables();
            var currentPaths = new HashSet<string>();
            foreach (var p in currentPackables)
            {
                if (p != null) currentPaths.Add(AssetDatabase.GetAssetPath(p));
            }

            var objects = new List<UnityEngine.Object>();
            var removed = new List<string>();
            var notFound = new List<string>();
            var notInAtlas = new List<string>();

            foreach (var p in paths)
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p);
                if (obj == null)
                {
                    notFound.Add(p);
                    continue;
                }
                if (!currentPaths.Contains(p))
                {
                    notInAtlas.Add(p);
                    continue;
                }
                objects.Add(obj);
                removed.Add(p);
            }

            if (objects.Count > 0)
            {
                atlas.Remove(objects.ToArray());
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssets();
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "removed", removed },
                { "notFound", notFound },
                { "notInAtlas", notInAtlas },
                { "totalPackables", atlas.GetPackables().Length }
            };
        }

        // ─── Settings ───

        public static object SetSpriteAtlasSettings(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                return Error("path is required");

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null)
                return Error("SpriteAtlas not found at '" + path + "'");

            var updated = new List<string>();

            if (args.ContainsKey("includeInBuild"))
            {
                atlas.SetIncludeInBuild(GetBool(args, "includeInBuild", true));
                updated.Add("includeInBuild");
            }

            var packSettings = atlas.GetPackingSettings();
            bool packChanged = false;
            if (args.ContainsKey("enableRotation"))
            {
                packSettings.enableRotation = GetBool(args, "enableRotation", false);
                updated.Add("enableRotation");
                packChanged = true;
            }
            if (args.ContainsKey("enableTightPacking"))
            {
                packSettings.enableTightPacking = GetBool(args, "enableTightPacking", false);
                updated.Add("enableTightPacking");
                packChanged = true;
            }
            if (args.ContainsKey("padding"))
            {
                packSettings.padding = GetInt(args, "padding", 4);
                updated.Add("padding");
                packChanged = true;
            }
            if (packChanged)
                atlas.SetPackingSettings(packSettings);

            var texSettings = atlas.GetTextureSettings();
            bool texChanged = false;
            if (args.ContainsKey("readable"))
            {
                texSettings.readable = GetBool(args, "readable", false);
                updated.Add("readable");
                texChanged = true;
            }
            if (args.ContainsKey("generateMipMaps"))
            {
                texSettings.generateMipMaps = GetBool(args, "generateMipMaps", false);
                updated.Add("generateMipMaps");
                texChanged = true;
            }
            if (args.ContainsKey("sRGB"))
            {
                texSettings.sRGB = GetBool(args, "sRGB", true);
                updated.Add("sRGB");
                texChanged = true;
            }
            if (args.ContainsKey("filterMode"))
            {
                var fm = GetString(args, "filterMode");
                if (!string.IsNullOrEmpty(fm) && Enum.TryParse<FilterMode>(fm, true, out var mode))
                {
                    texSettings.filterMode = mode;
                    updated.Add("filterMode");
                    texChanged = true;
                }
            }
            if (texChanged)
                atlas.SetTextureSettings(texSettings);

            if (updated.Count == 0)
                return Error("No valid settings provided");

            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "updated", updated }
            };
        }

        // ─── Delete ───

        public static object DeleteSpriteAtlas(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                return Error("path is required");

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null)
                return Error("SpriteAtlas not found at '" + path + "'");

            AssetDatabase.DeleteAsset(path);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "deleted", path }
            };
        }

        // ─── List ───

        public static object ListSpriteAtlases(Dictionary<string, object> args)
        {
            string folder = GetString(args, "folder");
            string[] searchFolders = string.IsNullOrEmpty(folder)
                ? null
                : new[] { folder };

            var guids = searchFolders != null
                ? AssetDatabase.FindAssets("t:SpriteAtlas", searchFolders)
                : AssetDatabase.FindAssets("t:SpriteAtlas");

            var atlases = new List<Dictionary<string, object>>();
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(p);
                if (atlas == null) continue;
                atlases.Add(new Dictionary<string, object>
                {
                    { "path", p },
                    { "name", atlas.name },
                    { "spriteCount", atlas.spriteCount },
                    { "packableCount", atlas.GetPackables().Length },
                    { "isVariant", atlas.isVariant }
                });
            }

            return new Dictionary<string, object>
            {
                { "count", atlases.Count },
                { "atlases", atlases }
            };
        }

        // ─── Helpers ───

        static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "error", message } };
        }

        static string GetString(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.ContainsKey(key)) return null;
            var val = args[key];
            return val != null ? val.ToString() : null;
        }

        static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.ContainsKey(key)) return defaultValue;
            try { return Convert.ToBoolean(args[key]); }
            catch { return defaultValue; }
        }

        static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.ContainsKey(key)) return defaultValue;
            try { return Convert.ToInt32(args[key]); }
            catch { return defaultValue; }
        }

        static List<string> GetStringList(Dictionary<string, object> args, string arrayKey, string singleKey)
        {
            var result = new List<string>();
            if (args == null) return result;

            if (args.ContainsKey(arrayKey))
            {
                var raw = args[arrayKey];
                if (raw is IList<object> list)
                    foreach (var item in list)
                        if (item != null) result.Add(item.ToString());
                else if (raw is string s && !string.IsNullOrEmpty(s))
                    result.Add(s);
            }
            else if (args.ContainsKey(singleKey))
            {
                var val = args[singleKey];
                if (val != null) result.Add(val.ToString());
            }

            return result;
        }

        static void EnsureFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
                return;

            var parts = folderPath.Replace("\\", "/").Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
