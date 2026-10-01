using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UMA;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class McpUmaValidation
{
    public static void RunUnavailable()
    {
        var report = new Dictionary<string, object> { { "unityVersion", Application.unityVersion } };
        try
        {
            if (!File.Exists(".unity-mcp-validation")) throw new Exception("Unmarked validation project");
            var checks = new List<object>();
            foreach (var method in typeof(MCPUMACommands).GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var result = method.Invoke(null, new object[] { new Dictionary<string, object>() }) as Dictionary<string, object>;
                bool passed = result != null && result.ContainsKey("error") && (bool)result["umaInstalled"]
                    && !(bool)result["integrationAvailable"];
                checks.Add(new { method = method.Name, passed });
                if (!passed) throw new Exception("Expected installed UMA with disabled integration: " + method.Name);
            }
            report["checks"] = checks;
            report["passed"] = checks.Count == 16;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); }
        File.WriteAllText("Library/McpUmaUnavailableValidation.json", MiniJson.Serialize(report));
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    public static void Run()
    {
        string root = "Assets/__McpUmaValidation";
        string sentinel = "Assets/Assets/__McpUmaSentinel.txt";
        var report = new Dictionary<string, object> { { "unityVersion", Application.unityVersion } };
        var failures = new List<string>();
        GameObject model = null;
        bool owned = false;
        bool sentinelOwned = false;
        string indexPath = null;
        byte[] indexSnapshot = null;
        try
        {
            if (!File.Exists(".unity-mcp-validation")) throw new Exception("Unmarked validation project");
            if (Directory.Exists(root) || Directory.Exists("Assets/Assets")) throw new Exception("Fixture path already exists");
            Directory.CreateDirectory(root);
            owned = true;
            Directory.CreateDirectory("Assets/Assets");
            sentinelOwned = true;
            File.WriteAllText(sentinel, "unrelated fixture asset\n");
            AssetDatabase.Refresh();
            indexPath = AssetDatabase.GetAssetPath(UMAAssetIndexer.Instance);
            if (!string.IsNullOrEmpty(indexPath) && File.Exists(indexPath)) indexSnapshot = File.ReadAllBytes(indexPath);
            report["config"] = Checked(MCPUMACommands.GetProjectConfig(new Dictionary<string, object>()));
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, root + "/Material.mat");
            var umaMaterial = ScriptableObject.CreateInstance<UMAMaterial>();
            umaMaterial.material = material;
            umaMaterial.channels = new[] { new UMAMaterial.MaterialChannel {
                channelType = UMAMaterial.ChannelType.Texture, materialPropertyName = "_MainTex" } };
            AssetDatabase.CreateAsset(umaMaterial, root + "/UmaMaterial.asset");
            var mesh = new Mesh { name = "__McpMesh" };
            mesh.vertices = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(0,1,0),
                new Vector3(2,0,0), new Vector3(3,0,0), new Vector3(2,1,0) };
            mesh.uv = new[] { new Vector2(.1f,.1f), new Vector2(.8f,.1f), new Vector2(.1f,.8f),
                new Vector2(.1f,.1f), new Vector2(.8f,.1f), new Vector2(.1f,.8f) };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            AssetDatabase.CreateAsset(mesh, root + "/Mesh.asset");
            model = new GameObject("__McpModel");
            var bone = new GameObject("Root"); bone.transform.SetParent(model.transform, false);
            var body = new GameObject("Body"); body.transform.SetParent(model.transform, false);
            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh; renderer.bones = new[] { bone.transform }; renderer.rootBone = bone.transform;
            renderer.sharedMaterials = new[] { material, material };
            PrefabUtility.SaveAsPrefabAsset(model, root + "/Model.prefab");
            UnityEngine.Object.DestroyImmediate(model); model = null;
            report["inspect"] = Checked(MCPUMACommands.InspectFbx(new Dictionary<string, object> { { "fbxPath", root + "/Model.prefab" } }));
            var created = Checked(MCPUMACommands.CreateSlot(new Dictionary<string, object> {
                { "fbxPath", root + "/Model.prefab" }, { "smrName", "Body" }, { "slotName", "__McpSlot" },
                { "outputFolder", root + "/Slots" }, { "umaMaterialPath", root + "/UmaMaterial.asset" } }));
            report["created"] = created;
            report["unrelatedAssetsPreserved"] = File.Exists(sentinel);
            var paths = AssetDatabase.FindAssets("t:SlotDataAsset", new[] { root }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            report["createdSlotPaths"] = paths;
            report["slotNames"] = paths.Select(path => AssetDatabase.LoadAssetAtPath<SlotDataAsset>(path).slotName).ToArray();
            if (paths.Length != 2) throw new Exception("Expected two generated slots, received " + paths.Length);
            if (!File.Exists(sentinel)) throw new Exception("CreateSlot deleted an unrelated asset folder");
            var originalSlotBytes = paths.ToDictionary(path => path, File.ReadAllBytes);
            bool slotCollisionRejected = false;
            try
            {
                Checked(MCPUMACommands.CreateSlot(new Dictionary<string, object> {
                    { "fbxPath", root + "/Model.prefab" }, { "smrName", "Body" }, { "slotName", "__McpSlot" },
                    { "outputFolder", root + "/Slots" }, { "umaMaterialPath", root + "/UmaMaterial.asset" } }));
            }
            catch (Exception error) { slotCollisionRejected = true; report["slotCollisionError"] = error.Message; }
            Check(slotCollisionRejected && originalSlotBytes.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value))
                && Directory.Exists(root + "/Slots/__McpSlot"), "slotCollisionPreservesFiles", report, failures);
            AssetDatabase.DeleteAsset(root + "/Slots/__McpSlot");
            var overlayResult = Checked(MCPUMACommands.CreateOverlay(new Dictionary<string, object> {
                { "overlayName", "__McpOverlay" }, { "outputFolder", root + "/Overlays" },
                { "umaMaterialPath", root + "/UmaMaterial.asset" }, { "textures", new List<object> { "" } } }));
            report["overlay"] = overlayResult;
            var slots = paths.Select(path => (object)new Dictionary<string, object> {
                { "slotName", AssetDatabase.LoadAssetAtPath<SlotDataAsset>(path).slotName },
                { "overlays", new List<object> { new Dictionary<string, object> { { "overlayName", "__McpOverlay" }, { "channelCount", 1 } } } }
            }).ToList();
            var recipeResult = Checked(MCPUMACommands.CreateWardrobeRecipe(new Dictionary<string, object> {
                { "recipeName", "__McpRecipe" }, { "outputFolder", root + "/Recipes" }, { "wardrobeSlot", "Chest" },
                { "compatibleRaces", new List<object> { "Human Male 3.0" } }, { "slots", slots } }));
            report["recipe"] = recipeResult;
            report["registered"] = Checked(MCPUMACommands.RegisterAssets(new Dictionary<string, object> { { "folderPath", root } }));
            report["library"] = Checked(MCPUMACommands.ListGlobalLibrary(new Dictionary<string, object> { { "nameFilter", "__Mcp" } }));
            report["wardrobeSlots"] = Checked(MCPUMACommands.ListWardrobeSlots(new Dictionary<string, object> { { "raceName", "Human Male 3.0" } }));
            var verification = Checked(MCPUMACommands.VerifyRecipe(new Dictionary<string, object> { { "recipePath", recipeResult["recipePath"] } }));
            report["verification"] = verification;
            Check((bool)verification["valid"], "recipeValid", report, failures);
            var recipe = AssetDatabase.LoadAssetAtPath<UMA.CharacterSystem.UMAWardrobeRecipe>(recipeResult["recipePath"].ToString());
            var legacyRecipes = new List<UMA.CharacterSystem.UMAWardrobeRecipe>();
            foreach (int version in new[] { 1, 2 })
            {
                var legacy = ScriptableObject.CreateInstance<UMA.CharacterSystem.UMAWardrobeRecipe>();
                legacy.recipeString = version == 1
                    ? "{\"version\":1,\"slotsV3\":[],\"packedSlotDataList\":[{\"slotID\":\"__McpSlot\",\"OverlayDataList\":[{\"overlayID\":\"__McpOverlay\"}]}]}"
                    : "{\"version\":2,\"slotsV3\":[],\"slotsV2\":[null,{\"id\":\"__McpSlot\",\"overlays\":[{\"id\":\"__McpOverlay\"}]}]}";
                AssetDatabase.CreateAsset(legacy, root + "/Recipes/__McpLegacy" + version + ".asset");
                legacyRecipes.Add(legacy);
            }
            var overlayAsset = AssetDatabase.LoadAssetAtPath<OverlayDataAsset>(overlayResult["overlayAssetPath"].ToString());
            report["overlayHashBefore"] = overlayAsset.nameHash;
            var slotAsset = paths.Select(path => AssetDatabase.LoadAssetAtPath<SlotDataAsset>(path)).First(slot => slot.slotName == "__McpSlot");
            report["slotHashBefore"] = slotAsset.nameHash;
            string oldJson = recipe.recipeString;
            string[] legacyBeforeDryRun = legacyRecipes.Select(item => item.recipeString).ToArray();
            var dryRun = Checked(MCPUMACommands.RenameAsset(new Dictionary<string, object> {
                { "assetType", "overlay" }, { "oldName", "__McpOverlay" }, { "newName", "__McpRenamedOverlay" }, { "dryRun", true } }));
            report["dryRun"] = dryRun;
            if (recipe.recipeString != oldJson) throw new Exception("Dry-run changed recipe JSON");
            Check(legacyRecipes.Select(item => item.recipeString).SequenceEqual(legacyBeforeDryRun), "legacyDryRunPreserved", report, failures);
            report["renamed"] = Checked(MCPUMACommands.RenameAsset(new Dictionary<string, object> {
                { "assetType", "overlay" }, { "oldName", "__McpOverlay" }, { "newName", "__McpRenamedOverlay" } }));
            if (recipe.recipeString.Contains("\"__McpOverlay\"") || !recipe.recipeString.Contains("\"__McpRenamedOverlay\""))
                throw new Exception("Overlay rename did not propagate to slotsV3 JSON");
            Check(overlayAsset.nameHash == UMAUtils.StringToHash("__McpRenamedOverlay"), "overlayHashUpdated", report, failures);
            Check(legacyRecipes.All(item => !item.recipeString.Contains("\"__McpOverlay\"")), "legacyOverlayPropagation", report, failures);
            report["renamedSlot"] = Checked(MCPUMACommands.RenameAsset(new Dictionary<string, object> {
                { "assetType", "slot" }, { "oldName", "__McpSlot" }, { "newName", "__McpRenamedSlot" } }));
            Check(slotAsset.nameHash == UMAUtils.StringToHash("__McpRenamedSlot"), "slotHashUpdated", report, failures);
            Check(legacyRecipes.All(item => !item.recipeString.Contains("\"__McpSlot\"")), "legacySlotPropagation", report, failures);
            report["wardrobeFromModel"] = Checked(MCPUMACommands.CreateWardrobeFromFbx(new Dictionary<string, object> {
                { "fbxPath", root + "/Model.prefab" }, { "outputFolder", root + "/Wardrobe" },
                { "wardrobeSlot", "Chest" }, { "race", "Human Male 3.0" }, { "umaMaterialPath", root + "/UmaMaterial.asset" },
                { "variants", new List<object> { new Dictionary<string, object> { { "suffix", "Default" } } } } }));
            var wardrobe = (Dictionary<string, object>)report["wardrobeFromModel"];
            var wardrobeVerification = (Dictionary<string, object>)wardrobe["verification"];
            Check((bool)wardrobeVerification["allSlotsFound"] && (bool)wardrobeVerification["allOverlaysFound"]
                && (bool)wardrobeVerification["allRecipesValid"], "wardrobeVerified", report, failures);
            report["clonedRace"] = Checked(MCPUMACommands.CreateRace(new Dictionary<string, object> {
                { "raceName", "__McpRace" }, { "sourceRaceName", "Human Male 3.0" }, { "outputFolder", root + "/Race" } }));
            report["editedRace"] = Checked(MCPUMACommands.EditRace(new Dictionary<string, object> {
                { "racePath", root + "/Race/__McpRace.asset" }, { "newRaceName", "__McpRenamedRace" },
                { "addWardrobeSlots", new List<object> { "__McpWardrobeSlot" } } }));
            var race = AssetDatabase.LoadAssetAtPath<RaceData>(root + "/Race/__McpRenamedRace.asset");
            Check(race != null && race.raceName == "__McpRenamedRace" && race.wardrobeSlots.Contains("__McpWardrobeSlot"), "raceUpdated", report, failures);
            report["raceFromModel"] = Checked(MCPUMACommands.CreateRace(new Dictionary<string, object> {
                { "raceName", "__McpBodyRace" }, { "outputFolder", root + "/BodyRace" },
                { "fbxPath", root + "/Model.prefab" }, { "umaMaterialPath", root + "/UmaMaterial.asset" } }));
            Check(AssetDatabase.FindAssets("t:SlotDataAsset", new[] { root + "/BodyRace" }).Length == 2,
                "raceBodySlotsCreated", report, failures);
            Checked(MCPUMACommands.CreateOverlay(new Dictionary<string, object> {
                { "overlayName", "__McpOccupiedOverlay" }, { "outputFolder", root + "/Overlays" },
                { "umaMaterialPath", root + "/UmaMaterial.asset" }, { "textures", new List<object> { "" } } }));
            string beforeCollision = recipe.recipeString;
            var collision = MiniJson.Deserialize(MiniJson.Serialize(MCPUMACommands.RenameAsset(new Dictionary<string, object> {
                { "assetType", "overlay" }, { "oldName", "__McpRenamedOverlay" }, { "newName", "__McpOccupiedOverlay_Overlay" } }))) as Dictionary<string, object>;
            Check(collision != null && collision.ContainsKey("error") && overlayAsset.overlayName == "__McpRenamedOverlay"
                && recipe.recipeString == beforeCollision, "collisionLeavesReferencesIntact", report, failures);
            report["failures"] = failures;
            Check(File.Exists(sentinel), "unrelatedAssetsPreservedAtEnd", report, failures);
            report["passed"] = failures.Count == 0;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); }
        finally
        {
            if (model != null) UnityEngine.Object.DestroyImmediate(model);
            if (owned) AssetDatabase.DeleteAsset(root);
            if (sentinelOwned && AssetDatabase.IsValidFolder("Assets/Assets")) AssetDatabase.DeleteAsset("Assets/Assets");
            if (indexSnapshot != null)
            {
                EditorUtility.ClearDirty(UMAAssetIndexer.Instance);
                File.WriteAllBytes(indexPath, indexSnapshot);
                AssetDatabase.ImportAsset(indexPath, ImportAssetOptions.ForceUpdate);
                report["libraryRestored"] = File.ReadAllBytes(indexPath).SequenceEqual(indexSnapshot);
                if (!(bool)report["libraryRestored"]) report["passed"] = false;
            }
            report["fixtureRemoved"] = !Directory.Exists(root);
            File.WriteAllText("Library/McpUmaValidation.json", MiniJson.Serialize(report));
            EditorApplication.Exit(report.TryGetValue("passed", out var passed) && (bool)passed ? 0 : 1);
        }
    }

    private static void Check(bool passed, string name, Dictionary<string, object> report, List<string> failures)
    {
        report[name] = passed;
        if (!passed) failures.Add(name);
    }

    private static Dictionary<string, object> Checked(object result)
    {
        var value = MiniJson.Deserialize(MiniJson.Serialize(result)) as Dictionary<string, object>;
        if (value == null || value.ContainsKey("error") || value.TryGetValue("success", out var success) && success is bool ok && !ok)
            throw new Exception("Handler failed: " + MiniJson.Serialize(result));
        return value;
    }
}
