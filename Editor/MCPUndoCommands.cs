using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for interacting with the Unity Undo system.
    /// </summary>
    public static class MCPUndoCommands
    {
        // ─── Undo ───

        public static object PerformUndo(Dictionary<string, object> args)
        {
            Undo.PerformUndo();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", "Undo performed" },
            };
        }

        // ─── Redo ───

        public static object PerformRedo(Dictionary<string, object> args)
        {
            Undo.PerformRedo();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", "Redo performed" },
            };
        }

        // ─── Get Undo History ───

        public static object GetUndoHistory(Dictionary<string, object> args)
        {
            Undo.GetCurrentGroupName();
            int currentGroup = Undo.GetCurrentGroup();

            return new Dictionary<string, object>
            {
                { "currentGroupName", Undo.GetCurrentGroupName() },
                { "currentGroup", currentGroup },
            };
        }

        // ─── Clear Undo ───

        public static object ClearUndo(Dictionary<string, object> args)
        {
            if (args.ContainsKey("objectPath"))
            {
                var go = GameObject.Find(args["objectPath"].ToString());
                if (go != null)
                {
                    Undo.ClearUndo(go);
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "message", $"Cleared undo for '{go.name}'" },
                    };
                }
                return new { error = "GameObject not found" };
            }

            Undo.ClearAll();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", "All undo history cleared" },
            };
        }
    }
}
