using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing Animator Controllers, Animation Clips, States, Transitions, and Parameters.
    /// </summary>
    public static class MCPAnimationCommands
    {
        // ─── Animator Controller ───

        public static object CreateController(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (e.g. 'Assets/Animations/PlayerController.controller')" };

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller == null)
                return new { error = "Failed to create animator controller" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "name", controller.name },
                { "layers", controller.layers.Length },
                { "parameters", controller.parameters.Length },
            };
        }

        public static object GetControllerInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            var layers = new List<Dictionary<string, object>>();
            for (int i = 0; i < controller.layers.Length; i++)
            {
                var layer = controller.layers[i];
                var states = new List<Dictionary<string, object>>();
                foreach (var state in layer.stateMachine.states)
                {
                    states.Add(new Dictionary<string, object>
                    {
                        { "name", state.state.name },
                        { "nameHash", state.state.nameHash },
                        { "speed", state.state.speed },
                        { "motion", state.state.motion != null ? state.state.motion.name : null },
                        { "position", new Dictionary<string, object> { { "x", state.position.x }, { "y", state.position.y } } },
                        { "isDefault", layer.stateMachine.defaultState == state.state },
                        { "transitionCount", state.state.transitions.Length },
                    });
                }

                var subStateMachines = new List<string>();
                foreach (var sub in layer.stateMachine.stateMachines)
                    subStateMachines.Add(sub.stateMachine.name);

                layers.Add(new Dictionary<string, object>
                {
                    { "name", layer.name },
                    { "index", i },
                    { "weight", layer.defaultWeight },
                    { "blendingMode", layer.blendingMode.ToString() },
                    { "states", states },
                    { "subStateMachines", subStateMachines },
                    { "defaultState", layer.stateMachine.defaultState != null ? layer.stateMachine.defaultState.name : null },
                    { "anyStateTransitionCount", layer.stateMachine.anyStateTransitions.Length },
                });
            }

            var parameters = new List<Dictionary<string, object>>();
            foreach (var param in controller.parameters)
            {
                var paramInfo = new Dictionary<string, object>
                {
                    { "name", param.name },
                    { "type", param.type.ToString() },
                };
                switch (param.type)
                {
                    case AnimatorControllerParameterType.Float:
                        paramInfo["defaultValue"] = param.defaultFloat;
                        break;
                    case AnimatorControllerParameterType.Int:
                        paramInfo["defaultValue"] = param.defaultInt;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        paramInfo["defaultValue"] = param.defaultBool;
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        paramInfo["defaultValue"] = false;
                        break;
                }
                parameters.Add(paramInfo);
            }

            return new Dictionary<string, object>
            {
                { "name", controller.name },
                { "path", path },
                { "layerCount", controller.layers.Length },
                { "parameterCount", controller.parameters.Length },
                { "layers", layers },
                { "parameters", parameters },
            };
        }

        // ─── Parameters ───

        public static object AddParameter(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string paramName = args.ContainsKey("parameterName") ? args["parameterName"].ToString() : "";
            string paramType = args.ContainsKey("parameterType") ? args["parameterType"].ToString() : "Float";

            if (string.IsNullOrEmpty(paramName))
                return new { error = "parameterName is required" };

            AnimatorControllerParameterType type;
            if (!Enum.TryParse(paramType, true, out type))
                return new { error = $"Invalid parameter type: {paramType}. Use Float, Int, Bool, or Trigger." };

            controller.AddParameter(paramName, type);

            // Set default value if provided
            if (args.ContainsKey("defaultValue"))
            {
                var parameters = controller.parameters;
                var param = parameters[parameters.Length - 1];
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        param.defaultFloat = Convert.ToSingle(args["defaultValue"]);
                        break;
                    case AnimatorControllerParameterType.Int:
                        param.defaultInt = Convert.ToInt32(args["defaultValue"]);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        param.defaultBool = Convert.ToBoolean(args["defaultValue"]);
                        break;
                }
                controller.parameters = parameters;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, controllerPath = path, parameterName = paramName, parameterType = paramType };
        }

        public static object RemoveParameter(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string paramName = args.ContainsKey("parameterName") ? args["parameterName"].ToString() : "";
            if (string.IsNullOrEmpty(paramName))
                return new { error = "parameterName is required" };

            var parameters = controller.parameters.ToList();
            int index = parameters.FindIndex(p => p.name == paramName);
            if (index < 0)
                return new { error = $"Parameter '{paramName}' not found" };

            controller.RemoveParameter(index);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, removed = paramName };
        }

        // ─── States ───

        public static object AddState(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string stateName = args.ContainsKey("stateName") ? args["stateName"].ToString() : "";
            if (string.IsNullOrEmpty(stateName))
                return new { error = "stateName is required" };

            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : 0;
            if (layerIndex >= controller.layers.Length)
                return new { error = $"Layer index {layerIndex} out of range (count: {controller.layers.Length})" };

            var stateMachine = controller.layers[layerIndex].stateMachine;
            var state = stateMachine.AddState(stateName);

            // Set speed
            if (args.ContainsKey("speed"))
                state.speed = Convert.ToSingle(args["speed"]);

            // Assign animation clip if provided
            if (args.ContainsKey("clipPath"))
            {
                string clipPath = args["clipPath"].ToString();
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip != null) state.motion = clip;
            }

            // Set as default if requested
            if (args.ContainsKey("isDefault") && Convert.ToBoolean(args["isDefault"]))
                stateMachine.defaultState = state;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "stateName", state.name },
                { "nameHash", state.nameHash },
                { "layerIndex", layerIndex },
                { "isDefault", stateMachine.defaultState == state },
            };
        }

        public static object RemoveState(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string stateName = args.ContainsKey("stateName") ? args["stateName"].ToString() : "";
            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : 0;

            var stateMachine = controller.layers[layerIndex].stateMachine;
            var stateEntry = stateMachine.states.FirstOrDefault(s => s.state.name == stateName);
            if (stateEntry.state == null)
                return new { error = $"State '{stateName}' not found in layer {layerIndex}" };

            stateMachine.RemoveState(stateEntry.state);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, removed = stateName, layerIndex };
        }

        // ─── Transitions ───

        public static object AddTransition(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string sourceName = args.ContainsKey("sourceState") ? args["sourceState"].ToString() : "";
            string destName = args.ContainsKey("destinationState") ? args["destinationState"].ToString() : "";
            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : 0;
            bool fromAnyState = args.ContainsKey("fromAnyState") && Convert.ToBoolean(args["fromAnyState"]);

            var stateMachine = controller.layers[layerIndex].stateMachine;

            AnimatorState destState = null;
            if (!string.IsNullOrEmpty(destName))
            {
                var destEntry = stateMachine.states.FirstOrDefault(s => s.state.name == destName);
                destState = destEntry.state;
                if (destState == null)
                    return new { error = $"Destination state '{destName}' not found" };
            }

            AnimatorStateTransition transition;

            if (fromAnyState)
            {
                transition = stateMachine.AddAnyStateTransition(destState);
            }
            else
            {
                if (string.IsNullOrEmpty(sourceName))
                    return new { error = "sourceState is required (or set fromAnyState to true)" };

                var sourceEntry = stateMachine.states.FirstOrDefault(s => s.state.name == sourceName);
                if (sourceEntry.state == null)
                    return new { error = $"Source state '{sourceName}' not found" };

                transition = sourceEntry.state.AddTransition(destState);
            }

            // Configure transition
            if (args.ContainsKey("hasExitTime"))
                transition.hasExitTime = Convert.ToBoolean(args["hasExitTime"]);
            if (args.ContainsKey("exitTime"))
                transition.exitTime = Convert.ToSingle(args["exitTime"]);
            if (args.ContainsKey("duration"))
                transition.duration = Convert.ToSingle(args["duration"]);
            if (args.ContainsKey("offset"))
                transition.offset = Convert.ToSingle(args["offset"]);
            if (args.ContainsKey("hasFixedDuration"))
                transition.hasFixedDuration = Convert.ToBoolean(args["hasFixedDuration"]);

            // Add conditions
            if (args.ContainsKey("conditions"))
            {
                var conditions = args["conditions"] as List<object>;
                if (conditions != null)
                {
                    foreach (var condObj in conditions)
                    {
                        var cond = condObj as Dictionary<string, object>;
                        if (cond == null) continue;

                        string paramName = cond.ContainsKey("parameter") ? cond["parameter"].ToString() : "";
                        string modeStr = cond.ContainsKey("mode") ? cond["mode"].ToString() : "If";
                        float threshold = cond.ContainsKey("threshold") ? Convert.ToSingle(cond["threshold"]) : 0f;

                        AnimatorConditionMode mode;
                        if (!Enum.TryParse(modeStr, true, out mode))
                            mode = AnimatorConditionMode.If;

                        transition.AddCondition(mode, threshold, paramName);
                    }
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "source", fromAnyState ? "AnyState" : sourceName },
                { "destination", destName },
                { "hasExitTime", transition.hasExitTime },
                { "duration", transition.duration },
                { "conditionCount", transition.conditions.Length },
            };
        }

        // ─── Animation Clips ───

        public static object CreateClip(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (e.g. 'Assets/Animations/Walk.anim')" };

            // Ensure directory
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            var clip = new AnimationClip();
            clip.name = Path.GetFileNameWithoutExtension(path);

            if (args.ContainsKey("loop"))
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = Convert.ToBoolean(args["loop"]);
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }

            if (args.ContainsKey("frameRate"))
                clip.frameRate = Convert.ToSingle(args["frameRate"]);

            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "name", clip.name },
                { "length", clip.length },
                { "frameRate", clip.frameRate },
                { "isLooping", clip.isLooping },
            };
        }

        public static object GetClipInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var curves = new List<Dictionary<string, object>>();
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                curves.Add(new Dictionary<string, object>
                {
                    { "path", binding.path },
                    { "propertyName", binding.propertyName },
                    { "type", binding.type.Name },
                    { "keyframeCount", curve.keys.Length },
                });
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            return new Dictionary<string, object>
            {
                { "name", clip.name },
                { "path", path },
                { "length", clip.length },
                { "frameRate", clip.frameRate },
                { "isLooping", settings.loopTime },
                { "wrapMode", clip.wrapMode.ToString() },
                { "curveCount", curves.Count },
                { "curves", curves },
                { "events", clip.events.Length },
                { "isHumanMotion", clip.humanMotion },
            };
        }

        public static object SetClipCurve(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            string relativePath = args.ContainsKey("relativePath") ? args["relativePath"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";
            string typeName = args.ContainsKey("type") ? args["type"].ToString() : "Transform";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            Type type = Type.GetType($"UnityEngine.{typeName}, UnityEngine") ??
                        Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule") ??
                        typeof(Transform);

            // Build keyframes
            var keyframes = new List<Keyframe>();
            if (args.ContainsKey("keyframes"))
            {
                var kfList = args["keyframes"] as List<object>;
                if (kfList != null)
                {
                    foreach (var kfObj in kfList)
                    {
                        var kf = kfObj as Dictionary<string, object>;
                        if (kf == null) continue;
                        float time = kf.ContainsKey("time") ? Convert.ToSingle(kf["time"]) : 0f;
                        float value = kf.ContainsKey("value") ? Convert.ToSingle(kf["value"]) : 0f;
                        keyframes.Add(new Keyframe(time, value));
                    }
                }
            }

            var curve = new AnimationCurve(keyframes.ToArray());
            clip.SetCurve(relativePath, type, propertyName, curve);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "clipPath", path },
                { "relativePath", relativePath },
                { "propertyName", propertyName },
                { "keyframeCount", keyframes.Count },
            };
        }

        // ─── Layers ───

        public static object AddLayer(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string layerName = args.ContainsKey("layerName") ? args["layerName"].ToString() : "New Layer";

            controller.AddLayer(layerName);

            // Set weight if provided
            if (args.ContainsKey("weight"))
            {
                var layers = controller.layers;
                layers[layers.Length - 1].defaultWeight = Convert.ToSingle(args["weight"]);
                controller.layers = layers;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, layerName, layerIndex = controller.layers.Length - 1 };
        }

        // ─── Assign Controller to GameObject ───

        public static object AssignController(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string controllerPath = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
            if (controller == null)
                return new { error = $"Animator controller not found at '{controllerPath}'" };

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                animator = Undo.AddComponent<Animator>(go);

            Undo.RecordObject(animator, "Assign Animator Controller");
            animator.runtimeAnimatorController = controller;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "controller", controller.name },
            };
        }

        // ─── Keyframe Detail Operations ───

        public static object GetCurveKeyframes(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            string relativePath = args.ContainsKey("relativePath") ? args["relativePath"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            var bindings = AnimationUtility.GetCurveBindings(clip);
            EditorCurveBinding? targetBinding = null;

            foreach (var binding in bindings)
            {
                if (binding.propertyName == propertyName &&
                    binding.path == relativePath)
                {
                    targetBinding = binding;
                    break;
                }
            }

            if (!targetBinding.HasValue)
                return new { error = $"Curve not found for property '{propertyName}' at path '{relativePath}'" };

            var curve = AnimationUtility.GetEditorCurve(clip, targetBinding.Value);
            var keyframes = new List<Dictionary<string, object>>();

            for (int i = 0; i < curve.keys.Length; i++)
            {
                var kf = curve.keys[i];
                keyframes.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "time", kf.time },
                    { "value", kf.value },
                    { "inTangent", kf.inTangent },
                    { "outTangent", kf.outTangent },
                    { "inWeight", kf.inWeight },
                    { "outWeight", kf.outWeight },
                    { "weightedMode", kf.weightedMode.ToString() },
                });
            }

            return new Dictionary<string, object>
            {
                { "clipPath", path },
                { "relativePath", relativePath },
                { "propertyName", propertyName },
                { "type", targetBinding.Value.type.Name },
                { "keyframeCount", keyframes.Count },
                { "keyframes", keyframes.ToArray() },
            };
        }

        public static object RemoveCurve(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            string relativePath = args.ContainsKey("relativePath") ? args["relativePath"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";
            string typeName = args.ContainsKey("type") ? args["type"].ToString() : "Transform";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            Type type = Type.GetType($"UnityEngine.{typeName}, UnityEngine") ??
                        Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule") ??
                        typeof(Transform);

            clip.SetCurve(relativePath, type, propertyName, null);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new { success = true, clipPath = path, removedProperty = propertyName };
        }

        public static object AddKeyframe(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            string relativePath = args.ContainsKey("relativePath") ? args["relativePath"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };
            if (!args.ContainsKey("time") || !args.ContainsKey("value"))
                return new { error = "time and value are required" };

            float time = Convert.ToSingle(args["time"]);
            float value = Convert.ToSingle(args["value"]);

            // Find existing curve binding
            var bindings = AnimationUtility.GetCurveBindings(clip);
            EditorCurveBinding? targetBinding = null;

            foreach (var binding in bindings)
            {
                if (binding.propertyName == propertyName && binding.path == relativePath)
                {
                    targetBinding = binding;
                    break;
                }
            }

            AnimationCurve curve;
            EditorCurveBinding curveBinding;

            if (targetBinding.HasValue)
            {
                curveBinding = targetBinding.Value;
                curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            }
            else
            {
                // Create new curve binding
                string typeName = args.ContainsKey("type") ? args["type"].ToString() : "Transform";
                Type type = Type.GetType($"UnityEngine.{typeName}, UnityEngine") ??
                            Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule") ??
                            typeof(Transform);
                curveBinding = EditorCurveBinding.FloatCurve(relativePath, type, propertyName);
                curve = new AnimationCurve();
            }

            // Create keyframe with full tangent control
            var keyframe = new Keyframe(time, value);
            if (args.ContainsKey("inTangent"))
                keyframe.inTangent = Convert.ToSingle(args["inTangent"]);
            if (args.ContainsKey("outTangent"))
                keyframe.outTangent = Convert.ToSingle(args["outTangent"]);
            if (args.ContainsKey("inWeight"))
                keyframe.inWeight = Convert.ToSingle(args["inWeight"]);
            if (args.ContainsKey("outWeight"))
                keyframe.outWeight = Convert.ToSingle(args["outWeight"]);
            if (args.ContainsKey("weightedMode"))
            {
                WeightedMode wm;
                if (Enum.TryParse(args["weightedMode"].ToString(), true, out wm))
                    keyframe.weightedMode = wm;
            }

            int idx = curve.AddKey(keyframe);

            AnimationUtility.SetEditorCurve(clip, curveBinding, curve);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "clipPath", path },
                { "propertyName", propertyName },
                { "keyframeIndex", idx },
                { "time", time },
                { "value", value },
                { "totalKeyframes", curve.keys.Length },
            };
        }

        public static object RemoveKeyframe(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            string relativePath = args.ContainsKey("relativePath") ? args["relativePath"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";
            int keyIndex = args.ContainsKey("keyframeIndex") ? Convert.ToInt32(args["keyframeIndex"]) : -1;

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };
            if (keyIndex < 0)
                return new { error = "keyframeIndex is required (0-based)" };

            var bindings = AnimationUtility.GetCurveBindings(clip);
            EditorCurveBinding? targetBinding = null;
            foreach (var binding in bindings)
            {
                if (binding.propertyName == propertyName && binding.path == relativePath)
                {
                    targetBinding = binding;
                    break;
                }
            }

            if (!targetBinding.HasValue)
                return new { error = $"Curve not found for property '{propertyName}'" };

            var curve = AnimationUtility.GetEditorCurve(clip, targetBinding.Value);
            if (keyIndex >= curve.keys.Length)
                return new { error = $"Keyframe index {keyIndex} out of range (count: {curve.keys.Length})" };

            curve.RemoveKey(keyIndex);
            AnimationUtility.SetEditorCurve(clip, targetBinding.Value, curve);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new { success = true, removedIndex = keyIndex, remainingKeyframes = curve.keys.Length };
        }

        // ─── Animation Events ───

        public static object AddAnimationEvent(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            if (!args.ContainsKey("time") || !args.ContainsKey("functionName"))
                return new { error = "time and functionName are required" };

            var evt = new AnimationEvent();
            evt.time = Convert.ToSingle(args["time"]);
            evt.functionName = args["functionName"].ToString();

            if (args.ContainsKey("stringParameter"))
                evt.stringParameter = args["stringParameter"].ToString();
            if (args.ContainsKey("intParameter"))
                evt.intParameter = Convert.ToInt32(args["intParameter"]);
            if (args.ContainsKey("floatParameter"))
                evt.floatParameter = Convert.ToSingle(args["floatParameter"]);

            var events = clip.events.ToList();
            events.Add(evt);
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "clipPath", path },
                { "functionName", evt.functionName },
                { "time", evt.time },
                { "totalEvents", clip.events.Length },
            };
        }

        public static object RemoveAnimationEvent(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            int eventIndex = args.ContainsKey("eventIndex") ? Convert.ToInt32(args["eventIndex"]) : -1;
            if (eventIndex < 0)
                return new { error = "eventIndex is required (0-based)" };

            var events = clip.events.ToList();
            if (eventIndex >= events.Count)
                return new { error = $"Event index {eventIndex} out of range (count: {events.Count})" };

            string removedName = events[eventIndex].functionName;
            events.RemoveAt(eventIndex);
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new { success = true, removedFunction = removedName, remainingEvents = clip.events.Length };
        }

        public static object GetAnimationEvents(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            var events = new List<Dictionary<string, object>>();
            for (int i = 0; i < clip.events.Length; i++)
            {
                var evt = clip.events[i];
                events.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "time", evt.time },
                    { "functionName", evt.functionName },
                    { "stringParameter", evt.stringParameter },
                    { "intParameter", evt.intParameter },
                    { "floatParameter", evt.floatParameter },
                });
            }

            return new Dictionary<string, object>
            {
                { "clipPath", path },
                { "eventCount", events.Count },
                { "events", events.ToArray() },
            };
        }

        // ─── Clip Settings ───

        public static object SetClipSettings(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("clipPath") ? args["clipPath"].ToString() : "";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return new { error = $"Animation clip not found at '{path}'" };

            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            if (args.ContainsKey("loopTime"))
                settings.loopTime = Convert.ToBoolean(args["loopTime"]);
            if (args.ContainsKey("loopBlend"))
                settings.loopBlend = Convert.ToBoolean(args["loopBlend"]);
            if (args.ContainsKey("loopBlendOrientation"))
                settings.loopBlendOrientation = Convert.ToBoolean(args["loopBlendOrientation"]);
            if (args.ContainsKey("loopBlendPositionY"))
                settings.loopBlendPositionY = Convert.ToBoolean(args["loopBlendPositionY"]);
            if (args.ContainsKey("loopBlendPositionXZ"))
                settings.loopBlendPositionXZ = Convert.ToBoolean(args["loopBlendPositionXZ"]);
            if (args.ContainsKey("keepOriginalOrientation"))
                settings.keepOriginalOrientation = Convert.ToBoolean(args["keepOriginalOrientation"]);
            if (args.ContainsKey("keepOriginalPositionY"))
                settings.keepOriginalPositionY = Convert.ToBoolean(args["keepOriginalPositionY"]);
            if (args.ContainsKey("keepOriginalPositionXZ"))
                settings.keepOriginalPositionXZ = Convert.ToBoolean(args["keepOriginalPositionXZ"]);
            if (args.ContainsKey("mirror"))
                settings.mirror = Convert.ToBoolean(args["mirror"]);
            if (args.ContainsKey("startTime"))
                settings.startTime = Convert.ToSingle(args["startTime"]);
            if (args.ContainsKey("stopTime"))
                settings.stopTime = Convert.ToSingle(args["stopTime"]);
            if (args.ContainsKey("level"))
                settings.level = Convert.ToSingle(args["level"]);

            AnimationUtility.SetAnimationClipSettings(clip, settings);

            if (args.ContainsKey("frameRate"))
                clip.frameRate = Convert.ToSingle(args["frameRate"]);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "clipPath", path },
                { "loopTime", settings.loopTime },
                { "loopBlend", settings.loopBlend },
                { "startTime", settings.startTime },
                { "stopTime", settings.stopTime },
                { "frameRate", clip.frameRate },
            };
        }

        // ─── Transition Management ───

        public static object RemoveTransition(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string sourceName = args.ContainsKey("sourceState") ? args["sourceState"].ToString() : "";
            string destName = args.ContainsKey("destinationState") ? args["destinationState"].ToString() : "";
            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : 0;
            bool fromAnyState = args.ContainsKey("fromAnyState") && Convert.ToBoolean(args["fromAnyState"]);
            int transitionIndex = args.ContainsKey("transitionIndex") ? Convert.ToInt32(args["transitionIndex"]) : -1;

            var stateMachine = controller.layers[layerIndex].stateMachine;

            if (fromAnyState)
            {
                var transitions = stateMachine.anyStateTransitions;
                AnimatorStateTransition toRemove = null;

                if (transitionIndex >= 0 && transitionIndex < transitions.Length)
                {
                    toRemove = transitions[transitionIndex];
                }
                else if (!string.IsNullOrEmpty(destName))
                {
                    toRemove = transitions.FirstOrDefault(t => t.destinationState != null && t.destinationState.name == destName);
                }

                if (toRemove == null)
                    return new { error = "AnyState transition not found" };

                stateMachine.RemoveAnyStateTransition(toRemove);
            }
            else
            {
                if (string.IsNullOrEmpty(sourceName))
                    return new { error = "sourceState is required (or set fromAnyState to true)" };

                var sourceEntry = stateMachine.states.FirstOrDefault(s => s.state.name == sourceName);
                if (sourceEntry.state == null)
                    return new { error = $"Source state '{sourceName}' not found" };

                var transitions = sourceEntry.state.transitions;
                AnimatorStateTransition toRemove = null;

                if (transitionIndex >= 0 && transitionIndex < transitions.Length)
                {
                    toRemove = transitions[transitionIndex];
                }
                else if (!string.IsNullOrEmpty(destName))
                {
                    toRemove = transitions.FirstOrDefault(t => t.destinationState != null && t.destinationState.name == destName);
                }

                if (toRemove == null)
                    return new { error = "Transition not found" };

                sourceEntry.state.RemoveTransition(toRemove);
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, source = fromAnyState ? "AnyState" : sourceName, destination = destName };
        }

        // ─── Layer Management ───

        public static object RemoveLayer(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : -1;
            if (layerIndex < 0)
                return new { error = "layerIndex is required" };
            if (layerIndex == 0)
                return new { error = "Cannot remove the base layer (index 0)" };
            if (layerIndex >= controller.layers.Length)
                return new { error = $"Layer index {layerIndex} out of range (count: {controller.layers.Length})" };

            string removedName = controller.layers[layerIndex].name;
            controller.RemoveLayer(layerIndex);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, removedLayer = removedName, remainingLayers = controller.layers.Length };
        }

        // ─── Blend Trees ───

        public static object CreateBlendTree(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string stateName = args.ContainsKey("stateName") ? args["stateName"].ToString() : "Blend Tree";
            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : 0;
            string blendType = args.ContainsKey("blendType") ? args["blendType"].ToString() : "Simple1D";
            string blendParameter = args.ContainsKey("blendParameter") ? args["blendParameter"].ToString() : "Blend";

            if (layerIndex >= controller.layers.Length)
                return new { error = $"Layer index {layerIndex} out of range" };

            BlendTree tree;
            var state = controller.CreateBlendTreeInController(stateName, out tree, layerIndex);

            // Set blend type
            BlendTreeType btType;
            if (Enum.TryParse(blendType, true, out btType))
                tree.blendType = btType;

            tree.blendParameter = blendParameter;
            if (args.ContainsKey("blendParameterY"))
                tree.blendParameterY = args["blendParameterY"].ToString();

            // Add motions if provided
            if (args.ContainsKey("motions"))
            {
                var motions = args["motions"] as List<object>;
                if (motions != null)
                {
                    foreach (var motionObj in motions)
                    {
                        var m = motionObj as Dictionary<string, object>;
                        if (m == null) continue;

                        string clipPath = m.ContainsKey("clipPath") ? m["clipPath"].ToString() : "";
                        float threshold = m.ContainsKey("threshold") ? Convert.ToSingle(m["threshold"]) : 0f;
                        float timeScale = m.ContainsKey("timeScale") ? Convert.ToSingle(m["timeScale"]) : 1f;

                        Motion motion = null;
                        if (!string.IsNullOrEmpty(clipPath))
                            motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

                        tree.AddChild(motion, threshold);

                        // Set time scale on the last child
                        if (timeScale != 1f)
                        {
                            var children = tree.children;
                            var child = children[children.Length - 1];
                            child.timeScale = timeScale;
                            children[children.Length - 1] = child;
                            tree.children = children;
                        }
                    }
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "controllerPath", path },
                { "stateName", state.name },
                { "blendType", tree.blendType.ToString() },
                { "blendParameter", tree.blendParameter },
                { "childCount", tree.children.Length },
            };
        }

        public static object GetBlendTreeInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("controllerPath") ? args["controllerPath"].ToString() : "";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return new { error = $"Animator controller not found at '{path}'" };

            string stateName = args.ContainsKey("stateName") ? args["stateName"].ToString() : "";
            int layerIndex = args.ContainsKey("layerIndex") ? Convert.ToInt32(args["layerIndex"]) : 0;

            if (string.IsNullOrEmpty(stateName))
                return new { error = "stateName is required" };

            var stateMachine = controller.layers[layerIndex].stateMachine;
            var stateEntry = stateMachine.states.FirstOrDefault(s => s.state.name == stateName);
            if (stateEntry.state == null)
                return new { error = $"State '{stateName}' not found" };

            var blendTree = stateEntry.state.motion as BlendTree;
            if (blendTree == null)
                return new { error = $"State '{stateName}' does not contain a blend tree" };

            var children = new List<Dictionary<string, object>>();
            for (int i = 0; i < blendTree.children.Length; i++)
            {
                var child = blendTree.children[i];
                children.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "motion", child.motion != null ? child.motion.name : null },
                    { "motionPath", child.motion != null ? AssetDatabase.GetAssetPath(child.motion) : null },
                    { "threshold", child.threshold },
                    { "position", new Dictionary<string, object> { { "x", child.position.x }, { "y", child.position.y } } },
                    { "timeScale", child.timeScale },
                    { "isBlendTree", child.motion is BlendTree },
                });
            }

            return new Dictionary<string, object>
            {
                { "stateName", stateName },
                { "blendType", blendTree.blendType.ToString() },
                { "blendParameter", blendTree.blendParameter },
                { "blendParameterY", blendTree.blendParameterY },
                { "minThreshold", blendTree.minThreshold },
                { "maxThreshold", blendTree.maxThreshold },
                { "childCount", blendTree.children.Length },
                { "children", children.ToArray() },
            };
        }
    }
}
