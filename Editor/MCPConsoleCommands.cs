using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPConsoleCommands
    {
        // Store log messages via Application.logMessageReceived
        private static readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private static bool _isListening = false;

        private struct LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestamp;
        }

        private static void EnsureListening()
        {
            if (_isListening) return;
            Application.logMessageReceived += OnLogMessage;
            _isListening = true;
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logEntries)
            {
                _logEntries.Add(new LogEntry
                {
                    message = message,
                    stackTrace = stackTrace,
                    type = type,
                    timestamp = DateTime.Now,
                });

                // Keep max 500 entries
                if (_logEntries.Count > 500)
                    _logEntries.RemoveRange(0, _logEntries.Count - 500);
            }
        }

        public static object GetLog(Dictionary<string, object> args)
        {
            EnsureListening();

            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 50;
            string typeFilter = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "all";

            var entries = new List<Dictionary<string, object>>();
            lock (_logEntries)
            {
                for (int i = Math.Max(0, _logEntries.Count - count); i < _logEntries.Count; i++)
                {
                    var entry = _logEntries[i];
                    string logType = entry.type.ToString().ToLower();

                    if (typeFilter != "all")
                    {
                        if (typeFilter == "error" && entry.type != LogType.Error && entry.type != LogType.Exception)
                            continue;
                        if (typeFilter == "warning" && entry.type != LogType.Warning)
                            continue;
                        if (typeFilter == "info" && entry.type != LogType.Log)
                            continue;
                    }

                    entries.Add(new Dictionary<string, object>
                    {
                        { "message", entry.message },
                        { "type", logType },
                        { "timestamp", entry.timestamp.ToString("HH:mm:ss.fff") },
                        { "stackTrace", entry.stackTrace },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "entries", entries },
            };
        }

        public static object Clear()
        {
            EnsureListening();
            lock (_logEntries) { _logEntries.Clear(); }
            return new { success = true, message = "Console log buffer cleared" };
        }
    }
}
