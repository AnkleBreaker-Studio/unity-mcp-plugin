using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing MPPM (Multiplayer PlayMode) scenarios via reflection.
    /// All MPPM scenario types are internal, so we use reflection to access them.
    /// </summary>
    public static class MCPScenarioCommands
    {
        // Assembly: Unity.Multiplayer.PlayMode.Scenarios.Editor
        private static Type _scenarioConfigType;   // Unity.Multiplayer.PlayMode.Scenarios.Editor.ScenarioConfig (internal)
        private static Type _scenarioRunnerType;    // Unity.Multiplayer.PlayMode.Scenarios.Editor.ScenarioRunner (internal)
        private static Type _scenarioStatusType;    // Unity.Multiplayer.PlayMode.Scenarios.Editor.Api.ScenarioStatus (internal struct)
        private static Type _scenarioType;          // Unity.Multiplayer.PlayMode.Scenarios.Editor.GraphsFoundation.Scenario (internal)
        private static Type _instanceDescriptionType; // Unity.Multiplayer.PlayMode.Scenarios.Editor.InstanceDescription (internal)

        // Assembly: Unity.Multiplayer.Playmode
        private static Type _currentPlayerType;     // Unity.Multiplayer.Playmode.CurrentPlayer (public)

        private static bool _initialized = false;
        private static bool _mppmAvailable = false;

        private static void InitializeReflection()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // Find assemblies by name since the types are internal
                Assembly scenariosAssembly = null;
                Assembly mppmAssembly = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name;
                    if (asmName == "Unity.Multiplayer.PlayMode.Scenarios.Editor")
                        scenariosAssembly = asm;
                    else if (asmName == "Unity.Multiplayer.Playmode")
                        mppmAssembly = asm;
                }

                if (scenariosAssembly != null)
                {
                    _scenarioConfigType = scenariosAssembly.GetType("Unity.Multiplayer.PlayMode.Scenarios.Editor.ScenarioConfig");
                    _scenarioRunnerType = scenariosAssembly.GetType("Unity.Multiplayer.PlayMode.Scenarios.Editor.ScenarioRunner");
                    _scenarioStatusType = scenariosAssembly.GetType("Unity.Multiplayer.PlayMode.Scenarios.Editor.Api.ScenarioStatus");
                    _scenarioType = scenariosAssembly.GetType("Unity.Multiplayer.PlayMode.Scenarios.Editor.GraphsFoundation.Scenario");
                    _instanceDescriptionType = scenariosAssembly.GetType("Unity.Multiplayer.PlayMode.Scenarios.Editor.InstanceDescription");
                }

                if (mppmAssembly != null)
                {
                    _currentPlayerType = mppmAssembly.GetType("Unity.Multiplayer.Playmode.CurrentPlayer");
                }

                _mppmAvailable = _scenarioConfigType != null && _scenarioRunnerType != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityMCP] MPPM reflection init failed: {ex.Message}");
                _mppmAvailable = false;
            }
        }

        private static object WrapError(string message)
        {
            return new Dictionary<string, object>
            {
                { "error", message },
                { "mppmAvailable", _mppmAvailable }
            };
        }

        /// <summary>
        /// List all ScenarioConfig assets in the project.
        /// Since GetAllInstances is an instance method, we find configs via AssetDatabase.
        /// </summary>
        public static object ListScenarios(Dictionary<string, object> args)
        {
            InitializeReflection();

            if (!_mppmAvailable)
                return WrapError("MPPM (Multiplayer PlayMode) is not installed in this project");

            try
            {
                var result = new Dictionary<string, object>();
                var scenarioList = new List<Dictionary<string, object>>();

                // Find all ScenarioConfig ScriptableObject assets via AssetDatabase
                var guids = AssetDatabase.FindAssets("t:ScriptableObject");

                // Get properties we need (using NonPublic since type is internal)
                var bindFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var scenarioProperty = _scenarioConfigType.GetProperty("Scenario", bindFlags);
                var descriptionProperty = _scenarioConfigType.GetProperty("Description", bindFlags);
                var editorInstanceProperty = _scenarioConfigType.GetProperty("EditorInstance", bindFlags);
                var virtualEditorInstancesProperty = _scenarioConfigType.GetProperty("VirtualEditorInstances", bindFlags);
                var localInstancesProperty = _scenarioConfigType.GetProperty("LocalInstances", bindFlags);

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadAssetAtPath(path, _scenarioConfigType);
                    if (asset == null) continue;

                    try
                    {
                        var scenarioObj = scenarioProperty?.GetValue(asset);
                        var scenarioName = (scenarioObj as UnityEngine.Object)?.name ?? asset.name;
                        var description = descriptionProperty?.GetValue(asset) as string ?? "";

                        var editorInst = editorInstanceProperty?.GetValue(asset);
                        var virtualInsts = virtualEditorInstancesProperty?.GetValue(asset) as System.Collections.IEnumerable;
                        var localInsts = localInstancesProperty?.GetValue(asset) as System.Collections.IEnumerable;

                        var scenarioInfo = new Dictionary<string, object>
                        {
                            { "name", scenarioName },
                            { "path", path },
                            { "description", description },
                            { "hasEditorInstance", editorInst != null },
                            { "virtualInstanceCount", virtualInsts?.Cast<object>().Count() ?? 0 },
                            { "localInstanceCount", localInsts?.Cast<object>().Count() ?? 0 }
                        };

                        // Add instance details if available
                        var instancesInfo = new List<Dictionary<string, object>>();

                        if (editorInst != null)
                        {
                            instancesInfo.Add(GetInstanceInfo(editorInst, "Editor"));
                        }

                        if (virtualInsts != null)
                        {
                            int idx = 0;
                            foreach (var inst in virtualInsts)
                            {
                                instancesInfo.Add(GetInstanceInfo(inst, $"VirtualEditor{idx}"));
                                idx++;
                            }
                        }

                        if (localInsts != null)
                        {
                            int idx = 0;
                            foreach (var inst in localInsts)
                            {
                                instancesInfo.Add(GetInstanceInfo(inst, $"LocalInstance{idx}"));
                                idx++;
                            }
                        }

                        scenarioInfo["instances"] = instancesInfo;
                        scenarioList.Add(scenarioInfo);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[UnityMCP] Error processing scenario config at {path}: {ex.Message}");
                    }
                }

                result["scenarios"] = scenarioList;
                result["count"] = scenarioList.Count;
                return result;
            }
            catch (Exception ex)
            {
                return WrapError($"Failed to list scenarios: {ex.Message}");
            }
        }

        private static Dictionary<string, object> GetInstanceInfo(object instance, string typeName)
        {
            var info = new Dictionary<string, object>
            {
                { "type", typeName }
            };

            try
            {
                if (instance == null) return info;

                var instanceType = instance.GetType();
                var bindFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var typeNameProperty = instanceType.GetProperty("InstanceTypeName", bindFlags);
                var runModeStateProperty = instanceType.GetProperty("RunModeState", bindFlags);

                if (typeNameProperty != null)
                    info["instanceTypeName"] = typeNameProperty.GetValue(instance) as string ?? "Unknown";

                if (runModeStateProperty != null)
                    info["runModeState"] = runModeStateProperty.GetValue(instance)?.ToString() ?? "Unknown";
            }
            catch (Exception ex)
            {
                info["error"] = ex.Message;
            }

            return info;
        }

        /// <summary>
        /// Get the current scenario runner status.
        /// </summary>
        public static object GetScenarioStatus(Dictionary<string, object> args)
        {
            InitializeReflection();

            if (!_mppmAvailable)
                return WrapError("MPPM is not installed");

            try
            {
                var result = new Dictionary<string, object>();

                // ScenarioRunner methods/properties are static but internal — use NonPublic flag
                var bindFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var getStatusMethod = _scenarioRunnerType.GetMethod("GetScenarioStatus", bindFlags);
                var isRunningProperty = _scenarioRunnerType.GetProperty("IsRunning", bindFlags);
                var activeScenarioProperty = _scenarioRunnerType.GetProperty("ActiveScenario", bindFlags);

                result["isRunning"] = isRunningProperty?.GetValue(null) ?? false;
                result["activeScenarioName"] = (activeScenarioProperty?.GetValue(null) as UnityEngine.Object)?.name ?? "None";

                // Get detailed status
                if (getStatusMethod != null)
                {
                    try
                    {
                        object scenarioStatus = getStatusMethod.Invoke(null, null);

                        if (scenarioStatus != null && _scenarioStatusType != null)
                        {
                            // ScenarioStatus is a struct with FIELDS, not properties
                            var instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                            var stateField = _scenarioStatusType.GetField("State", instanceFlags);
                            var currentStageField = _scenarioStatusType.GetField("CurrentStage", instanceFlags);
                            var totalProgressField = _scenarioStatusType.GetField("TotalProgress", instanceFlags);

                            if (stateField != null)
                                result["state"] = stateField.GetValue(scenarioStatus)?.ToString() ?? "Unknown";

                            if (currentStageField != null)
                                result["currentStage"] = currentStageField.GetValue(scenarioStatus)?.ToString() ?? "N/A";

                            if (totalProgressField != null)
                            {
                                var progress = totalProgressField.GetValue(scenarioStatus);
                                if (progress is float f)
                                    result["progress"] = f;
                                else if (progress != null)
                                    result["progress"] = progress.ToString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result["statusError"] = ex.Message;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                return WrapError($"Failed to get scenario status: {ex.Message}");
            }
        }

        /// <summary>
        /// Load/activate a scenario by asset path.
        /// LoadScenario takes a Scenario object (not ScenarioConfig), so we extract it via the Scenario property.
        /// </summary>
        public static object ActivateScenario(Dictionary<string, object> args)
        {
            InitializeReflection();

            if (!_mppmAvailable)
                return WrapError("MPPM is not installed");

            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return WrapError("path parameter is required");

            try
            {
                // Load the ScenarioConfig asset
                var configAsset = AssetDatabase.LoadAssetAtPath(path, _scenarioConfigType);
                if (configAsset == null)
                    return WrapError($"Could not load ScenarioConfig at path: {path}");

                // Get the Scenario object from the config
                var scenarioProperty = _scenarioConfigType.GetProperty("Scenario",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (scenarioProperty == null)
                    return WrapError("Could not find ScenarioConfig.Scenario property");

                var scenarioObj = scenarioProperty.GetValue(configAsset);
                if (scenarioObj == null)
                    return WrapError("ScenarioConfig.Scenario returned null");

                // Call ScenarioRunner.LoadScenario(Scenario scenario)
                // The parameter type is Scenario (GraphsFoundation.Scenario), not ScenarioConfig
                var loadMethod = _scenarioRunnerType.GetMethod("LoadScenario",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (loadMethod == null)
                    return WrapError("Could not find ScenarioRunner.LoadScenario() method");

                loadMethod.Invoke(null, new[] { scenarioObj });

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "scenario", (scenarioObj as UnityEngine.Object)?.name ?? configAsset.name },
                    { "path", path }
                };
            }
            catch (Exception ex)
            {
                return WrapError($"Failed to activate scenario: {ex.Message}");
            }
        }

        /// <summary>
        /// Start the active scenario.
        /// </summary>
        public static object StartScenario(Dictionary<string, object> args)
        {
            InitializeReflection();

            if (!_mppmAvailable)
                return WrapError("MPPM is not installed");

            try
            {
                var bindFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var startMethod = _scenarioRunnerType.GetMethod("StartScenario", bindFlags,
                    null, Type.EmptyTypes, null);

                if (startMethod == null)
                    return WrapError("Could not find ScenarioRunner.StartScenario() method");

                startMethod.Invoke(null, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "message", "Scenario started" }
                };
            }
            catch (Exception ex)
            {
                return WrapError($"Failed to start scenario: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the running scenario.
        /// </summary>
        public static object StopScenario(Dictionary<string, object> args)
        {
            InitializeReflection();

            if (!_mppmAvailable)
                return WrapError("MPPM is not installed");

            try
            {
                var bindFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var stopMethod = _scenarioRunnerType.GetMethod("StopScenario", bindFlags,
                    null, Type.EmptyTypes, null);

                if (stopMethod == null)
                    return WrapError("Could not find ScenarioRunner.StopScenario() method");

                stopMethod.Invoke(null, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "message", "Scenario stopped" }
                };
            }
            catch (Exception ex)
            {
                return WrapError($"Failed to stop scenario: {ex.Message}");
            }
        }

        /// <summary>
        /// Get CurrentPlayer info and MPPM package version.
        /// </summary>
        public static object GetMultiplayerInfo(Dictionary<string, object> args)
        {
            InitializeReflection();

            var result = new Dictionary<string, object>();
            result["mppmAvailable"] = _mppmAvailable;

            // Get MPPM package version from package.json
            string mppmVersion = "unknown";
            try
            {
                var packageJsonPath = "Packages/com.unity.multiplayer.playmode/package.json";
                if (System.IO.File.Exists(packageJsonPath))
                {
                    var json = System.IO.File.ReadAllText(packageJsonPath);
                    var match = System.Text.RegularExpressions.Regex.Match(json, @"""version""\s*:\s*""([^""]+)""");
                    if (match.Success)
                        mppmVersion = match.Groups[1].Value;
                }
                else
                {
                    mppmVersion = "not installed";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityMCP] Could not read MPPM version: {ex.Message}");
            }

            result["mppmVersion"] = mppmVersion;

            // Get CurrentPlayer info (this type is public, so Public binding flag works)
            if (_currentPlayerType != null)
            {
                try
                {
                    var isMainEditorProperty = _currentPlayerType.GetProperty("IsMainEditor",
                        BindingFlags.Static | BindingFlags.Public);

                    var readOnlyTagsMethod = _currentPlayerType.GetMethod("ReadOnlyTags",
                        BindingFlags.Static | BindingFlags.Public);

                    if (isMainEditorProperty != null)
                        result["isMainEditor"] = isMainEditorProperty.GetValue(null) ?? false;

                    if (readOnlyTagsMethod != null)
                    {
                        var tags = readOnlyTagsMethod.Invoke(null, null);
                        if (tags is System.Collections.IEnumerable enumerable)
                        {
                            result["tags"] = enumerable.Cast<object>().Select(t => t.ToString()).ToList();
                        }
                    }
                }
                catch (Exception ex)
                {
                    result["currentPlayerError"] = ex.Message;
                }
            }

            return result;
        }
    }
}
