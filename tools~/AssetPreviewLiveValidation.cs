using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpAssetPreviewLiveValidation
{
    private static string folder;
    private static UnityEngine.SceneManagement.Scene original, fixtureScene;
    private static int sceneCount;
    private static GameObject root;
    private static MCPRequestQueue.RequestTicket probe;
    private static int heartbeats;
    private static bool done, readBeforePreview;
    private static double dispatchMs;
    private static object probeImage;
    private static bool deferred;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static object OpenLive()
    {
        Require(File.Exists(Path.Combine(Application.dataPath, "../.unity-mcp-validation")), "Validation marker missing");
        Require(folder == null && !EditorApplication.isPlaying && !EditorApplication.isCompiling, "Fixture/editor not idle");
        original = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Require(!original.isDirty && !string.IsNullOrEmpty(original.path), "Initial scene must be saved and clean");
        sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
        folder = "Assets/__AssetPreviewLive_" + Guid.NewGuid().ToString("N");
        EditorApplication.quitting += OnEnd; AssemblyReloadEvents.beforeAssemblyReload += OnEnd;
        try
        {
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            fixtureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(fixtureScene);
            var material = new Material(Shader.Find("Standard")); material.color = Color.red;
            AssetDatabase.CreateAsset(material, folder + "/Material.mat");
            var texture = new Texture2D(32, 16, TextureFormat.RGB24, false);
            try { texture.SetPixels(Enumerable.Repeat(Color.green, 512).ToArray()); File.WriteAllBytes(folder + "/Texture.png", texture.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(folder + "/Texture.png", ImportAssetOptions.ForceSynchronousImport);
            root = GameObject.CreatePrimitive(PrimitiveType.Cube); root.name = Path.GetFileName(folder);
            root.GetComponent<Renderer>().sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(root, folder + "/Prefab.prefab");
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(original);
            return new { materialPath = folder + "/Material.mat", texturePath = folder + "/Texture.png", prefabPath = folder + "/Prefab.prefab", objectPath = root.name };
        }
        catch { OnEnd(); throw; }
    }

    public static object StartColdProbe()
    {
        Require(folder != null && (probe == null || done), "Previous cold preview remains active");
        var material = new Material(Shader.Find("Standard")); material.color = Color.cyan;
        string path = folder + "/Cold_" + Guid.NewGuid().ToString("N") + ".mat";
        AssetDatabase.CreateAsset(material, path);
        var args = new Dictionary<string, object> { { "assetPath", path } };
        heartbeats = 0; done = false; readBeforePreview = false; probeImage = null;
        EditorApplication.update += Heartbeat;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var routes = (IDictionary)typeof(MCPBridgeServer).GetField("_deferredRoutes", flags).GetValue(null);
        const string route = "graphics/asset-preview";
        deferred = routes.Contains(route);
        if (deferred)
        {
            var handler = (Action<Dictionary<string, object>, Action<object>, Func<bool>>)routes[route];
            probe = MCPRequestQueue.SubmitDeferredRequest("asset-preview-cold", route, (resolve, active) =>
                handler(args, result => { FinishProbe(result); resolve(result); }, active));
        }
        else probe = MCPRequestQueue.SubmitRequest("asset-preview-cold", route, () => {
            var result = MCPGraphicsCommands.CaptureAssetPreview(args); FinishProbe(result); return result;
        });
        var watch = Stopwatch.StartNew(); MCPRequestQueue.ProcessNextRequests(); watch.Stop(); dispatchMs = watch.Elapsed.TotalMilliseconds;
        MCPRequestQueue.SubmitRequest("asset-preview-independent", "editor/state", () => { readBeforePreview = !done; return true; });
        return new { deferred, dispatchMs, completedAtDispatch = done };
    }
    private static void Heartbeat() { if (!done) heartbeats++; }
    private static void FinishProbe(object result)
    {
        var data = MiniJson.Deserialize(MiniJson.Serialize(result)) as Dictionary<string, object>;
        Require(data.ContainsKey("base64"), MiniJson.Serialize(data));
        probeImage = CheckPng((string)data["base64"], 0, 0, false);
        done = true; EditorApplication.update -= Heartbeat;
    }
    public static object ReadColdProbe() => new { done, deferred, dispatchMs, heartbeats, readBeforePreview,
        processingMs = probe?.ProcessingTimeMs ?? 0, image = probeImage };

    public static object CheckPng(string base64, int width, int height, bool green)
    {
        var bytes = Convert.FromBase64String(base64);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            Require(texture.LoadImage(bytes), "Invalid PNG");
            if (width > 0) Require(texture.width == width && texture.height == height, "Wrong PNG size");
            var pixels = texture.GetPixels32(); var rgba = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                if (green) Require(pixel.r < 5 && pixel.g > 250 && pixel.b < 5, "Wrong texture color");
                rgba[i * 4] = pixel.r; rgba[i * 4 + 1] = pixel.g; rgba[i * 4 + 2] = pixel.b; rgba[i * 4 + 3] = pixel.a;
            }
            string pixelSha256;
            using (var hash = System.Security.Cryptography.SHA256.Create()) pixelSha256 = BitConverter.ToString(hash.ComputeHash(rgba)).Replace("-", "").ToLowerInvariant();
            return new { width = texture.width, height = texture.height, bytes = bytes.Length, pixelSha256, checkedPixels = green ? texture.width * texture.height : 0 };
        }
        finally { UnityEngine.Object.DestroyImmediate(texture); }
    }
    public static object CloseLive()
    {
        Require(probe == null || done, "Wait for the native preview before closing the fixture");
        return Cleanup();
    }
    private static void OnEnd() { Cleanup(); }
    private static object Cleanup()
    {
        EditorApplication.update -= Heartbeat;
        EditorApplication.quitting -= OnEnd; AssemblyReloadEvents.beforeAssemblyReload -= OnEnd;
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
        if (original.IsValid() && original.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(original);
        if (fixtureScene.IsValid() && fixtureScene.isLoaded) EditorSceneManager.CloseScene(fixtureScene, true);
        bool assetsRemoved = folder == null || !AssetDatabase.IsValidFolder(folder) || AssetDatabase.DeleteAsset(folder);
        root = null; folder = null; probe = null; fixtureScene = default;
        return new { assetsRemoved, sceneCountRestored = UnityEngine.SceneManagement.SceneManager.sceneCount == sceneCount,
            originalSceneClean = original.IsValid() && !original.isDirty };
    }
}
