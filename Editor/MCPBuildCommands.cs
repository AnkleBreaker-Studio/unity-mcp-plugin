using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPBuildCommands
    {
        public static object StartBuild(Dictionary<string, object> args)
        {
            string targetStr = args.ContainsKey("target") ? args["target"].ToString() : "StandaloneWindows64";
            string outputPath = args.ContainsKey("outputPath") ? args["outputPath"].ToString() : "";
            bool devBuild = args.ContainsKey("developmentBuild") && Convert.ToBoolean(args["developmentBuild"]);

#if UNITY_6000_6_OR_NEWER
            var variant = devBuild ? ManagedCodeVariant.Checked : ManagedCodeVariant.Release;
            if (args.TryGetValue("managedCodeVariant", out var requestedVariant))
            {
                if (!(requestedVariant is string variantName)
                    || !Enum.GetNames(typeof(ManagedCodeVariant)).Any(name => string.Equals(name, variantName, StringComparison.OrdinalIgnoreCase))
                    || !Enum.TryParse(variantName, true, out variant))
                    return new { error = "managedCodeVariant must be Release, Instrumented, Checked or Debug" };
            }
#else
            if (args.ContainsKey("managedCodeVariant"))
                return new { error = "managedCodeVariant requires Unity 6.6 or newer" };
#endif

            if (string.IsNullOrEmpty(outputPath))
                return new { error = "outputPath is required" };

            if (!Enum.TryParse<BuildTarget>(targetStr, out var target))
                return new { error = $"Unknown build target: {targetStr}" };

            // Get scenes
            string[] scenes;
            if (args.ContainsKey("scenes"))
            {
                var sceneList = args["scenes"] as List<object>;
                scenes = sceneList?.Select(s => s.ToString()).ToArray() ?? new string[0];
            }
            else
            {
                scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
            }

            if (scenes.Length == 0)
                return new { error = "No scenes to build. Add scenes to Build Settings or provide them." };

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = target,
                options = devBuild ? BuildOptions.Development : BuildOptions.None,
            };

            try
            {
#if UNITY_6000_6_OR_NEWER
                var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
                var previousVariant = PlayerSettings.GetManagedCodeVariant(namedTarget);
                BuildReport report;
                try
                {
                    // Development no longer enables these diagnostics by itself in Unity 6.6.
                    if (previousVariant != variant) PlayerSettings.SetManagedCodeVariant(namedTarget, variant);
                    report = BuildPipeline.BuildPlayer(options);
                }
                finally
                {
                    // One MCP build must not change the diagnostics of later user builds.
                    if (previousVariant != variant) PlayerSettings.SetManagedCodeVariant(namedTarget, previousVariant);
                }
#else
                var report = BuildPipeline.BuildPlayer(options);
#endif

                return new Dictionary<string, object>
                {
                    { "success", report.summary.result == BuildResult.Succeeded },
                    { "result", report.summary.result.ToString() },
                    { "totalErrors", report.summary.totalErrors },
                    { "totalWarnings", report.summary.totalWarnings },
                    { "totalTime", report.summary.totalTime.TotalSeconds },
                    { "outputPath", report.summary.outputPath },
                    { "totalSize", report.summary.totalSize },
                    { "platform", report.summary.platform.ToString() },
                    { "developmentBuild", devBuild },
#if UNITY_6000_6_OR_NEWER
                    { "managedCodeVariant", variant.ToString() },
#endif
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Build failed: {ex.Message}" };
            }
        }
    }
}
