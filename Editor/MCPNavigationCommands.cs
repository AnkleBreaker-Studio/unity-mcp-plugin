using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for NavMesh navigation system.
    /// </summary>
    public static class MCPNavigationCommands
    {
        // ─── Bake NavMesh ───

        public static object BakeNavMesh(Dictionary<string, object> args)
        {
            // NavMesh.GetSettingsByIndex returns a struct copy, so assigning agent values to it
            // never reached the bake. The legacy bake reads the active scene's NavMesh build
            // settings (what the Navigation window's Bake tab edits): write those instead.
            Dictionary<string, object> agentSettings = null;
            if (AgentBuildSettingNames.Any(args.ContainsKey))
            {
                var settingsError = ApplyAgentBuildSettings(args, out agentSettings);
                if (settingsError != null) return settingsError;
            }

#pragma warning disable CS0618 // NavMeshBuilder: migration to NavMeshSurface API deferred
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
#pragma warning restore CS0618

            var triangulation = NavMesh.CalculateTriangulation();
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "vertices", triangulation.vertices.Length },
                { "triangles", triangulation.indices.Length / 3 },
            };
            if (agentSettings != null)
            {
                result["agentSettings"] = agentSettings;
                result["agentSettingsScope"] = "Written to the active scene's NavMesh bake settings; they persist with the scene.";
            }
            return result;
        }

        private static readonly string[] AgentBuildSettingNames = { "agentRadius", "agentHeight", "agentSlope", "agentClimb" };

        /// <summary>
        /// Write the requested agent* arguments to the active scene's m_BuildSettings and read
        /// them back. Returns an error object (nothing is baked) when a value is out of range or
        /// a property is missing on this Unity version, otherwise null with the effective values.
        /// </summary>
        private static object ApplyAgentBuildSettings(Dictionary<string, object> args, out Dictionary<string, object> effective)
        {
            effective = null;
            var requested = new Dictionary<string, float>();
            foreach (var name in AgentBuildSettingNames)
            {
                if (!args.ContainsKey(name)) continue;
                float value = Convert.ToSingle(args[name]);
                bool valid = name == "agentSlope" ? value >= 0f && value <= 60f
                    : name == "agentClimb" ? value >= 0f
                    : value > 0f;
                if (!valid)
                    return new { error = $"{name} is out of range: {value} (agentRadius and agentHeight must be > 0, agentSlope within [0, 60], agentClimb >= 0). NavMesh was not baked." };
                requested[name] = value;
            }

#pragma warning disable CS0618 // NavMeshBuilder: migration to NavMeshSurface API deferred
            var settingsObject = UnityEditor.AI.NavMeshBuilder.navMeshSettingsObject;
#pragma warning restore CS0618
            if (settingsObject == null)
                return new { error = "The active scene has no NavMesh settings object; agent settings cannot be applied. NavMesh was not baked." };

            var serialized = new SerializedObject(settingsObject);
            var properties = new Dictionary<string, SerializedProperty>();
            foreach (var name in AgentBuildSettingNames)
            {
                var property = serialized.FindProperty("m_BuildSettings." + name);
                if (property == null || property.propertyType != SerializedPropertyType.Float)
                    return new { error = $"NavMesh setting m_BuildSettings.{name} was not found on this Unity version; agent settings cannot be applied. NavMesh was not baked." };
                properties[name] = property;
            }

            foreach (var entry in requested)
                properties[entry.Key].floatValue = entry.Value;
            serialized.ApplyModifiedProperties();
            if (!EditorApplication.isPlaying)
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            serialized.Update();
            foreach (var entry in requested)
            {
                if (!Mathf.Approximately(properties[entry.Key].floatValue, entry.Value))
                    return new { error = $"NavMesh setting {entry.Key} did not persist (requested {entry.Value}, read back {properties[entry.Key].floatValue}). NavMesh was not baked." };
            }

            effective = new Dictionary<string, object>();
            foreach (var name in AgentBuildSettingNames)
                effective[name] = properties[name].floatValue;
            return null;
        }

        // ─── Clear NavMesh ───

        public static object ClearNavMesh(Dictionary<string, object> args)
        {
#pragma warning disable CS0618 // NavMeshBuilder: migration to NavMeshSurface API deferred
            UnityEditor.AI.NavMeshBuilder.ClearAllNavMeshes();
#pragma warning restore CS0618
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", "All NavMeshes cleared" },
            };
        }

        // ─── Add NavMeshAgent ───

        public static object AddNavMeshAgent(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                Undo.AddComponent<NavMeshAgent>(go);
                agent = go.GetComponent<NavMeshAgent>();
            }

            if (args.ContainsKey("speed"))
                agent.speed = Convert.ToSingle(args["speed"]);
            if (args.ContainsKey("angularSpeed"))
                agent.angularSpeed = Convert.ToSingle(args["angularSpeed"]);
            if (args.ContainsKey("acceleration"))
                agent.acceleration = Convert.ToSingle(args["acceleration"]);
            if (args.ContainsKey("stoppingDistance"))
                agent.stoppingDistance = Convert.ToSingle(args["stoppingDistance"]);
            if (args.ContainsKey("radius"))
                agent.radius = Convert.ToSingle(args["radius"]);
            if (args.ContainsKey("height"))
                agent.height = Convert.ToSingle(args["height"]);

            EditorUtility.SetDirty(go);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "speed", agent.speed },
                { "angularSpeed", agent.angularSpeed },
                { "acceleration", agent.acceleration },
                { "stoppingDistance", agent.stoppingDistance },
                { "radius", agent.radius },
                { "height", agent.height },
            };
        }

        // ─── Add NavMeshObstacle ───

        public static object AddNavMeshObstacle(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var obstacle = go.GetComponent<NavMeshObstacle>();
            if (obstacle == null)
            {
                Undo.AddComponent<NavMeshObstacle>(go);
                obstacle = go.GetComponent<NavMeshObstacle>();
            }

            if (args.ContainsKey("carve"))
                obstacle.carving = Convert.ToBoolean(args["carve"]);
            if (args.ContainsKey("shape"))
            {
                string shape = args["shape"].ToString().ToLower();
                obstacle.shape = shape == "capsule" ? NavMeshObstacleShape.Capsule : NavMeshObstacleShape.Box;
            }

            EditorUtility.SetDirty(go);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "carving", obstacle.carving },
                { "shape", obstacle.shape.ToString() },
            };
        }

        // ─── Get NavMesh Info ───

        public static object GetNavMeshInfo(Dictionary<string, object> args)
        {
            var triangulation = NavMesh.CalculateTriangulation();
            
            int agentCount = 0;
            int obstacleCount = 0;
            var agents = UnityEngine.Object.FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var obstacles = UnityEngine.Object.FindObjectsByType<NavMeshObstacle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            agentCount = agents.Length;
            obstacleCount = obstacles.Length;

            var agentTypes = new List<Dictionary<string, object>>();
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var settings = NavMesh.GetSettingsByIndex(i);
                agentTypes.Add(new Dictionary<string, object>
                {
                    { "id", settings.agentTypeID },
                    { "name", NavMesh.GetSettingsNameFromID(settings.agentTypeID) },
                    { "radius", settings.agentRadius },
                    { "height", settings.agentHeight },
                    { "slope", settings.agentSlope },
                    { "climb", settings.agentClimb },
                });
            }

            return new Dictionary<string, object>
            {
                { "hasNavMesh", triangulation.vertices.Length > 0 },
                { "vertices", triangulation.vertices.Length },
                { "triangles", triangulation.indices.Length / 3 },
                { "agentCount", agentCount },
                { "obstacleCount", obstacleCount },
                { "agentTypes", agentTypes },
            };
        }

        // ─── Set NavMeshAgent Destination ───

        public static object SetAgentDestination(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null)
                return new { error = $"No NavMeshAgent on '{path}'" };

            if (!args.ContainsKey("destination"))
                return new { error = "destination {x, y, z} is required" };

            float x = 0, y = 0, z = 0;
            if (args["destination"] is Dictionary<string, object> destDict)
            {
                x = destDict.ContainsKey("x") ? Convert.ToSingle(destDict["x"]) : 0;
                y = destDict.ContainsKey("y") ? Convert.ToSingle(destDict["y"]) : 0;
                z = destDict.ContainsKey("z") ? Convert.ToSingle(destDict["z"]) : 0;
            }

            agent.SetDestination(new Vector3(x, y, z));

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "destination", $"({x}, {y}, {z})" },
            };
        }
    }
}
