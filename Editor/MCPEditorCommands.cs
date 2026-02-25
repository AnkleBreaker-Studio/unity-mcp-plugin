using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPEditorCommands
    {
        public static object GetEditorState()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isCompiling", EditorApplication.isCompiling },
                { "activeScene", scene.name },
                { "activeScenePath", scene.path },
                { "sceneDirty", scene.isDirty },
                { "unityVersion", Application.unityVersion },
                { "platform", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "projectPath", Application.dataPath.Replace("/Assets", "") },
            };
        }

        public static object SetPlayMode(Dictionary<string, object> args)
        {
            string action = args.ContainsKey("action") ? args["action"].ToString() : "play";

            switch (action.ToLower())
            {
                case "play":
                    EditorApplication.isPlaying = true;
                    return new { success = true, action = "play" };
                case "pause":
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    return new { success = true, action = "pause", isPaused = EditorApplication.isPaused };
                case "stop":
                    EditorApplication.isPlaying = false;
                    return new { success = true, action = "stop" };
                default:
                    return new { error = $"Unknown action: {action}. Use 'play', 'pause', or 'stop'." };
            }
        }

        public static object ExecuteMenuItem(Dictionary<string, object> args)
        {
            string menuPath = args.ContainsKey("menuPath") ? args["menuPath"].ToString() : "";
            if (string.IsNullOrEmpty(menuPath))
                return new { error = "menuPath is required" };

            bool result = EditorApplication.ExecuteMenuItem(menuPath);
            return new { success = result, menuPath };
        }

        public static object ExecuteCode(Dictionary<string, object> args)
        {
            string code = args.ContainsKey("code") ? args["code"].ToString() : "";
            if (string.IsNullOrEmpty(code))
                return new { error = "code is required" };

            try
            {
                // Wrap user code in a static method so it can use 'return' to send data back
                string fullCode = @"
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class MCPDynamicCode
{
    public static object Execute()
    {
        " + code + @"
        return null;
    }
}";

                // Use CSharpCodeProvider to compile at runtime
                var provider = new Microsoft.CSharp.CSharpCodeProvider();
                var parameters = new System.CodeDom.Compiler.CompilerParameters();

                // Add references to all loaded assemblies
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(assembly.Location))
                            parameters.ReferencedAssemblies.Add(assembly.Location);
                    }
                    catch { }
                }

                parameters.GenerateInMemory = true;
                parameters.GenerateExecutable = false;

                var results = provider.CompileAssemblyFromSource(parameters, fullCode);

                if (results.Errors.HasErrors)
                {
                    var errors = new List<string>();
                    foreach (System.CodeDom.Compiler.CompilerError error in results.Errors)
                    {
                        if (!error.IsWarning)
                            errors.Add($"Line {error.Line}: {error.ErrorText}");
                    }
                    return new Dictionary<string, object>
                    {
                        { "error", "Compilation failed" },
                        { "errors", errors },
                        { "code", code },
                    };
                }

                var compiledType = results.CompiledAssembly.GetType("MCPDynamicCode");
                var method = compiledType.GetMethod("Execute");
                var result = method.Invoke(null, null);

                // Attempt to serialize the result
                if (result == null)
                    return new { success = true, result = (object)null };

                // If it's a primitive or string, return directly
                if (result is string || result is int || result is float || result is double || result is bool || result is long)
                    return new Dictionary<string, object> { { "success", true }, { "result", result } };

                // Try to convert to string for complex types
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "result", result.ToString() },
                    { "type", result.GetType().Name },
                };
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                return new { error = ex.InnerException?.Message ?? ex.Message, stackTrace = ex.InnerException?.StackTrace ?? ex.StackTrace };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message, stackTrace = ex.StackTrace };
            }
        }
    }
}
