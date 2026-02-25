using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Lighting commands: light management, environment settings, light probes, reflection probes.
    /// </summary>
    public static class MCPLightingCommands
    {
        public static object GetLightingInfo(Dictionary<string, object> args)
        {
            var lights = UnityEngine.Object.FindObjectsOfType<Light>();
            var lightList = new List<Dictionary<string, object>>();
            foreach (var light in lights)
            {
                var info = new Dictionary<string, object>
                {
                    { "name", light.gameObject.name },
                    { "instanceId", light.gameObject.GetInstanceID() },
                    { "type", light.type.ToString() },
                    { "color", new Dictionary<string, object> { { "r", light.color.r }, { "g", light.color.g }, { "b", light.color.b }, { "a", light.color.a } } },
                    { "intensity", light.intensity },
                    { "range", light.range },
                    { "spotAngle", light.spotAngle },
                    { "shadows", light.shadows.ToString() },
                    { "enabled", light.enabled },
                    { "renderMode", light.renderMode.ToString() },
                };
                lightList.Add(info);
            }

            return new Dictionary<string, object>
            {
                { "lightCount", lightList.Count },
                { "lights", lightList },
                { "ambientMode", RenderSettings.ambientMode.ToString() },
                { "ambientColor", ColorToDict(RenderSettings.ambientLight) },
                { "ambientIntensity", RenderSettings.ambientIntensity },
                { "fogEnabled", RenderSettings.fog },
                { "fogColor", ColorToDict(RenderSettings.fogColor) },
                { "fogDensity", RenderSettings.fogDensity },
                { "skybox", RenderSettings.skybox != null ? RenderSettings.skybox.name : null },
            };
        }

        public static object CreateLight(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "New Light";
            string typeStr = args.ContainsKey("lightType") ? args["lightType"].ToString() : "Point";

            LightType lightType;
            if (!Enum.TryParse(typeStr, true, out lightType))
                return new { error = $"Invalid light type: {typeStr}. Use Point, Directional, Spot, or Area." };

            var go = new GameObject(name);
            var light = go.AddComponent<Light>();
            light.type = lightType;

            if (args.ContainsKey("color"))
            {
                var cd = args["color"] as Dictionary<string, object>;
                if (cd != null) light.color = DictToColor(cd);
            }

            if (args.ContainsKey("intensity"))
                light.intensity = Convert.ToSingle(args["intensity"]);

            if (args.ContainsKey("range"))
                light.range = Convert.ToSingle(args["range"]);

            if (args.ContainsKey("spotAngle"))
                light.spotAngle = Convert.ToSingle(args["spotAngle"]);

            if (args.ContainsKey("shadows"))
            {
                LightShadows shadows;
                if (Enum.TryParse(args["shadows"].ToString(), true, out shadows))
                    light.shadows = shadows;
            }

            if (args.ContainsKey("position"))
                go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);

            if (args.ContainsKey("rotation"))
                go.transform.eulerAngles = MCPGameObjectCommands.DictToVector3(args["rotation"] as Dictionary<string, object>);

            Undo.RegisterCreatedObjectUndo(go, $"Create Light {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", go.GetInstanceID() },
                { "lightType", lightType.ToString() },
                { "intensity", light.intensity },
                { "position", MCPGameObjectCommands.Vector3ToDict(go.transform.position) },
            };
        }

        public static object SetEnvironment(Dictionary<string, object> args)
        {
            if (args.ContainsKey("ambientMode"))
            {
                AmbientMode mode;
                if (Enum.TryParse(args["ambientMode"].ToString(), true, out mode))
                    RenderSettings.ambientMode = mode;
            }

            if (args.ContainsKey("ambientColor"))
            {
                var cd = args["ambientColor"] as Dictionary<string, object>;
                if (cd != null) RenderSettings.ambientLight = DictToColor(cd);
            }

            if (args.ContainsKey("ambientIntensity"))
                RenderSettings.ambientIntensity = Convert.ToSingle(args["ambientIntensity"]);

            if (args.ContainsKey("fogEnabled"))
                RenderSettings.fog = Convert.ToBoolean(args["fogEnabled"]);

            if (args.ContainsKey("fogColor"))
            {
                var cd = args["fogColor"] as Dictionary<string, object>;
                if (cd != null) RenderSettings.fogColor = DictToColor(cd);
            }

            if (args.ContainsKey("fogDensity"))
                RenderSettings.fogDensity = Convert.ToSingle(args["fogDensity"]);

            if (args.ContainsKey("fogMode"))
            {
                FogMode mode;
                if (Enum.TryParse(args["fogMode"].ToString(), true, out mode))
                    RenderSettings.fogMode = mode;
            }

            if (args.ContainsKey("skyboxMaterialPath"))
            {
                string matPath = args["skyboxMaterialPath"].ToString();
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat != null) RenderSettings.skybox = mat;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "ambientMode", RenderSettings.ambientMode.ToString() },
                { "ambientColor", ColorToDict(RenderSettings.ambientLight) },
                { "fogEnabled", RenderSettings.fog },
                { "fogColor", ColorToDict(RenderSettings.fogColor) },
                { "fogDensity", RenderSettings.fogDensity },
            };
        }

        public static object CreateReflectionProbe(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Reflection Probe";

            var go = new GameObject(name);
            var probe = go.AddComponent<ReflectionProbe>();

            if (args.ContainsKey("position"))
                go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);

            if (args.ContainsKey("size"))
                probe.size = MCPGameObjectCommands.DictToVector3(args["size"] as Dictionary<string, object>);

            if (args.ContainsKey("resolution"))
                probe.resolution = Convert.ToInt32(args["resolution"]);

            if (args.ContainsKey("mode"))
            {
                ReflectionProbeMode mode;
                if (Enum.TryParse(args["mode"].ToString(), true, out mode))
                    probe.mode = mode;
            }

            Undo.RegisterCreatedObjectUndo(go, $"Create Reflection Probe {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", go.GetInstanceID() },
                { "position", MCPGameObjectCommands.Vector3ToDict(go.transform.position) },
                { "size", MCPGameObjectCommands.Vector3ToDict(probe.size) },
            };
        }

        public static object CreateLightProbeGroup(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Light Probe Group";

            var go = new GameObject(name);
            var group = go.AddComponent<LightProbeGroup>();

            if (args.ContainsKey("position"))
                go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);

            Undo.RegisterCreatedObjectUndo(go, $"Create Light Probe Group {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", go.GetInstanceID() },
                { "probeCount", group.probePositions.Length },
            };
        }

        // ─── Helpers ───

        private static Dictionary<string, object> ColorToDict(Color c)
        {
            return new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } };
        }

        private static Color DictToColor(Dictionary<string, object> d)
        {
            return new Color(
                d.ContainsKey("r") ? Convert.ToSingle(d["r"]) : 1f,
                d.ContainsKey("g") ? Convert.ToSingle(d["g"]) : 1f,
                d.ContainsKey("b") ? Convert.ToSingle(d["b"]) : 1f,
                d.ContainsKey("a") ? Convert.ToSingle(d["a"]) : 1f
            );
        }
    }
}
