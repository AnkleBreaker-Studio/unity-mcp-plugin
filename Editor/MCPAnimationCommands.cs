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
            string dir = Path.GetDirectoryName(path);
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
            string dir = Path.GetDirectoryName(path);
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
    }
}
