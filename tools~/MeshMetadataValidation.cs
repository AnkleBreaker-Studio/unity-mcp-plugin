using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMCP.Editor;

public static class UnityMcpMeshMetadataValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
    private static UnityEngine.SceneManagement.Scene fixtureScene, previousScene;
    private static int previousSceneCount;
    private static string prefix;
    private static GameObject root;
    private static Mesh mesh;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
    }
    private static Dictionary<string, object> Data(object value) => MiniJson.Deserialize(MiniJson.Serialize(value)) as Dictionary<string, object>;
    private static Dictionary<string, object> Args(string key = "gameObjectPath", string path = null) => new Dictionary<string, object> { { key, path ?? root.name } };
    private static Dictionary<string, object> MeshData(Dictionary<string, object> args = null) => Data(MCPGraphicsCommands.GetMeshInfo(args ?? Args()));
    private static Dictionary<string, object> RendererData(Dictionary<string, object> args = null) => Data(MCPGraphicsCommands.GetRendererInfo(args ?? Args()));
    private static void Count(Dictionary<string, object> data, string field, long expected)
    {
        Require(data.ContainsKey(field) && Convert.ToInt64(data[field]) == expected, field + " expected " + expected + ": " + MiniJson.Serialize(data));
    }
    private static T Own<T>(T value) where T : UnityEngine.Object { Owned.Add(value); return value; }
    private static Mesh MakeMesh(int vertices = 4)
    {
        var value = Own(new Mesh { name = prefix + "Mesh", indexFormat = vertices > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 });
        var positions = new Vector3[vertices];
        for (int i = 0; i < vertices; i++) positions[i] = new Vector3(i % 2, i / 2 % 2, 0);
        value.vertices = positions;
        value.normals = Enumerable.Repeat(Vector3.forward, vertices).ToArray();
        value.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), vertices).ToArray();
        value.colors32 = Enumerable.Repeat(new Color32(20, 40, 60, 255), vertices).ToArray();
        var uvs = Enumerable.Repeat(Vector2.one, vertices).ToList();
        foreach (int channel in new[] { 0, 3, 4, 7 }) value.SetUVs(channel, uvs);
        var indices = new int[vertices / 3 * 3];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        value.triangles = indices;
        return value;
    }
    public static object OpenLive()
    {
        Require(File.Exists(Path.Combine(Application.dataPath, "../.unity-mcp-validation")), "Validation project marker missing");
        Require(!EditorApplication.isPlaying && !EditorApplication.isCompiling && !fixtureScene.IsValid(), "Fixture requires an idle editor");
        previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Require(!previousScene.isDirty, "Original scene must be clean");
        Require(!string.IsNullOrEmpty(previousScene.path), "Save a clean initial scene before opening the fixture");
        previousSceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
        prefix = "__MeshMetadata_" + Guid.NewGuid().ToString("N") + "_";
        fixtureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(fixtureScene);
        EditorApplication.quitting += OnLiveEnd; AssemblyReloadEvents.beforeAssemblyReload += OnLiveEnd;
        try
        {
            mesh = MakeMesh();
            root = Own(new GameObject(prefix + "Root"));
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterials = new Material[] { null, null };
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
            return new { objectPath = root.name, missingPath = prefix + "Missing", vertexCount = mesh.vertexCount, triangleCount = 1, uvChannelCount = 4 };
        }
        catch { CloseLive(); throw; }
    }
    private static void OnLiveEnd() { CloseLive(); }
    public static object CloseLive()
    {
        EditorApplication.quitting -= OnLiveEnd; AssemblyReloadEvents.beforeAssemblyReload -= OnLiveEnd;
        foreach (var obj in Owned) if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
        Owned.Clear();
        if (previousScene.IsValid() && previousScene.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
        if (fixtureScene.IsValid() && fixtureScene.isLoaded) EditorSceneManager.CloseScene(fixtureScene, true);
        fixtureScene = default; root = null; mesh = null;
        return new { remaining = Resources.FindObjectsOfTypeAll<GameObject>().Count(item => prefix != null && item.name.StartsWith(prefix, StringComparison.Ordinal)),
            sceneCountRestored = UnityEngine.SceneManagement.SceneManager.sceneCount == previousSceneCount, originalSceneClean = previousScene.IsValid() && !previousScene.isDirty };
    }
    private static ProfilerRecorder Recorder() => ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
        ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
    private static object Measure(Func<object> action)
    {
        for (int i = 0; i < 5; i++) action();
        long controlCount, controlValue;
        using (var recorder = Recorder())
        {
            var bytes = new byte[1024 * 1024]; recorder.Stop(); GC.KeepAlive(bytes);
            controlCount = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
            controlValue = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Value : 0;
        }
        Require(controlCount > 0, "Allocation recorder failed its positive control");
        var samples = new List<object>();
        for (int sample = 0; sample < 5; sample++)
        {
            var watch = Stopwatch.StartNew();
            using (var recorder = Recorder())
            {
                object last = null;
                for (int i = 0; i < 20; i++) last = action();
                recorder.Stop(); watch.Stop(); GC.KeepAlive(last);
                samples.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, allocations = recorder.GetSample(0).Count,
                    rawRecorderValue = recorder.GetSample(0).Value });
            }
        }
        return new { vertices = 120000, iterationsPerSample = 20, controlCount, controlValue, byteMeasurementSupported = controlValue >= 1024 * 1024, samples };
    }
    public static void Run()
    {
        Require(Application.isBatchMode, "Use the disposable batch runner");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string baselineScene = "Assets/__MeshMetadataBaseline_" + Guid.NewGuid().ToString("N") + ".unity";
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), baselineScene);
        OpenLive();
        string folder = "Assets/" + prefix + "Assets";
        object cleanup = null;
        try
        {
            Check("Legacy mesh path preserves triangles, attributes and source", () => {
                Require(((Dictionary<string, object>)MCPGraphicsCommands.GetMeshInfo(Args()))["triangleCount"] is int, "Ordinary C# triangle count type changed");
                var rendererResult = (Dictionary<string, object>)MCPGraphicsCommands.GetRendererInfo(Args());
                Require(((Dictionary<string, object>)rendererResult["mesh"])["triangleCount"] is int, "Ordinary nested C# triangle count type changed");
                var data = MeshData(); Count(data, "vertexCount", 4); Count(data, "triangleCount", 1); Count(data, "subMeshCount", 1);
                Require((bool)data["hasNormals"] && (bool)data["hasTangents"] && (bool)data["hasColors"], "Attributes missing");
                Require(data["source"].ToString() == root.name + " (MeshFilter)", "Source changed"); return data;
            });
            Check("Documented mesh objectPath resolves the scene object", () => { var data = MeshData(Args("objectPath")); Count(data, "vertexCount", 4); return data; });
            Check("Documented renderer objectPath resolves the scene object", () => { var data = RendererData(Args("objectPath")); Count(data, "materialCount", 2); return data; });
            Check("Sparse UV channels include channels four and seven", () => { var data = MeshData(); Count(data, "uvChannelCount", 4); return data; });
            Check("Legacy paths take precedence when both aliases are supplied", () => {
                var args = Args(); args["objectPath"] = prefix + "Missing";
                Count(MeshData(args), "vertexCount", 4); Count(RendererData(args), "materialCount", 2); return true;
            });
            Check("Missing paths and missing components retain structured errors", () => {
                var empty = Own(new GameObject(prefix + "Empty")); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(empty, fixtureScene);
                foreach (string path in new[] { prefix + "Missing", empty.name })
                    Require(MeshData(Args(path: path)).ContainsKey("error") && RendererData(Args(path: path)).ContainsKey("error"), "Missing object/component accepted");
                Require(MeshData(new Dictionary<string, object>()).ContainsKey("error") && RendererData(new Dictionary<string, object>()).ContainsKey("error"), "Empty request accepted"); return true;
            });
            Check("Renderer preserves shared mesh and null material slots", () => {
                var filter = root.GetComponent<MeshFilter>(); var renderer = root.GetComponent<MeshRenderer>();
                var data = RendererData(); Count((Dictionary<string, object>)data["mesh"], "triangleCount", 1);
                Count(data, "materialCount", 2); Require(filter.sharedMesh == mesh && renderer.sharedMaterials.All(value => value == null), "Shared resources changed"); return data;
            });
            Check("Non-readable mesh retains metadata outside the game loop", () => {
                mesh.UploadMeshData(true); Require(!mesh.isReadable, "Fixture remained readable");
                var data = MeshData(); Count(data, "vertexCount", 4); Count(data, "triangleCount", 1); Count(data, "uvChannelCount", 4);
                Require((bool)data["hasNormals"] && (bool)data["hasTangents"] && (bool)data["hasColors"], "Attributes missing");
                Count((Dictionary<string, object>)RendererData()["mesh"], "triangleCount", 1); return data;
            });
            Check("Asset lookup retains precedence and works without a scene object", () => {
                AssetDatabase.CreateFolder("Assets", prefix + "Assets"); var asset = MakeMesh(); Owned.Remove(asset);
                AssetDatabase.CreateAsset(asset, folder + "/Mesh.asset"); var args = Args(path: prefix + "Missing"); args["assetPath"] = folder + "/Mesh.asset";
                var data = MeshData(args); Count(data, "vertexCount", 4); Require(data["source"].ToString() == args["assetPath"].ToString(), "Asset source changed"); return data;
            });
            Check("Empty mesh reports no geometry or populated attributes", () => {
                var empty = Own(new Mesh()); root.GetComponent<MeshFilter>().sharedMesh = empty;
                try { var data = MeshData(); Count(data, "vertexCount", 0); Count(data, "triangleCount", 0); Count(data, "uvChannelCount", 0);
                    Require(!(bool)data["hasNormals"] && !(bool)data["hasTangents"] && !(bool)data["hasColors"], "Empty attributes reported as populated"); return data; }
                finally { root.GetComponent<MeshFilter>().sharedMesh = mesh; }
            });
            Check("Mixed topology counts triangles and triangulated quads only", () => {
                var mixed = MakeMesh(); mixed.subMeshCount = 4;
                mixed.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
                mixed.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Quads, 1);
                mixed.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Lines, 2);
                mixed.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Points, 3);
                root.GetComponent<MeshFilter>().sharedMesh = mixed;
                try { var data = MeshData(); Count(data, "triangleCount", 3); Count((Dictionary<string, object>)RendererData()["mesh"], "triangleCount", 3); return data; }
                finally { root.GetComponent<MeshFilter>().sharedMesh = mesh; }
            });
            Check("Skinned mesh retains bone count and shared resources", () => {
                var skinnedRoot = Own(new GameObject(prefix + "Skinned")); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(skinnedRoot, fixtureScene);
                var renderer = skinnedRoot.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh; renderer.bones = new[] { skinnedRoot.transform, root.transform };
                var args = Args(path: skinnedRoot.name); var data = MeshData(args); var render = RendererData(args);
                Count(data, "boneCount", 2); Count(render, "boneCount", 2); Require((bool)data["isSkinned"] && (bool)render["isSkinned"] && renderer.sharedMesh == mesh, "Skinned metadata changed"); return data;
            });
            Check("All eight UV channels and a lone highest channel are counted", () => {
                var uvMesh = MakeMesh(); root.GetComponent<MeshFilter>().sharedMesh = uvMesh;
                try {
                    var uvs = Enumerable.Repeat(Vector2.one, 4).ToList();
                    for (int channel = 0; channel < 8; channel++) uvMesh.SetUVs(channel, uvs);
                    Count(MeshData(), "uvChannelCount", 8);
                    for (int channel = 0; channel < 7; channel++) uvMesh.SetUVs(channel, new List<Vector2>());
                    Count(MeshData(), "uvChannelCount", 1); return true;
                } finally { root.GetComponent<MeshFilter>().sharedMesh = mesh; }
            });
            Check("Each primitive topology retains the native triangle count", () => {
                var topologyMesh = MakeMesh(); root.GetComponent<MeshFilter>().sharedMesh = topologyMesh; var counts = new Dictionary<string, object>();
                try {
                    foreach (var topology in new[] { MeshTopology.Triangles, MeshTopology.Quads, MeshTopology.Lines, MeshTopology.LineStrip, MeshTopology.Points }) {
                        topologyMesh.SetIndices(topology == MeshTopology.Triangles ? new[] { 0, 1, 2 } : new[] { 0, 1, 2, 3 }, topology, 0);
                        int expected = topologyMesh.triangles.Length / 3;
                        Count(MeshData(), "triangleCount", expected); Count((Dictionary<string, object>)RendererData()["mesh"], "triangleCount", expected);
                        counts[topology.ToString()] = expected;
                    }
                    return counts;
                } finally { root.GetComponent<MeshFilter>().sharedMesh = mesh; }
            });
            Check("Cleared meshes retain empty attribute semantics despite their layout", () => {
                var cleared = MakeMesh(); cleared.Clear(true); root.GetComponent<MeshFilter>().sharedMesh = cleared;
                try { var data = MeshData(); Count(data, "vertexCount", 0); Count(data, "uvChannelCount", 0);
                    Require(!(bool)data["hasNormals"] && !(bool)data["hasTangents"] && !(bool)data["hasColors"], "Empty layout mistaken for populated data"); return data; }
                finally { root.GetComponent<MeshFilter>().sharedMesh = mesh; }
            });
            Check("Empty or null legacy aliases fall back to the documented path", () => {
                foreach (object legacy in new object[] { "", null }) {
                    var args = Args("objectPath"); args["gameObjectPath"] = legacy; args["assetPath"] = null;
                    Count(MeshData(args), "vertexCount", 4); Count(RendererData(args), "materialCount", 2);
                }
                return true;
            });
            Check("Mesh blend shapes are retained without modifying the source", () => {
                var blend = MakeMesh(); blend.AddBlendShapeFrame("FixtureShape", 100, new Vector3[4], new Vector3[4], new Vector3[4]);
                root.GetComponent<MeshFilter>().sharedMesh = blend;
                try { var data = MeshData(); Count(data, "blendShapeCount", 1); Require(blend.GetBlendShapeName(0) == "FixtureShape", "Blend shape changed"); return data; }
                finally { root.GetComponent<MeshFilter>().sharedMesh = mesh; }
            });
            Check("Prefab asset lookup retains MeshFilter and skinned mesh sources", () => {
                var asset = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/Mesh.asset");
                var prefabRoot = Own(new GameObject(prefix + "Prefab")); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(prefabRoot, fixtureScene);
                foreach (bool skinned in new[] { false, true }) {
                    if (skinned) { UnityEngine.Object.DestroyImmediate(prefabRoot.GetComponent<MeshFilter>()); prefabRoot.AddComponent<SkinnedMeshRenderer>().sharedMesh = asset; }
                    else prefabRoot.AddComponent<MeshFilter>().sharedMesh = asset;
                    string path = folder + (skinned ? "/Skinned.prefab" : "/Filtered.prefab"); PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                    var data = MeshData(new Dictionary<string, object> { { "assetPath", path } }); Count(data, "vertexCount", 4);
                    Require((bool)data["isSkinned"] == skinned && data["source"].ToString().Contains(skinned ? "SkinnedMeshRenderer" : "MeshFilter"), "Prefab source changed");
                }
                return true;
            });
            var large = MakeMesh(120000); root.GetComponent<MeshFilter>().sharedMesh = large; var measureArgs = Args();
            Check("Large mesh metadata timing and allocation control", () => Measure(() => MCPGraphicsCommands.GetMeshInfo(measureArgs)));
            Check("Large renderer metadata timing and allocation control", () => Measure(() => MCPGraphicsCommands.GetRendererInfo(measureArgs)));
        }
        finally {
            cleanup = CloseLive(); if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); AssetDatabase.DeleteAsset(baselineScene);
        }
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpMeshMetadataValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks, cleanup, fixtureAssetsRemoved = !AssetDatabase.IsValidFolder(folder) }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
