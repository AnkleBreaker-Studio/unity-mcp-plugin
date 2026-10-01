using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpUndoValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static readonly List<object> Snapshots = new List<object>();
    private const string Prefix = "__McpUndoValidation_";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Dictionary<string, object> Args(params object[] pairs)
    {
        var args = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) args[(string)pairs[i]] = pairs[i + 1];
        return args;
    }
    private static void Clean()
    {
        Undo.ClearAll();
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            if (go.name.StartsWith(Prefix, StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(go);
        MCPActionHistory.Clear();
    }
    private static void Check(string name, Func<object> check)
    {
        Clean();
        try { Checks.Add(new { name, passed = true, evidence = check() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { Clean(); }
    }
    private static object RunCommand(string agent, string route, Func<object> action)
    {
        var ticket = MCPRequestQueue.SubmitRequest(agent, route, action);
        MCPRequestQueue.ProcessNextRequests();
        var result = MCPRequestQueue.GetTicketStatus(ticket.TicketId);
        Require((string)result["status"] == "Completed", "Fixture command failed: " + route);
        return result["result"];
    }
    private static GameObject Create(string agent, string name)
    {
        GameObject result = null;
        RunCommand(agent, "gameobject/create", () => {
            result = new GameObject(Prefix + name);
            Undo.RegisterCreatedObjectUndo(result, "Create " + name);
            return Args("name", result.name);
        });
        return result;
    }
    private static List<Dictionary<string, object>> History()
    {
        var result = (Dictionary<string, object>)MCPUndoCommands.GetUndoHistory(Args("count", 100));
        return (List<Dictionary<string, object>>)result["actions"];
    }
    private static object Snapshot(string name)
    {
        var undos = new List<string>(); var redos = new List<string>();
        var records = typeof(Undo).GetMethod("GetRecords", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(List<string>), typeof(List<string>) }, null);
        records.Invoke(null, new object[] { undos, redos });
        var group = typeof(Undo).GetMethod("GetGroupFromStack", BindingFlags.NonPublic | BindingFlags.Static);
        var groups = new List<int>();
        for (int i = 0; i < undos.Count; i++) groups.Add((int)group.Invoke(null, new object[] { i }));
        var snapshot = new { name, currentGroup = Undo.GetCurrentGroup(), undos, redos, groups,
            recorded = MCPActionHistory.GetAll().Select(r => new { r.ActionName, r.AgentId, r.UndoGroup }).ToArray() };
        Snapshots.Add(snapshot);
        return snapshot;
    }
    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a disposable marked project");
        bool persistence = MCPSettingsManager.ActionHistoryPersistence;
        const string historyPath = "Library/MCPActionHistory.json";
        byte[] history = File.Exists(historyPath) ? File.ReadAllBytes(historyPath) : null;
        MCPSettingsManager.ActionHistoryPersistence = false;
        try {
            Check("Native stack probe", () => {
                Create("a", "ProbeA"); Create("b", "ProbeB"); Create("c", "ProbeC");
                Snapshot("Three creations"); Undo.PerformUndo(); Snapshot("After one native undo");
                Undo.PerformRedo(); return Snapshot("After native redo");
            });
            Check("Latest MCP creation can be reverted", () => {
                var go = Create("a", "Latest");
                var response = MCPUndoCommands.UndoLast(Args());
                Require(go == null, "MCP creation was not reverted");
                Require(History().All(r => !(bool)r["undoable"]), "Reverted record still claims to be undoable");
                return response;
            });
            Check("Transform edits are registered before the queue reports completion", () => {
                var go = new GameObject(Prefix + "Transform");
                RunCommand("a", "gameobject/set-transform", () => MCPGameObjectCommands.SetTransform(Args(
                    "path", go.name, "position", Args("x", 3.0, "y", 4.0, "z", 5.0))));
                Undo.FlushUndoRecordObjects();
                Require(MCPActionHistory.GetAll().Single().UndoGroup >= 0, "Deferred RecordObject change was omitted from undo history");
                var response = MCPUndoCommands.UndoLast(Args());
                Require(go != null && go.transform.position == Vector3.zero, "Transform was not restored");
                return response;
            });
            Check("Native undo removes the undone record from undo eligibility", () => {
                var a = Create("a", "NativeA"); var b = Create("b", "NativeB");
                Undo.PerformUndo(); Require(b == null && a != null, "Native undo fixture failed");
                Require(History().Count(r => (bool)r["undoable"]) == 1, "Native undo left stale MCP undo eligibility");
                var response = MCPUndoCommands.UndoLast(Args());
                Require(a == null, "UndoLast selected an already-undone record");
                return response;
            });
            Check("Native redo restores MCP undo eligibility", () => {
                Create("a", "Redo"); Undo.PerformUndo();
                Require(History().All(r => !(bool)r["undoable"]), "Native undo left stale eligibility before redo");
                Undo.PerformRedo();
                Require(History().Count(r => (bool)r["undoable"]) == 1, "Redo did not restore eligibility");
                MCPUndoCommands.UndoLast(Args());
                Require(GameObject.Find(Prefix + "Redo") == null, "Redone action could not be reverted");
                return new { restored = true };
            });
            Check("Clearing Unity Undo clears MCP undo eligibility", () => {
                var go = Create("a", "Cleared");
                MCPUndoCommands.ClearUndo(Args());
                Require(History().All(r => !(bool)r["undoable"]), "Cleared stack still advertises undoable actions");
                var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args());
                Require(response.ContainsKey("error") && go != null, "Empty stack reported a successful revert");
                return response;
            });
            Check("A newer native edit requires cascade opt-in", () => {
                var a = Create("a", "McpBeforeNative");
                Undo.IncrementCurrentGroup();
                var b = new GameObject(Prefix + "NativeAfterMcp");
                Undo.RegisterCreatedObjectUndo(b, "Native user creation");
                Undo.IncrementCurrentGroup();
                var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a"));
                Require(response.ContainsKey("error") && a != null && b != null, "UndoLast silently reverted a newer native edit");
                return response;
            });
            Check("A newer agent requires cascade opt-in and force reverts both", () => {
                var a = Create("a", "AgentA"); var b = Create("b", "AgentB");
                var refused = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a"));
                Require(refused.ContainsKey("error") && a != null && b != null, "Agent collateral was not protected");
                var response = MCPUndoCommands.UndoLast(Args("agentId", "a", "force", true));
                Require(a == null && b == null, "Explicit cascade did not revert both agents");
                return response;
            });
            Check("Later native work cannot join the MCP group's boundary", () => {
                var a = Create("a", "SealedMcp");
                var b = new GameObject(Prefix + "SealedNative");
                Undo.RegisterCreatedObjectUndo(b, "Later native work");
                var refused = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a"));
                Require(refused.ContainsKey("error") && a != null && b != null, "Native work joined the MCP group and was silently reverted");
                var response = MCPUndoCommands.UndoLast(Args("agentId", "a", "force", true));
                Require(a == null && b == null, "Explicit native cascade did not revert both groups");
                return response;
            });
            Check("A no-op transform does not hide the previous real edit", () => {
                var go = Create("a", "NoOp");
                RunCommand("a", "gameobject/set-transform", () => MCPGameObjectCommands.SetTransform(Args(
                    "path", go.name, "position", Args("x", 0.0, "y", 0.0, "z", 0.0))));
                Require(!(bool)History()[0]["undoable"], "A no-op became the newest undo target");
                var response = MCPUndoCommands.UndoLast(Args());
                Require(go == null, "No-op hid the actual creation");
                return response;
            });
            Check("History persistence retains identity only within the same editor session", () => {
                var go = Create("a", "Persistence");
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                typeof(MCPActionHistory).GetMethod("SaveToDisk", flags).Invoke(null, null);
                string saved = File.ReadAllText(historyPath);
                typeof(MCPActionHistory).GetMethod("LoadFromDisk", flags).Invoke(null, null);
                Require((bool)History().Single()["undoable"], "Same-session persistence lost Undo identity");
                var record = MCPActionHistory.GetAll().Single();
                string session = (string)typeof(MCPActionRecord).GetProperty("UndoSessionId").GetValue(record);
                File.WriteAllText(historyPath, saved.Replace(session, Guid.NewGuid().ToString("N")));
                typeof(MCPActionHistory).GetMethod("LoadFromDisk", flags).Invoke(null, null);
                Require(!(bool)History().Single()["undoable"], "Previous-session record matched a reused native group ID");
                var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("force", true));
                Require(response.ContainsKey("error") && go != null, "Forced undo trusted a foreign editor session");
                return response;
            });
            Check("Legacy history without session identity remains readable without claiming Undo", () => {
                var go = Create("a", "LegacyHistory");
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                typeof(MCPActionHistory).GetMethod("SaveToDisk", flags).Invoke(null, null);
                var legacy = (Dictionary<string, object>)MiniJson.Deserialize(File.ReadAllText(historyPath));
                foreach (var entry in ((List<object>)legacy["records"]).Cast<Dictionary<string, object>>())
                {
                    entry.Remove("undoSessionId");
                    entry.Remove("undoSignature");
                }
                File.WriteAllText(historyPath, MiniJson.Serialize(legacy));
                typeof(MCPActionHistory).GetMethod("LoadFromDisk", flags).Invoke(null, null);
                Require(History().Count == 1 && !(bool)History()[0]["undoable"], "Legacy record was lost or trusted as a live group");
                Require(go != null, "Legacy history mutated Unity");
                return History();
            });
            Check("History-window confirmation is invalidated when the native stack changes", () => {
                var a = Create("a", "Preview");
                var record = MCPActionHistory.GetAll().Single();
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var previewMethod = typeof(MCPUndoCommands).GetMethod("PreviewRecordedAction", flags);
                var revertMethod = typeof(MCPUndoCommands).GetMethod("RevertRecordedAction", flags);
                var preview = (Dictionary<string, object>)previewMethod.Invoke(null, new object[] { record });
                var b = new GameObject(Prefix + "AfterPreview");
                Undo.RegisterCreatedObjectUndo(b, "Native edit after preview");
                var refused = (Dictionary<string, object>)revertMethod.Invoke(null, new object[] { record, true, preview["stackRevision"] });
                Require(refused.ContainsKey("error") && a != null && b != null, "A stale confirmation reverted new work");
                preview = (Dictionary<string, object>)previewMethod.Invoke(null, new object[] { record });
                Require(((string)preview["message"]).Contains("1 newer Unity"), "Confirmation omitted newer groups");
                var response = revertMethod.Invoke(null, new object[] { record, true, preview["stackRevision"] });
                Require(a == null && b == null, "Current confirmation did not revert the reviewed groups");
                return response;
            });
            Check("An externally merged group cannot conceal another agent's edit", () => {
                var a = Create("a", "MergedA");
                int group = MCPActionHistory.GetAll().Single().UndoGroup;
                var b = Create("b", "MergedB");
                Undo.CollapseUndoOperations(group);
                var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a"));
                Require(response.ContainsKey("error") && a != null && b != null, "A merged native group concealed another agent's edit");
                return response;
            });
            Check("Merged changes on the same Transform cannot conceal another agent", () => {
                var go = new GameObject(Prefix + "MergedTransform");
                RunCommand("a", "gameobject/set-transform", () => MCPGameObjectCommands.SetTransform(Args(
                    "path", go.name, "position", Args("x", 3.0, "y", 0.0, "z", 0.0))));
                int group = MCPActionHistory.GetAll().Single().UndoGroup;
                RunCommand("b", "gameobject/set-transform", () => MCPGameObjectCommands.SetTransform(Args(
                    "path", go.name, "position", Args("x", 7.0, "y", 0.0, "z", 0.0))));
                Undo.CollapseUndoOperations(group);
                Snapshot("Merged transforms");
                var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a"));
                Require(response.ContainsKey("error") && go.transform.position.x == 7, "Merged Transform changes concealed another agent");
                return response;
            });
            Check("Object-specific Undo clearing cannot leave a trusted partial group", () => {
                var a = Create("a", "ObjectClearA"); var b = Create("b", "ObjectClearB");
                MCPUndoCommands.ClearUndo(Args("objectPath", a.name));
                Require(!(bool)History().Single(r => (string)r["agentId"] == "a")["undoable"], "Partially cleared group still claims to represent the original action");
                var refused = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a", "force", true));
                Require(refused.ContainsKey("error") && a != null && b != null, "Cleared group was trusted");
                MCPUndoCommands.UndoLast(Args("agentId", "b"));
                Require(a != null && b == null, "Clearing one object invalidated an independent agent's group");
                return refused;
            });
            Check("Scene replacement does not make stale history match new native groups", () => {
                Create("a", "OldScene");
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                var b = Create("b", "NewScene");
                var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("agentId", "a", "force", true));
                Require(response.ContainsKey("error") && b != null, "Scene replacement reused stale Undo identity");
                MCPUndoCommands.UndoLast(Args("agentId", "b"));
                Require(b == null, "Current scene's native group was not usable");
                return response;
            });
            Check("Unavailable native Undo inspection refuses targeted rollback", () => {
                var go = Create("a", "Unavailable");
                var type = typeof(MCPUndoCommands).Assembly.GetType("UnityMCP.Editor.MCPUndoState");
                var field = type.GetField("GetRecords", BindingFlags.NonPublic | BindingFlags.Static);
                var saved = field.GetValue(null);
                try {
                    field.SetValue(null, new Action<List<string>, List<string>>((u, r) => { throw new InvalidOperationException("Controlled native lookup failure"); }));
                    var response = (Dictionary<string, object>)MCPUndoCommands.UndoLast(Args("force", true));
                    Require(response.ContainsKey("error") && go != null, "Unavailable inspection allowed rollback");
                    Require(!(bool)History().Single()["undoable"], "Unavailable inspection claimed eligibility");
                    return response;
                } finally { field.SetValue(null, saved); }
            });
        } finally {
            Clean(); MCPSettingsManager.ActionHistoryPersistence = persistence;
            if (history != null) File.WriteAllBytes(historyPath, history);
        }
        bool passed = Checks.All(c => (bool)c.GetType().GetProperty("passed").GetValue(c));
        File.WriteAllText("Library/UnityMcpUndoValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks, snapshots = Snapshots }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
