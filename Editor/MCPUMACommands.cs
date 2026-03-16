#if UMA_INSTALLED
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UMA;
using UMA.CharacterSystem;
using UMA.Editors;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// MCP commands for UMA (Unity Multipurpose Avatar) asset creation and management.
    /// Provides high-level tools that handle all the documented pitfalls of programmatic
    /// UMA slot, overlay, and wardrobe recipe creation.
    ///
    /// REQUIRES: UMA 2.x installed (guarded by #if UMA_INSTALLED).
    /// </summary>
    public static class MCPUMACommands
    {
        // ───────────────────── uma/inspect-fbx ─────────────────────

        /// <summary>
        /// Inspect an FBX to list all SkinnedMeshRenderers with vertex counts,
        /// weighted bones (keepList), and bone count. Essential pre-step before
        /// creating slots.
        /// </summary>
        public static object InspectFbx(Dictionary<string, object> args)
        {
            string fbxPath = GetRequiredString(args, "fbxPath");

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
                return Error($"FBX not found at: {fbxPath}");

            var smrs = fbx.GetComponentsInChildren<SkinnedMeshRenderer>();
            if (smrs.Length == 0)
                return Error("No SkinnedMeshRenderers found in this FBX.");

            var results = new List<Dictionary<string, object>>();
            foreach (var smr in smrs)
            {
                var mesh = smr.sharedMesh;
                var bones = smr.bones;
                var weights = mesh.boneWeights;

                // Discover ALL weighted bones for this SMR
                var usedBones = new HashSet<string>();
                foreach (var bw in weights)
                {
                    if (bw.weight0 > 0 && bw.boneIndex0 < bones.Length)
                        usedBones.Add(bones[bw.boneIndex0].name);
                    if (bw.weight1 > 0 && bw.boneIndex1 < bones.Length)
                        usedBones.Add(bones[bw.boneIndex1].name);
                    if (bw.weight2 > 0 && bw.boneIndex2 < bones.Length)
                        usedBones.Add(bones[bw.boneIndex2].name);
                    if (bw.weight3 > 0 && bw.boneIndex3 < bones.Length)
                        usedBones.Add(bones[bw.boneIndex3].name);
                }

                results.Add(new Dictionary<string, object>
                {
                    { "smrName", smr.name },
                    { "vertexCount", mesh.vertexCount },
                    { "triangleCount", mesh.triangles.Length / 3 },
                    { "totalBones", bones.Length },
                    { "weightedBoneCount", usedBones.Count },
                    { "weightedBones", usedBones.OrderBy(b => b).ToList() },
                    { "subMeshCount", mesh.subMeshCount },
                    { "blendShapeCount", mesh.blendShapeCount }
                });
            }

            return new Dictionary<string, object>
            {
                { "fbxPath", fbxPath },
                { "smrCount", smrs.Length },
                { "skinnedMeshRenderers", results }
            };
        }

        // ───────────────────── uma/create-slot ─────────────────────

        /// <summary>
        /// Create a SlotDataAsset from an FBX's SkinnedMeshRenderer.
        /// Handles all documented pitfalls: keepList, SlotName, tags, Races,
        /// subfolder cleanup, parasite folder cleanup.
        /// </summary>
        public static object CreateSlot(Dictionary<string, object> args)
        {
            string fbxPath = GetRequiredString(args, "fbxPath");
            string smrName = GetOptionalString(args, "smrName", null);
            string slotName = GetRequiredString(args, "slotName");
            string outputFolder = GetRequiredString(args, "outputFolder");
            string umaMaterialPath = GetOptionalString(args, "umaMaterialPath", null)
                ?? GetOptionalString(args, "umaMaterial", null)
                ?? "Assets/UMA/Content/UMA_Core/MaterialSamples/UMA_ClothesBase.asset";
            // Validate umaMaterialPath: if agent passed a name instead of a path, try to resolve it
            if (!umaMaterialPath.Contains("/") && !umaMaterialPath.EndsWith(".asset"))
            {
                var matGuids = AssetDatabase.FindAssets("t:UMA.Core.UMAMaterial " + umaMaterialPath);
                if (matGuids.Length == 1)
                {
                    umaMaterialPath = AssetDatabase.GUIDToAssetPath(matGuids[0]);
                    Debug.LogWarning("[MCP-UMA] umaMaterialPath was a name, auto-resolved to: " + umaMaterialPath);
                }
                else if (matGuids.Length > 1)
                    return Error("umaMaterialPath '" + umaMaterialPath + "' looks like a name (not a path) and matches multiple UMAMaterials. Pass the full asset path starting with 'Assets/'.");
                else
                    return Error("umaMaterialPath '" + umaMaterialPath + "' is not a valid asset path and no UMAMaterial with that name was found. Expected format: 'Assets/UMA/.../MyMaterial.asset'");
            }
            string rootBone = GetOptionalString(args, "rootBone", "Root");

            // Load FBX
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
                return Error("FBX not found at: " + fbxPath);

            // Find the target SMR
            SkinnedMeshRenderer smr;
            if (!string.IsNullOrEmpty(smrName))
            {
                smr = fbx.GetComponentsInChildren<SkinnedMeshRenderer>()
                    .FirstOrDefault(s => s.name == smrName);
                if (smr == null)
                    return Error("SMR '" + smrName + "' not found in FBX. Use uma/inspect-fbx first.");
            }
            else
            {
                var smrs = fbx.GetComponentsInChildren<SkinnedMeshRenderer>();
                if (smrs.Length == 0)
                    return Error("No SkinnedMeshRenderers found in FBX.");
                if (smrs.Length > 1)
                    return Error("Multiple SMRs found — specify smrName. Use uma/inspect-fbx first.");
                smr = smrs[0];
            }

            // Load UMA Material
            var umaMaterial = AssetDatabase.LoadAssetAtPath<UMAMaterial>(umaMaterialPath);
            if (umaMaterial == null)
                return Error("UMA Material not found at: " + umaMaterialPath);

            // Ensure output folder exists
            EnsureFolderExists(outputFolder);

            // Build SlotBuilderParameters
            var sbp = new SlotBuilderParameters();
            sbp.slotMesh = smr;
            sbp.seamsMesh = null;
            sbp.material = umaMaterial;
            sbp.rootBone = rootBone;
            sbp.assetName = slotName;
            sbp.slotName = slotName;
            sbp.assetFolder = slotName;
            sbp.slotFolder = outputFolder;
            sbp.binarySerialization = false;
            sbp.calculateNormals = false;
            sbp.calculateTangents = true; // matches Slot Builder default
            sbp.udimAdjustment = true; // matches Slot Builder default
            sbp.useRootFolder = false; // matches Slot Builder default (unchecked)
            sbp.nameByMaterial = false;

            // Pitfall #1 & #2: keepAllBones must be false, keepList must never be null
            sbp.keepAllBones = false; // matches Slot Builder default
            sbp.stripBones = "";
            sbp.keepList = new List<string>(); // empty but non-null (Pitfall #2) - let UMA handle bone filtering

            // Create the slot
            SlotDataAsset slotAsset;
            try
            {
                // Debug: verify all critical references before calling UMA
                if (sbp.slotMesh == null) return Error("DEBUG: sbp.slotMesh is null");
                if (sbp.slotMesh.transform == null) return Error("DEBUG: sbp.slotMesh.transform is null");
                if (sbp.slotMesh.transform.parent == null) return Error("DEBUG: sbp.slotMesh.transform.parent is null (SMR is root object)");
                if (sbp.slotMesh.transform.parent.gameObject == null) return Error("DEBUG: parent.gameObject is null");
                if (sbp.slotMesh.sharedMesh == null) return Error("DEBUG: sbp.slotMesh.sharedMesh is null");
                if (sbp.material == null) return Error("DEBUG: sbp.material (UMAMaterial) is null");
                if (sbp.keepList == null) return Error("DEBUG: sbp.keepList is null");
                Debug.Log($"[MCP-UMA] CreateSlot: mesh={sbp.slotMesh.name}, parent={sbp.slotMesh.transform.parent.name}, rootBone={sbp.rootBone}, folder={sbp.slotFolder}, useRoot={sbp.useRootFolder}");
                
                slotAsset = UMASlotProcessingUtil.CreateSlotData(sbp);
            }
            catch (Exception ex)
            {
                return Error("CreateSlotData failed: " + ex.Message + "\nStackTrace: " + ex.StackTrace);
            }

            if (slotAsset == null)
                return Error("CreateSlotData returned null — check Unity Console for errors.");

            // Pitfall #3: Fix SlotName (CreateSlotData does NOT set it)
            slotAsset.meshData.SlotName = slotName;

            // Fix: UMA uses materialName (string) to resolve UMAMaterial at runtime.
            // Without this, the slot renders invisibly even if the material ref is set.
            if (slotAsset.material != null)
                slotAsset.materialName = slotAsset.material.name;

            // Pitfall #13: Initialize tags and Races arrays (null causes NullReferenceException)
            slotAsset.tags = new string[0];
            slotAsset.Races = new string[0];

            EditorUtility.SetDirty(slotAsset);
            AssetDatabase.SaveAssets();

            // Pitfall #4: Move files from subfolder to output folder
            string subfolder = outputFolder + "/" + slotName;
            string finalSlotPath = null;
            if (AssetDatabase.IsValidFolder(subfolder))
            {
                var guids = AssetDatabase.FindAssets("", new[] { subfolder });
                foreach (var guid in guids)
                {
                    string src = AssetDatabase.GUIDToAssetPath(guid);
                    string fileName = System.IO.Path.GetFileName(src);
                    string dest = outputFolder + "/" + fileName;
                    AssetDatabase.MoveAsset(src, dest);
                    if (fileName.EndsWith("_slot.asset") || fileName.EndsWith("_Slot.asset"))
                        finalSlotPath = dest;
                }
                AssetDatabase.DeleteAsset(subfolder);
            }

            // Pitfall #18: Clean parasite "Assets" folders
            if (AssetDatabase.IsValidFolder("Assets/Assets"))
                AssetDatabase.DeleteAsset("Assets/Assets");
            if (AssetDatabase.IsValidFolder(outputFolder + "/Assets"))
                AssetDatabase.DeleteAsset(outputFolder + "/Assets");

            AssetDatabase.Refresh();

            // FIX UMA-MCP-002: Post-process ALL SlotDataAssets created by CreateSlotData.
            // When the FBX has multiple subMeshes, CreateSlotData produces _0, _1 etc.
            // Only the first one gets materialName set — the rest render invisibly.
            var allSlotGuids = AssetDatabase.FindAssets("t:SlotDataAsset " + slotName, new[] { outputFolder });
            var allCreatedSlots = new List<Dictionary<string, object>>();

            foreach (var guid in allSlotGuids)
            {
                string slotPath = AssetDatabase.GUIDToAssetPath(guid);
                var sda = AssetDatabase.LoadAssetAtPath<SlotDataAsset>(slotPath);
                if (sda == null) continue;

                // Fix materialName on ALL sub-slots
                if (string.IsNullOrEmpty(sda.materialName) && umaMaterial != null)
                {
                    sda.materialName = umaMaterial.name;
                    Debug.Log("[MCP-UMA] Fixed materialName on " + sda.slotName + " -> " + umaMaterial.name);
                }

                // Fix SlotName if missing
                if (sda.meshData != null && string.IsNullOrEmpty(sda.meshData.SlotName))
                    sda.meshData.SlotName = sda.slotName;

                // Fix null arrays
                if (sda.tags == null) sda.tags = new string[0];
                if (sda.Races == null) sda.Races = new string[0];

                EditorUtility.SetDirty(sda);

                allCreatedSlots.Add(new Dictionary<string, object>
                {
                    { "slotName", sda.slotName },
                    { "slotAssetPath", slotPath },
                    { "materialName", sda.materialName ?? "" }
                });
            }

            AssetDatabase.SaveAssets();

            // Determine primary slot path for backward compat
            if (finalSlotPath == null && allCreatedSlots.Count > 0)
                finalSlotPath = allCreatedSlots[0]["slotAssetPath"]?.ToString();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "slotName", slotName },
                { "slotAssetPath", finalSlotPath ?? "unknown" },
                { "slotCount", allCreatedSlots.Count },
                { "allSlots", allCreatedSlots },
                // keepList removed - UMA handles bone filtering internally
                
                { "vertexCount", smr.sharedMesh.vertexCount }
            };
        }
        // ───────────────────── uma/create-overlay ─────────────────────

        /// <summary>
        /// Create an OverlayDataAsset with correct channel count matching the UMA Material.
        /// </summary>
        public static object CreateOverlay(Dictionary<string, object> args)
        {
            string overlayName = GetRequiredString(args, "overlayName");
            string outputFolder = GetRequiredString(args, "outputFolder");
            string umaMaterialPath = GetOptionalString(args, "umaMaterialPath",
                "Assets/UMA/Content/UMA_Core/MaterialSamples/UMA_ClothesBase.asset");

            // Validate umaMaterialPath: if agent passed a name instead of a path, try to resolve it
            if (!umaMaterialPath.Contains("/") && !umaMaterialPath.EndsWith(".asset"))
            {
                var matGuids = AssetDatabase.FindAssets("t:UMA.Core.UMAMaterial " + umaMaterialPath);
                if (matGuids.Length == 1)
                {
                    umaMaterialPath = AssetDatabase.GUIDToAssetPath(matGuids[0]);
                    Debug.LogWarning("[MCP-UMA] CreateOverlay: umaMaterialPath was a name, auto-resolved to: " + umaMaterialPath);
                }
                else if (matGuids.Length > 1)
                    return Error("umaMaterialPath '" + umaMaterialPath + "' looks like a name (not a path) and matches multiple UMAMaterials. Pass the full asset path starting with 'Assets/'.");
                else
                    return Error("umaMaterialPath '" + umaMaterialPath + "' is not a valid asset path and no UMAMaterial with that name was found. Expected format: 'Assets/UMA/.../MyMaterial.asset'");
            }
            // Texture paths — array of paths matching material channel count
            var texturePaths = GetOptionalList(args, "textures");

            // Robust fallback: handle all agent texture formats
            if (texturePaths == null || texturePaths.Count == 0)
            {
                // Case 1: agent passed textures as dict {"0":"path","1":"path"} or {"channel0":"path"}
                if (args.ContainsKey("textures") && args["textures"] is Dictionary<string, object> texDict)
                {
                    texturePaths = new List<object>();
                    for (int ch = 0; ch < 8; ch++)
                    {
                        string val = null;
                        // Try numeric key "0", "1", ...
                        if (texDict.ContainsKey(ch.ToString()))
                            val = texDict[ch.ToString()]?.ToString();
                        // Try "channel0", "channel1", ...
                        else if (texDict.ContainsKey("channel" + ch))
                            val = texDict["channel" + ch]?.ToString();
                        else
                            break;
                        texturePaths.Add(val);
                    }
                    if (texturePaths.Count > 0)
                        Debug.LogWarning("[MCP-UMA] CreateOverlay: textures were passed as dict instead of array. Auto-converted " + texturePaths.Count + " entries.");
                }
                // Case 2: agent passed channel0/channel1 at root level
                else if (args.ContainsKey("channel0"))
                {
                    texturePaths = new List<object>();
                    for (int ch = 0; ch < 8; ch++)
                    {
                        string key = "channel" + ch;
                        if (args.ContainsKey(key))
                            texturePaths.Add(args[key]?.ToString());
                        else
                            break;
                    }
                    Debug.LogWarning("[MCP-UMA] CreateOverlay: textures were passed as channel0/channel1/... instead of array. Auto-converted.");
                }
            }

            var umaMaterial = AssetDatabase.LoadAssetAtPath<UMAMaterial>(umaMaterialPath);
            if (umaMaterial == null)
                return Error("UMA Material not found at: " + umaMaterialPath);

            EnsureFolderExists(outputFolder);

            var overlay = ScriptableObject.CreateInstance<OverlayDataAsset>();
            overlay.overlayName = overlayName;
            overlay.material = umaMaterial;

            // Channel count from UMA Material
            int channelCount = umaMaterial.channels.Length;
            overlay.textureList = new Texture2D[channelCount];

            // Assign textures if provided
            if (texturePaths != null)
            {
                for (int i = 0; i < texturePaths.Count && i < channelCount; i++)
                {
                    string texPath = texturePaths[i]?.ToString();
                    if (!string.IsNullOrEmpty(texPath) && texPath != "null")
                    {
                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                        if (tex != null)
                            overlay.textureList[i] = tex;
                        else
                            Debug.LogWarning("[MCP UMA] Texture not found at: " + texPath);
                    }
                }
            }

            // FIX UMA-MCP-004: Avoid double _Overlay suffix if overlayName already ends with it
            string overlayFileName = overlayName.EndsWith("_Overlay", StringComparison.OrdinalIgnoreCase)
                ? overlayName + ".asset"
                : overlayName + "_Overlay.asset";
            string assetPath = outputFolder + "/" + overlayFileName;
            AssetDatabase.CreateAsset(overlay, assetPath);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "overlayName", overlayName },
                { "overlayAssetPath", assetPath },
                { "channelCount", channelCount },
                { "texturesAssigned", overlay.textureList.Count(t => t != null) }
            };
        }

        // ——————————————————— uma/create-wardrobe-recipe ———————————————————

        /// <summary>
        /// Create a UMAWardrobeRecipe with correct recipeString v3 JSON.
        /// Handles all documented pitfalls: fColors, overlay scale, blendModes,
        /// Tags, tiling, DisplayValue, compatibleRaces, Hides.
        /// Supports single-slot and multi-slot (armor+body) recipes with
        /// multiple overlays per slot (e.g. armor color + naked skin stacking).
        /// </summary>
        public static object CreateWardrobeRecipe(Dictionary<string, object> args)
        {
            string recipeName = GetRequiredString(args, "recipeName");
            string displayName = GetOptionalString(args, "displayName", recipeName);
            string wardrobeSlot = GetRequiredString(args, "wardrobeSlot");
            string outputFolder = GetRequiredString(args, "outputFolder");

            // FIX Issue #1: Resolve compatibleRaces with auto-detection.
            // Supports both "compatibleRaces" (array from TS tool) and "race" (string, legacy).
            // If "Generic" is passed or nothing, auto-detect from existing wardrobe recipes.
            var resolvedRaces = new List<string>();
            var compatRacesList = GetOptionalList(args, "compatibleRaces");
            if (compatRacesList != null && compatRacesList.Count > 0)
            {
                foreach (var r in compatRacesList)
                    if (r != null) resolvedRaces.Add(r.ToString());
            }
            else
            {
                string singleRace = GetOptionalString(args, "race", null);
                if (!string.IsNullOrEmpty(singleRace))
                    resolvedRaces.Add(singleRace);
            }

            // Auto-detect: if empty or contains only "Generic", query existing recipes
            bool needsAutoDetect = resolvedRaces.Count == 0
                || (resolvedRaces.Count == 1 && resolvedRaces[0].Equals("Generic", StringComparison.OrdinalIgnoreCase));
            if (needsAutoDetect)
            {
                var detectedRaces = AutoDetectRaces();
                if (detectedRaces.Count > 0)
                {
                    resolvedRaces = detectedRaces;
                    Debug.Log("[MCP UMA] Auto-detected compatibleRaces: " + string.Join(", ", resolvedRaces));
                }
                else if (resolvedRaces.Count == 0)
                {
                    resolvedRaces.Add("HumanRace"); // Safe default
                    Debug.LogWarning("[MCP UMA] Could not auto-detect race. Using default: HumanRace");
                }
            }

            // Use first race for recipeString JSON "race" field
            string race = resolvedRaces[0];

            // Slots — each entry: { slotName, overlays: [{ overlayName, channelCount? }] }
            var slotsData = GetRequiredList(args, "slots");
            var hidesList = GetOptionalList(args, "hides");

            if (slotsData == null || slotsData.Count == 0)
                return Error("'slots' array is required and must contain at least one slot entry.");

            EnsureFolderExists(outputFolder);

            // Build the recipeString JSON (v3 format)
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"version\":3,");
            sb.Append("\"packedSlotDataList\":[],");
            sb.Append("\"slotsV2\":[],");
            sb.Append("\"slotsV3\":[");

            // FIX Issue #2: Single neutral fColor - all overlays share colorIdx 0
            // Multiple fColors with distinct colorIdx causes overlay rendering failures.
            // The working pattern (validated against 15+ manually-created recipes) uses
            // exactly ONE fColor entry and all overlays reference colorIdx 0.
            bool isFirstSlot = true;
            int totalOverlays = 0;
            int maxChannelCount = 3;

            foreach (var slotObj in slotsData)
            {
                var slotDict = slotObj as Dictionary<string, object>;
                if (slotDict == null) continue;

                string slotId = GetRequiredString(slotDict, "slotName");

                // Get overlays array for this slot
                var overlaysList = GetOptionalList(slotDict, "overlays");

                // Backward compat: if no "overlays" array, try single "overlayName"
                if (overlaysList == null || overlaysList.Count == 0)
                {
                    string singleOverlay = GetOptionalString(slotDict, "overlayName", null);
                    if (singleOverlay != null)
                    {
                        int ch = GetOptionalInt(slotDict, "channelCount", 3);
                        overlaysList = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                { "overlayName", singleOverlay },
                                { "channelCount", ch }
                            }
                        };
                    }
                    else
                    {
                        return Error($"Slot '{slotId}' must have an 'overlays' array or a single 'overlayName'.");
                    }
                }

                if (!isFirstSlot) sb.Append(",");
                isFirstSlot = false;

                // Build slot entry
                sb.Append("{");
                sb.Append("\"id\":\"" + EscapeJson(slotId) + "\",");
                sb.Append("\"scale\":100,");
                sb.Append("\"copyIdx\":-1,");
                sb.Append("\"overlays\":[");

                bool isFirstOverlay = true;
                foreach (var overlayObj in overlaysList)
                {
                    var overlayDict = overlayObj as Dictionary<string, object>;
                    if (overlayDict == null) continue;

                    string overlayId = GetRequiredString(overlayDict, "overlayName");
                    int channelCount = GetOptionalInt(overlayDict, "channelCount", 3);
                    totalOverlays++;

                    if (!isFirstOverlay) sb.Append(",");
                    isFirstOverlay = false;

                    sb.Append("{");
                    sb.Append("\"id\":\"" + EscapeJson(overlayId) + "\",");
                    sb.Append("\"colorIdx\":0,");
                    sb.Append("\"rect\":[0.0,0.0,0.0,0.0],");
                    sb.Append("\"isTransformed\":false,");
                    sb.Append("\"scale\":{\"x\":1.0,\"y\":1.0,\"z\":1.0},"); // Pitfall #6
                    sb.Append("\"rotation\":0.0,");

                    // Pitfall #7: blendModes, Tags, tiling — one entry per channel
                    sb.Append("\"blendModes\":[");
                    sb.Append(string.Join(",", Enumerable.Repeat("0", channelCount)));
                    sb.Append("],");
                    sb.Append("\"Tags\":[],");
                    sb.Append("\"tiling\":[");
                    sb.Append(string.Join(",", Enumerable.Repeat("false", channelCount)));
                    sb.Append("],");
                    sb.Append("\"uvOverride\":0");
                    sb.Append("}");

                    // Track max channel count for single fColor entry
                    if (channelCount > maxChannelCount) maxChannelCount = channelCount;
                }

                sb.Append("],"); // end overlays array

                sb.Append("\"Tags\":[],\"Races\":[],");
                sb.Append("\"blendShapeTarget\":\"\",");
                sb.Append("\"overSmoosh\":0.009999999776482582,");
                sb.Append("\"smooshDistance\":0.0010000000474974514,");
                sb.Append("\"smooshInvertX\":false,");
                sb.Append("\"smooshInvertY\":true,");
                sb.Append("\"smooshInvertZ\":false,");
                sb.Append("\"smooshInvertDist\":true,");
                sb.Append("\"smooshTargetTag\":\"\",");
                sb.Append("\"smooshableTag\":\"\",");
                sb.Append("\"isSwapSlot\":false,");
                sb.Append("\"swapTag\":\"LongHair\",");
                sb.Append("\"uvOverride\":0,");
                sb.Append("\"isDisabled\":false,");
                sb.Append("\"expandAlongNormal\":0");
                sb.Append("}");
            }

            sb.Append("],"); // end slotsV3

            sb.Append("\"colors\":[],");
            // FIX Issue #2: Single neutral fColor entry (all overlays share colorIdx 0)
            sb.Append("\"fColors\":[{");
            sb.Append("\"name\":\"" + (maxChannelCount > 1 ? "-" : "") + "\",");
            sb.Append("\"colors\":[");
            var channelColorParts = new List<string>();
            for (int c = 0; c < maxChannelCount; c++)
                channelColorParts.Add("255,255,255,255,0,0,0,0");
            sb.Append(string.Join(",", channelColorParts));
            sb.Append("],");
            sb.Append("\"ShaderParms\":[],");
            sb.Append("\"alwaysUpdate\":false,");
            sb.Append("\"alwaysUpdateParms\":false,");
            sb.Append("\"isBaseColor\":false,");
            sb.Append("\"displayColor\":-1");
            sb.Append("}],");

            sb.Append("\"sharedColorCount\":0,");
            sb.Append("\"race\":\"" + EscapeJson(race) + "\",");
            sb.Append("\"packedDna\":[],");
            sb.Append("\"uvOverride\":0");
            sb.Append("}");

            string recipeJson = sb.ToString();

            // Create the recipe asset
            var recipe = ScriptableObject.CreateInstance<UMAWardrobeRecipe>();
            recipe.name = recipeName;

            string recipePath = outputFolder + "/" + recipeName + ".asset";
            AssetDatabase.CreateAsset(recipe, recipePath);

            // Use SerializedObject for internal fields
            var so = new SerializedObject(recipe);
            so.FindProperty("recipeString").stringValue = recipeJson;
            so.FindProperty("recipeType").stringValue = "Wardrobe"; // Pitfall #10
            so.FindProperty("DisplayValue").stringValue = displayName; // Pitfall #8
            so.FindProperty("wardrobeSlot").stringValue = wardrobeSlot;

            // Compatible races - FIX Issue #1: support multiple races from resolvedRaces
            var racesProp = so.FindProperty("compatibleRaces");
            racesProp.arraySize = resolvedRaces.Count;
            for (int i = 0; i < resolvedRaces.Count; i++)
                racesProp.GetArrayElementAtIndex(i).stringValue = resolvedRaces[i];

            // Hides — chest recipes should hide base torso, etc.
            if (hidesList != null && hidesList.Count > 0)
            {
                var hidesProp = so.FindProperty("Hides");
                hidesProp.arraySize = hidesList.Count;
                for (int i = 0; i < hidesList.Count; i++)
                    hidesProp.GetArrayElementAtIndex(i).stringValue = hidesList[i]?.ToString() ?? "";
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "recipeName", recipeName },
                { "recipePath", recipePath },
                { "wardrobeSlot", wardrobeSlot },
                { "race", race },
                { "slotCount", slotsData.Count },
                { "overlayCount", totalOverlays },
                { "recipeJsonLength", recipeJson.Length }
            };
        }
        // ───────────────────── uma/register-assets ─────────────────────

        /// <summary>
        /// Register UMA assets (Slot, Overlay, WardrobeRecipe) in the Global Library
        /// using EvilAddAsset + ForceSave.
        /// </summary>
        public static object RegisterAssets(Dictionary<string, object> args)
        {
            // FIX UMA-MCP-005: Support both explicit assetPaths and folderPath (recursive scan)
            var assetPaths = GetOptionalList(args, "assetPaths");
            string folderPath = GetOptionalString(args, "folderPath", null);

            if (!string.IsNullOrEmpty(folderPath))
            {
                if (assetPaths == null) assetPaths = new List<object>();
                var slotGuids = AssetDatabase.FindAssets("t:SlotDataAsset", new[] { folderPath });
                foreach (var g in slotGuids) assetPaths.Add(AssetDatabase.GUIDToAssetPath(g));
                var overlayGuids = AssetDatabase.FindAssets("t:OverlayDataAsset", new[] { folderPath });
                foreach (var g in overlayGuids) assetPaths.Add(AssetDatabase.GUIDToAssetPath(g));
                var recipeGuids = AssetDatabase.FindAssets("t:UMAWardrobeRecipe", new[] { folderPath });
                foreach (var g in recipeGuids) assetPaths.Add(AssetDatabase.GUIDToAssetPath(g));
                Debug.Log("[MCP-UMA] Scanned folder " + folderPath + ": found " + assetPaths.Count + " UMA assets.");
            }

            if (assetPaths == null || assetPaths.Count == 0)
                return Error("Either 'assetPaths' array or 'folderPath' is required.");

            var context = UMAAssetIndexer.Instance;
            var registered = new List<Dictionary<string, object>>();
            var errors = new List<string>();

            foreach (var pathObj in assetPaths)
            {
                string path = pathObj?.ToString();
                if (string.IsNullOrEmpty(path)) continue;

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                {
                    errors.Add("Asset not found: " + path);
                    continue;
                }

                try
                {
                    string assetType = "unknown";
                    string assetName = "";

                    if (asset is SlotDataAsset slot)
                    {
                        assetType = "SlotDataAsset";
                        assetName = slot.slotName;
                        if (!context.HasAsset<SlotDataAsset>(slot.slotName))
                            context.EvilAddAsset(typeof(SlotDataAsset), slot);
                    }
                    else if (asset is OverlayDataAsset overlay)
                    {
                        assetType = "OverlayDataAsset";
                        assetName = overlay.overlayName;
                        if (!context.HasAsset<OverlayDataAsset>(overlay.overlayName))
                            context.EvilAddAsset(typeof(OverlayDataAsset), overlay);
                    }
                    else if (asset is UMAWardrobeRecipe recipe)
                    {
                        assetType = "UMAWardrobeRecipe";
                        assetName = recipe.name;
                        if (!context.HasAsset<UMAWardrobeRecipe>(assetName))
                            context.EvilAddAsset(typeof(UMAWardrobeRecipe), recipe);
                    }
                    else
                    {
                        errors.Add("Unsupported asset type at: " + path + " (type: " + asset.GetType().Name + ")");
                        continue;
                    }

                    registered.Add(new Dictionary<string, object>
                    {
                        { "path", path },
                        { "type", assetType },
                        { "name", assetName }
                    });
                }
                catch (Exception ex)
                {
                    errors.Add("Failed to register " + path + ": " + ex.Message);
                }
            }

            context.ForceSave();

            return new Dictionary<string, object>
            {
                { "success", errors.Count == 0 },
                { "registeredCount", registered.Count },
                { "registered", registered },
                { "errors", errors }
            };
        }

        // ───────────────────── uma/list-global-library ─────────────────────

        /// <summary>
        /// List assets registered in the UMA Global Library, optionally filtered by type.
        /// Accepts short names (Slot, Overlay, WardrobeRecipe, Race), full C# type names
        /// (SlotDataAsset, OverlayDataAsset, UMAWardrobeRecipe, RaceData), or lowercase
        /// aliases (slot, overlay, recipe, race). All comparisons are case-insensitive.
        /// </summary>
        public static object ListGlobalLibrary(Dictionary<string, object> args)
        {
            string typeFilter = GetOptionalString(args, "type", "all");
            string nameFilter = GetOptionalString(args, "nameFilter", null);
            int limit = GetOptionalInt(args, "limit", 100);

            // Normalize type filter — map all accepted aliases to canonical lowercase keys
            string normalized = NormalizeTypeFilter(typeFilter);

            var context = UMAAssetIndexer.Instance;
            var results = new List<Dictionary<string, object>>();

            void AddItems<T>(string typeName) where T : UnityEngine.Object
            {
                var items = context.GetAllAssets<T>(null);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    string itemName = item.name;
                    if (nameFilter != null && !itemName.ToLower().Contains(nameFilter.ToLower()))
                        continue;
                    if (results.Count >= limit)
                        return;

                    string path = AssetDatabase.GetAssetPath(item);
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", itemName },
                        { "type", typeName },
                        { "path", path }
                    });
                }
            }

            if (normalized == "all" || normalized == "slot")
                AddItems<SlotDataAsset>("SlotDataAsset");
            if (normalized == "all" || normalized == "overlay")
                AddItems<OverlayDataAsset>("OverlayDataAsset");
            if (normalized == "all" || normalized == "recipe")
                AddItems<UMAWardrobeRecipe>("UMAWardrobeRecipe");
            if (normalized == "all" || normalized == "race")
                AddItems<RaceData>("RaceData");

            return new Dictionary<string, object>
            {
                { "count", results.Count },
                { "typeFilter", typeFilter },
                { "assets", results }
            };
        }

        /// <summary>
        /// Normalize a type filter string to a canonical lowercase key.
        /// Accepts: short names (Slot, Overlay, WardrobeRecipe, Race),
        /// full C# names (SlotDataAsset, OverlayDataAsset, UMAWardrobeRecipe, RaceData),
        /// and lowercase aliases (slot, overlay, recipe, race, all).
        /// </summary>
        private static string NormalizeTypeFilter(string typeFilter)
        {
            if (string.IsNullOrEmpty(typeFilter))
                return "all";

            // Case-insensitive lookup table: all accepted values → canonical key
            var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Canonical lowercase
                { "all",                "all" },
                { "slot",               "slot" },
                { "overlay",            "overlay" },
                { "recipe",             "recipe" },
                { "race",               "race" },
                // Short user-friendly names (from MCP tool description)
                { "WardrobeRecipe",     "recipe" },
                // Full C# type names (UMA internals)
                { "SlotDataAsset",      "slot" },
                { "OverlayDataAsset",   "overlay" },
                { "UMAWardrobeRecipe",  "recipe" },
                { "RaceData",           "race" },
            };

            if (typeMap.TryGetValue(typeFilter, out string canonical))
                return canonical;

            // Unknown filter — return as-is lowercase so it won't match anything
            // but won't crash either. The "count: 0" result signals the problem.
            Debug.LogWarning($"[MCP UMA] Unknown type filter '{typeFilter}'. " +
                "Accepted values: Slot, Overlay, WardrobeRecipe, Race, All, " +
                "or full type names: SlotDataAsset, OverlayDataAsset, UMAWardrobeRecipe, RaceData.");
            return typeFilter.ToLowerInvariant();
        }

        // ───────────────────── uma/list-wardrobe-slots ─────────────────────

        /// <summary>
        /// List available wardrobe slots for a given race.
        /// Accepts "raceName" (from MCP tool schema) or "race" (legacy).
        /// </summary>
        public static object ListWardrobeSlots(Dictionary<string, object> args)
        {
            // Accept both "raceName" (MCP schema) and "race" (legacy) keys
            string raceName = GetOptionalString(args, "raceName", null)
                           ?? GetOptionalString(args, "race", "HumanRace");

            var context = UMAAssetIndexer.Instance;
            var raceAssets = context.GetAllAssets<RaceData>(null);
            RaceData race = null;

            foreach (var rd in raceAssets)
            {
                if (rd != null && rd.raceName == raceName)
                {
                    race = rd;
                    break;
                }
            }

            if (race == null)
            {
                // List all available races as hint
                var raceNames = raceAssets.Where(r => r != null).Select(r => r.raceName).ToList();
                return new Dictionary<string, object>
                {
                    { "error", "Race '" + raceName + "' not found in Global Library." },
                    { "availableRaces", raceNames }
                };
            }

            var slots = new List<string>();
            if (race.wardrobeSlots != null)
            {
                foreach (var ws in race.wardrobeSlots)
                    slots.Add(ws);
            }

            return new Dictionary<string, object>
            {
                { "race", raceName },
                { "wardrobeSlotCount", slots.Count },
                { "wardrobeSlots", slots }
            };
        }

        // ───────────────────── uma/list-uma-materials ─────────────────────

        /// <summary>
        /// List all UMA Material assets in the project.
        /// </summary>
        public static object ListUMAMaterials(Dictionary<string, object> args)
        {
            var guids = AssetDatabase.FindAssets("t:UMAMaterial");
            var results = new List<Dictionary<string, object>>();

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<UMAMaterial>(path);
                if (mat == null) continue;

                results.Add(new Dictionary<string, object>
                {
                    { "name", mat.name },
                    { "path", path },
                    { "channelCount", mat.channels != null ? mat.channels.Length : 0 }
                });
            }

            return new Dictionary<string, object>
            {
                { "count", results.Count },
                { "materials", results }
            };
        }

        // ───────────────────── uma/verify-recipe ─────────────────────

        /// <summary>
        /// Validate a wardrobe recipe by checking that all referenced slots and overlays
        /// exist in the Global Library, that materialName is set on slots, and that fColors
        /// has at least one entry if overlays reference colorIdx.
        /// FR-001: Post-creation verification tool.
        /// </summary>
        public static object VerifyRecipe(Dictionary<string, object> args)
        {
            string recipePath = GetRequiredString(args, "recipePath");

            var recipe = AssetDatabase.LoadAssetAtPath<UMAWardrobeRecipe>(recipePath);
            if (recipe == null)
                return Error("WardrobeRecipe not found at: " + recipePath);

            var issues = new List<Dictionary<string, object>>();
            var info = new Dictionary<string, object>();

            // Basic recipe info
            info["recipeName"] = recipe.name;
            info["wardrobeSlot"] = recipe.wardrobeSlot ?? "";
            info["compatibleRaces"] = recipe.compatibleRaces != null
                ? recipe.compatibleRaces.ToList()
                : new List<string>();

            // Parse recipeString JSON
            string recipeJson = recipe.recipeString;
            if (string.IsNullOrEmpty(recipeJson))
            {
                issues.Add(MakeIssue("critical", "recipeString", "recipeString is empty — recipe has no content."));
                return BuildVerifyResult(info, issues);
            }

            var indexer = UMAAssetIndexer.Instance;

            // Parse slotsV3 to extract slot and overlay names
            // Simple JSON parsing — find slotsV3 array and extract id fields
            var slotNames = new List<string>();
            var overlayNames = new List<string>();
            int maxColorIdx = -1;

            try
            {
                // Extract slot IDs from slotsV3
                int slotsStart = recipeJson.IndexOf("\"slotsV3\":[");
                if (slotsStart < 0)
                {
                    issues.Add(MakeIssue("critical", "slotsV3", "No slotsV3 array found in recipeString."));
                    return BuildVerifyResult(info, issues);
                }

                // Parse slot IDs — find all "id":"xxx" within slotsV3
                int searchPos = slotsStart;
                int slotsEnd = FindMatchingBracket(recipeJson, recipeJson.IndexOf('[', slotsStart));

                // Find slot-level IDs (direct children of slotsV3 array items)
                // and overlay IDs (inside overlays arrays within each slot)
                bool inOverlays = false;
                int pos = slotsStart;
                while (pos < slotsEnd && pos >= 0)
                {
                    int overlaysIdx = recipeJson.IndexOf("\"overlays\":[", pos);
                    int idIdx = recipeJson.IndexOf("\"id\":\"", pos);

                    if (idIdx < 0 || idIdx >= slotsEnd) break;

                    // Check if we hit an overlays array before the next id
                    if (overlaysIdx >= 0 && overlaysIdx < idIdx)
                    {
                        inOverlays = true;
                    }

                    // Extract the id value
                    int valueStart = idIdx + 6; // after "id":"
                    int valueEnd = recipeJson.IndexOf('"', valueStart);
                    if (valueEnd < 0) break;
                    string idValue = recipeJson.Substring(valueStart, valueEnd - valueStart);

                    if (inOverlays)
                        overlayNames.Add(idValue);
                    else
                        slotNames.Add(idValue);

                    pos = valueEnd + 1;

                    // Reset overlay flag at closing bracket
                    if (inOverlays)
                    {
                        int closeBracket = recipeJson.IndexOf(']', pos);
                        if (closeBracket >= 0 && closeBracket < recipeJson.IndexOf("\"id\":\"", pos))
                            inOverlays = false;
                    }
                }

                // Extract colorIdx values
                int colorIdxPos = 0;
                while (true)
                {
                    int ci = recipeJson.IndexOf("\"colorIdx\":", colorIdxPos);
                    if (ci < 0) break;
                    int numStart = ci + 11;
                    int numEnd = numStart;
                    while (numEnd < recipeJson.Length && (char.IsDigit(recipeJson[numEnd]) || recipeJson[numEnd] == '-'))
                        numEnd++;
                    if (int.TryParse(recipeJson.Substring(numStart, numEnd - numStart), out int idx))
                    {
                        if (idx > maxColorIdx) maxColorIdx = idx;
                    }
                    colorIdxPos = numEnd;
                }
            }
            catch (Exception ex)
            {
                issues.Add(MakeIssue("warning", "parsing", "Failed to fully parse recipeString: " + ex.Message));
            }

            info["slotCount"] = slotNames.Count;
            info["overlayCount"] = overlayNames.Count;
            info["slots"] = slotNames;
            info["overlays"] = overlayNames;

            // Verify slots exist in Global Library and have materialName
            foreach (string sName in slotNames)
            {
                if (!indexer.HasAsset<SlotDataAsset>(sName))
                {
                    issues.Add(MakeIssue("critical", "slot_missing",
                        "Slot '" + sName + "' not found in Global Library. The recipe will fail at runtime."));
                    continue;
                }

                // Check materialName on the actual SlotDataAsset
                var allSlots = indexer.GetAllAssets<SlotDataAsset>();
                foreach (var slot in allSlots)
                {
                    if (slot != null && slot.slotName == sName)
                    {
                        if (string.IsNullOrEmpty(slot.materialName))
                            issues.Add(MakeIssue("critical", "slot_no_material",
                                "Slot '" + sName + "' has empty materialName — it will render invisibly."));
                        break;
                    }
                }
            }

            // Verify overlays exist in Global Library
            foreach (string oName in overlayNames)
            {
                if (!indexer.HasAsset<OverlayDataAsset>(oName))
                {
                    issues.Add(MakeIssue("critical", "overlay_missing",
                        "Overlay '" + oName + "' not found in Global Library. The recipe will crash at runtime."));
                }
            }

            // Verify fColors
            int fColorCount = 0;
            int fColorsStart = recipeJson.IndexOf("\"fColors\":[");
            if (fColorsStart >= 0)
            {
                // Count fColor entries (count '{' at depth 1 inside the array)
                int arrStart = recipeJson.IndexOf('[', fColorsStart);
                int depth = 0;
                for (int i = arrStart + 1; i < recipeJson.Length; i++)
                {
                    if (recipeJson[i] == '{') { depth++; if (depth == 1) fColorCount++; }
                    if (recipeJson[i] == '}') depth--;
                    if (recipeJson[i] == ']' && depth == 0) break;
                }
            }

            info["fColorCount"] = fColorCount;

            if (maxColorIdx >= 0 && fColorCount == 0)
            {
                issues.Add(MakeIssue("critical", "fColors_empty",
                    "Overlays reference colorIdx " + maxColorIdx + " but fColors is empty. " +
                    "This will cause IndexOutOfRangeException at runtime."));
            }
            else if (maxColorIdx >= fColorCount)
            {
                issues.Add(MakeIssue("critical", "fColors_insufficient",
                    "Overlays reference colorIdx " + maxColorIdx + " but only " + fColorCount +
                    " fColor entries exist. Index out of bounds at runtime."));
            }

            // Check compatibleRaces
            if (recipe.compatibleRaces == null || recipe.compatibleRaces.Count == 0)
            {
                issues.Add(MakeIssue("warning", "no_races",
                    "No compatibleRaces set. Recipe may not appear in character creator."));
            }

            // Check wardrobeSlot
            if (string.IsNullOrEmpty(recipe.wardrobeSlot))
            {
                issues.Add(MakeIssue("warning", "no_wardrobe_slot",
                    "wardrobeSlot is empty. Recipe cannot be equipped via wardrobe system."));
            }

            return BuildVerifyResult(info, issues);
        }

        private static Dictionary<string, object> MakeIssue(string severity, string code, string message)
        {
            return new Dictionary<string, object>
            {
                { "severity", severity },
                { "code", code },
                { "message", message }
            };
        }

        private static Dictionary<string, object> BuildVerifyResult(
            Dictionary<string, object> info, List<Dictionary<string, object>> issues)
        {
            int criticalCount = issues.Count(i => i["severity"]?.ToString() == "critical");
            int warningCount = issues.Count(i => i["severity"]?.ToString() == "warning");

            return new Dictionary<string, object>
            {
                { "valid", criticalCount == 0 },
                { "issueCount", issues.Count },
                { "criticalCount", criticalCount },
                { "warningCount", warningCount },
                { "issues", issues },
                { "recipeInfo", info }
            };
        }

        private static int FindMatchingBracket(string json, int openPos)
        {
            if (openPos < 0 || openPos >= json.Length) return json.Length;
            char open = json[openPos];
            char close = open == '[' ? ']' : '}';
            int depth = 0;
            for (int i = openPos; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                if (json[i] == close) { depth--; if (depth == 0) return i; }
            }
            return json.Length;
        }


        // ───────────────────── uma/create-wardrobe-from-fbx ─────────────────────

        /// <summary>
        /// Unified tool: takes an FBX and produces all UMA assets for a functional wardrobe item.
        /// Phases: Inspect FBX → Create Slots → Create Overlays → Assemble Recipes → Register → Verify.
        /// </summary>
        public static object CreateWardrobeFromFbx(Dictionary<string, object> args)
        {
            // ── Parse parameters ──
            string fbxPath = GetRequiredString(args, "fbxPath");
            string outputFolder = GetRequiredString(args, "outputFolder");
            string wardrobeSlot = GetRequiredString(args, "wardrobeSlot");
            string umaMaterialPath = GetOptionalString(args, "umaMaterialPath", null)
                ?? GetOptionalString(args, "umaMaterial", null);
            if (string.IsNullOrEmpty(umaMaterialPath))
                return Error("Missing required parameter: umaMaterialPath (or umaMaterial). Pass the full asset path like 'Assets/UMA/.../MyMaterial.asset' or just the material name.");

            // Validate umaMaterialPath: if agent passed a name instead of a path, try to resolve it
            if (!umaMaterialPath.Contains("/") && !umaMaterialPath.EndsWith(".asset"))
            {
                var matGuids = AssetDatabase.FindAssets("t:UMA.Core.UMAMaterial " + umaMaterialPath);
                if (matGuids.Length == 1)
                {
                    umaMaterialPath = AssetDatabase.GUIDToAssetPath(matGuids[0]);
                    Debug.LogWarning("[MCP-UMA] umaMaterialPath was a name, auto-resolved to: " + umaMaterialPath);
                }
                else if (matGuids.Length > 1)
                    return Error("umaMaterialPath '" + umaMaterialPath + "' looks like a name (not a path) and matches multiple UMAMaterials. Pass the full asset path starting with 'Assets/'.");
                else
                    return Error("umaMaterialPath '" + umaMaterialPath + "' is not a valid asset path and no UMAMaterial with that name was found. Expected format: 'Assets/UMA/.../MyMaterial.asset'");
            }
            var variantsList = GetRequiredList(args, "variants");

            string recipeNamePattern = GetOptionalString(args, "recipeName", "{pieceName}_{variantSuffix}_Recipe");
            string race = GetOptionalString(args, "race", "HumanRace");
            bool registerInGlobalLibrary = GetOptionalBool(args, "registerInGlobalLibrary", true);
            bool verifyAfterCreation = GetOptionalBool(args, "verifyAfterCreation", true);
            string rootBone = GetOptionalString(args, "rootBone", "Root");

            // ── Phase 1: Inspect FBX ──
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
                return Error("FBX not found at: " + fbxPath);

            var smr = fbx.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null)
                return Error("No SkinnedMeshRenderer found in " + fbxPath);

            int subMeshCount = smr.sharedMesh.subMeshCount;            if (subMeshCount == 0)
                return Error("FBX mesh has 0 subMeshes.");

            var sharedMats = smr.sharedMaterials;

            // Extract pieceName: SK_Hu_M_{pieceName}.fbx
            string fbxFileName = System.IO.Path.GetFileNameWithoutExtension(fbxPath);
            string pieceName = fbxFileName;
            if (pieceName.StartsWith("SK_Hu_M_", StringComparison.OrdinalIgnoreCase))
                pieceName = pieceName.Substring(8);
            else if (pieceName.StartsWith("SK_Hu_F_", StringComparison.OrdinalIgnoreCase))
                pieceName = pieceName.Substring(8);

            // Load UMA Material
            var umaMaterial = AssetDatabase.LoadAssetAtPath<UMAMaterial>(umaMaterialPath);
            if (umaMaterial == null)
                return Error("UMA Material not found at: " + umaMaterialPath);

            int channelCount = umaMaterial.channels.Length;

            // Parse variants - each variant only needs a suffix.
            // Textures are always read directly from the FBX Unity materials (sharedMaterials).
            var variants = new List<VariantDef>();
            foreach (var vObj in variantsList)
            {
                var vDict = vObj as Dictionary<string, object>;
                if (vDict == null) continue;
                var vd = new VariantDef();
                // Accept "suffix", "name", or "variantName" - agents frequently use the wrong key
                vd.suffix = GetOptionalString(vDict, "suffix", null)
                    ?? GetOptionalString(vDict, "name", null)
                    ?? GetOptionalString(vDict, "variantName", null);
                if (string.IsNullOrEmpty(vd.suffix))
                    return Error("Each variant must have a 'suffix' field. Example: { \"suffix\": \"Iron\" }");

                // Read textures directly from FBX sharedMaterials for each sub-mesh
                vd.textures = new List<VariantTexture>();
                for (int si = 0; si < subMeshCount; si++)
                {
                    var vt = new VariantTexture();
                    vt.subMeshIndex = si;
                    if (si < sharedMats.Length && sharedMats[si] != null)
                    {
                        var (diffuse, normal, metallicR) = ExtractTexturesFromMaterial(sharedMats[si]);
                        vt.diffuse = diffuse;
                        vt.normal = normal;
                        vt.metallicRoughness = metallicR;
                    }
                    vd.textures.Add(vt);
                }
                Debug.Log("[MCP-UMA] Variant " + vd.suffix + ": textures extracted from " + subMeshCount + " FBX materials.");
                variants.Add(vd);
            }

            EnsureFolderExists(outputFolder);

            // Tracking lists
            var createdSlotPaths = new List<string>();
            var createdOverlayPaths = new List<string>();
            var createdRecipePaths = new List<string>();
            var slotNames = new List<string>();            var materialInfos = new List<Dictionary<string, object>>();

            // ── Phase 2: Create Slots ──

            string slotBaseName = pieceName;

            var sbp = new SlotBuilderParameters();
            sbp.slotMesh = smr;
            sbp.seamsMesh = null;
            sbp.material = umaMaterial;
            sbp.rootBone = rootBone;
            sbp.assetName = slotBaseName;
            sbp.slotName = slotBaseName;
            sbp.assetFolder = slotBaseName;
            sbp.slotFolder = outputFolder;
            sbp.binarySerialization = false;
            sbp.calculateNormals = false;            sbp.calculateTangents = true; // matches Slot Builder default
            sbp.udimAdjustment = true; // matches Slot Builder default
            sbp.useRootFolder = false; // matches Slot Builder default (unchecked)
            sbp.nameByMaterial = false;
            sbp.keepAllBones = false; // matches Slot Builder default
            sbp.stripBones = "";
            sbp.keepList = new List<string>(); // empty but non-null (Pitfall #2) - let UMA handle bone filtering

            SlotDataAsset primarySlot;
            try
            {
                primarySlot = UMASlotProcessingUtil.CreateSlotData(sbp);
            }
            catch (Exception ex)
            {
                return Error("CreateSlotData failed: " + ex.Message);
            }
            if (primarySlot == null)
                return Error("CreateSlotData returned null — check Unity Console.");

            // Post-process primary slot
            primarySlot.meshData.SlotName = slotBaseName;
            primarySlot.material = umaMaterial;
            primarySlot.materialName = umaMaterial.name;
            primarySlot.tags = new string[0];
            primarySlot.Races = new string[0];
            EditorUtility.SetDirty(primarySlot);
            AssetDatabase.SaveAssets();
            // Move files from subfolder
            string subfolder = outputFolder + "/" + slotBaseName;
            if (AssetDatabase.IsValidFolder(subfolder))
            {
                var guids = AssetDatabase.FindAssets("", new[] { subfolder });
                foreach (var guid in guids)
                {
                    string src = AssetDatabase.GUIDToAssetPath(guid);
                    string fileName = System.IO.Path.GetFileName(src);
                    string dest = outputFolder + "/" + fileName;
                    AssetDatabase.MoveAsset(src, dest);
                }
                AssetDatabase.DeleteAsset(subfolder);
            }

            // Clean parasite folders
            if (AssetDatabase.IsValidFolder("Assets/Assets"))
                AssetDatabase.DeleteAsset("Assets/Assets");
            if (AssetDatabase.IsValidFolder(outputFolder + "/Assets"))
                AssetDatabase.DeleteAsset(outputFolder + "/Assets");

            AssetDatabase.Refresh();

            // Post-process ALL SlotDataAssets (multi-subMesh fix)
            var allSlotGuids = AssetDatabase.FindAssets("t:SlotDataAsset " + slotBaseName, new[] { outputFolder });
            foreach (var guid in allSlotGuids)
            {
                string slotPath = AssetDatabase.GUIDToAssetPath(guid);
                var sda = AssetDatabase.LoadAssetAtPath<SlotDataAsset>(slotPath);                if (sda == null) continue;

                sda.material = umaMaterial;
                if (string.IsNullOrEmpty(sda.materialName))
                    sda.materialName = umaMaterial.name;
                if (sda.meshData != null && string.IsNullOrEmpty(sda.meshData.SlotName))
                    sda.meshData.SlotName = sda.slotName;
                if (sda.tags == null) sda.tags = new string[0];
                if (sda.Races == null) sda.Races = new string[0];
                EditorUtility.SetDirty(sda);

                slotNames.Add(sda.slotName);
                createdSlotPaths.Add(slotPath);
            }
            AssetDatabase.SaveAssets();

            slotNames.Sort();
            createdSlotPaths.Sort();

            for (int i = 0; i < sharedMats.Length && i < subMeshCount; i++)
            {
                materialInfos.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "name", sharedMats[i] != null ? sharedMats[i].name : "null" }
                });
            }

            Debug.Log("[MCP-UMA] Phase 2 done: " + slotNames.Count + " slots created for " + pieceName);
            // ── Phase 3: Create Overlays ──
            // Key: texture fingerprint → overlay asset path (for deduplication)
            var overlayCache = new Dictionary<string, string>();
            // Map: (variantSuffix, subMeshIndex) → overlayName
            var variantOverlayMap = new Dictionary<string, Dictionary<int, string>>();

            foreach (var v in variants)
            {
                variantOverlayMap[v.suffix] = new Dictionary<int, string>();

                foreach (var t in v.textures)
                {
                    // Build a fingerprint for dedup: subMeshIndex|diffuse|normal|mr
                    // subMeshIndex is included so that two sub-meshes with identical textures
                    // still get separate overlays (they are distinct geometry with different UVs).
                    string fingerprint = t.subMeshIndex + "|" + (t.diffuse ?? "") + "|" + (t.normal ?? "") + "|" + (t.metallicRoughness ?? "");

                    string overlayName;
                    if (overlayCache.ContainsKey(fingerprint))
                    {
                        // Reuse existing overlay
                        string existingPath = overlayCache[fingerprint];
                        var existingOverlay = AssetDatabase.LoadAssetAtPath<OverlayDataAsset>(existingPath);
                        overlayName = existingOverlay != null ? existingOverlay.overlayName : pieceName + "_" + v.suffix + "_sub" + t.subMeshIndex + "_Overlay";
                        variantOverlayMap[v.suffix][t.subMeshIndex] = overlayName;
                        Debug.Log("[MCP-UMA] Reusing overlay " + overlayName + " for variant " + v.suffix + " sub" + t.subMeshIndex);
                        continue;
                    }

                    // Create new overlay
                    overlayName = pieceName + "_" + v.suffix + "_sub" + t.subMeshIndex + "_Overlay";
                    var overlay = ScriptableObject.CreateInstance<OverlayDataAsset>();
                    overlay.overlayName = overlayName;
                    overlay.material = umaMaterial;
                    overlay.textureList = new Texture2D[channelCount];

                    if (!string.IsNullOrEmpty(t.diffuse))
                    {
                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(t.diffuse);
                        if (tex != null) overlay.textureList[0] = tex;
                        else Debug.LogWarning("[MCP-UMA] Diffuse texture not found: " + t.diffuse);
                    }
                    if (!string.IsNullOrEmpty(t.normal) && channelCount > 1)
                    {
                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(t.normal);
                        if (tex != null) overlay.textureList[1] = tex;
                        else Debug.LogWarning("[MCP-UMA] Normal texture not found: " + t.normal);
                    }
                    if (!string.IsNullOrEmpty(t.metallicRoughness) && channelCount > 2)
                    {
                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(t.metallicRoughness);
                        if (tex != null) overlay.textureList[2] = tex;
                        else Debug.LogWarning("[MCP-UMA] MetallicRoughness texture not found: " + t.metallicRoughness);
                    }

                    string overlayPath = outputFolder + "/" + overlayName + ".asset";
                    AssetDatabase.CreateAsset(overlay, overlayPath);
                    createdOverlayPaths.Add(overlayPath);
                    overlayCache[fingerprint] = overlayPath;                    variantOverlayMap[v.suffix][t.subMeshIndex] = overlayName;
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[MCP-UMA] Phase 3 done: " + createdOverlayPaths.Count + " overlays created.");

            // ── Phase 4: Assemble Wardrobe Recipes ──
            foreach (var v in variants)
            {
                string variantSuffix = v.suffix;
                string recipeName = recipeNamePattern
                    .Replace("{pieceName}", pieceName)
                    .Replace("{variantSuffix}", variantSuffix);

                var recipe = ScriptableObject.CreateInstance<UMAWardrobeRecipe>();
                recipe.name = recipeName;

                var sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"version\":3,");
                sb.Append("\"packedSlotDataList\":[],");
                sb.Append("\"slotsV2\":[],");
                sb.Append("\"slotsV3\":[");

                for (int si = 0; si < slotNames.Count; si++)
                {
                    if (si > 0) sb.Append(",");
                    string slotId = slotNames[si];

                    int subIdx = si;                    string overlayId = "";
                    if (variantOverlayMap[variantSuffix].ContainsKey(subIdx))
                        overlayId = variantOverlayMap[variantSuffix][subIdx];

                    sb.Append("{");
                    sb.Append("\"id\":\"" + EscapeJson(slotId) + "\",");
                    sb.Append("\"scale\":100,");
                    sb.Append("\"copyIdx\":-1,");
                    sb.Append("\"overlays\":[{");
                    sb.Append("\"id\":\"" + EscapeJson(overlayId) + "\",");
                    sb.Append("\"colorIdx\":0,");
                    sb.Append("\"rect\":[0.0,0.0,0.0,0.0],");
                    sb.Append("\"isTransformed\":false,");
                    sb.Append("\"scale\":{\"x\":1.0,\"y\":1.0,\"z\":1.0},");
                    sb.Append("\"rotation\":0.0,");
                    sb.Append("\"blendModes\":[" + string.Join(",", Enumerable.Repeat("0", channelCount)) + "],");
                    sb.Append("\"Tags\":[],");
                    sb.Append("\"tiling\":[" + string.Join(",", Enumerable.Repeat("false", channelCount)) + "],");
                    sb.Append("\"uvOverride\":0");
                    sb.Append("}],");

                    sb.Append("\"Tags\":[],\"Races\":[],");
                    sb.Append("\"blendShapeTarget\":\"\",");
                    sb.Append("\"overSmoosh\":0.009999999776482582,");
                    sb.Append("\"smooshDistance\":0.0010000000474974514,");
                    sb.Append("\"smooshInvertX\":false,");
                    sb.Append("\"smooshInvertY\":true,");
                    sb.Append("\"smooshInvertZ\":false,");
                    sb.Append("\"smooshInvertDist\":true,");
                    sb.Append("\"smooshTargetTag\":\"\",");                    sb.Append("\"smooshableTag\":\"\",");
                    sb.Append("\"isSwapSlot\":false,");
                    sb.Append("\"swapTag\":\"LongHair\",");
                    sb.Append("\"uvOverride\":0,");
                    sb.Append("\"isDisabled\":false,");
                    sb.Append("\"expandAlongNormal\":0");
                    sb.Append("}");
                }

                sb.Append("],");
                sb.Append("\"colors\":[],");

                // fColors: single neutral entry
                sb.Append("\"fColors\":[{");
                sb.Append("\"name\":\"-\",");
                sb.Append("\"colors\":[");
                var channelColors = new List<string>();
                for (int c = 0; c < channelCount; c++)
                    channelColors.Add("255,255,255,255,0,0,0,0");
                sb.Append(string.Join(",", channelColors));
                sb.Append("],");
                sb.Append("\"ShaderParms\":[],");
                sb.Append("\"alwaysUpdate\":false,");
                sb.Append("\"alwaysUpdateParms\":false,");
                sb.Append("\"isBaseColor\":false,");
                sb.Append("\"displayColor\":-1");
                sb.Append("}],");

                sb.Append("\"sharedColorCount\":0,");
                sb.Append("\"race\":\"" + EscapeJson(race) + "\",");                sb.Append("\"packedDna\":[],");
                sb.Append("\"uvOverride\":0");
                sb.Append("}");

                string recipeJson = sb.ToString();

                string recipePath = outputFolder + "/" + recipeName + ".asset";
                AssetDatabase.CreateAsset(recipe, recipePath);

                var so = new SerializedObject(recipe);
                so.FindProperty("recipeString").stringValue = recipeJson;
                so.FindProperty("recipeType").stringValue = "Wardrobe";
                so.FindProperty("DisplayValue").stringValue = recipeName;
                so.FindProperty("wardrobeSlot").stringValue = wardrobeSlot;

                var racesProp = so.FindProperty("compatibleRaces");
                racesProp.arraySize = 1;
                racesProp.GetArrayElementAtIndex(0).stringValue = race;

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(recipePath, ImportAssetOptions.ForceUpdate);
                createdRecipePaths.Add(recipePath);
            }
            Debug.Log("[MCP-UMA] Phase 4 done: " + createdRecipePaths.Count + " recipes created.");

            // ── Phase 5: Register in Global Library ──
            bool registered = false;
            if (registerInGlobalLibrary)
            {
                try                {
                    var indexer = UMAAssetIndexer.Instance;
                    foreach (string path in createdSlotPaths)
                    {
                        var slot = AssetDatabase.LoadAssetAtPath<SlotDataAsset>(path);
                        if (slot != null && !indexer.HasAsset<SlotDataAsset>(slot.slotName))
                            indexer.EvilAddAsset(typeof(SlotDataAsset), slot);
                    }
                    foreach (string path in createdOverlayPaths)
                    {
                        var overlay = AssetDatabase.LoadAssetAtPath<OverlayDataAsset>(path);
                        if (overlay != null && !indexer.HasAsset<OverlayDataAsset>(overlay.overlayName))
                            indexer.EvilAddAsset(typeof(OverlayDataAsset), overlay);
                    }
                    foreach (string path in createdRecipePaths)
                    {
                        var rec = AssetDatabase.LoadAssetAtPath<UMAWardrobeRecipe>(path);
                        if (rec != null && !indexer.HasAsset<UMAWardrobeRecipe>(rec.name))
                            indexer.EvilAddAsset(typeof(UMAWardrobeRecipe), rec);
                    }
                    indexer.ForceSave();
                    AssetDatabase.SaveAssets();
                    registered = true;
                    Debug.Log("[MCP-UMA] Phase 5 done: all assets registered in Global Library.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MCP-UMA] Phase 5 warning: registration failed: " + ex.Message);
                }
            }
            // ── Phase 6: Verification ──
            Dictionary<string, object> verification = null;
            if (verifyAfterCreation)
            {
                var verifyErrors = new List<string>();
                bool allSlotsFound = true;
                bool allOverlaysFound = true;
                bool allMaterialNamesSet = true;
                bool fColorsValid = true;
                bool allTexturesExist = true;

                var indexer = UMAAssetIndexer.Instance;

                foreach (string sName in slotNames)
                {
                    if (!indexer.HasAsset<SlotDataAsset>(sName))
                    {
                        allSlotsFound = false;
                        verifyErrors.Add("Slot '" + sName + "' not found in Global Library.");
                    }
                    else
                    {
                        var allSlots = indexer.GetAllAssets<SlotDataAsset>();
                        foreach (var s in allSlots)
                        {
                            if (s != null && s.slotName == sName && string.IsNullOrEmpty(s.materialName))
                            {
                                allMaterialNamesSet = false;
                                verifyErrors.Add("Slot '" + sName + "' has empty materialName.");                            }
                        }
                    }
                }

                foreach (string oPath in createdOverlayPaths)
                {
                    var ov = AssetDatabase.LoadAssetAtPath<OverlayDataAsset>(oPath);
                    if (ov != null && !indexer.HasAsset<OverlayDataAsset>(ov.overlayName))
                    {
                        allOverlaysFound = false;
                        verifyErrors.Add("Overlay '" + ov.overlayName + "' not found in Global Library.");
                    }
                }

                // Verify recipes: recipeString must be non-empty and wardrobeSlot must match
                bool allRecipesValid = true;
                foreach (string rPath in createdRecipePaths)
                {
                    var rec = AssetDatabase.LoadAssetAtPath<UMAWardrobeRecipe>(rPath);
                    if (rec == null)
                    {
                        allRecipesValid = false;
                        verifyErrors.Add("Recipe asset not found at: " + rPath);
                        continue;
                    }
                    var rso = new SerializedObject(rec);
                    string rs = rso.FindProperty("recipeString")?.stringValue ?? "";
                    string ws = rso.FindProperty("wardrobeSlot")?.stringValue ?? "";
                    if (string.IsNullOrEmpty(rs))
                    {
                        allRecipesValid = false;
                        verifyErrors.Add("Recipe '" + rec.name + "' has EMPTY recipeString — it will not work at runtime.");
                    }
                    if (ws == "None" || string.IsNullOrEmpty(ws))
                    {
                        allRecipesValid = false;
                        verifyErrors.Add("Recipe '" + rec.name + "' has wardrobeSlot='" + ws + "' instead of '" + wardrobeSlot + "'.");
                    }
                }

                foreach (var v in variants)
                {
                    foreach (var t in v.textures)
                    {
                        if (!string.IsNullOrEmpty(t.diffuse) && AssetDatabase.LoadAssetAtPath<Texture2D>(t.diffuse) == null)
                        {
                            allTexturesExist = false;
                            verifyErrors.Add("Texture not found: " + t.diffuse);
                        }
                        if (!string.IsNullOrEmpty(t.normal) && AssetDatabase.LoadAssetAtPath<Texture2D>(t.normal) == null)
                        {
                            allTexturesExist = false;
                            verifyErrors.Add("Texture not found: " + t.normal);
                        }                        if (!string.IsNullOrEmpty(t.metallicRoughness) && AssetDatabase.LoadAssetAtPath<Texture2D>(t.metallicRoughness) == null)
                        {
                            allTexturesExist = false;
                            verifyErrors.Add("Texture not found: " + t.metallicRoughness);
                        }
                    }
                }

                verification = new Dictionary<string, object>
                {
                    { "allSlotsFound", allSlotsFound },
                    { "allOverlaysFound", allOverlaysFound },
                    { "allRecipesValid", allRecipesValid },
                    { "allMaterialNamesSet", allMaterialNamesSet },
                    { "fColorsValid", fColorsValid },
                    { "allTexturesExist", allTexturesExist },
                    { "errors", verifyErrors }
                };
            }

            // ── Return ──
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "pieceName", pieceName },
                { "subMeshCount", subMeshCount },
                { "materials", materialInfos },
                { "created", new Dictionary<string, object>
                    {
                        { "slots", createdSlotPaths },
                        { "overlays", createdOverlayPaths },                        { "recipes", createdRecipePaths }
                    }
                },
                { "registeredInGlobalLibrary", registered }
            };
            if (verification != null)
                result["verification"] = verification;

            return result;
        }

        // Internal data classes for CreateWardrobeFromFbx
        private class VariantDef
        {
            public string suffix;
                public List<VariantTexture> textures;
        }

        private class VariantTexture
        {
            public int subMeshIndex;
            public string diffuse;
            public string normal;
            public string metallicRoughness;
        }

        /// <summary>
        /// Extract texture asset paths from a Unity Material.
        /// Checks URP properties first (_BaseMap, _BumpMap, _MetallicGlossMap),
        /// then falls back to Standard shader properties (_MainTex).
        /// Returns (diffuse, normal, metallicRoughness) asset paths (null if not set).
        /// </summary>
        private static (string diffuse, string normal, string metallicRoughness) ExtractTexturesFromMaterial(Material mat)
        {
            string diffuse = null;
            string normal = null;
            string mr = null;

            if (mat == null) return (diffuse, normal, mr);

            // Diffuse: try _BaseMap (URP/HDRP) then _MainTex (Standard)
            Texture tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
            if (tex == null && mat.HasProperty("_MainTex"))
                tex = mat.GetTexture("_MainTex");
            if (tex != null)
                diffuse = AssetDatabase.GetAssetPath(tex);

            // Normal: _BumpMap
            tex = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            if (tex != null)
                normal = AssetDatabase.GetAssetPath(tex);

            // Metallic/Roughness: _MetallicGlossMap
            tex = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
            if (tex != null)
                mr = AssetDatabase.GetAssetPath(tex);

            return (diffuse, normal, mr);
        }


                        // ───────────────────── Helpers ─────────────────────

        // ───────────────────── uma/rebuild-global-library ─────────────────────

        /// <summary>
        /// Rebuild the UMA Global Library by clearing and re-scanning the entire project.
        /// Reproduces exactly what the "Rebuild From Project" button does in the
        /// UMA Global Library editor window (AssetIndexerWindow.cs).
        ///
        /// Modes:
        ///   "rebuild"           — Standard full rebuild (default). Clears index, rescans all
        ///                         UMA assets in the project, restores "always loaded" flags.
        ///   "rebuild_with_text" — Same as rebuild but also includes UMATextRecipe assets
        ///                         (not just wardrobe recipes).
        ///   "repair"            — Light repair: rebuilds type tables and removes invalid/broken
        ///                         entries without doing a full rescan.
        /// </summary>
        public static object RebuildGlobalLibrary(Dictionary<string, object> args)
        {
            try
            {
                string mode = GetOptionalString(args, "mode", "rebuild");
                var indexer = UMAAssetIndexer.Instance;
                if (indexer == null)
                    return Error("UMA Global Library (UMAAssetIndexer) not found. Is UMA_GLIB in the scene?");

                int beforeCount = 0;
                try { beforeCount = indexer.GetAllAssets<SlotDataAsset>().Count
                                  + indexer.GetAllAssets<OverlayDataAsset>().Count
                                  + indexer.GetAllAssets<UMAWardrobeRecipe>().Count
                                  + indexer.GetAllAssets<RaceData>().Count; }
                catch { /* ignore count errors */ }

                if (mode == "repair")
                {
                    // Light repair — rebuild type tables and remove broken entries
                    indexer.BuildStringTypes();
                    indexer.RepairAndCleanup();
                    Resources.UnloadUnusedAssets();
                }
                else
                {
                    // Full rebuild — mirrors AssetIndexerWindow "Rebuild From Project"
                    bool includeText = (mode == "rebuild_with_text");
                    indexer.SaveKeeps();
                    indexer.Clear();
                    indexer.BuildStringTypes();
                    indexer.AddEverything(includeText);
                    indexer.RestoreKeeps();
                    indexer.ForceSave();
                    Resources.UnloadUnusedAssets();
                }

                // Count assets after rebuild
                int slotCount = indexer.GetAllAssets<SlotDataAsset>().Count;
                int overlayCount = indexer.GetAllAssets<OverlayDataAsset>().Count;
                int recipeCount = indexer.GetAllAssets<UMAWardrobeRecipe>().Count;
                int raceCount = indexer.GetAllAssets<RaceData>().Count;
                int afterCount = slotCount + overlayCount + recipeCount + raceCount;

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "mode", mode },
                    { "assetCountBefore", beforeCount },
                    { "assetCountAfter", afterCount },
                    { "breakdown", new Dictionary<string, object>
                        {
                            { "slots", slotCount },
                            { "overlays", overlayCount },
                            { "wardrobeRecipes", recipeCount },
                            { "races", raceCount }
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return Error("RebuildGlobalLibrary failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Get all recipe assets from the Global Library, combining both UMATextRecipe
        /// and UMAWardrobeRecipe queries. GetAllAssets&lt;T&gt; matches the exact registered
        /// type, not subclasses — wardrobe recipes registered via EvilAddAsset(typeof(UMAWardrobeRecipe))
        /// are NOT returned by GetAllAssets&lt;UMATextRecipe&gt;(). This helper merges both,
        /// deduplicating by InstanceID.
        /// </summary>
        private static List<UMATextRecipe> GetAllRecipesFromLibrary(UMAAssetIndexer indexer)
        {
            var textRecipes = indexer.GetAllAssets<UMATextRecipe>();
            var wardrobeRecipes = indexer.GetAllAssets<UMAWardrobeRecipe>();
            var seen = new HashSet<int>();
            var combined = new List<UMATextRecipe>();
            if (textRecipes != null)
                foreach (var r in textRecipes)
                    if (r != null && seen.Add(r.GetInstanceID())) combined.Add(r);
            if (wardrobeRecipes != null)
                foreach (var r in wardrobeRecipes)
                    if (r != null && seen.Add(r.GetInstanceID())) combined.Add(r);
            return combined;
        }

        // ═══════════════════ uma/wardrobe-equip ═══════════════════

        /// <summary>
        /// Equip or unequip a UMA wardrobe recipe on a DynamicCharacterAvatar.
        /// Auto-detects Play/Edit mode and uses the correct API path.
        /// Play Mode: runtime DCA API (SetSlot/ClearSlot + BuildCharacter).
        /// Edit Mode: modifies serialized preloadWardrobeRecipes + GenerateSingleUMA rebuild.
        /// </summary>
        public static object WardrobeEquip(Dictionary<string, object> args)
        {
            try
            {
                string gameObjectPath = GetRequiredString(args, "gameObjectPath");
                string wardrobeSlot = GetRequiredString(args, "wardrobeSlot");
                string recipeName = GetOptionalString(args, "recipeName", null);

                bool isPlayMode = Application.isPlaying;
                string action = string.IsNullOrEmpty(recipeName) ? "unequip" : "equip";

                // Find the GameObject
                var go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return Error($"GameObject not found: {gameObjectPath}");

                // Find DynamicCharacterAvatar (on this GO or children)
                var dca = go.GetComponentInChildren<DynamicCharacterAvatar>();
                if (dca == null)
                    return Error($"DynamicCharacterAvatar not found on '{gameObjectPath}' or its children.");

                if (isPlayMode)
                {
                    // ——— PLAY MODE PATH ———
                    if (string.IsNullOrEmpty(recipeName))
                    {
                        dca.ClearSlot(wardrobeSlot);
                    }
                    else
                    {
                        dca.SetSlot(wardrobeSlot, recipeName);
                    }
                    dca.BuildCharacter(true);
                    dca.ForceUpdate(true, true, true);
                }
                else
                {
                    // ——— EDIT MODE PATH ———
                    var bindFlags = System.Reflection.BindingFlags.Public
                                  | System.Reflection.BindingFlags.NonPublic
                                  | System.Reflection.BindingFlags.Instance;

                    // Access preloadWardrobeRecipes
                    var preloadField = typeof(DynamicCharacterAvatar).GetField("preloadWardrobeRecipes", bindFlags);
                    if (preloadField == null)
                        return Error("Could not find field 'preloadWardrobeRecipes' on DynamicCharacterAvatar.");

                    var preload = preloadField.GetValue(dca);
                    var recipesField = preload.GetType().GetField("recipes", bindFlags);
                    if (recipesField == null)
                        return Error("Could not find field 'recipes' on WardrobeRecipeList.");

                    var list = recipesField.GetValue(preload) as System.Collections.IList;
                    if (list == null)
                        return Error("preloadWardrobeRecipes.recipes is null.");

                    // Get the nested WardrobeRecipeListItem type
                    var itemType = typeof(DynamicCharacterAvatar).GetNestedType(
                        "WardrobeRecipeListItem",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );
                    if (itemType == null)
                        return Error("Could not find nested type 'WardrobeRecipeListItem'.");

                    var recipeRef = itemType.GetField("_recipe", bindFlags);

                    // Remove existing recipe for this wardrobe slot
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var existing = recipeRef.GetValue(list[i]) as UMATextRecipe;
                        if (existing is UMAWardrobeRecipe wr && wr.wardrobeSlot == wardrobeSlot)
                            list.RemoveAt(i);
                    }

                    if (!string.IsNullOrEmpty(recipeName))
                    {
                        // Load recipe from Global Library
                        var recipe = UMAAssetIndexer.Instance.GetAsset<UMATextRecipe>(recipeName);
                        if (recipe == null)
                        {
                            // Fallback: search by asset name in project
                            string[] guids = AssetDatabase.FindAssets(recipeName + " t:UMATextRecipe");
                            if (guids.Length > 0)
                                recipe = AssetDatabase.LoadAssetAtPath<UMATextRecipe>(AssetDatabase.GUIDToAssetPath(guids[0]));
                        }
                        if (recipe == null)
                            return Error($"Recipe '{recipeName}' not found in UMA Global Library or project.");

                        // Create WardrobeRecipeListItem
                        var item = Activator.CreateInstance(itemType);
                        itemType.GetField("_recipeName", bindFlags).SetValue(item, recipe.name);
                        itemType.GetField("_recipe", bindFlags).SetValue(item, recipe);
                        var enabledField = itemType.GetField("_enabledInDefaultWardrobe", bindFlags);
                        if (enabledField != null)
                            enabledField.SetValue(item, true);

                        list.Add(item);
                    }

                    // Trigger edit-mode rebuild
                    EditorUtility.SetDirty(dca);
                    dca.LoadDefaultWardrobe();
                    dca.GenerateSingleUMA(false);
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "mode", isPlayMode ? "play" : "edit" },
                    { "action", action },
                    { "wardrobeSlot", wardrobeSlot },
                    { "recipeName", recipeName ?? "(cleared)" },
                    { "gameObject", go.name }
                };
            }
            catch (System.Exception ex)
            {
                return Error($"WardrobeEquip failed: {ex.Message}");
            }
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "error", message } };
        }

        private static string GetRequiredString(Dictionary<string, object> args, string key)
        {
            if (args.ContainsKey(key) && args[key] != null)
                return args[key].ToString();
            throw new ArgumentException("Missing required parameter: " + key);
        }

        private static string GetOptionalString(Dictionary<string, object> args, string key, string defaultValue)
        {
            if (args.ContainsKey(key) && args[key] != null)
                return args[key].ToString();
            return defaultValue;
        }

        private static int GetOptionalInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args.ContainsKey(key) && args[key] != null)
            {
                if (int.TryParse(args[key].ToString(), out int val))
                    return val;
            }
            return defaultValue;
        }


        private static bool GetOptionalBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args.ContainsKey(key) && args[key] != null)
            {
                string val = args[key].ToString().ToLowerInvariant();
                if (val == "true" || val == "1") return true;
                if (val == "false" || val == "0") return false;
            }
            return defaultValue;
        }
        private static List<object> GetRequiredList(Dictionary<string, object> args, string key)
        {
            if (args.ContainsKey(key) && args[key] is List<object> list)
                return list;
            throw new ArgumentException("Missing required list parameter: " + key);
        }

        private static List<object> GetOptionalList(Dictionary<string, object> args, string key)
        {
            if (args.ContainsKey(key) && args[key] is List<object> list)
                return list;
            return null;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Get project-specific UMA configuration by querying existing assets.
        /// Returns detected races, UMA materials, wardrobe slots, and recipe patterns.
        /// </summary>
        public static object GetProjectConfig(Dictionary<string, object> args)
        {
            try
            {
                var indexer = UMAAssetIndexer.Instance;
                if (indexer == null)
                    return Error("UMA Global Library not found. Is UMA_GLIB in the scene?");

                // 1. Detect races from existing wardrobe recipes
                var detectedRaces = AutoDetectRaces();
                if (detectedRaces.Count == 0)
                    detectedRaces.Add("HumanRace"); // safe default

                // 2. Gather UMA Materials
                var umaMaterials = new List<Dictionary<string, object>>();
                string[] matGuids = AssetDatabase.FindAssets("t:UMAMaterial");
                foreach (string guid in matGuids)
                {
                    string matPath = AssetDatabase.GUIDToAssetPath(guid);
                    var mat = AssetDatabase.LoadAssetAtPath<UMA.UMAMaterial>(matPath);
                    if (mat == null) continue;
                    umaMaterials.Add(new Dictionary<string, object>
                    {
                        { "name", mat.name },
                        { "path", matPath },
                        { "channelCount", mat.channels != null ? mat.channels.Length : 0 }
                    });
                }

                // 3. Gather wardrobe slots from the most common race
                var wardrobeSlots = new List<string>();
                if (detectedRaces.Count > 0)
                {
                    var allRaces = indexer.GetAllAssets<RaceData>();
                    foreach (var raceData in allRaces)
                    {
                        if (raceData != null && raceData.raceName == detectedRaces[0])
                        {
                            if (raceData.wardrobeSlots != null)
                            {
                                foreach (var ws in raceData.wardrobeSlots)
                                    wardrobeSlots.Add(ws);
                            }
                            break;
                        }
                    }
                }

                // 4. Analyze existing recipe patterns (fColor count, naming)
                var allRecipes = GetAllRecipesFromLibrary(indexer);
                int singleFColorCount = 0;
                int multiFColorCount = 0;
                var slotNames = new HashSet<string>();

                foreach (var recipe in allRecipes)
                {
                    if (recipe == null) continue;
                    if (!string.IsNullOrEmpty(recipe.wardrobeSlot))
                        slotNames.Add(recipe.wardrobeSlot);

                    // Parse recipeString to check fColor pattern
                    if (!string.IsNullOrEmpty(recipe.recipeString))
                    {
                        try
                        {
                            int fColorArrayStart = recipe.recipeString.IndexOf("\"fColors\":[");
                            if (fColorArrayStart >= 0)
                            {
                                // Count '{' after fColors to determine number of fColor entries
                                int braceCount = 0;
                                int entryCount = 0;
                                bool inArray = false;
                                for (int ci = fColorArrayStart + 10; ci < recipe.recipeString.Length; ci++)
                                {
                                    char c = recipe.recipeString[ci];
                                    if (c == '[') inArray = true;
                                    if (c == '{') { braceCount++; if (braceCount == 1) entryCount++; }
                                    if (c == '}') braceCount--;
                                    if (c == ']' && inArray && braceCount == 0) break;
                                }
                                if (entryCount <= 1) singleFColorCount++;
                                else multiFColorCount++;
                            }
                        }
                        catch { /* ignore parse errors */ }
                    }
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "races", detectedRaces },
                    { "umaMaterials", umaMaterials },
                    { "wardrobeSlots", wardrobeSlots },
                    { "usedWardrobeSlots", new List<string>(slotNames) },
                    { "recipePattern", new Dictionary<string, object>
                        {
                            { "compatibleRaces", detectedRaces },
                            { "singleFColorRecipes", singleFColorCount },
                            { "multiFColorRecipes", multiFColorCount },
                            { "recommendedFColorPattern", "single-neutral" }
                        }
                    },
                    { "totalRegisteredRecipes", allRecipes.Count }
                };
            }
            catch (System.Exception ex)
            {
                return Error("GetProjectConfig failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Auto-detect the compatibleRaces used by existing wardrobe recipes in the project.
        /// Queries the UMA Global Library for all registered UMATextRecipe assets
        /// and returns the most commonly used race names, excluding "Generic".
        /// </summary>
        private static List<string> AutoDetectRaces()
        {
            var result = new List<string>();
            try
            {
                var indexer = UMAAssetIndexer.Instance;
                if (indexer == null) return result;

                var allRecipes = GetAllRecipesFromLibrary(indexer);
                if (allRecipes == null || allRecipes.Count == 0) return result;

                // Count race occurrences across all wardrobe recipes
                var raceCounts = new Dictionary<string, int>();
                foreach (var recipe in allRecipes)
                {
                    if (recipe == null || recipe.compatibleRaces == null) continue;
                    foreach (string raceName in recipe.compatibleRaces)
                    {
                        if (string.IsNullOrEmpty(raceName)) continue;
                        if (raceName.Equals("Generic", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!raceCounts.ContainsKey(raceName))
                            raceCounts[raceName] = 0;
                        raceCounts[raceName]++;
                    }
                }

                // Return races sorted by usage count (most common first)
                foreach (var kvp in raceCounts.OrderByDescending(x => x.Value))
                    result.Add(kvp.Key);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[MCP UMA] AutoDetectRaces failed: " + ex.Message);
            }
            return result;
        }

                private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string[] parts = folderPath.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
