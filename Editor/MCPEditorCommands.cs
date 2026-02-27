using System;
using System.Collections.Generic;
using System.IO;
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

        // Short temp directory to avoid Windows 260-char path limit
        private static readonly string _shortTempDir = Path.Combine(Path.GetTempPath(), "umcp");

        /// <summary>
        /// Get a short temporary directory that avoids Windows path length issues.
        /// Creates a very short path to prevent the CodeDom compiler from hitting the
        /// 260-char limit when combining Unity's deep install path with temp file paths.
        /// </summary>
        private static string GetShortTempDir()
        {
            if (!Directory.Exists(_shortTempDir))
                Directory.CreateDirectory(_shortTempDir);
            return _shortTempDir;
        }

        /// <summary>
        /// Filter assembly references to only essential ones, using short paths where possible.
        /// The Windows path length bug occurs because CodeDom builds a command line with ALL
        /// assembly paths as -r: flags. With 200+ assemblies in long Unity paths, it overflows.
        /// </summary>
        private static void AddFilteredAssemblyReferences(System.CodeDom.Compiler.CompilerParameters parameters)
        {
            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Skip dynamic assemblies (no location)
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                        continue;

                    // Skip duplicate assembly names
                    string asmName = assembly.GetName().Name;
                    if (addedNames.Contains(asmName))
                        continue;

                    // Skip test/editor-only assemblies that are rarely needed
                    if (asmName.Contains(".Tests") || asmName.Contains("NUnit") || asmName.Contains("Moq"))
                        continue;

                    addedNames.Add(asmName);
                    parameters.ReferencedAssemblies.Add(assembly.Location);
                }
                catch { }
            }
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

                // Use short temp directory to avoid Windows 260-char path limit
                string tempDir = GetShortTempDir();
                parameters.TempFiles = new System.CodeDom.Compiler.TempFileCollection(tempDir, false);

                // Add filtered assembly references (skip duplicates and test assemblies)
                AddFilteredAssemblyReferences(parameters);

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

                // Serialize the result
                return SerializeResult(result);
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

        /// <summary>
        /// Serialize the result of ExecuteCode into a JSON-friendly structure.
        /// Handles primitives, dictionaries, anonymous objects, lists, arrays,
        /// and Unity types (Vector3, Color, etc.)
        /// </summary>
        private static object SerializeResult(object result)
        {
            if (result == null)
                return new { success = true, result = (object)null };

            // Primitives and strings
            if (result is string || result is int || result is float || result is double
                || result is bool || result is long || result is decimal)
                return new Dictionary<string, object> { { "success", true }, { "result", result } };

            // Unity Vector types
            if (result is Vector2 v2)
                return new Dictionary<string, object> { { "success", true }, { "result", new { x = v2.x, y = v2.y } } };
            if (result is Vector3 v3)
                return new Dictionary<string, object> { { "success", true }, { "result", new { x = v3.x, y = v3.y, z = v3.z } } };
            if (result is Color col)
                return new Dictionary<string, object> { { "success", true }, { "result", new { r = col.r, g = col.g, b = col.b, a = col.a } } };

            // Dictionaries
            if (result is System.Collections.IDictionary dict)
                return new Dictionary<string, object> { { "success", true }, { "result", result } };

            // Lists and arrays - serialize elements
            if (result is System.Collections.IList list)
            {
                var items = new List<object>();
                foreach (var item in list)
                    items.Add(item?.ToString());
                return new Dictionary<string, object> { { "success", true }, { "result", items }, { "count", items.Count } };
            }

            // Anonymous types and complex objects - serialize via reflection
            var type = result.GetType();
            if (type.Name.Contains("AnonymousType") || type.IsClass)
            {
                try
                {
                    var props = type.GetProperties();
                    if (props.Length > 0)
                    {
                        var obj = new Dictionary<string, object>();
                        foreach (var prop in props)
                        {
                            try { obj[prop.Name] = prop.GetValue(result)?.ToString(); }
                            catch { obj[prop.Name] = "<error>"; }
                        }
                        return new Dictionary<string, object> { { "success", true }, { "result", obj } };
                    }
                }
                catch { }
            }

            // Fallback: ToString
            return new Dictionary<string, object>
            {
                { "success", true },
                { "result", result.ToString() },
                { "type", type.Name },
            };
        }
    }
}
