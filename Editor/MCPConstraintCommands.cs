using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing constraints and LOD groups.
    /// </summary>
    public static class MCPConstraintCommands
    {
        // ─── Add Constraint ───

        public static object AddConstraint(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "";
            if (string.IsNullOrEmpty(type))
                return new { error = "type is required (position, rotation, scale, aim, parent, lookat)" };

            string sourcePath = args.ContainsKey("source") ? args["source"].ToString() : "";

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            Transform sourceTransform = null;
            if (!string.IsNullOrEmpty(sourcePath))
            {
                var sourceGo = GameObject.Find(sourcePath);
                if (sourceGo != null)
                    sourceTransform = sourceGo.transform;
            }

            string resultType = "";
            switch (type)
            {
                case "position":
                    var pc = Undo.AddComponent<PositionConstraint>(go);
                    if (sourceTransform != null)
                    {
                        var source = new ConstraintSource { sourceTransform = sourceTransform, weight = 1f };
                        pc.AddSource(source);
                    }
                    pc.constraintActive = args.ContainsKey("activate") && Convert.ToBoolean(args["activate"]);
                    resultType = "PositionConstraint";
                    break;
                case "rotation":
                    var rc = Undo.AddComponent<RotationConstraint>(go);
                    if (sourceTransform != null)
                    {
                        var source = new ConstraintSource { sourceTransform = sourceTransform, weight = 1f };
                        rc.AddSource(source);
                    }
                    rc.constraintActive = args.ContainsKey("activate") && Convert.ToBoolean(args["activate"]);
                    resultType = "RotationConstraint";
                    break;
                case "scale":
                    var sc = Undo.AddComponent<ScaleConstraint>(go);
                    if (sourceTransform != null)
                    {
                        var source = new ConstraintSource { sourceTransform = sourceTransform, weight = 1f };
                        sc.AddSource(source);
                    }
                    sc.constraintActive = args.ContainsKey("activate") && Convert.ToBoolean(args["activate"]);
                    resultType = "ScaleConstraint";
                    break;
                case "aim":
                    var ac = Undo.AddComponent<AimConstraint>(go);
                    if (sourceTransform != null)
                    {
                        var source = new ConstraintSource { sourceTransform = sourceTransform, weight = 1f };
                        ac.AddSource(source);
                    }
                    ac.constraintActive = args.ContainsKey("activate") && Convert.ToBoolean(args["activate"]);
                    resultType = "AimConstraint";
                    break;
                case "parent":
                    var parc = Undo.AddComponent<ParentConstraint>(go);
                    if (sourceTransform != null)
                    {
                        var source = new ConstraintSource { sourceTransform = sourceTransform, weight = 1f };
                        parc.AddSource(source);
                    }
                    parc.constraintActive = args.ContainsKey("activate") && Convert.ToBoolean(args["activate"]);
                    resultType = "ParentConstraint";
                    break;
                case "lookat":
                    var lc = Undo.AddComponent<LookAtConstraint>(go);
                    if (sourceTransform != null)
                    {
                        var source = new ConstraintSource { sourceTransform = sourceTransform, weight = 1f };
                        lc.AddSource(source);
                    }
                    lc.constraintActive = args.ContainsKey("activate") && Convert.ToBoolean(args["activate"]);
                    resultType = "LookAtConstraint";
                    break;
                default:
                    return new { error = $"Unknown constraint type '{type}'" };
            }

            EditorUtility.SetDirty(go);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "constraintType", resultType },
                { "hasSource", sourceTransform != null },
            };
        }

        // ─── Get Constraint Info ───

        public static object GetConstraintInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var constraints = new List<Dictionary<string, object>>();
            foreach (var comp in go.GetComponents<IConstraint>())
            {
                var c = comp as Component;
                if (c == null) continue;
                var info = new Dictionary<string, object>
                {
                    { "type", c.GetType().Name },
                    { "active", comp.constraintActive },
                    { "weight", comp.weight },
                    { "sourceCount", comp.sourceCount },
                };
                constraints.Add(info);
            }

            return new Dictionary<string, object>
            {
                { "gameObject", go.name },
                { "count", constraints.Count },
                { "constraints", constraints },
            };
        }

        // ─── Create LOD Group ───

        public static object CreateLODGroup(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup == null)
            {
                Undo.AddComponent<LODGroup>(go);
                lodGroup = go.GetComponent<LODGroup>();
            }

            int levels = 3;
            if (args.ContainsKey("levels"))
            {
                var levelsVal = args["levels"];
                if (levelsVal is System.Collections.IList list)
                    levels = list.Count;
                else
                    levels = Convert.ToInt32(levelsVal);
            }
            levels = Mathf.Clamp(levels, 1, 8);

            var lods = new LOD[levels];
            var renderers = go.GetComponentsInChildren<Renderer>();

            // Check if levels array has screenRelativeHeight values
            var levelsList = args.ContainsKey("levels") && args["levels"] is System.Collections.IList ? (System.Collections.IList)args["levels"] : null;

            for (int i = 0; i < levels; i++)
            {
                float transition = 1f - ((float)(i + 1) / levels);
                if (levelsList != null && i < levelsList.Count && levelsList[i] is System.Collections.IDictionary ld && ld.Contains("screenRelativeHeight"))
                    transition = Convert.ToSingle(ld["screenRelativeHeight"]);
                lods[i] = new LOD(transition, i == 0 ? renderers : new Renderer[0]);
            }

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            EditorUtility.SetDirty(go);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "lodLevels", levels },
            };
        }

        // ─── Get LOD Group Info ───

        public static object GetLODGroupInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup == null)
                return new { error = $"No LODGroup on '{path}'" };

            var lods = lodGroup.GetLODs();
            var lodInfos = new List<Dictionary<string, object>>();
            for (int i = 0; i < lods.Length; i++)
            {
                lodInfos.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "screenRelativeTransitionHeight", lods[i].screenRelativeTransitionHeight },
                    { "rendererCount", lods[i].renderers?.Length ?? 0 },
                });
            }

            return new Dictionary<string, object>
            {
                { "gameObject", go.name },
                { "lodCount", lods.Length },
                { "size", lodGroup.size },
                { "lods", lodInfos },
            };
        }
    }
}
