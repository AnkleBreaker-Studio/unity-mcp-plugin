using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace UnityMCP.Editor.Welcome
{
    internal static class UnityMcpWelcomeProjectCache
    {
        private const string Key = "AnkleBreaker.Welcome.ProjectCache.v1";
        private static Dictionary<string, object> State
        {
            get
            {
                var state = AppDomain.CurrentDomain.GetData(Key) as Dictionary<string, object>;
                if (state != null) return state;
                state = new Dictionary<string, object> { ["revision"] = 0, ["queries"] = new Dictionary<string, string[]>() };
                AppDomain.CurrentDomain.SetData(Key, state);
                EditorApplication.projectChanged += () => State["revision"] = (int)State["revision"] + 1;
                UnityEditor.PackageManager.Events.registeredPackages += _ => Invalidate();
                return state;
            }
        }

        public static int Revision => (int)State["revision"];

        public static void Invalidate()
        {
            var state = State;
            state["revision"] = (int)state["revision"] + 1;
            ((Dictionary<string, string[]>)state["queries"]).Clear();
        }

        public static string[] FindAssets(string filter, string[] folders = null)
        {
            var queries = (Dictionary<string, string[]>)State["queries"];
            string key = filter + "\n" + (folders == null ? "*" : string.Join("\n", folders));
            if (queries.TryGetValue(key, out var result)) return result;
            if (folders == null && filter.EndsWith(".welcome t:TextAsset", StringComparison.Ordinal))
            {
                string name = filter.Substring(0, filter.Length - " t:TextAsset".Length);
                result = FindAssets("welcome t:TextAsset").Where(g =>
                    Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == name).ToArray();
                queries[key] = result;
                return result;
            }
            result = UnityMcpWelcomePerf.FindAssets(filter, folders);
            queries[key] = result;
            return result;
        }

        public static void Changed(string[] paths)
        {
            var queries = (Dictionary<string, string[]>)State["queries"];
            foreach (string key in new List<string>(queries.Keys))
            {
                string[] parts = key.Split('\n');
                foreach (string path in paths)
                {
                    bool scoped = parts[1] == "*" || parts.Skip(1).Any(folder =>
                        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ||
                        folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) || path == folder);
                    if (!scoped) continue;
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension.Length > 0 && parts[0].Contains("welcome t:TextAsset") && !path.EndsWith(".welcome.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (extension.Length > 0 && extension != ".asset")
                    {
                        if (parts[0].Contains("t:Prefab") && extension != ".prefab") continue;
                        if (parts[0].Contains("t:Scene") && extension != ".unity") continue;
                        if (parts[0].Contains("t:Material") && extension != ".mat" && extension != ".shader" && extension != ".shadergraph") continue;
                    }
                    queries.Remove(key);
                    break;
                }
            }
            State["revision"] = (int)State["revision"] + 1;
        }
    }

    internal sealed class UnityMcpWelcomeCacheChanges : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] previous)
        {
            string[] paths = imported.Concat(deleted).Concat(moved).Concat(previous).ToArray();
            UnityMcpWelcomeProjectCache.Changed(paths);
            UnityMcpWelcomeImages.Changed(paths);
        }
    }
}
