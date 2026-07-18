using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Shared guards for asset-writing MCP handlers. Two recurring data-loss classes
    /// are centralized here:
    ///   1. Path resolution — the project root is the folder CONTAINING Assets/, i.e.
    ///      Path.GetDirectoryName(Application.dataPath). The old idiom
    ///      dataPath.Replace("/Assets","") stripped EVERY "/Assets" occurrence, so a
    ///      project under a path containing "/Assets" resolved to the wrong root and
    ///      writes landed outside the project (import silently no-ops). Paths are also
    ///      canonicalized and confined under the project root so "../" or an absolute
    ///      path can't escape and clobber arbitrary files on disk.
    ///   2. Overwrite — CreateAsset/File.WriteAllText on an existing asset destroys the
    ///      user's asset (and, for CreateAsset, every reference to it since the GUID is
    ///      reused). Callers gate on AssetWouldOverwrite unless the caller passes overwrite:true.
    /// </summary>
    internal static class MCPAssetSafety
    {
        /// <summary>Absolute path of the folder containing Assets/ (and Packages/, ProjectSettings/).</summary>
        internal static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        /// <summary>
        /// Resolve a project-relative asset path (e.g. "Assets/Scripts/X.cs") to an absolute
        /// path, confined under the project root. Returns false with a message if the path is
        /// empty, absolute, or escapes the project via "..".
        /// </summary>
        internal static bool TryResolveProjectPath(string assetPath, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;

            if (string.IsNullOrEmpty(assetPath))
            {
                error = "path is required";
                return false;
            }

            // Reject rooted/absolute inputs outright: Path.Combine returns the second
            // argument verbatim when it is rooted, which would escape the project.
            if (Path.IsPathRooted(assetPath))
            {
                error = $"path must be project-relative (under Assets/ or Packages/), got absolute: {assetPath}";
                return false;
            }

            string root = ProjectRoot;
            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(root, assetPath));
            }
            catch (Exception ex)
            {
                error = $"invalid path '{assetPath}': {ex.Message}";
                return false;
            }

            string rootFull = Path.GetFullPath(root);
            string rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            // Case-insensitive on Windows/macOS default filesystems; ordinal is the safe superset.
            if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"path escapes the project root: {assetPath}";
                return false;
            }

            fullPath = combined;
            return true;
        }

        /// <summary>Project-relative asset path normalized to forward slashes (for AssetDatabase APIs).</summary>
        internal static string ToAssetDatabasePath(string assetPath)
        {
            return assetPath.Replace('\\', '/');
        }

        /// <summary>True if an asset already exists at this project path (AssetDatabase view).</summary>
        internal static bool AssetWouldOverwrite(string assetPath)
        {
            string dbPath = ToAssetDatabasePath(assetPath);
            return AssetDatabase.LoadMainAssetAtPath(dbPath) != null;
        }

        /// <summary>
        /// Standard overwrite guard for asset creators. Returns an error object to return
        /// directly, or null if creation may proceed. Pass the caller's args so a caller can
        /// opt in with overwrite:true.
        /// </summary>
        internal static object OverwriteGuard(string assetPath, System.Collections.Generic.Dictionary<string, object> args)
        {
            bool overwrite = args != null && args.ContainsKey("overwrite")
                && args["overwrite"] != null
                && (args["overwrite"].ToString().ToLowerInvariant() == "true" || args["overwrite"].ToString() == "1");
            if (!overwrite && AssetWouldOverwrite(assetPath))
            {
                return new
                {
                    error = $"An asset already exists at '{ToAssetDatabasePath(assetPath)}'. Pass overwrite:true to replace it (this destroys the existing asset and its references).",
                    existingAsset = ToAssetDatabasePath(assetPath),
                };
            }
            return null;
        }
    }
}
