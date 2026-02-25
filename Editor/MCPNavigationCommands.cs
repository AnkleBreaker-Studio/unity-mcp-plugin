using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

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
            var settings = NavMesh.GetSettingsByIndex(0);
            
            if (args.ContainsKey("agentRadius"))
                settings.agentRadius = Convert.ToSingle(args["agentRadius"]);
            if (args.ContainsKey("agentHeight"))
                settings.agentHeight = Convert.ToSingle(args["agentHeight"]);
            if (args.ContainsKey("agentSlope"))
                settings.agentSlope = Convert.ToSingle(args["agentSlope"]);
            if (args.ContainsKey("agentClimb"))
                settings.agentClimb = Convert.ToSingle(args["agentClimb"]);

            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();

            var triangulation = NavMesh.CalculateTriangulation();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "vertices", triangulation.vertices.Length },
                { "triangles", triangulation.indices.Length / 3 },
            };
        }

        // ─── Clear NavMesh ───

        public static object ClearNavMesh(Dictionary<string, object> args)
        {
            UnityEditor.AI.NavMeshBuilder.ClearAllNavMeshes();
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
