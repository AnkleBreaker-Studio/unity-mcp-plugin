using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for interacting with the Unity Undo system.
    ///
    /// The queue (<see cref="MCPRequestQueue"/>) wraps every WRITE action in its own named,
    /// collapsed Undo group and records that group on the action's <see cref="MCPActionRecord"/>.
    /// That makes per-action / per-agent undo possible: <see cref="UndoLast"/> reverts a specific
    /// recorded action by its group.
    ///
    /// Honesty about Unity's model: the Undo stack is GLOBAL and LINEAR. Reverting a group reverts
    /// that group AND everything stacked on top of it — you cannot cherry-pick one action out of
    /// the middle. UndoLast surfaces exactly which later actions would be caught in the revert and
    /// refuses to cascade unless the caller passes force:true.
    /// </summary>
    public static class MCPUndoCommands
    {
        // ─── Undo (single global step) ───

        public static object PerformUndo(Dictionary<string, object> args)
        {
            string name = Undo.GetCurrentGroupName();
            Undo.PerformUndo();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", string.IsNullOrEmpty(name) ? "Undo performed" : $"Undo performed: {name}" },
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

        // ─── Undo Last MCP Action (per-agent aware) ───

        /// <summary>
        /// Revert the most recent undoable MCP action. With <c>agentId</c>, targets that agent's
        /// most recent action instead. Because Unity's undo is linear, if later actions are stacked
        /// on top of the target they are reverted too — this is reported and requires <c>force:true</c>.
        /// </summary>
        public static object UndoLast(Dictionary<string, object> args)
        {
            string agentId = GetString(args, "agentId");
            bool force = GetBool(args, "force");

            // Undoable = a completed write action that opened its own undo group.
            var undoable = MCPActionHistory.GetAll()
                .Where(r => r.UndoGroup >= 0 && r.Status == "Completed")
                .ToList();
            if (undoable.Count == 0)
                return Err("No undoable action recorded yet. (Reads and failed actions are never undoable.)");

            // Newest undoable action overall, or the newest for the given agent.
            MCPActionRecord target = string.IsNullOrEmpty(agentId)
                ? undoable[undoable.Count - 1]
                : undoable.LastOrDefault(r => string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return Err($"No undoable action recorded for agent '{agentId}'.");

            // Linear model: reverting target also reverts every action stacked above it
            // (a strictly higher undo group). Surface them before cascading.
            var collateral = undoable
                .Where(r => r.Id != target.Id && r.UndoGroup > target.UndoGroup)
                .OrderByDescending(r => r.UndoGroup)
                .ToList();

            if (collateral.Count > 0 && !force)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Unity's undo is linear: reverting '{target.ActionName}' would also revert {collateral.Count} newer action(s) stacked on top of it. Pass force:true to revert them all, or undo the most recent action instead (omit agentId)." },
                    { "target", Describe(target) },
                    { "wouldAlsoRevert", collateral.Select(Describe).ToList() },
                };
            }

            // Perform the revert down to (and including) the target's group.
            Undo.RevertAllDownToGroup(target.UndoGroup);

            // The reverted records are no longer on the stack — mark them so history and a
            // follow-up undo/last reflect reality (GetAll returns the live record instances).
            var reverted = new List<MCPActionRecord> { target };
            reverted.AddRange(collateral);
            foreach (var r in reverted) r.UndoGroup = -1;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", collateral.Count > 0
                    ? $"Reverted '{target.ActionName}' and {collateral.Count} newer action(s)."
                    : $"Reverted '{target.ActionName}'." },
                { "revertedCount", reverted.Count },
                { "reverted", reverted.Select(Describe).ToList() },
            };
        }

        // ─── Undo History (enriched with the per-agent action log) ───

        public static object GetUndoHistory(Dictionary<string, object> args)
        {
            int count = Math.Max(1, GetInt(args, "count", 20));
            string agentId = GetString(args, "agentId");

            var all = MCPActionHistory.GetAll();
            IEnumerable<MCPActionRecord> query = all;
            if (!string.IsNullOrEmpty(agentId))
                query = query.Where(r => string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

            // Newest first, capped at count.
            var recent = query.Reverse().Take(count).ToList();

            return new Dictionary<string, object>
            {
                { "currentGroup", Undo.GetCurrentGroup() },
                { "currentGroupName", Undo.GetCurrentGroupName() },
                { "totalActions", all.Count },
                { "undoableCount", all.Count(r => r.UndoGroup >= 0 && r.Status == "Completed") },
                { "actions", recent.Select(Describe).ToList() },
            };
        }

        // ─── Clear Undo ───

        public static object ClearUndo(Dictionary<string, object> args)
        {
            if (args != null && args.ContainsKey("objectPath"))
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
                return Err("GameObject not found");
            }

            Undo.ClearAll();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", "All undo history cleared" },
            };
        }

        // ─── Helpers ───

        private static Dictionary<string, object> Describe(MCPActionRecord r) =>
            new Dictionary<string, object>
            {
                { "id", r.Id },
                { "agentId", r.AgentId },
                { "action", r.ActionName },
                { "category", r.Category },
                { "target", r.TargetPath ?? r.TargetInstanceId },
                { "status", r.Status },
                { "undoable", r.UndoGroup >= 0 && r.Status == "Completed" },
                { "timestamp", r.Timestamp.ToString("o") },
            };

        private static object Err(string msg) => new Dictionary<string, object> { { "error", msg } };

        private static string GetString(Dictionary<string, object> a, string k) =>
            a != null && a.ContainsKey(k) && a[k] != null ? a[k].ToString() : null;

        private static bool GetBool(Dictionary<string, object> a, string k)
        {
            if (a == null || !a.ContainsKey(k) || a[k] == null) return false;
            string s = a[k].ToString().ToLowerInvariant();
            return s == "true" || s == "1";
        }

        private static int GetInt(Dictionary<string, object> a, string k, int def) =>
            a != null && a.ContainsKey(k) && a[k] != null && int.TryParse(a[k].ToString(), out var i) ? i : def;
    }
}
