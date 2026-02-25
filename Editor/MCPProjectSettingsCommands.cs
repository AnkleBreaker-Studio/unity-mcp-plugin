using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for reading and modifying Unity project settings.
    /// </summary>
    public static class MCPProjectSettingsCommands
    {
        // ─── Quality Settings ───

        public static object GetQualitySettings(Dictionary<string, object> args)
        {
            var levels = new List<Dictionary<string, object>>();
            string[] names = QualitySettings.names;
            int current = QualitySettings.GetQualityLevel();

            for (int i = 0; i < names.Length; i++)
            {
                levels.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "name", names[i] },
                    { "isCurrent", i == current },
                });
            }

            return new Dictionary<string, object>
            {
                { "currentLevel", current },
                { "currentName", names[current] },
                { "levels", levels },
                { "pixelLightCount", QualitySettings.pixelLightCount },
                { "shadows", QualitySettings.shadows.ToString() },
                { "shadowResolution", QualitySettings.shadowResolution.ToString() },
                { "shadowDistance", QualitySettings.shadowDistance },
                { "antiAliasing", QualitySettings.antiAliasing },
                { "vSyncCount", QualitySettings.vSyncCount },
                { "lodBias", QualitySettings.lodBias },
                { "maximumLODLevel", QualitySettings.maximumLODLevel },
                { "particleRaycastBudget", QualitySettings.particleRaycastBudget },
                { "anisotropicFiltering", QualitySettings.anisotropicFiltering.ToString() },
            };
        }

        public static object SetQualityLevel(Dictionary<string, object> args)
        {
            if (!args.ContainsKey("level"))
                return new { error = "level is required (index or name)" };

            string levelStr = args["level"].ToString();
            int level;

            if (!int.TryParse(levelStr, out level))
            {
                // Search by name
                string[] names = QualitySettings.names;
                level = -1;
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].Equals(levelStr, StringComparison.OrdinalIgnoreCase))
                    {
                        level = i;
                        break;
                    }
                }
            }

            if (level < 0 || level >= QualitySettings.names.Length)
                return new { error = $"Quality level '{levelStr}' not found" };

            QualitySettings.SetQualityLevel(level, true);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "level", level },
                { "name", QualitySettings.names[level] },
            };
        }

        // ─── Physics Settings ───

        public static object GetPhysicsSettings(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "gravity", new Dictionary<string, object>
                    {
                        { "x", Physics.gravity.x },
                        { "y", Physics.gravity.y },
                        { "z", Physics.gravity.z },
                    }
                },
                { "defaultSolverIterations", Physics.defaultSolverIterations },
                { "defaultSolverVelocityIterations", Physics.defaultSolverVelocityIterations },
                { "sleepThreshold", Physics.sleepThreshold },
                { "defaultContactOffset", Physics.defaultContactOffset },
                { "bounceThreshold", Physics.bounceThreshold },
                { "defaultMaxAngularSpeed", Physics.defaultMaxAngularSpeed },
                { "queriesHitTriggers", Physics.queriesHitTriggers },
                { "queriesHitBackfaces", Physics.queriesHitBackfaces },
                { "autoSimulation", Physics.simulationMode.ToString() },
            };
        }

        public static object SetPhysicsSettings(Dictionary<string, object> args)
        {
            var updated = new List<string>();

            if (args.ContainsKey("gravity"))
            {
                if (args["gravity"] is Dictionary<string, object> g)
                {
                    float x = g.ContainsKey("x") ? Convert.ToSingle(g["x"]) : Physics.gravity.x;
                    float y = g.ContainsKey("y") ? Convert.ToSingle(g["y"]) : Physics.gravity.y;
                    float z = g.ContainsKey("z") ? Convert.ToSingle(g["z"]) : Physics.gravity.z;
                    Physics.gravity = new Vector3(x, y, z);
                    updated.Add("gravity");
                }
            }

            if (args.ContainsKey("defaultSolverIterations"))
            {
                Physics.defaultSolverIterations = Convert.ToInt32(args["defaultSolverIterations"]);
                updated.Add("defaultSolverIterations");
            }

            if (args.ContainsKey("sleepThreshold"))
            {
                Physics.sleepThreshold = Convert.ToSingle(args["sleepThreshold"]);
                updated.Add("sleepThreshold");
            }

            if (args.ContainsKey("bounceThreshold"))
            {
                Physics.bounceThreshold = Convert.ToSingle(args["bounceThreshold"]);
                updated.Add("bounceThreshold");
            }

            if (args.ContainsKey("defaultContactOffset"))
            {
                Physics.defaultContactOffset = Convert.ToSingle(args["defaultContactOffset"]);
                updated.Add("defaultContactOffset");
            }

            if (args.ContainsKey("queriesHitTriggers"))
            {
                Physics.queriesHitTriggers = Convert.ToBoolean(args["queriesHitTriggers"]);
                updated.Add("queriesHitTriggers");
            }

            if (updated.Count == 0)
                return new { error = "No valid settings provided to update" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        // ─── Time Settings ───

        public static object GetTimeSettings(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "fixedDeltaTime", Time.fixedDeltaTime },
                { "maximumDeltaTime", Time.maximumDeltaTime },
                { "timeScale", Time.timeScale },
                { "maximumParticleDeltaTime", Time.maximumParticleDeltaTime },
                { "fixedTimestep", Time.fixedDeltaTime },
                { "realtimeSinceStartup", Time.realtimeSinceStartup },
            };
        }

        public static object SetTimeSettings(Dictionary<string, object> args)
        {
            var updated = new List<string>();

            if (args.ContainsKey("fixedDeltaTime"))
            {
                Time.fixedDeltaTime = Convert.ToSingle(args["fixedDeltaTime"]);
                updated.Add("fixedDeltaTime");
            }

            if (args.ContainsKey("maximumDeltaTime"))
            {
                Time.maximumDeltaTime = Convert.ToSingle(args["maximumDeltaTime"]);
                updated.Add("maximumDeltaTime");
            }

            if (args.ContainsKey("timeScale"))
            {
                Time.timeScale = Convert.ToSingle(args["timeScale"]);
                updated.Add("timeScale");
            }

            if (updated.Count == 0)
                return new { error = "No valid time settings provided" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        // ─── Player Settings ───

        public static object GetPlayerSettings(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "companyName", PlayerSettings.companyName },
                { "productName", PlayerSettings.productName },
                { "applicationIdentifier", PlayerSettings.applicationIdentifier },
                { "bundleVersion", PlayerSettings.bundleVersion },
                { "defaultIsFullScreen", PlayerSettings.defaultIsNativeResolution },
                { "runInBackground", PlayerSettings.runInBackground },
                { "colorSpace", PlayerSettings.colorSpace.ToString() },
                { "gpuSkinning", PlayerSettings.gpuSkinning },
                { "apiCompatibilityLevel", PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup).ToString() },
                { "scriptingBackend", PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup).ToString() },
                { "targetArchitecture", EditorUserBuildSettings.activeBuildTarget.ToString() },
            };
        }

        public static object SetPlayerSettings(Dictionary<string, object> args)
        {
            var updated = new List<string>();

            if (args.ContainsKey("companyName"))
            {
                PlayerSettings.companyName = args["companyName"].ToString();
                updated.Add("companyName");
            }

            if (args.ContainsKey("productName"))
            {
                PlayerSettings.productName = args["productName"].ToString();
                updated.Add("productName");
            }

            if (args.ContainsKey("bundleVersion"))
            {
                PlayerSettings.bundleVersion = args["bundleVersion"].ToString();
                updated.Add("bundleVersion");
            }

            if (args.ContainsKey("runInBackground"))
            {
                PlayerSettings.runInBackground = Convert.ToBoolean(args["runInBackground"]);
                updated.Add("runInBackground");
            }

            if (updated.Count == 0)
                return new { error = "No valid player settings provided" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        // ─── Render Pipeline ───

        public static object GetRenderPipelineInfo(Dictionary<string, object> args)
        {
            var current = GraphicsSettings.currentRenderPipeline;
            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;

            return new Dictionary<string, object>
            {
                { "currentPipeline", current != null ? current.name : "Built-in" },
                { "currentPipelineType", current != null ? current.GetType().Name : "Built-in Render Pipeline" },
                { "defaultPipeline", defaultPipeline != null ? defaultPipeline.name : "Built-in" },
                { "colorSpace", QualitySettings.activeColorSpace.ToString() },
                { "renderPipelineAssetPath", defaultPipeline != null ? AssetDatabase.GetAssetPath(defaultPipeline) : "" },
            };
        }
    }
}
