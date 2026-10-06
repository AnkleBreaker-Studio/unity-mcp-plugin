using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Welcome
{
    internal static class UnityMcpWelcomeImages
    {
        private const string Key = "AnkleBreaker.Welcome.Images.v1";
        private const long Budget = 128L * 1024 * 1024;

        private static Dictionary<string, object> State
        {
            get
            {
                var state = AppDomain.CurrentDomain.GetData(Key) as Dictionary<string, object>;
                if (state != null) return state;
                state = new Dictionary<string, object>
                {
                    ["cache"] = new Dictionary<string, Texture2D>(), ["pins"] = new Dictionary<Texture2D, int>(),
                    ["replaced"] = new HashSet<Texture2D>(),
                    ["pending"] = new HashSet<string>(), ["failed"] = new HashSet<string>(), ["retired"] = new HashSet<string>(), ["queue"] = new Queue<string>(),
                    ["listeners"] = new List<Action<string>>(), ["users"] = 0, ["poll"] = (EditorApplication.CallbackFunction)Poll
                };
                AppDomain.CurrentDomain.SetData(Key, state);
                AssemblyReloadEvents.beforeAssemblyReload += Clear;
                EditorApplication.quitting += Clear;
                return state;
            }
        }

        public static void Subscribe(Action<string> listener) => ((List<Action<string>>)State["listeners"]).Add(listener);
        public static void Acquire() => State["users"] = (int)State["users"] + 1;
        public static void Release()
        {
            State["users"] = Math.Max(0, (int)State["users"] - 1);
            if ((int)State["users"] == 0) Clear();
        }

        public static void Pin(Texture2D texture, int delta)
        {
            if (texture == null) return;
            var pins = (Dictionary<Texture2D, int>)State["pins"];
            pins.TryGetValue(texture, out int count);
            if (count + delta <= 0) pins.Remove(texture);
            else pins[texture] = count + delta;
            if (!pins.ContainsKey(texture) && ((HashSet<Texture2D>)State["replaced"]).Remove(texture))
                UnityEngine.Object.DestroyImmediate(texture);
            if (delta < 0) RetireUnused();
        }

        public static void Changed(string[] paths)
        {
            var state = AppDomain.CurrentDomain.GetData(Key) as Dictionary<string, object>;
            if (state == null) return;
            var cache = (Dictionary<string, Texture2D>)state["cache"];
            foreach (string path in paths)
            {
                bool changed = ((HashSet<string>)state["failed"]).Remove(path);
                if (((HashSet<string>)state["pending"]).Remove(path))
                {
                    changed = true;
                    if (state.TryGetValue("path", out object current) && (string)current == path) state.Remove("read");
                    var queue = (Queue<string>)state["queue"];
                    int count = queue.Count;
                    for (int i = 0; i < count; i++) { string queued = queue.Dequeue(); if (queued != path) queue.Enqueue(queued); }
                }
                if (cache.TryGetValue(path, out var texture))
                {
                    changed = true;
                    cache.Remove(path);
                    if (((Dictionary<Texture2D, int>)state["pins"]).ContainsKey(texture))
                        ((HashSet<Texture2D>)state["replaced"]).Add(texture);
                    else UnityEngine.Object.DestroyImmediate(texture);
                }
                if (!changed) continue;
                foreach (Action<string> listener in ((List<Action<string>>)state["listeners"]).ToArray()) listener(path);
            }
        }

        public static void Retire(string path)
        {
            if (!string.IsNullOrEmpty(path)) ((HashSet<string>)State["retired"]).Add(path);
            RetireUnused();
        }

        private static void RetireUnused()
        {
            var state = State;
            var retired = (HashSet<string>)state["retired"];
            if (retired.Count == 0) return;
            var cache = (Dictionary<string, Texture2D>)state["cache"];
            var pins = (Dictionary<Texture2D, int>)state["pins"];
            foreach (string path in new List<string>(retired))
            {
                if (cache.TryGetValue(path, out var texture))
                {
                    if (pins.ContainsKey(texture)) continue;
                    cache.Remove(path);
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                if (((HashSet<string>)state["pending"]).Contains(path)) continue;
                retired.Remove(path);
            }
        }

        public static Texture2D Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var state = State;
            if (((Dictionary<string, Texture2D>)state["cache"]).TryGetValue(path, out var texture) && texture != null) return texture;
            if (((HashSet<string>)state["failed"]).Contains(path) || ((HashSet<string>)state["pending"]).Contains(path) || !File.Exists(path)) return null;
            if (((HashSet<string>)state["pending"]).Add(path))
            {
                ((Queue<string>)state["queue"]).Enqueue(path);
                EditorApplication.update -= (EditorApplication.CallbackFunction)State["poll"];
                EditorApplication.update += (EditorApplication.CallbackFunction)State["poll"];
            }
            return null;
        }

        public static bool HasUsableFile(string path) => !string.IsNullOrEmpty(path) &&
            !((HashSet<string>)State["failed"]).Contains(path) &&
            (((Dictionary<string, Texture2D>)State["cache"]).TryGetValue(path, out var texture) && texture != null ||
                ((HashSet<string>)State["pending"]).Contains(path) || File.Exists(path));

        private static void Poll()
        {
            var state = State;
            state.TryGetValue("read", out object work);
            var read = work as Task<byte[]>;
            if (read != null)
            {
                if (!read.IsCompleted) return;
                string path = (string)state["path"];
                state.Remove("read");
                byte[] bytes = read.GetAwaiter().GetResult();
                Texture2D texture = null;
                try
                {
                    using var perf = new UnityMcpWelcomePerf.Scope("DecodeImage");
                    if (bytes != null)
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                        if (!texture.LoadImage(bytes, true)) { UnityEngine.Object.DestroyImmediate(texture); texture = null; }
                    }
                }
                catch (Exception) { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); texture = null; }
                ((HashSet<string>)state["pending"]).Remove(path);
                if (texture != null)
                {
                    texture.filterMode = FilterMode.Bilinear;
                    ((Dictionary<string, Texture2D>)state["cache"])[path] = texture;
                }
                else ((HashSet<string>)state["failed"]).Add(path);
                foreach (Action<string> listener in ((List<Action<string>>)state["listeners"]).ToArray()) listener(path);
                // A superseded cover may finish decoding after retirement; visible consumers pin it above.
                RetireUnused();
                Trim();
            }
            var queue = (Queue<string>)state["queue"];
            if (queue.Count == 0) { EditorApplication.update -= (EditorApplication.CallbackFunction)State["poll"]; return; }
            string next = queue.Dequeue();
            state["path"] = next;
            state["read"] = Task.Run(() => { try { return File.ReadAllBytes(next); } catch (Exception) { return null; } });
        }

        private static void Trim()
        {
            var cache = (Dictionary<string, Texture2D>)State["cache"];
            var pins = (Dictionary<Texture2D, int>)State["pins"];
            long bytes = 0;
            foreach (var texture in cache.Values) bytes += (long)texture.width * texture.height * 4;
            if (bytes <= Budget) return;
            foreach (string path in new List<string>(cache.Keys))
            {
                Texture2D texture = cache[path];
                if (pins.ContainsKey(texture)) continue;
                bytes -= (long)texture.width * texture.height * 4;
                cache.Remove(path);
                UnityEngine.Object.DestroyImmediate(texture);
                if (bytes <= Budget) break;
            }
        }

        public static void ClearIfUnused() { if ((int)State["users"] == 0) Clear(); }

        public static void Clear()
        {
            EditorApplication.update -= (EditorApplication.CallbackFunction)State["poll"];
            var state = State;
            foreach (var texture in ((Dictionary<string, Texture2D>)state["cache"]).Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            foreach (var texture in ((HashSet<Texture2D>)state["replaced"]))
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            ((HashSet<Texture2D>)state["replaced"]).Clear();
            ((Dictionary<string, Texture2D>)state["cache"]).Clear();
            ((Dictionary<Texture2D, int>)state["pins"]).Clear();
            ((HashSet<string>)state["pending"]).Clear();
            ((HashSet<string>)state["failed"]).Clear();
            ((HashSet<string>)state["retired"]).Clear();
            ((Queue<string>)state["queue"]).Clear();
            state.Remove("read");
        }
    }
}
