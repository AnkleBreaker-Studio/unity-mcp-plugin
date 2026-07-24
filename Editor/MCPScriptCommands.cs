using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPScriptCommands
    {
        public static object Create(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string content = args.ContainsKey("content") ? args["content"].ToString() : "";

            if (string.IsNullOrEmpty(content))
                return new { error = "content is required" };

            // Resolve under the project root and reject traversal/absolute escapes.
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new { error = pathError };

            // Never silently overwrite an existing script (source-code loss).
            var overwriteError = MCPAssetSafety.OverwriteGuard(path, args);
            if (overwriteError != null)
                return overwriteError;

            string dir = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(MCPAssetSafety.ToAssetDatabasePath(path));

            return new { success = true, path, size = content.Length };
        }

        public static object Read(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";

            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new { error = pathError };

            if (!File.Exists(fullPath))
                return new { error = $"File not found: {path}" };

            string content = File.ReadAllText(fullPath);
            return new Dictionary<string, object>
            {
                { "path", path },
                { "content", content },
                { "lines", content.Split('\n').Length },
                { "size", content.Length },
            };
        }

        public static object Update(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";

            // Update REQUIRES non-empty content: an unvalidated empty/missing content used to
            // truncate the target source file to zero bytes and import the wreckage. Empty
            // string is the most likely accidental shape (a templating var that resolved to ""),
            // so reject it like Create does — clearing a file must be deliberate, not a default.
            string content = (args.ContainsKey("content") ? args["content"]?.ToString() : null);
            if (string.IsNullOrEmpty(content))
                return new { error = "content is required (non-empty). To intentionally clear a file, write a single newline." };

            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new { error = pathError };

            if (!File.Exists(fullPath))
                return new { error = $"File not found: {path}. Use script/create for a new file." };

            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(MCPAssetSafety.ToAssetDatabasePath(path));

            return new { success = true, path, size = content.Length };
        }
    }
}
