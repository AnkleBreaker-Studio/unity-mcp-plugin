using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Checks for plugin updates by querying GitHub releases API.
    /// Results are cached in EditorPrefs for 1 hour.
    /// </summary>
    public static class MCPUpdateChecker
    {
        private const string GitHubApiUrl =
            "https://api.github.com/repos/AnkleBreaker-Studio/unity-mcp-plugin/releases/latest";

        private const string CurrentVersion = "2.0.3";
        private const string CacheKey = "UnityMCP_LatestVersion";
        private const string CacheTimeKey = "UnityMCP_LatestVersionTime";
        private const double CacheHours = 1.0;

        private static UnityWebRequestAsyncOperation _pendingRequest;
        private static Action<bool, string> _pendingCallback;

        /// <summary>
        /// Check for updates. Callback receives (hasUpdate, latestVersion).
        /// Uses a 1-hour cache to avoid excessive API calls.
        /// </summary>
        public static void CheckForUpdates(Action<bool, string> callback)
        {
            // Check cache first
            string cachedTime = EditorPrefs.GetString(CacheTimeKey, "");
            if (!string.IsNullOrEmpty(cachedTime) &&
                DateTime.TryParse(cachedTime, out DateTime cacheDate) &&
                (DateTime.UtcNow - cacheDate).TotalHours < CacheHours)
            {
                string cachedVersion = EditorPrefs.GetString(CacheKey, CurrentVersion);
                callback(IsNewer(cachedVersion, CurrentVersion), cachedVersion);
                return;
            }

            // Fetch from GitHub
            var request = UnityWebRequest.Get(GitHubApiUrl);
            request.SetRequestHeader("User-Agent", "unity-mcp-plugin");
            request.SetRequestHeader("Accept", "application/vnd.github.v3+json");

            _pendingCallback = callback;
            _pendingRequest = request.SendWebRequest();
            _pendingRequest.completed += OnRequestComplete;
        }

        private static void OnRequestComplete(AsyncOperation op)
        {
            var request = _pendingRequest.webRequest;
            var callback = _pendingCallback;
            _pendingRequest = null;
            _pendingCallback = null;

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Unity MCP Update] Failed: {request.error}");
                callback?.Invoke(false, CurrentVersion);
                request.Dispose();
                return;
            }

            try
            {
                string json = request.downloadHandler.text;

                // Parse tag_name from JSON (minimal parsing without full JSON lib)
                string latestVersion = ExtractTagName(json);
                if (string.IsNullOrEmpty(latestVersion))
                {
                    callback?.Invoke(false, CurrentVersion);
                    request.Dispose();
                    return;
                }

                // Strip leading 'v' if present
                if (latestVersion.StartsWith("v"))
                    latestVersion = latestVersion.Substring(1);

                // Cache result
                EditorPrefs.SetString(CacheKey, latestVersion);
                EditorPrefs.SetString(CacheTimeKey, DateTime.UtcNow.ToString("O"));

                callback?.Invoke(IsNewer(latestVersion, CurrentVersion), latestVersion);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Unity MCP Update] Parse error: {ex.Message}");
                callback?.Invoke(false, CurrentVersion);
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>Extract "tag_name" value from GitHub release JSON.</summary>
        private static string ExtractTagName(string json)
        {
            // Simple string parsing to avoid dependency on JSON library
            string key = "\"tag_name\"";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return null;

            int startQuote = json.IndexOf('"', colonIdx + 1);
            if (startQuote < 0) return null;

            int endQuote = json.IndexOf('"', startQuote + 1);
            if (endQuote < 0) return null;

            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        /// <summary>Returns true if version a is newer than version b (semver).</summary>
        private static bool IsNewer(string a, string b)
        {
            var aParts = a.Split('.');
            var bParts = b.Split('.');

            for (int i = 0; i < 3; i++)
            {
                int aNum = i < aParts.Length && int.TryParse(aParts[i], out int av) ? av : 0;
                int bNum = i < bParts.Length && int.TryParse(bParts[i], out int bv) ? bv : 0;

                if (aNum > bNum) return true;
                if (aNum < bNum) return false;
            }

            return false;
        }
    }
}
