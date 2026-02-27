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

        // ─── Roslyn via Reflection ───
        // Roslyn types are accessed purely through reflection so that the plugin compiles
        // even when the Microsoft.CodeAnalysis assemblies aren't directly referenced
        // (e.g. Unity 6000.3+ changed how editor assemblies are exposed).

        private static Assembly _roslynCSharpAsm;
        private static Assembly _roslynCoreAsm;
        private static bool _roslynProbed;

        /// <summary>
        /// Try to locate the Roslyn assemblies from the currently loaded AppDomain.
        /// Returns true if both Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis are found.
        /// </summary>
        private static bool TryLoadRoslyn()
        {
            if (_roslynProbed) return _roslynCSharpAsm != null && _roslynCoreAsm != null;
            _roslynProbed = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (name == "Microsoft.CodeAnalysis.CSharp") _roslynCSharpAsm = asm;
                else if (name == "Microsoft.CodeAnalysis") _roslynCoreAsm = asm;
            }

            // If not already loaded, try to find and load them from the Unity editor directory
            if (_roslynCSharpAsm == null || _roslynCoreAsm == null)
            {
                string editorDir = Path.GetDirectoryName(EditorApplication.applicationPath);
                string managedDir = Path.Combine(editorDir, "Data", "Managed");
                string toolsDir = Path.Combine(editorDir, "Data", "Tools", "Roslyn");
                string sdkRoslynDir = Path.Combine(editorDir, "Data", "DotNetSdkRoslyn");

                foreach (var searchDir in new[] { managedDir, toolsDir, sdkRoslynDir, editorDir })
                {
                    if (!Directory.Exists(searchDir)) continue;
                    try
                    {
                        if (_roslynCoreAsm == null)
                        {
                            string corePath = Path.Combine(searchDir, "Microsoft.CodeAnalysis.dll");
                            if (File.Exists(corePath))
                                _roslynCoreAsm = Assembly.LoadFrom(corePath);
                        }
                        if (_roslynCSharpAsm == null)
                        {
                            string csharpPath = Path.Combine(searchDir, "Microsoft.CodeAnalysis.CSharp.dll");
                            if (File.Exists(csharpPath))
                                _roslynCSharpAsm = Assembly.LoadFrom(csharpPath);
                        }
                    }
                    catch { }
                }
            }

            return _roslynCSharpAsm != null && _roslynCoreAsm != null;
        }

        /// <summary>
        /// Collect MetadataReference objects for Roslyn from all loaded assemblies (via reflection).
        /// </summary>
        private static object GetMetadataReferencesReflection()
        {
            // MetadataReference.CreateFromFile(string) is in Microsoft.CodeAnalysis
            var metadataRefType = _roslynCoreAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
            var createFromFile = metadataRefType.GetMethod("CreateFromFile",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);

            // Fallback: find CreateFromFile with string as first param (may have optional params)
            if (createFromFile == null)
            {
                foreach (var m in metadataRefType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "CreateFromFile") continue;
                    var pars = m.GetParameters();
                    if (pars.Length >= 1 && pars[0].ParameterType == typeof(string))
                    {
                        createFromFile = m;
                        break;
                    }
                }
            }

            // We need to find the base type for the list — use the abstract PortableExecutableReference or MetadataReference
            var listType = typeof(List<>).MakeGenericType(metadataRefType);
            var refs = (System.Collections.IList)Activator.CreateInstance(listType);
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
                    var cfPars = createFromFile.GetParameters();
                    var cfArgs = new object[cfPars.Length];
                    cfArgs[0] = assembly.Location;
                    for (int i = 1; i < cfPars.Length; i++)
                        cfArgs[i] = cfPars[i].HasDefaultValue ? cfPars[i].DefaultValue : null;
                    var metaRef = createFromFile.Invoke(null, cfArgs);
                    refs.Add(metaRef);
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

            if (!TryLoadRoslyn())
            {
                return new Dictionary<string, object>
                {
                    { "error", "Roslyn (Microsoft.CodeAnalysis) is not available in this Unity version. ExecuteCode requires Roslyn for dynamic compilation." },
                };
            }

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

                // --- Roslyn-based compilation (via reflection) ---
                // All Roslyn types accessed through reflection to avoid compile-time dependency.
                // Unity 6000+ uses CoreCLR where CodeDom/mcs can't handle netstandard facades.
                // Roslyn resolves type forwarding correctly.

                // CSharpSyntaxTree.ParseText(string)
                var syntaxTreeType = _roslynCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                var parseText = syntaxTreeType.GetMethod("ParseText",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);

                // Fallback: ParseText may have more parameters; find the best match
                if (parseText == null)
                {
                    foreach (var m in syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "ParseText") continue;
                        var pars = m.GetParameters();
                        if (pars.Length >= 1 && pars[0].ParameterType == typeof(string))
                        {
                            parseText = m;
                            break;
                        }
                    }
                }

                // Build argument array matching ParseText signature (fill optional params with defaults)
                object syntaxTree;
                {
                    var pars = parseText.GetParameters();
                    var invokeArgs = new object[pars.Length];
                    invokeArgs[0] = fullCode;
                    for (int i = 1; i < pars.Length; i++)
                        invokeArgs[i] = pars[i].HasDefaultValue ? pars[i].DefaultValue : null;
                    syntaxTree = parseText.Invoke(null, invokeArgs);
                }

                var references = GetMetadataReferencesReflection();

                string tempDir = GetShortTempDir();
                string outputPath = Path.Combine(tempDir, $"mcp_dynamic_{Guid.NewGuid():N}.dll");

                // OutputKind.DynamicallyLinkedLibrary
                var outputKindType = _roslynCoreAsm.GetType("Microsoft.CodeAnalysis.OutputKind");
                var dllOutputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");

                // CSharpCompilationOptions(OutputKind, ...)
                var compilationOptionsType = _roslynCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
                object compilationOptions;
                {
                    // Find constructor: CSharpCompilationOptions(OutputKind outputKind, ...)
                    ConstructorInfo optionsCtor = null;
                    foreach (var ctor in compilationOptionsType.GetConstructors())
                    {
                        var pars = ctor.GetParameters();
                        if (pars.Length >= 1 && pars[0].ParameterType == outputKindType)
                        {
                            optionsCtor = ctor;
                            break;
                        }
                    }
                    var ctorPars = optionsCtor.GetParameters();
                    var ctorArgs = new object[ctorPars.Length];
                    ctorArgs[0] = dllOutputKind;
                    for (int i = 1; i < ctorPars.Length; i++)
                        ctorArgs[i] = ctorPars[i].HasDefaultValue ? ctorPars[i].DefaultValue : null;
                    // Set allowUnsafe if there's such a parameter
                    for (int i = 1; i < ctorPars.Length; i++)
                    {
                        if (ctorPars[i].Name == "allowUnsafe")
                            ctorArgs[i] = true;
                    }
                    compilationOptions = optionsCtor.Invoke(ctorArgs);
                }

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                var compilationType = _roslynCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                MethodInfo createMethod = null;
                foreach (var m in compilationType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Create") continue;
                    var pars = m.GetParameters();
                    if (pars.Length == 4 && pars[0].ParameterType == typeof(string))
                    {
                        createMethod = m;
                        break;
                    }
                }

                // Wrap syntaxTree in array
                var syntaxTreeBaseType = _roslynCoreAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree");
                var syntaxTreeArray = Array.CreateInstance(syntaxTreeBaseType, 1);
                syntaxTreeArray.SetValue(syntaxTree, 0);

                var compilation = createMethod.Invoke(null, new object[]
                {
                    Path.GetFileNameWithoutExtension(outputPath),
                    syntaxTreeArray,
                    references,
                    compilationOptions
                });

                // compilation.Emit(string outputPath)
                // Use the stream overload: Emit(Stream)
                object emitResult;
                using (var stream = new FileStream(outputPath, FileMode.Create))
                {
                    var emitMethod = compilation.GetType().GetMethod("Emit",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(Stream) }, null);

                    // Fallback: find Emit with Stream as first param
                    if (emitMethod == null)
                    {
                        foreach (var m in compilation.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m.Name != "Emit") continue;
                            var pars = m.GetParameters();
                            if (pars.Length >= 1 && pars[0].ParameterType == typeof(Stream))
                            {
                                emitMethod = m;
                                break;
                            }
                        }
                    }

                    var emitArgs = new object[emitMethod.GetParameters().Length];
                    emitArgs[0] = stream;
                    for (int i = 1; i < emitArgs.Length; i++)
                    {
                        var p = emitMethod.GetParameters()[i];
                        emitArgs[i] = p.HasDefaultValue ? p.DefaultValue : null;
                    }
                    emitResult = emitMethod.Invoke(compilation, emitArgs);
                }

                // Check emitResult.Success
                bool success = (bool)emitResult.GetType().GetProperty("Success").GetValue(emitResult);

                if (!success)
                {
                    // Get Diagnostics
                    var diagnostics = (System.Collections.IEnumerable)emitResult.GetType()
                        .GetProperty("Diagnostics").GetValue(emitResult);

                    var diagnosticSeverityType = _roslynCoreAsm.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity");
                    var errorSeverity = Enum.Parse(diagnosticSeverityType, "Error");

                    var errors = new List<string>();
                    foreach (var diag in diagnostics)
                    {
                        var severity = diag.GetType().GetProperty("Severity").GetValue(diag);
                        if (!severity.Equals(errorSeverity)) continue;

                        var location = diag.GetType().GetProperty("Location").GetValue(diag);
                        var lineSpan = location.GetType().GetMethod("GetMappedLineSpan").Invoke(location, null);
                        var startPos = lineSpan.GetType().GetProperty("StartLinePosition").GetValue(lineSpan);
                        int line = (int)startPos.GetType().GetProperty("Line").GetValue(startPos);
                        string message;
                        try
                        {
                            // GetMessage has signature GetMessage(IFormatProvider = null)
                            var getMsg = diag.GetType().GetMethod("GetMessage");
                            message = getMsg != null
                                ? (string)getMsg.Invoke(diag, new object[] { null })
                                : diag.ToString();
                        }
                        catch { message = diag.ToString(); }

                        errors.Add($"Line {line + 1}: {message}");
                    }

                    return new Dictionary<string, object>
                    {
                        { "error", "Compilation failed" },
                        { "errors", errors },
                        { "code", code },
                    };
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
