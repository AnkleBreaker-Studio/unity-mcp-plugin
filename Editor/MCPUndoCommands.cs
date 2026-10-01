using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>Reverts recorded MCP groups only after checking Unity's current global Undo stack.</summary>
    public static class MCPUndoCommands
    {
        public static object PerformUndo(Dictionary<string, object> args)
        {
            var snapshot = MCPUndoState.Read();
            string name = snapshot.Available ? snapshot.Groups.Values.OrderByDescending(g => g.Id).FirstOrDefault()?.Name : null;
            Undo.PerformUndo();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", string.IsNullOrEmpty(name) ? "Undo performed" : $"Undo performed: {name}" },
            };
        }

        public static object PerformRedo(Dictionary<string, object> args)
        {
            Undo.PerformRedo();
            return new Dictionary<string, object> { { "success", true }, { "message", "Redo performed" } };
        }

        public static object UndoLast(Dictionary<string, object> args)
        {
            MCPRequestQueue.FlushCompletedHistory(10_000);
            var snapshot = MCPUndoState.Read();
            if (!snapshot.Available) return Err("Unity's Undo stack could not be inspected. Use native Undo or review the editor before retrying.");
            string agentId = GetString(args, "agentId");
            var target = MCPActionHistory.GetAll().LastOrDefault(r => snapshot.State(r) == "available"
                && (string.IsNullOrEmpty(agentId) || string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase)));
            if (target == null)
                return Err(string.IsNullOrEmpty(agentId) ? "No verified undoable MCP action remains on Unity's current stack."
                    : $"No verified undoable action remains for agent '{agentId}'.");
            return RevertRecordedAction(target, GetBool(args, "force"));
        }

        private sealed class UndoPlan
        {
            internal MCPUndoState.Snapshot Snapshot;
            internal List<MCPActionRecord> Records;
            internal List<MCPUndoState.Group> Groups;
            internal List<MCPUndoState.Group> OtherGroups;
        }

        private static UndoPlan Plan(MCPActionRecord target, out string error)
        {
            MCPRequestQueue.FlushCompletedHistory(10_000);
            var all = MCPActionHistory.GetAll();
            var snapshot = MCPUndoState.Read();
            if (target == null || !all.Contains(target) || snapshot.State(target) != "available")
            {
                error = "This action is no longer verified on Unity's current Undo stack. Refresh the history before choosing another action.";
                return null;
            }
            var records = all.Where(r => snapshot.State(r) == "available" && r.UndoGroup >= target.UndoGroup)
                .OrderByDescending(r => r.UndoGroup).ToList();
            var groups = snapshot.Groups.Values.Where(g => g.Id >= target.UndoGroup).OrderByDescending(g => g.Id).ToList();
            var known = new HashSet<int>(records.Select(r => r.UndoGroup));
            error = null;
            return new UndoPlan { Snapshot = snapshot, Records = records, Groups = groups,
                OtherGroups = groups.Where(g => !known.Contains(g.Id)).ToList() };
        }

        internal static Dictionary<string, object> PreviewRecordedAction(MCPActionRecord target)
        {
            var plan = Plan(target, out string error);
            if (plan == null) return Err(error);
            return new Dictionary<string, object>
            {
                { "success", true }, { "stackRevision", plan.Snapshot.Revision },
                { "message", $"Revert '{target.ActionName}' and {plan.Groups.Count - 1} newer Unity Undo group(s)?\n\nThis includes {plan.Records.Count - 1} newer recorded MCP action(s) and {plan.OtherGroups.Count} other Unity group(s). These changes will not be available through Redo." },
            };
        }

        internal static Dictionary<string, object> RevertRecordedAction(MCPActionRecord target, bool force, string expectedStack = null)
        {
            var plan = Plan(target, out string error);
            if (plan == null) return Err(error);
            if (expectedStack != null && expectedStack != plan.Snapshot.Revision)
                return Err("Unity's Undo stack changed after the preview. Review the action again before reverting.");
            var collateral = plan.Records.Where(r => r != target).ToList();
            if (plan.Groups.Count > 1 && !force)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Unity's undo is linear: reverting '{target.ActionName}' would also revert {plan.Groups.Count - 1} newer Unity Undo group(s). Pass force:true only to revert them all." },
                    { "target", Describe(target, plan.Snapshot) },
                    { "wouldAlsoRevert", collateral.Select(r => Describe(r, plan.Snapshot)).ToList() },
                    { "wouldAlsoRevertUnity", plan.OtherGroups.Select(g => g.Describe()).ToList() },
                };
            }

            Undo.RevertAllDownToGroup(target.UndoGroup);
            var after = MCPUndoState.Read();
            if (!after.Available || plan.Groups.Any(g => after.Groups.ContainsKey(g.Id)))
                return new Dictionary<string, object> { { "error", "Undo was requested but its final stack state could not be verified. Inspect Unity before retrying." }, { "outcomeUnknown", true } };
            foreach (var record in plan.Records) record.UndoGroup = -1;
            var reverted = new List<MCPActionRecord> { target };
            reverted.AddRange(collateral);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "message", plan.Groups.Count > 1 ? $"Reverted '{target.ActionName}' and {plan.Groups.Count - 1} newer Unity Undo group(s)." : $"Reverted '{target.ActionName}'." },
                { "revertedCount", reverted.Count },
                { "reverted", reverted.Select(r => Describe(r, after)).ToList() },
                { "revertedGroupCount", plan.Groups.Count },
                { "revertedUnityGroups", plan.OtherGroups.Select(g => g.Describe()).ToList() },
            };
        }

        public static object GetUndoHistory(Dictionary<string, object> args)
        {
            MCPRequestQueue.FlushCompletedHistory(10_000);
            int count = Math.Max(1, GetInt(args, "count", 20));
            string agentId = GetString(args, "agentId");
            var snapshot = MCPUndoState.Read();
            var all = MCPActionHistory.GetAll();
            IEnumerable<MCPActionRecord> query = all;
            if (!string.IsNullOrEmpty(agentId))
                query = query.Where(r => string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            var recent = query.Reverse().Take(count).ToList();
            return new Dictionary<string, object>
            {
                { "currentGroup", Undo.GetCurrentGroup() },
                { "currentGroupName", Undo.GetCurrentGroupName() },
                { "totalActions", all.Count },
                { "undoableCount", all.Count(r => snapshot.State(r) == "available") },
                { "undoStateAvailable", snapshot.Available },
                { "actions", recent.Select(r => Describe(r, snapshot)).ToList() },
            };
        }

        public static object ClearUndo(Dictionary<string, object> args)
        {
            if (args != null && args.ContainsKey("objectPath"))
            {
                var go = GameObject.Find(args["objectPath"].ToString());
                if (go != null)
                {
                    Undo.ClearUndo(go);
                    return new Dictionary<string, object> { { "success", true }, { "message", $"Cleared undo for '{go.name}'" } };
                }
                return Err("GameObject not found");
            }
            Undo.ClearAll();
            return new Dictionary<string, object> { { "success", true }, { "message", "All undo history cleared" } };
        }

        private static Dictionary<string, object> Describe(MCPActionRecord r, MCPUndoState.Snapshot snapshot) =>
            new Dictionary<string, object>
            {
                { "id", r.Id }, { "agentId", r.AgentId }, { "action", r.ActionName }, { "category", r.Category },
                { "target", r.TargetPath ?? r.TargetInstanceId }, { "status", r.Status },
                { "commandFailed", r.CommandFailed }, { "errorMessage", r.ErrorMessage ?? "" },
                { "undoable", snapshot.State(r) == "available" }, { "undoState", snapshot.State(r) },
                { "timestamp", r.Timestamp.ToString("o") },
            };

        private static Dictionary<string, object> Err(string msg) => new Dictionary<string, object> { { "error", msg } };
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
