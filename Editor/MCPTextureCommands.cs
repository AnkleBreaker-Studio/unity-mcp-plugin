using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for inspecting and modifying texture import settings.
    /// </summary>
    public static class MCPTextureCommands
    {
        // ─── Get Texture Info ───

        public static object GetTextureInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (texture == null)
                return new { error = $"Texture not found at '{path}'" };

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return new { error = $"No texture importer for '{path}'" };

            var result = new Dictionary<string, object>
            {
                { "path", path },
                { "name", texture.name },
                { "width", texture.width },
                { "height", texture.height },
                { "textureType", importer.textureType.ToString() },
                { "spriteMode", importer.spriteImportMode.ToString() },
                { "sRGB", importer.sRGBTexture },
                { "alphaSource", importer.alphaSource.ToString() },
                { "alphaIsTransparency", importer.alphaIsTransparency },
                { "readable", importer.isReadable },
                { "mipmapEnabled", importer.mipmapEnabled },
                { "filterMode", importer.filterMode.ToString() },
                { "wrapMode", importer.wrapMode.ToString() },
                { "anisoLevel", importer.anisoLevel },
                { "maxTextureSize", importer.maxTextureSize },
                { "textureCompression", importer.textureCompression.ToString() },
                { "compressionQuality", importer.compressionQuality },
                { "npotScale", importer.npotScale.ToString() },
            };

            if (importer.textureType == TextureImporterType.Sprite)
            {
                result["spritePixelsPerUnit"] = importer.spritePixelsPerUnit;
                result["spritePivot"] = new Dictionary<string, object>
                {
                    { "x", importer.spritePivot.x },
                    { "y", importer.spritePivot.y },
                };
            }

            if (importer.textureType == TextureImporterType.NormalMap)
            {
                result["convertToNormalmap"] = importer.convertToNormalmap;
            }

            return result;
        }

        // ─── Set Texture Import Settings ───

        public static object SetTextureImportSettings(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return new { error = $"No texture importer for '{path}'" };

            var updated = new List<string>();

            if (args.ContainsKey("textureType"))
            {
                if (Enum.TryParse<TextureImporterType>(args["textureType"].ToString(), true, out var texType))
                {
                    importer.textureType = texType;
                    updated.Add("textureType");
                }
            }

            if (args.ContainsKey("sRGB"))
            {
                importer.sRGBTexture = Convert.ToBoolean(args["sRGB"]);
                updated.Add("sRGB");
            }

            if (args.ContainsKey("readable"))
            {
                importer.isReadable = Convert.ToBoolean(args["readable"]);
                updated.Add("readable");
            }

            if (args.ContainsKey("mipmapEnabled"))
            {
                importer.mipmapEnabled = Convert.ToBoolean(args["mipmapEnabled"]);
                updated.Add("mipmapEnabled");
            }

            if (args.ContainsKey("filterMode"))
            {
                if (Enum.TryParse<FilterMode>(args["filterMode"].ToString(), true, out var fm))
                {
                    importer.filterMode = fm;
                    updated.Add("filterMode");
                }
            }

            if (args.ContainsKey("wrapMode"))
            {
                if (Enum.TryParse<TextureWrapMode>(args["wrapMode"].ToString(), true, out var wm))
                {
                    importer.wrapMode = wm;
                    updated.Add("wrapMode");
                }
            }

            if (args.ContainsKey("maxTextureSize"))
            {
                importer.maxTextureSize = Convert.ToInt32(args["maxTextureSize"]);
                updated.Add("maxTextureSize");
            }

            if (args.ContainsKey("textureCompression"))
            {
                if (Enum.TryParse<TextureImporterCompression>(args["textureCompression"].ToString(), true, out var comp))
                {
                    importer.textureCompression = comp;
                    updated.Add("textureCompression");
                }
            }

            if (args.ContainsKey("anisoLevel"))
            {
                importer.anisoLevel = Convert.ToInt32(args["anisoLevel"]);
                updated.Add("anisoLevel");
            }

            if (args.ContainsKey("alphaIsTransparency"))
            {
                importer.alphaIsTransparency = Convert.ToBoolean(args["alphaIsTransparency"]);
                updated.Add("alphaIsTransparency");
            }

            if (args.ContainsKey("spritePixelsPerUnit"))
            {
                importer.spritePixelsPerUnit = Convert.ToSingle(args["spritePixelsPerUnit"]);
                updated.Add("spritePixelsPerUnit");
            }

            if (args.ContainsKey("spriteMode"))
            {
                if (Enum.TryParse<SpriteImportMode>(args["spriteMode"].ToString(), true, out var sm))
                {
                    importer.spriteImportMode = sm;
                    updated.Add("spriteMode");
                }
            }

            if (args.ContainsKey("npotScale"))
            {
                if (Enum.TryParse<TextureImporterNPOTScale>(args["npotScale"].ToString(), true, out var npot))
                {
                    importer.npotScale = npot;
                    updated.Add("npotScale");
                }
            }

            if (updated.Count == 0)
                return new { error = "No valid import settings provided" };

            importer.SaveAndReimport();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "updated", updated },
            };
        }

        // ─── Reimport Texture ───

        public static object ReimportTexture(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
            };
        }

        // ─── Set Texture as Sprite ───

        public static object SetAsSprite(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return new { error = $"No texture importer for '{path}'" };

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;

            if (args.ContainsKey("pixelsPerUnit"))
                importer.spritePixelsPerUnit = Convert.ToSingle(args["pixelsPerUnit"]);

            if (args.ContainsKey("multiple") && Convert.ToBoolean(args["multiple"]))
                importer.spriteImportMode = SpriteImportMode.Multiple;

            importer.SaveAndReimport();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "textureType", "Sprite" },
                { "spriteMode", importer.spriteImportMode.ToString() },
            };
        }

        // ─── Set Texture as Normal Map ───

        public static object SetAsNormalMap(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return new { error = $"No texture importer for '{path}'" };

            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "textureType", "NormalMap" },
            };
        }
    }
}
