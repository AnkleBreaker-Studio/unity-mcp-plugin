using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpAssetPreviewValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static Dictionary<string, object> Data(object value) => MiniJson.Deserialize(MiniJson.Serialize(value)) as Dictionary<string, object>;
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
    }
    private static Dictionary<string, object> Args(string path) => new Dictionary<string, object> { { "assetPath", path } };
    private static object Pixels(object value, int width = 0, int height = 0, bool green = false)
    {
        var data = Data(value);
        Require(data.ContainsKey("base64"), MiniJson.Serialize(data));
        var png = Convert.FromBase64String((string)data["base64"]);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            Require(texture.LoadImage(png), "Invalid PNG");
            if (width > 0) Require(texture.width == width && texture.height == height, "Wrong PNG size: " + texture.width + "x" + texture.height);
            if (green) foreach (var pixel in texture.GetPixels32()) Require(pixel.r < 5 && pixel.g > 250 && pixel.b < 5, "Wrong texture color");
            return new { width = texture.width, height = texture.height, bytes = png.Length, checkedPixels = green ? texture.width * texture.height : 0 };
        }
        finally { UnityEngine.Object.DestroyImmediate(texture); }
    }
    private static void Error(object value)
    {
        var data = Data(value); Require(data.ContainsKey("error"), "Expected a structured error: " + MiniJson.Serialize(data));
    }
    private static void SchedulerChecks()
    {
        var type = typeof(MCPGraphicsCommands).Assembly.GetType("UnityMCP.Editor.MCPAssetPreviewScheduler", true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var schedule = (Action<Func<Texture2D>, Func<bool>, Func<Texture2D>, Action<Texture2D>, Action<Exception>, Func<bool>, double>)Delegate.CreateDelegate(
            typeof(Action<Func<Texture2D>, Func<bool>, Func<Texture2D>, Action<Texture2D>, Action<Exception>, Func<bool>, double>), type.GetMethod("Schedule", flags));
        var progress = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), type.GetMethod("Progress", flags));
        var clear = (Action)Delegate.CreateDelegate(typeof(Action), type.GetMethod("Clear", flags));
        Func<int> count = () => ((System.Collections.ICollection)type.GetField("Pending", flags).GetValue(null)).Count;
        Func<bool> subscribed = () => (bool)type.GetField("_subscribed", flags).GetValue(null);
        var pixel = new Texture2D(2, 2);
        Action<Exception> unexpected = error => { throw new Exception("Unexpected scheduler failure", error); };
        try
        {
            Check("Pending previews yield while independent queued reads complete", () => {
                clear(); bool ready = false; int resolved = 0;
                var ticket = MCPRequestQueue.SubmitDeferredRequest("preview-wait", "graphics/asset-preview", (resolve, active) =>
                    schedule(() => ready ? pixel : null, () => true, () => pixel, value => { resolved++; resolve("preview"); }, unexpected, active, 3));
                var watch = Stopwatch.StartNew(); MCPRequestQueue.ProcessNextRequests(); watch.Stop();
                progress(EditorApplication.timeSinceStartup);
                var read = MCPRequestQueue.SubmitRequest("preview-reader", "editor/state", () => "read");
                MCPRequestQueue.ProcessNextRequests();
                Require(ticket.Status == MCPRequestQueue.RequestStatus.Executing && read.Status == MCPRequestQueue.RequestStatus.Completed, "Preview blocked another agent's read");
                ready = true; progress(EditorApplication.timeSinceStartup + 0.1); progress(EditorApplication.timeSinceStartup + 0.1);
                Require(ticket.Status == MCPRequestQueue.RequestStatus.Completed && resolved == 1 && count() == 0 && !subscribed(), "Preview did not complete exactly once");
                return new { dispatchMs = watch.Elapsed.TotalMilliseconds, independentReadBeforePreview = true };
            });
            Check("Unavailable preview falls back once when native loading stops", () => {
                clear(); int resolved = 0, fallbacks = 0;
                schedule(() => null, () => false, () => { fallbacks++; return pixel; }, value => { Require(value == pixel, "Wrong fallback"); resolved++; }, unexpected, () => true, 3);
                progress(EditorApplication.timeSinceStartup); progress(EditorApplication.timeSinceStartup);
                Require(fallbacks == 1 && resolved == 1 && count() == 0, "Fallback repeated"); return true;
            });
            Check("Loading deadline uses a fallback without sleeping", () => {
                clear(); bool resolved = false;
                schedule(() => null, () => true, () => pixel, value => resolved = value == pixel, unexpected, () => true, 3);
                progress(EditorApplication.timeSinceStartup); Require(!resolved, "Preview fell back before deadline");
                progress(EditorApplication.timeSinceStartup + 4); Require(resolved && count() == 0, "Deadline fallback missing"); return true;
            });
            Check("Native preview polling is throttled across rapid editor updates", () => {
                clear(); int polls = 0; double now = EditorApplication.timeSinceStartup;
                schedule(() => { polls++; return null; }, () => true, () => pixel, value => { }, unexpected, () => true, 3);
                for (int i = 0; i < 100; i++) progress(now);
                Require(polls == 1, "Rapid updates repeatedly polled the same native preview");
                progress(now + 0.06); Require(polls == 2, "Due polling did not resume"); clear(); return true;
            });
            Check("Inactive work is neither scheduled nor polled", () => {
                clear(); int polls = 0, results = 0; bool active = false;
                schedule(() => { polls++; return pixel; }, () => true, () => pixel, value => results++, unexpected, () => active, 3);
                Require(count() == 0, "Inactive work was retained");
                active = true; schedule(() => { polls++; return null; }, () => true, () => pixel, value => results++, unexpected, () => active, 3);
                active = false; progress(EditorApplication.timeSinceStartup);
                Require(polls == 0 && results == 0 && count() == 0 && !subscribed(), "Expired work was polled"); return true;
            });
            Check("Expiration during native polling skips result encoding", () => {
                clear(); bool active = true; int results = 0;
                schedule(() => { active = false; return pixel; }, () => true, () => pixel, value => results++, unexpected, () => active, 3);
                progress(EditorApplication.timeSinceStartup); Require(results == 0 && count() == 0, "Expired preview delivered"); return true;
            });
            Check("Polling and fallback exceptions release the operation once", () => {
                foreach (bool fallback in new[] { false, true }) {
                    clear(); int failures = 0;
                    schedule(() => { if (!fallback) throw new Exception("poll"); return null; }, () => false,
                        () => { throw new Exception("fallback"); }, value => { throw new Exception("Unexpected result"); }, error => failures++, () => true, 3);
                    progress(EditorApplication.timeSinceStartup); progress(EditorApplication.timeSinceStartup);
                    Require(failures == 1 && count() == 0 && !subscribed(), "Failed operation was retained or repeated");
                }
                return true;
            });
            Check("Result construction failure releases the operation once", () => {
                clear(); int failures = 0;
                schedule(() => pixel, () => false, () => pixel, value => { throw new Exception("encode"); }, error => failures++, () => true, 3);
                progress(EditorApplication.timeSinceStartup); progress(EditorApplication.timeSinceStartup);
                Require(failures == 1 && count() == 0, "Result failure was repeated"); return true;
            });
            Check("Pending admission and per-update polling are bounded and fair", () => {
                clear(); int failures = 0, polls = 0, resolved = 0; bool ready = false;
                for (int i = 0; i < 65; i++) schedule(() => { polls++; return ready ? pixel : null; }, () => true, () => pixel, value => resolved++, error => failures++, () => true, 3);
                Require(count() == 64 && failures == 1, "Pending admission is not bounded");
                progress(EditorApplication.timeSinceStartup); Require(polls > 0 && polls <= 4, "Too many polls in one update");
                ready = true;
                for (int i = 0; i < 100 && count() > 0; i++) progress(EditorApplication.timeSinceStartup + 0.1);
                Require(resolved == 64 && count() == 0 && !subscribed(), "Pending previews did not all complete"); return true;
            });
            Check("Reload cleanup detaches updates and drops pending callbacks", () => {
                clear(); int results = 0;
                schedule(() => null, () => true, () => pixel, value => results++, unexpected, () => true, 3);
                clear(); progress(EditorApplication.timeSinceStartup + 4);
                Require(results == 0 && count() == 0 && !subscribed(), "Reload cleanup retained callbacks"); return true;
            });
            Check("All four preview routes use the shared deferred dispatcher", () => {
                var routes = (System.Collections.IDictionary)typeof(MCPBridgeServer).GetField("_deferredRoutes", flags).GetValue(null);
                foreach (var route in new[] { "graphics/asset-preview", "graphics/prefab-render", "graphics/material-info", "graphics/texture-info" })
                    Require(routes.Contains(route), "Missing deferred route " + route);
                return true;
            });
        }
        finally { clear(); UnityEngine.Object.DestroyImmediate(pixel); }
    }
    public static void Run()
    {
        Require(Application.isBatchMode, "Use the disposable batch runner");
        Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "A graphics device is required");
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        string folder = "Assets/__AssetPreview_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        string matPath = folder + "/Material.mat", texPath = folder + "/Texture.png", prefabPath = folder + "/Prefab.prefab";
        var material = new Material(Shader.Find("Standard")); material.color = Color.red;
        AssetDatabase.CreateAsset(material, matPath);
        var source = new Texture2D(32, 16, TextureFormat.RGB24, false);
        try { source.SetPixels(Enumerable.Repeat(Color.green, 512).ToArray()); File.WriteAllBytes(texPath, source.EncodeToPNG()); }
        finally { UnityEngine.Object.DestroyImmediate(source); }
        AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceSynchronousImport);
        var root = GameObject.CreatePrimitive(PrimitiveType.Cube); root.name = "__AssetPreviewRenderer";
        root.GetComponent<Renderer>().sharedMaterial = material;
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        var sentinel = new RenderTexture(8, 8, 0); sentinel.Create(); var previousActive = RenderTexture.active;
        try
        {
            Check("Cold material preview records editor-thread occupancy", () => {
                var watch = Stopwatch.StartNew(); var result = MCPGraphicsCommands.CaptureAssetPreview(Args(matPath)); watch.Stop();
                return new { elapsedMs = watch.Elapsed.TotalMilliseconds, image = Pixels(result), scope = "Synchronous C# entry point on a newly imported material" };
            });
            Check("Asset preview retains valid default pixels", () => Pixels(MCPGraphicsCommands.CaptureAssetPreview(Args(texPath)), green: true));
            Check("Explicit asset dimensions are honored", () => {
                var args = Args(texPath); args["width"] = 40; args["height"] = 24;
                return Pixels(MCPGraphicsCommands.CaptureAssetPreview(args), 40, 24, true);
            });
            Check("Explicit prefab dimensions are honored", () => {
                var args = Args(prefabPath); args["width"] = 48; args["height"] = 32;
                return Pixels(MCPGraphicsCommands.RenderPrefabPreview(args), 48, 32);
            });
            Check("Material metadata can omit its preview", () => {
                var args = Args(matPath); args["includePreview"] = false; var result = Data(MCPGraphicsCommands.GetMaterialInfo(args));
                Require(result.ContainsKey("shaderName") && !result.ContainsKey("base64"), "includePreview=false was ignored"); return true;
            });
            Check("Texture metadata can omit its preview", () => {
                var args = Args(texPath); args["previewSize"] = 0; var result = Data(MCPGraphicsCommands.GetTextureInfo(args));
                Require(result.ContainsKey("importSettings") && !result.ContainsKey("base64"), "previewSize=0 was ignored"); return true;
            });
            Check("Texture preview size bounds the longest edge with aspect preserved", () => {
                var args = Args(texPath); args["previewSize"] = 24;
                return Pixels(MCPGraphicsCommands.GetTextureInfo(args), 24, 12, true);
            });
            Check("Documented material objectPath resolves the renderer material", () => {
                var args = new Dictionary<string, object> { { "objectPath", root.name }, { "includePreview", false } };
                var result = Data(MCPGraphicsCommands.GetMaterialInfo(args)); Require(result.ContainsKey("shaderName"), MiniJson.Serialize(result)); return true;
            });
            Check("Legacy material gameObjectPath remains accepted", () => {
                var args = new Dictionary<string, object> { { "gameObjectPath", root.name }, { "includePreview", false } };
                var result = Data(MCPGraphicsCommands.GetMaterialInfo(args)); Require(result.ContainsKey("shaderName"), MiniJson.Serialize(result)); return true;
            });
            Check("Negative material index returns a structured error", () => {
                var args = new Dictionary<string, object> { { "gameObjectPath", root.name }, { "materialIndex", -1 }, { "includePreview", false } };
                Error(MCPGraphicsCommands.GetMaterialInfo(args)); return true;
            });
            Check("Material metadata preview restores the active render target", () => {
                RenderTexture.active = sentinel; var result = MCPGraphicsCommands.GetMaterialInfo(Args(matPath));
                Require(RenderTexture.active == sentinel, "Material preview changed the active target"); return Pixels(result);
            });
            Check("Texture metadata preview restores the active render target", () => {
                RenderTexture.active = sentinel; var result = MCPGraphicsCommands.GetTextureInfo(Args(texPath));
                Require(RenderTexture.active == sentinel, "Texture preview changed the active target"); return Pixels(result, green: true);
            });
            Check("Invalid asset dimensions are refused before rendering", () => {
                var args = Args(texPath); args["width"] = 8193; args["height"] = 1; Error(MCPGraphicsCommands.CaptureAssetPreview(args)); return true;
            });
            Check("Fractional texture preview size is refused", () => {
                var args = Args(texPath); args["previewSize"] = 2.5; Error(MCPGraphicsCommands.GetTextureInfo(args)); return true;
            });
            Check("Missing asset still returns a structured error", () => { Error(MCPGraphicsCommands.CaptureAssetPreview(Args(folder + "/Missing.asset"))); return true; });
            SchedulerChecks();
        }
        finally
        {
            RenderTexture.active = previousActive; UnityEngine.Object.DestroyImmediate(sentinel); UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.DeleteAsset(folder);
        }
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpAssetPreviewValidation.json", MiniJson.Serialize(new {
            unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(), passed, checks = Checks,
            fixtureAssetsRemoved = !AssetDatabase.IsValidFolder(folder)
        }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
