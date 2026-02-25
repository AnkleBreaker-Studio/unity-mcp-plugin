using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Physics commands: raycasting, overlap queries, collision layer management.
    /// </summary>
    public static class MCPPhysicsCommands
    {
        public static object Raycast(Dictionary<string, object> args)
        {
            Vector3 origin = MCPGameObjectCommands.DictToVector3(args.ContainsKey("origin") ? args["origin"] as Dictionary<string, object> : null);
            Vector3 direction = MCPGameObjectCommands.DictToVector3(args.ContainsKey("direction") ? args["direction"] as Dictionary<string, object> : null);
            float maxDistance = args.ContainsKey("maxDistance") ? Convert.ToSingle(args["maxDistance"]) : Mathf.Infinity;
            int layerMask = args.ContainsKey("layerMask") ? Convert.ToInt32(args["layerMask"]) : Physics.DefaultRaycastLayers;

            if (direction == Vector3.zero)
                direction = Vector3.forward;

            bool allHits = args.ContainsKey("all") && Convert.ToBoolean(args["all"]);

            if (allHits)
            {
                var hits = Physics.RaycastAll(origin, direction, maxDistance, layerMask);
                var results = new List<Dictionary<string, object>>();
                foreach (var hit in hits)
                {
                    results.Add(HitToDict(hit));
                }
                return new Dictionary<string, object>
                {
                    { "hitCount", results.Count },
                    { "hits", results },
                    { "origin", MCPGameObjectCommands.Vector3ToDict(origin) },
                    { "direction", MCPGameObjectCommands.Vector3ToDict(direction) },
                };
            }
            else
            {
                RaycastHit hit;
                bool didHit = Physics.Raycast(origin, direction, out hit, maxDistance, layerMask);
                if (!didHit)
                    return new { hit = false, origin = MCPGameObjectCommands.Vector3ToDict(origin), direction = MCPGameObjectCommands.Vector3ToDict(direction) };

                return new Dictionary<string, object>
                {
                    { "hit", true },
                    { "hitInfo", HitToDict(hit) },
                    { "origin", MCPGameObjectCommands.Vector3ToDict(origin) },
                    { "direction", MCPGameObjectCommands.Vector3ToDict(direction) },
                };
            }
        }

        public static object OverlapSphere(Dictionary<string, object> args)
        {
            Vector3 center = MCPGameObjectCommands.DictToVector3(args.ContainsKey("center") ? args["center"] as Dictionary<string, object> : null);
            float radius = args.ContainsKey("radius") ? Convert.ToSingle(args["radius"]) : 1f;
            int layerMask = args.ContainsKey("layerMask") ? Convert.ToInt32(args["layerMask"]) : Physics.AllLayers;

            var colliders = Physics.OverlapSphere(center, radius, layerMask);
            var results = new List<Dictionary<string, object>>();
            foreach (var col in colliders)
            {
                results.Add(new Dictionary<string, object>
                {
                    { "gameObject", col.gameObject.name },
                    { "colliderType", col.GetType().Name },
                    { "position", MCPGameObjectCommands.Vector3ToDict(col.transform.position) },
                    { "instanceId", col.gameObject.GetInstanceID() },
                });
            }

            return new Dictionary<string, object>
            {
                { "center", MCPGameObjectCommands.Vector3ToDict(center) },
                { "radius", radius },
                { "count", results.Count },
                { "colliders", results },
            };
        }

        public static object OverlapBox(Dictionary<string, object> args)
        {
            Vector3 center = MCPGameObjectCommands.DictToVector3(args.ContainsKey("center") ? args["center"] as Dictionary<string, object> : null);
            Vector3 halfExtents = MCPGameObjectCommands.DictToVector3(args.ContainsKey("halfExtents") ? args["halfExtents"] as Dictionary<string, object> : null);
            if (halfExtents == Vector3.zero) halfExtents = Vector3.one * 0.5f;
            int layerMask = args.ContainsKey("layerMask") ? Convert.ToInt32(args["layerMask"]) : Physics.AllLayers;

            var colliders = Physics.OverlapBox(center, halfExtents, Quaternion.identity, layerMask);
            var results = new List<Dictionary<string, object>>();
            foreach (var col in colliders)
            {
                results.Add(new Dictionary<string, object>
                {
                    { "gameObject", col.gameObject.name },
                    { "colliderType", col.GetType().Name },
                    { "position", MCPGameObjectCommands.Vector3ToDict(col.transform.position) },
                    { "instanceId", col.gameObject.GetInstanceID() },
                });
            }

            return new Dictionary<string, object>
            {
                { "center", MCPGameObjectCommands.Vector3ToDict(center) },
                { "halfExtents", MCPGameObjectCommands.Vector3ToDict(halfExtents) },
                { "count", results.Count },
                { "colliders", results },
            };
        }

        public static object GetCollisionMatrix(Dictionary<string, object> args)
        {
            var matrix = new Dictionary<string, object>();
            for (int i = 0; i < 32; i++)
            {
                string layerName = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(layerName)) continue;

                var collidesWith = new List<string>();
                for (int j = 0; j < 32; j++)
                {
                    string otherName = LayerMask.LayerToName(j);
                    if (string.IsNullOrEmpty(otherName)) continue;
                    if (!Physics.GetIgnoreLayerCollision(i, j))
                        collidesWith.Add(otherName);
                }
                matrix[layerName] = collidesWith;
            }

            return new Dictionary<string, object>
            {
                { "matrix", matrix },
            };
        }

        public static object SetCollisionLayer(Dictionary<string, object> args)
        {
            int layer1 = args.ContainsKey("layer1") ? Convert.ToInt32(args["layer1"]) : -1;
            int layer2 = args.ContainsKey("layer2") ? Convert.ToInt32(args["layer2"]) : -1;
            bool ignore = args.ContainsKey("ignore") ? Convert.ToBoolean(args["ignore"]) : true;

            // Allow layer names
            if (args.ContainsKey("layer1Name"))
                layer1 = LayerMask.NameToLayer(args["layer1Name"].ToString());
            if (args.ContainsKey("layer2Name"))
                layer2 = LayerMask.NameToLayer(args["layer2Name"].ToString());

            if (layer1 < 0 || layer2 < 0)
                return new { error = "Valid layer indices or names are required" };

            Physics.IgnoreLayerCollision(layer1, layer2, ignore);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "layer1", LayerMask.LayerToName(layer1) },
                { "layer2", LayerMask.LayerToName(layer2) },
                { "ignoreCollision", ignore },
            };
        }

        public static object SetGravity(Dictionary<string, object> args)
        {
            if (args.ContainsKey("gravity"))
            {
                var gravity = MCPGameObjectCommands.DictToVector3(args["gravity"] as Dictionary<string, object>);
                Physics.gravity = gravity;
            }

            return new Dictionary<string, object>
            {
                { "gravity", MCPGameObjectCommands.Vector3ToDict(Physics.gravity) },
            };
        }

        // ─── Helpers ───

        private static Dictionary<string, object> HitToDict(RaycastHit hit)
        {
            return new Dictionary<string, object>
            {
                { "gameObject", hit.collider.gameObject.name },
                { "instanceId", hit.collider.gameObject.GetInstanceID() },
                { "point", MCPGameObjectCommands.Vector3ToDict(hit.point) },
                { "normal", MCPGameObjectCommands.Vector3ToDict(hit.normal) },
                { "distance", hit.distance },
                { "colliderType", hit.collider.GetType().Name },
            };
        }
    }
}
