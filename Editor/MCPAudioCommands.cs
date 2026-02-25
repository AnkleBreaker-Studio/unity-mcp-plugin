using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPAudioCommands
    {
        public static object GetAudioInfo(Dictionary<string, object> args)
        {
            var sources = UnityEngine.Object.FindObjectsOfType<AudioSource>();
            var sourceList = new List<Dictionary<string, object>>();
            foreach (var src in sources)
            {
                sourceList.Add(new Dictionary<string, object>
                {
                    { "gameObject", src.gameObject.name },
                    { "instanceId", src.gameObject.GetInstanceID() },
                    { "clip", src.clip != null ? src.clip.name : null },
                    { "volume", src.volume },
                    { "pitch", src.pitch },
                    { "loop", src.loop },
                    { "playOnAwake", src.playOnAwake },
                    { "spatialBlend", src.spatialBlend },
                    { "mute", src.mute },
                    { "enabled", src.enabled },
                });
            }

            var listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
            return new Dictionary<string, object>
            {
                { "sourceCount", sourceList.Count },
                { "sources", sourceList },
                { "listenerCount", listeners.Length },
                { "globalVolume", AudioListener.volume },
            };
        }

        public static object CreateAudioSource(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            bool createNew = go == null;
            
            if (createNew)
            {
                string name = args.ContainsKey("name") ? args["name"].ToString() : "Audio Source";
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, $"Create Audio Source {name}");
                
                if (args.ContainsKey("position"))
                    go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);
            }

            var source = go.GetComponent<AudioSource>();
            if (source == null)
                source = Undo.AddComponent<AudioSource>(go);

            if (args.ContainsKey("clipPath"))
            {
                string clipPath = args["clipPath"].ToString();
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip != null) source.clip = clip;
            }

            if (args.ContainsKey("volume")) source.volume = Convert.ToSingle(args["volume"]);
            if (args.ContainsKey("pitch")) source.pitch = Convert.ToSingle(args["pitch"]);
            if (args.ContainsKey("loop")) source.loop = Convert.ToBoolean(args["loop"]);
            if (args.ContainsKey("playOnAwake")) source.playOnAwake = Convert.ToBoolean(args["playOnAwake"]);
            if (args.ContainsKey("spatialBlend")) source.spatialBlend = Convert.ToSingle(args["spatialBlend"]);
            if (args.ContainsKey("minDistance")) source.minDistance = Convert.ToSingle(args["minDistance"]);
            if (args.ContainsKey("maxDistance")) source.maxDistance = Convert.ToSingle(args["maxDistance"]);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "instanceId", go.GetInstanceID() },
                { "clip", source.clip != null ? source.clip.name : null },
                { "volume", source.volume },
                { "loop", source.loop },
            };
        }

        public static object SetGlobalAudio(Dictionary<string, object> args)
        {
            if (args.ContainsKey("volume"))
                AudioListener.volume = Convert.ToSingle(args["volume"]);
            if (args.ContainsKey("pause"))
                AudioListener.pause = Convert.ToBoolean(args["pause"]);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "globalVolume", AudioListener.volume },
                { "paused", AudioListener.pause },
            };
        }
    }
}
