#if UMA_INSTALLED
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UMA;
using UMA.Editors;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static partial class MCPUMACommandsImplementation
    {
        private static readonly FieldInfo SlotMaterial = typeof(SlotDataAsset).GetField("material");
        private static readonly FieldInfo SlotMaterialName = typeof(SlotDataAsset).GetField("materialName");

        private static List<SlotDataAsset> CreateSlots(SlotBuilderParameters parameters)
        {
            string assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string requestedRoot = Path.GetFullPath(parameters.slotFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!requestedRoot.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Slot output folder must be inside Assets.");
            foreach (string name in new[] { parameters.assetName, parameters.assetFolder })
                if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".."
                    || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\"))
                    throw new ArgumentException("Slot and subfolder names must be simple asset names.");
            string generatedFolder = parameters.useRootFolder ? parameters.slotFolder
                : parameters.slotFolder + "/" + parameters.assetFolder;
            if (Directory.Exists(generatedFolder))
                throw new ArgumentException("Slot generation requires a new output subfolder: " + generatedFolder);
            // UMA 2 returns one slot; UMA 3 returns every generated slot in a result container.
            object result = UMASlotProcessingUtil.CreateSlotData(parameters);
            if (result == null) return new List<SlotDataAsset>();
            if (result is SlotDataAsset slot)
            {
                var generated = AssetDatabase.FindAssets("t:SlotDataAsset", new[] { generatedFolder })
                    .Select(guid => AssetDatabase.LoadAssetAtPath<SlotDataAsset>(AssetDatabase.GUIDToAssetPath(guid)))
                    .Where(item => item != null).ToList();
                generated.Remove(slot);
                generated.Insert(0, slot);
                return generated;
            }
            var slots = result.GetType().GetField("Slots")?.GetValue(result) as IEnumerable<SlotDataAsset>;
            if (slots == null) throw new NotSupportedException("Unrecognized UMA slot-builder result.");
            return slots.Where(item => item != null).ToList();
        }

        private static void FlattenGeneratedFolder(string folder, string destination)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                string source = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(source)) continue;
                string target = destination + "/" + Path.GetFileName(source);
                string error = AssetDatabase.MoveAsset(source, target);
                if (!string.IsNullOrEmpty(error))
                    throw new IOException("Generated asset remains at '" + source + "': " + error);
            }
            // A failed move or an unexpected generated asset must never be removed by folder cleanup.
            if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(path => !path.EndsWith(".meta")))
                throw new IOException("Generated folder still contains files: " + folder);
            AssetDatabase.DeleteAsset(folder);
        }

        private static string GetSlotMaterialName(SlotDataAsset slot)
            => SlotMaterialName?.GetValue(slot) as string ?? "";

        private static void SetSlotMaterial(SlotDataAsset slot, UMAMaterial material)
        {
            // UMA 3 resolves material through overlays; only older slots carry these fields.
            SlotMaterial?.SetValue(slot, material);
            SlotMaterialName?.SetValue(slot, material != null ? material.name : "");
        }

        private static bool HasMissingSlotMaterial(SlotDataAsset slot)
            => SlotMaterialName != null && string.IsNullOrEmpty(GetSlotMaterialName(slot));

        private static void SetLogicalName(UnityEngine.Object asset, string field, string value)
        {
            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(field)
                ?? serialized.FindProperty("_old" + char.ToUpperInvariant(field[0]) + field.Substring(1));
            if (property == null || property.propertyType != SerializedPropertyType.String)
                throw new NotSupportedException($"UMA {asset.GetType().Name} has no writable serialized '{field}'.");
            property.stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // Both UMA generations cache the logical identifier separately from serialized names.
            if (asset is SlotDataAsset || asset is OverlayDataAsset)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hash = asset.GetType().GetField("_nameHash", flags) ?? asset.GetType().GetField("nameHash", flags);
                if (hash != null && hash.FieldType == typeof(int)) hash.SetValue(asset, UMAUtils.StringToHash(value));
            }
        }

        private static Dictionary<string, object> ParseRecipeJson(string json)
        {
            return MiniJson.Deserialize(json) as Dictionary<string, object>
                ?? throw new FormatException("Recipe JSON must contain an object.");
        }

        private static object JsonValue(Dictionary<string, object> value, string key)
            => value.TryGetValue(key, out var found) ? found : null;

        private static List<Dictionary<string, object>> JsonObjects(object value)
        {
            if (value == null) return null;
            if (!(value is List<object> items)) throw new FormatException("Recipe field must contain an array.");
            return items.Where(item => item != null).Select(item => item as Dictionary<string, object>
                ?? throw new FormatException("Recipe array entries must contain objects.")).ToList();
        }

        private static bool RenameRecipeReferences(Dictionary<string, object> recipe, string assetType, string oldName, string newName)
        {
            bool modified = false;
            // Older recipes can retain empty arrays for newer formats; inspect each stored format.
            foreach (string format in new[] { "slotsV3", "slotsV2", "packedSlotDataList" })
            {
                var slots = JsonObjects(JsonValue(recipe, format));
                if (slots == null) continue;
                bool legacy = format == "packedSlotDataList";
                foreach (var slot in slots)
                {
                    var entries = assetType == "slot" ? new List<Dictionary<string, object>> { slot }
                        : JsonObjects(JsonValue(slot, legacy ? "OverlayDataList" : "overlays"));
                    if (entries == null) continue;
                    string key = legacy ? (assetType == "slot" ? "slotID" : "overlayID") : "id";
                    foreach (var entry in entries)
                    {
                        if (JsonValue(entry, key)?.ToString() != oldName) continue;
                        entry[key] = newName;
                        modified = true;
                    }
                }
            }
            return modified;
        }
    }
}
#endif
