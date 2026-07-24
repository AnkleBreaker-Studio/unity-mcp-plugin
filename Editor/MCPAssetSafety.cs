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

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = "path is required";
                return false;
            }

            string root, rootFull, combined;
            try
            {
                // IsPathRooted throws on invalid path chars on Mono — keep it inside the try.
                if (Path.IsPathRooted(assetPath))
                {
                    error = $"path must be project-relative (under Assets/ or Packages/), got absolute: {assetPath}";
                    return false;
                }
                root = ProjectRoot;
                rootFull = Path.GetFullPath(root);
                combined = Path.GetFullPath(Path.Combine(root, assetPath));
            }
            catch (Exception ex)
            {
                error = $"invalid path '{assetPath}': {ex.Message}";
                return false;
            }

            string sep = Path.DirectorySeparatorChar.ToString();
            string rootPrefix = rootFull.EndsWith(sep) ? rootFull : rootFull + sep;

            // Path comparison is case-insensitive only where the filesystem is (Windows/macOS);
            // on Linux it must be ordinal so a case-variant sibling can't slip through.
            var cmp = Application.platform == RuntimePlatform.LinuxEditor
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (!combined.StartsWith(rootPrefix, cmp))
            {
                error = $"path escapes the project root: {assetPath}";
                return false;
            }

            // Enforce the stated contract: writes/reads live under Assets/ or Packages/,
            // never project metadata (ProjectSettings/, Library/, .git/, ...).
            string relative = combined.Substring(rootPrefix.Length).Replace('\\', '/');
            string firstSegment = relative.Split('/')[0];
            if (!firstSegment.Equals("Assets", cmp) && !firstSegment.Equals("Packages", cmp))
            {
                error = $"path must be under Assets/ or Packages/, got: {assetPath}";
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

        /// <summary>
        /// True if something already lives at this project path — checked against BOTH the
        /// canonical on-disk path AND the AssetDatabase. A raw-string DB lookup alone was
        /// bypassable by a non-canonical spelling ("Assets//X", "Assets/./X") and blind to a
        /// file written moments earlier but not yet imported.
        /// </summary>
        internal static bool AssetWouldOverwrite(string assetPath)
        {
            if (TryResolveProjectPath(assetPath, out string fullPath, out _) && File.Exists(fullPath))
                return true;
            return AssetDatabase.LoadMainAssetAtPath(ToAssetDatabasePath(assetPath)) != null;
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
