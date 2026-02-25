using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for creating and configuring Particle Systems.
    /// </summary>
    public static class MCPParticleCommands
    {
        // ─── Create Particle System ───

        public static object CreateParticleSystem(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Particle System";

            var go = new GameObject(name);
            var ps = go.AddComponent<ParticleSystem>();
            Undo.RegisterCreatedObjectUndo(go, "Create Particle System");

            if (args.ContainsKey("position") && args["position"] is Dictionary<string, object> pos)
            {
                float x = pos.ContainsKey("x") ? Convert.ToSingle(pos["x"]) : 0;
                float y = pos.ContainsKey("y") ? Convert.ToSingle(pos["y"]) : 0;
                float z = pos.ContainsKey("z") ? Convert.ToSingle(pos["z"]) : 0;
                go.transform.position = new Vector3(x, y, z);
            }

            if (args.ContainsKey("parent"))
            {
                var parent = GameObject.Find(args["parent"].ToString());
                if (parent != null) go.transform.SetParent(parent.transform, true);
            }

            // Apply optional main module settings
            var main = ps.main;
            if (args.ContainsKey("duration")) main.duration = Convert.ToSingle(args["duration"]);
            if (args.ContainsKey("loop")) main.loop = Convert.ToBoolean(args["loop"]);
            if (args.ContainsKey("startLifetime")) main.startLifetime = Convert.ToSingle(args["startLifetime"]);
            if (args.ContainsKey("startSpeed")) main.startSpeed = Convert.ToSingle(args["startSpeed"]);
            if (args.ContainsKey("startSize")) main.startSize = Convert.ToSingle(args["startSize"]);
            if (args.ContainsKey("maxParticles")) main.maxParticles = Convert.ToInt32(args["maxParticles"]);
            if (args.ContainsKey("gravityModifier")) main.gravityModifier = Convert.ToSingle(args["gravityModifier"]);
            if (args.ContainsKey("playOnAwake")) main.playOnAwake = Convert.ToBoolean(args["playOnAwake"]);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", name },
                { "instanceId", go.GetInstanceID() },
            };
        }

        // ─── Get Particle System Info ───

        public static object GetParticleSystemInfo(Dictionary<string, object> args)
        {
            var ps = FindParticleSystem(args);
            if (ps == null) return new { error = "ParticleSystem not found" };

            var main = ps.main;
            var emission = ps.emission;
            var shape = ps.shape;

            return new Dictionary<string, object>
            {
                { "name", ps.gameObject.name },
                { "instanceId", ps.gameObject.GetInstanceID() },
                { "isPlaying", ps.isPlaying },
                { "isPaused", ps.isPaused },
                { "isStopped", ps.isStopped },
                { "particleCount", ps.particleCount },
                { "main", new Dictionary<string, object>
                    {
                        { "duration", main.duration },
                        { "loop", main.loop },
                        { "startLifetime", main.startLifetime.constant },
                        { "startSpeed", main.startSpeed.constant },
                        { "startSize", main.startSize.constant },
                        { "startRotation", main.startRotation.constant },
                        { "maxParticles", main.maxParticles },
                        { "gravityModifier", main.gravityModifier.constant },
                        { "simulationSpace", main.simulationSpace.ToString() },
                        { "playOnAwake", main.playOnAwake },
                        { "scalingMode", main.scalingMode.ToString() },
                    }
                },
                { "emission", new Dictionary<string, object>
                    {
                        { "enabled", emission.enabled },
                        { "rateOverTime", emission.rateOverTime.constant },
                        { "rateOverDistance", emission.rateOverDistance.constant },
                        { "burstCount", emission.burstCount },
                    }
                },
                { "shape", new Dictionary<string, object>
                    {
                        { "enabled", shape.enabled },
                        { "shapeType", shape.shapeType.ToString() },
                        { "radius", shape.radius },
                        { "angle", shape.angle },
                    }
                },
                { "colorOverLifetime", ps.colorOverLifetime.enabled },
                { "sizeOverLifetime", ps.sizeOverLifetime.enabled },
                { "velocityOverLifetime", ps.velocityOverLifetime.enabled },
                { "forceOverLifetime", ps.forceOverLifetime.enabled },
                { "noise", ps.noise.enabled },
                { "collision", ps.collision.enabled },
                { "subEmitters", ps.subEmitters.enabled },
                { "trails", ps.trails.enabled },
            };
        }

        // ─── Set Main Module ───

        public static object SetMainModule(Dictionary<string, object> args)
        {
            var ps = FindParticleSystem(args);
            if (ps == null) return new { error = "ParticleSystem not found" };

            Undo.RecordObject(ps, "Set Particle Main Module");

            var main = ps.main;
            var updated = new List<string>();

            if (args.ContainsKey("duration")) { main.duration = Convert.ToSingle(args["duration"]); updated.Add("duration"); }
            if (args.ContainsKey("loop")) { main.loop = Convert.ToBoolean(args["loop"]); updated.Add("loop"); }
            if (args.ContainsKey("startLifetime")) { main.startLifetime = Convert.ToSingle(args["startLifetime"]); updated.Add("startLifetime"); }
            if (args.ContainsKey("startSpeed")) { main.startSpeed = Convert.ToSingle(args["startSpeed"]); updated.Add("startSpeed"); }
            if (args.ContainsKey("startSize")) { main.startSize = Convert.ToSingle(args["startSize"]); updated.Add("startSize"); }
            if (args.ContainsKey("startRotation")) { main.startRotation = Convert.ToSingle(args["startRotation"]); updated.Add("startRotation"); }
            if (args.ContainsKey("maxParticles")) { main.maxParticles = Convert.ToInt32(args["maxParticles"]); updated.Add("maxParticles"); }
            if (args.ContainsKey("gravityModifier")) { main.gravityModifier = Convert.ToSingle(args["gravityModifier"]); updated.Add("gravityModifier"); }
            if (args.ContainsKey("playOnAwake")) { main.playOnAwake = Convert.ToBoolean(args["playOnAwake"]); updated.Add("playOnAwake"); }

            if (args.ContainsKey("simulationSpace"))
            {
                if (Enum.TryParse<ParticleSystemSimulationSpace>(args["simulationSpace"].ToString(), true, out var space))
                {
                    main.simulationSpace = space;
                    updated.Add("simulationSpace");
                }
            }

            if (updated.Count == 0)
                return new { error = "No valid main module properties provided" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        // ─── Set Emission ───

        public static object SetEmission(Dictionary<string, object> args)
        {
            var ps = FindParticleSystem(args);
            if (ps == null) return new { error = "ParticleSystem not found" };

            Undo.RecordObject(ps, "Set Particle Emission");

            var emission = ps.emission;
            var updated = new List<string>();

            if (args.ContainsKey("enabled")) { emission.enabled = Convert.ToBoolean(args["enabled"]); updated.Add("enabled"); }
            if (args.ContainsKey("rateOverTime")) { emission.rateOverTime = Convert.ToSingle(args["rateOverTime"]); updated.Add("rateOverTime"); }
            if (args.ContainsKey("rateOverDistance")) { emission.rateOverDistance = Convert.ToSingle(args["rateOverDistance"]); updated.Add("rateOverDistance"); }

            if (updated.Count == 0)
                return new { error = "No valid emission properties provided" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        // ─── Set Shape ───

        public static object SetShape(Dictionary<string, object> args)
        {
            var ps = FindParticleSystem(args);
            if (ps == null) return new { error = "ParticleSystem not found" };

            Undo.RecordObject(ps, "Set Particle Shape");

            var shape = ps.shape;
            var updated = new List<string>();

            if (args.ContainsKey("enabled")) { shape.enabled = Convert.ToBoolean(args["enabled"]); updated.Add("enabled"); }
            if (args.ContainsKey("shapeType"))
            {
                if (Enum.TryParse<ParticleSystemShapeType>(args["shapeType"].ToString(), true, out var shapeType))
                {
                    shape.shapeType = shapeType;
                    updated.Add("shapeType");
                }
            }
            if (args.ContainsKey("radius")) { shape.radius = Convert.ToSingle(args["radius"]); updated.Add("radius"); }
            if (args.ContainsKey("angle")) { shape.angle = Convert.ToSingle(args["angle"]); updated.Add("angle"); }
            if (args.ContainsKey("arc")) { shape.arc = Convert.ToSingle(args["arc"]); updated.Add("arc"); }
            if (args.ContainsKey("radiusThickness")) { shape.radiusThickness = Convert.ToSingle(args["radiusThickness"]); updated.Add("radiusThickness"); }

            if (updated.Count == 0)
                return new { error = "No valid shape properties provided" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        // ─── Play / Stop / Pause ───

        public static object PlaybackControl(Dictionary<string, object> args)
        {
            var ps = FindParticleSystem(args);
            if (ps == null) return new { error = "ParticleSystem not found" };

            string action = args.ContainsKey("action") ? args["action"].ToString().ToLower() : "play";

            switch (action)
            {
                case "play": ps.Play(true); break;
                case "stop": ps.Stop(true); break;
                case "pause": ps.Pause(true); break;
                case "restart":
                    ps.Stop(true);
                    ps.Clear(true);
                    ps.Play(true);
                    break;
                case "clear":
                    ps.Clear(true);
                    break;
                default:
                    return new { error = $"Unknown action '{action}'. Use: play, stop, pause, restart, clear" };
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "action", action },
                { "name", ps.gameObject.name },
            };
        }

        // ─── Helper ───

        private static ParticleSystem FindParticleSystem(Dictionary<string, object> args)
        {
            if (args.ContainsKey("path") || args.ContainsKey("gameObjectPath"))
            {
                string path = args.ContainsKey("path") ? args["path"].ToString() : args["gameObjectPath"].ToString();
                var go = GameObject.Find(path);
                return go != null ? go.GetComponent<ParticleSystem>() : null;
            }

            if (args.ContainsKey("instanceId"))
            {
                var obj = EditorUtility.InstanceIDToObject(Convert.ToInt32(args["instanceId"]));
                var go = obj as GameObject;
                return go != null ? go.GetComponent<ParticleSystem>() : null;
            }

            // Try selection
            if (Selection.activeGameObject != null)
                return Selection.activeGameObject.GetComponent<ParticleSystem>();

            return null;
        }
    }
}
