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

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };
            if (string.IsNullOrEmpty(content))
                return new { error = "content is required" };

            string fullPath = Path.Combine(Application.dataPath.Replace("/Assets", ""), path);
            string dir = Path.GetDirectoryName(fullPath);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(path);

            return new { success = true, path, size = content.Length };
        }

        public static object Read(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            string fullPath = Path.Combine(Application.dataPath.Replace("/Assets", ""), path);

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
            string content = args.ContainsKey("content") ? args["content"].ToString() : "";

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            string fullPath = Path.Combine(Application.dataPath.Replace("/Assets", ""), path);

            if (!File.Exists(fullPath))
                return new { error = $"File not found: {path}" };

            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(path);

            return new { success = true, path, size = content.Length };
        }
    }
}
