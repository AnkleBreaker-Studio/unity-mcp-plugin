using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        private static string GetShortTempDir()
        {
            if (!Directory.Exists(_shortTempDir))
                Directory.CreateDirectory(_shortTempDir);
            return _shortTempDir;
        }

        /// <summary>
        /// Collect MetadataReference objects for Roslyn from all loaded assemblies.
        /// Unlike CodeDom/mcs, Roslyn handles netstandard + type forwarding correctly,
        /// so we can reference ALL loaded assemblies without facade conflicts.
        /// </summary>
        private static List<Microsoft.CodeAnalysis.MetadataReference> GetMetadataReferences()
        {
            var refs = new List<Microsoft.CodeAnalysis.MetadataReference>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                        continue;
                    if (addedPaths.Contains(assembly.Location))
                        continue;
                    string asmName = assembly.GetName().Name;
                    if (asmName.Contains(".Tests") || asmName.Contains("NUnit") || asmName.Contains("Moq"))
                        continue;

                    addedPaths.Add(assembly.Location);
                    refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(assembly.Location));
                }
                catch { }
            }
            return refs;
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

                // --- Roslyn-based compilation ---
                // Unity 6000+ uses CoreCLR where CodeDom/mcs can't handle netstandard facades.
                // Roslyn (Microsoft.CodeAnalysis) resolves type forwarding correctly.
                var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(fullCode);
                var references = GetMetadataReferences();

                string tempDir = GetShortTempDir();
                string outputPath = Path.Combine(tempDir, $"mcp_dynamic_{Guid.NewGuid():N}.dll");

                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                    assemblyName: Path.GetFileNameWithoutExtension(outputPath),
                    syntaxTrees: new[] { syntaxTree },
                    references: references,
                    options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                        Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                        allowUnsafe: true
                    )
                );

                using (var stream = new FileStream(outputPath, FileMode.Create))
                {
                    var emitResult = compilation.Emit(stream);

                    if (!emitResult.Success)
                    {
                        var errors = emitResult.Diagnostics
                            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                            .Select(d =>
                            {
                                var lineSpan = d.Location.GetMappedLineSpan();
                                return $"Line {lineSpan.StartLinePosition.Line + 1}: {d.GetMessage()}";
                            })
                            .ToList();
                        return new Dictionary<string, object>
                        {
                            { "error", "Compilation failed" },
                            { "errors", errors },
                            { "code", code },
                        };
                    }
                }

                // Load and execute
                var compiledAssembly = Assembly.LoadFrom(outputPath);
                var compiledType = compiledAssembly.GetType("MCPDynamicCode");
                var method = compiledType.GetMethod("Execute");
                var result = method.Invoke(null, null);

                // Cleanup temp dll (best effort)
                try { File.Delete(outputPath); } catch { }

                return SerializeResult(result);
            }
            catch (TargetInvocationException ex)
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
