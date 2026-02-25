using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing EditorPrefs and PlayerPrefs.
    /// </summary>
    public static class MCPPrefsCommands
    {
        // ─── Get EditorPref ───

        public static object GetEditorPref(Dictionary<string, object> args)
        {
            string key = args.ContainsKey("key") ? args["key"].ToString() : "";
            if (string.IsNullOrEmpty(key))
                return new { error = "key is required" };

            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "string";

            object value = null;
            bool exists = EditorPrefs.HasKey(key);

            if (exists)
            {
                switch (type)
                {
                    case "int": value = EditorPrefs.GetInt(key); break;
                    case "float": value = EditorPrefs.GetFloat(key); break;
                    case "bool": value = EditorPrefs.GetBool(key); break;
                    default: value = EditorPrefs.GetString(key); break;
                }
            }

            return new Dictionary<string, object>
            {
                { "key", key },
                { "exists", exists },
                { "value", value },
                { "type", type },
            };
        }

        // ─── Set EditorPref ───

        public static object SetEditorPref(Dictionary<string, object> args)
        {
            string key = args.ContainsKey("key") ? args["key"].ToString() : "";
            if (string.IsNullOrEmpty(key))
                return new { error = "key is required" };

            if (!args.ContainsKey("value"))
                return new { error = "value is required" };

            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "string";

            switch (type)
            {
                case "int":
                    EditorPrefs.SetInt(key, Convert.ToInt32(args["value"]));
                    break;
                case "float":
                    EditorPrefs.SetFloat(key, Convert.ToSingle(args["value"]));
                    break;
                case "bool":
                    EditorPrefs.SetBool(key, Convert.ToBoolean(args["value"]));
                    break;
                default:
                    EditorPrefs.SetString(key, args["value"].ToString());
                    break;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "key", key },
                { "value", args["value"] },
                { "type", type },
            };
        }

        // ─── Delete EditorPref ───

        public static object DeleteEditorPref(Dictionary<string, object> args)
        {
            string key = args.ContainsKey("key") ? args["key"].ToString() : "";
            if (string.IsNullOrEmpty(key))
                return new { error = "key is required" };

            bool existed = EditorPrefs.HasKey(key);
            EditorPrefs.DeleteKey(key);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "key", key },
                { "existed", existed },
            };
        }

        // ─── Get PlayerPref ───

        public static object GetPlayerPref(Dictionary<string, object> args)
        {
            string key = args.ContainsKey("key") ? args["key"].ToString() : "";
            if (string.IsNullOrEmpty(key))
                return new { error = "key is required" };

            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "string";

            object value = null;
            bool exists = PlayerPrefs.HasKey(key);

            if (exists)
            {
                switch (type)
                {
                    case "int": value = PlayerPrefs.GetInt(key); break;
                    case "float": value = PlayerPrefs.GetFloat(key); break;
                    default: value = PlayerPrefs.GetString(key); break;
                }
            }

            return new Dictionary<string, object>
            {
                { "key", key },
                { "exists", exists },
                { "value", value },
                { "type", type },
            };
        }

        // ─── Set PlayerPref ───

        public static object SetPlayerPref(Dictionary<string, object> args)
        {
            string key = args.ContainsKey("key") ? args["key"].ToString() : "";
            if (string.IsNullOrEmpty(key))
                return new { error = "key is required" };

            if (!args.ContainsKey("value"))
                return new { error = "value is required" };

            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "string";

            switch (type)
            {
                case "int":
                    PlayerPrefs.SetInt(key, Convert.ToInt32(args["value"]));
                    break;
                case "float":
                    PlayerPrefs.SetFloat(key, Convert.ToSingle(args["value"]));
                    break;
                default:
                    PlayerPrefs.SetString(key, args["value"].ToString());
                    break;
            }

            PlayerPrefs.Save();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "key", key },
                { "value", args["value"] },
                { "type", type },
            };
        }

        // ─── Delete PlayerPref ───

        public static object DeletePlayerPref(Dictionary<string, object> args)
        {
            string key = args.ContainsKey("key") ? args["key"].ToString() : "";
            if (string.IsNullOrEmpty(key))
                return new { error = "key is required" };

            bool existed = PlayerPrefs.HasKey(key);
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "key", key },
                { "existed", existed },
            };
        }

        // ─── Delete All PlayerPrefs ───

        public static object DeleteAllPlayerPrefs(Dictionary<string, object> args)
        {
            PlayerPrefs.DeleteAll();
            PlayerPrefs.Save();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", "All PlayerPrefs deleted" },
            };
        }
    }
}
