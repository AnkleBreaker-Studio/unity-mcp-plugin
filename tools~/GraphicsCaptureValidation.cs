using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpGraphicsCaptureValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static UnityEngine.SceneManagement.Scene liveScene, previousScene;
    private static Camera liveCamera;
    private static GameObject liveEmpty;
    private static RenderTexture liveTarget;
    private static string livePrefix;
    private static int previousSceneCount;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
    }
    private static Dictionary<string, object> Data(object value) => MiniJson.Deserialize(MiniJson.Serialize(value)) as Dictionary<string, object>;
    private static Dictionary<string, object> Args(int width = 64, int height = 40, string camera = null)
    {
        var args = new Dictionary<string, object> { { "width", width }, { "height", height } };
        if (camera != null) args["cameraName"] = camera;
        return args;
    }
    private static void Error(object result, string contains = null)
    {
        var data = Data(result);
        Require(data.ContainsKey("error"), "Capture succeeded instead of refusing the request");
        if (contains != null) Require(data["error"].ToString().IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0, "Wrong refusal: " + data["error"]);
    }
    private static object Pixels(object result, int width, int height, Color? expected = null, string filename = null)
    {
        var data = Data(result);
        Require(data.ContainsKey("base64"), MiniJson.Serialize(data));
        byte[] png = Convert.FromBase64String(data["base64"].ToString());
        var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            Require(texture.LoadImage(png), "Invalid PNG");
            Require(texture.width == width && texture.height == height, "Wrong PNG dimensions");
            if (expected.HasValue)
                foreach (var pixel in texture.GetPixels32())
                    Require(Math.Abs(pixel.r - expected.Value.r * 255) < 5 && Math.Abs(pixel.g - expected.Value.g * 255) < 5
                        && Math.Abs(pixel.b - expected.Value.b * 255) < 5, "Rendered pixels do not match the chosen camera");
            if (filename != null) File.WriteAllBytes("Library/" + filename, png);
            return new { width, height, pngBytes = png.Length, checkedPixels = expected.HasValue ? width * height : 0 };
        }
        finally { UnityEngine.Object.DestroyImmediate(texture); }
    }
    private static Camera Camera(string name, Color color)
    {
        var root = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        var camera = root.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = color; camera.cullingMask = 0;
        return camera;
    }
    public static object OpenLive()
    {
        Require(File.Exists(Path.Combine(Application.dataPath, "../.unity-mcp-validation")), "Validation project marker missing");
        Require(!EditorApplication.isPlaying && !EditorApplication.isCompiling, "Editor must be idle");
        Require(!liveScene.IsValid(), "Live fixture already exists");
        previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Require(!previousScene.isDirty, "Initial scene must be clean");
        Require(!string.IsNullOrEmpty(previousScene.path), "Initial scene must be saved before adding the fixture scene");
        previousSceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
        livePrefix = "__GraphicsLive_" + Guid.NewGuid().ToString("N");
        liveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(liveScene);
        EditorApplication.quitting += OnLiveEnd; AssemblyReloadEvents.beforeAssemblyReload += OnLiveEnd;
        try
        {
            liveCamera = Camera(livePrefix + "_Camera", Color.blue); liveCamera.enabled = false;
            liveEmpty = new GameObject(livePrefix + "_Empty") { hideFlags = HideFlags.HideAndDontSave };
            liveTarget = new RenderTexture(8, 8, 24); liveTarget.Create(); liveCamera.targetTexture = liveTarget;
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
            return new { cameraName = liveCamera.name, missingName = livePrefix + "_Missing", emptyName = livePrefix + "_Empty", originalScene = previousScene.path };
        }
        catch { CloseLive(); throw; }
    }
    public static object CheckLivePng(string base64, int width, int height, bool blue)
    {
        Require(liveCamera != null && liveCamera.targetTexture == liveTarget, "Borrowed camera target changed");
        return Pixels(new Dictionary<string, object> { { "base64", base64 } }, width, height, blue ? (Color?)Color.blue : null);
    }
    private static void OnLiveEnd() { CloseLive(); }
    public static object CloseLive()
    {
        EditorApplication.quitting -= OnLiveEnd; AssemblyReloadEvents.beforeAssemblyReload -= OnLiveEnd;
        if (liveCamera != null) liveCamera.targetTexture = null;
        if (liveTarget != null) UnityEngine.Object.DestroyImmediate(liveTarget);
        if (liveCamera != null) UnityEngine.Object.DestroyImmediate(liveCamera.gameObject);
        if (liveEmpty != null) UnityEngine.Object.DestroyImmediate(liveEmpty);
        if (previousScene.IsValid() && previousScene.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
        if (liveScene.IsValid() && liveScene.isLoaded) EditorSceneManager.CloseScene(liveScene, true);
        liveScene = default; liveCamera = null; liveTarget = null; liveEmpty = null;
        int remaining = Resources.FindObjectsOfTypeAll<GameObject>().Count(item => livePrefix != null && item.name.StartsWith(livePrefix, StringComparison.Ordinal));
        return new { remaining, sceneCountRestored = UnityEngine.SceneManagement.SceneManager.sceneCount == previousSceneCount,
            originalSceneClean = previousScene.IsValid() && !previousScene.isDirty };
    }
    public static void Run()
    {
        Require(Application.isBatchMode, "Use the disposable batch runner");
        Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "This suite requires a graphics device");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var main = Camera("__GraphicsCaptureMain", Color.red); main.tag = "MainCamera";
        var named = Camera("__GraphicsCaptureNamed", Color.blue);
        var empty = new GameObject("__GraphicsCaptureNoCamera") { hideFlags = HideFlags.HideAndDontSave };
        var activeBefore = RenderTexture.active;
        var target = new RenderTexture(8, 8, 24); target.Create(); main.targetTexture = target;
        var sentinel = new RenderTexture(8, 8, 24); sentinel.Create();
        string assetFolder = "Assets/__GraphicsCapture_" + Guid.NewGuid().ToString("N");
        SceneView view = null;
        var activeViewField = typeof(SceneView).GetField("s_LastActiveSceneView", BindingFlags.Static | BindingFlags.NonPublic);
        var oldView = activeViewField?.GetValue(null);
        try
        {
            Check("Default game camera retains 512-square red PNG output", () => Pixels(MCPGraphicsCommands.CaptureGameView(new Dictionary<string, object>()), 512, 512, Color.red));
            Check("Explicit camera renders its own pixels at requested dimensions", () => Pixels(MCPGraphicsCommands.CaptureGameView(Args(camera: named.name)), 64, 40, Color.blue, "UnityMcpGraphicsNamed.png"));
            Check("Missing explicit camera cannot fall back to MainCamera", () => { Error(MCPGraphicsCommands.CaptureGameView(Args(camera: "__MissingCaptureCamera"))); return true; });
            Check("Explicit object without a Camera cannot fall back", () => { Error(MCPGraphicsCommands.CaptureGameView(Args(camera: empty.name))); return true; });
            Check("Fractional dimensions are refused instead of rounded", () => {
                var args = Args(); args["width"] = 32.5; Error(MCPGraphicsCommands.CaptureGameView(args), "dimension"); return true;
            });
            Check("Dimensions above the per-side limit are refused", () => { Error(MCPGraphicsCommands.CaptureGameView(Args(8193, 1)), "dimension"); return true; });
            Check("Oversized integer input returns a structured dimension error", () => {
                var args = Args(); args["width"] = (long)int.MaxValue + 1; Error(MCPGraphicsCommands.CaptureGameView(args), "dimension"); return true;
            });
            Check("Pixel budget is checked before camera lookup without a large allocation", () => {
                main.enabled = false;
                try { Error(MCPGraphicsCommands.CaptureGameView(Args(8192, 4097, "__MissingCaptureCamera")), "dimension"); return true; }
                finally { main.enabled = true; }
            });
            Check("Exact pixel budget remains admissible before missing-camera lookup", () => {
                main.enabled = false;
                try { Error(MCPGraphicsCommands.CaptureGameView(Args(8192, 4096, "__MissingCaptureCamera")), "camera"); return true; }
                finally { main.enabled = true; }
            });
            Check("Invalid dimension forms return errors without changing render state", () => {
                foreach (object value in new object[] { 0, -1, 1.25, double.NaN, double.PositiveInfinity, true, null, "wide", new Dictionary<string, object>() }) {
                    var args = Args(); args["width"] = value; RenderTexture.active = sentinel;
                    Error(MCPGraphicsCommands.CaptureGameView(args), "dimension");
                    Require(RenderTexture.active == sentinel && main.targetTexture == target, "Rejected dimensions changed render state");
                }
                return true;
            });
            Check("Whole-number numeric forms retain valid capture behavior", () => {
                foreach (object value in new object[] { 64L, 64.0, "64" }) {
                    var args = Args(camera: named.name); args["width"] = value;
                    Pixels(MCPGraphicsCommands.CaptureGameView(args), 64, 40, Color.blue);
                }
                return true;
            });
            Check("Game capture restores both borrowed render targets", () => {
                RenderTexture.active = sentinel;
                var result = MCPGraphicsCommands.CaptureGameView(Args());
                Require(main.targetTexture == target, "Camera target changed");
                Require(RenderTexture.active == sentinel, "Active render target changed");
                return Pixels(result, 64, 40, Color.red);
            });
            Check("Scene capture restores both borrowed render targets", () => {
                Require(activeViewField != null, "Scene fixture cannot select its owned view");
                view = ScriptableObject.CreateInstance<SceneView>(); activeViewField.SetValue(null, view);
                view.camera.targetTexture = target; RenderTexture.active = sentinel;
                var result = MCPGraphicsCommands.CaptureSceneView(Args());
                Require(view.camera.targetTexture == target, "Scene camera target changed");
                Require(RenderTexture.active == sentinel, "Scene capture changed active target");
                return Pixels(result, 64, 40);
            });
            Check("Scene capture enforces the same dimension boundary", () => {
                RenderTexture.active = sentinel;
                Error(MCPGraphicsCommands.CaptureSceneView(Args(8193, 1)), "dimension");
                Require(RenderTexture.active == sentinel, "Rejected Scene capture changed render state"); return true;
            });
            Check("Asset preview restores the borrowed active render target", () => {
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(assetFolder));
                var source = new Texture2D(16, 16, TextureFormat.RGB24, false);
                try {
                    source.SetPixels(Enumerable.Repeat(Color.green, 256).ToArray());
                    File.WriteAllBytes(assetFolder + "/Fixture.png", source.EncodeToPNG());
                } finally { UnityEngine.Object.DestroyImmediate(source); }
                AssetDatabase.ImportAsset(assetFolder + "/Fixture.png", ImportAssetOptions.ForceSynchronousImport);
                RenderTexture.active = sentinel;
                var result = Data(MCPGraphicsCommands.CaptureAssetPreview(new Dictionary<string, object> { { "assetPath", assetFolder + "/Fixture.png" } }));
                Require(result.ContainsKey("base64"), MiniJson.Serialize(result));
                Require(RenderTexture.active == sentinel, "Asset preview changed active target");
                return Pixels(result, Convert.ToInt32(result["width"]), Convert.ToInt32(result["height"]), Color.green);
            });
            Check("Repeated game captures do not retain capture texture objects", () => {
                for (int i = 0; i < 3; i++) MCPGraphicsCommands.CaptureGameView(Args());
                int before = Resources.FindObjectsOfTypeAll<Texture>().Length;
                for (int i = 0; i < 20; i++) MCPGraphicsCommands.CaptureGameView(Args());
                int after = Resources.FindObjectsOfTypeAll<Texture>().Length;
                Require(before == after, "Texture count grew from " + before + " to " + after);
                return new { before, after, repetitions = 20, scope = "Live texture objects after warmup; not total GPU memory" };
            });
            Check("Game capture timing samples preserve successful PNG results", () => {
                var samples = new List<double>();
                for (int warm = 0; warm < 3; warm++) MCPGraphicsCommands.CaptureGameView(Args(1024, 1024));
                for (int sample = 0; sample < 5; sample++) {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < 10; i++) Require(Data(MCPGraphicsCommands.CaptureGameView(Args(1024, 1024))).ContainsKey("base64"), "Capture failed");
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                return new { width = 1024, height = 1024, iterationsPerSample = 10, samplesMs = samples, medianMs = samples.OrderBy(x => x).ElementAt(2) };
            });
        }
        finally
        {
            if (view != null) { view.camera.targetTexture = null; UnityEngine.Object.DestroyImmediate(view); }
            activeViewField?.SetValue(null, oldView);
            main.targetTexture = null; RenderTexture.active = activeBefore;
            foreach (var obj in new UnityEngine.Object[] { main.gameObject, named.gameObject, empty, target, sentinel }) UnityEngine.Object.DestroyImmediate(obj);
            if (AssetDatabase.IsValidFolder(assetFolder)) AssetDatabase.DeleteAsset(assetFolder);
        }
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpGraphicsCaptureValidation.json", MiniJson.Serialize(new {
            unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(), maxTextureSize = SystemInfo.maxTextureSize,
            pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null ? "Built-in" : UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().FullName,
            passed, checks = Checks, fixtureAssetsRemoved = !AssetDatabase.IsValidFolder(assetFolder)
        }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
