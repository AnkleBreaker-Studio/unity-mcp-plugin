using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpHistoryPersistenceValidation
{
    private const string PathName = "Library/MCPActionHistory.json";
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<object> Checks = new List<object>();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Invoke(string name) => typeof(MCPActionHistory).GetMethod(name, Hidden).Invoke(null, null);
    private static long NextId => (long)typeof(MCPActionHistory).GetField("_nextId", Hidden).GetValue(null);
    private static Dictionary<string, object> Info => (Dictionary<string, object>)typeof(MCPActionHistory).GetMethod("GetPersistenceInfo", Hidden).Invoke(null, null);
    [Serializable] private class LegacyFile { public LegacyEntry[] records; }
    [Serializable] private class LegacyEntry
    {
        public long id, executionTimeMs;
        public string timestamp, agentId, actionName, category, status, errorMessage, targetInstanceId, targetPath, targetType, undoSessionId, undoSignature;
        public bool commandFailed;
        public int undoGroup;
    }
    private static MCPActionRecord Record(string agent = "persistence-fixture")
    {
        var record = new MCPActionRecord { Timestamp = DateTime.UtcNow, AgentId = agent, ActionName = "editor/state", Category = "editor", Status = "Completed", UndoGroup = -1 };
        MCPActionHistory.RecordAction(record); return record;
    }
    private static Dictionary<string, object> Entry(long id, object target = null) => new Dictionary<string, object>
    {
        { "id", id }, { "timestamp", "2026-10-01T10:00:00.0000000Z" }, { "agentId", "stored" },
        { "actionName", "gameobject/create" }, { "category", "gameobject" }, { "status", "Completed" },
        { "executionTimeMs", 23L }, { "targetInstanceId", target ?? "9223372036854775806" },
        { "targetPath", "Root/Legacy" }, { "targetType", "GameObject" }, { "undoGroup", 17 },
        { "undoSessionId", "stored-session" }, { "undoSignature", "stored-signature" },
        { "commandFailed", true }, { "errorMessage", "kept error" },
    };
    private static void Write(params object[] entries) => File.WriteAllText(PathName, MiniJson.Serialize(new Dictionary<string, object> { { "records", entries } }));
    private static void Check(string name, Func<object> run)
    {
        MCPSettingsManager.ActionHistoryMaxEntries = 500; MCPActionHistory.Clear();
        try { Checks.Add(new { name, passed = true, evidence = run() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { MCPSettingsManager.ActionHistoryMaxEntries = 500; MCPActionHistory.Clear(); }
    }
    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable project");
        int oldLimit = MCPSettingsManager.ActionHistoryMaxEntries;
        bool oldPersistence = MCPSettingsManager.ActionHistoryPersistence;
        byte[] oldFile = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
        MCPSettingsManager.ActionHistoryPersistence = false;
        try
        {
            Check("Restoration keeps only the configured newest records", () => {
                Write(Enumerable.Range(1, 1000).Select(i => (object)Entry(i)).ToArray());
                MCPSettingsManager.ActionHistoryMaxEntries = 8; Invoke("LoadFromDisk");
                var records = MCPActionHistory.GetAll(); Require(records.Count == 8 && records[0].Id == 993 && records[7].Id == 1000, "Restored " + records.Count + " entries instead of the newest eight");
                return new { stored = 1000, retained = records.Count, firstId = records[0].Id, lastId = records[7].Id };
            });
            Check("Zero retention stays empty through load and append", () => {
                MCPSettingsManager.ActionHistoryMaxEntries = 0; Write(Entry(2000)); Invoke("LoadFromDisk");
                Require(MCPActionHistory.Count == 0, "Load ignored zero retention"); var record = Record();
                Require(MCPActionHistory.Count == 0 && record.Id > 2000, "Append lost zero retention or reused an ID"); return true;
            });
            Check("Invalid negative preferences cannot fail record insertion", () => {
                MCPSettingsManager.ActionHistoryMaxEntries = -1; Record();
                Require(MCPSettingsManager.ActionHistoryMaxEntries >= 0, "Negative retention remained effective");
                return new { effectiveLimit = MCPSettingsManager.ActionHistoryMaxEntries, count = MCPActionHistory.Count };
            });
            Check("Legacy numeric object identities remain exact strings", () => {
                Write(Entry(3001, -2147483648L), Entry(3002, 9223372036854775806L)); Invoke("LoadFromDisk");
                var records = MCPActionHistory.GetAll(); Require(records.Count == 2, "Legacy entries were lost");
                Require(records[0].TargetInstanceId == "-2147483648" && records[1].TargetInstanceId == "9223372036854775806", "Numeric target identity was lost or rounded");
                return new { targets = records.Select(r => r.TargetInstanceId).ToArray() };
            });
            Check("An invalid entry cannot partially replace current history", () => {
                var existing = Record("keep-existing"); Write(Entry(4000), null, Entry(4002));
                string before = File.ReadAllText(PathName); Invoke("LoadFromDisk"); var records = MCPActionHistory.GetAll();
                Require(records.Count == 1 && ReferenceEquals(records[0], existing), "Invalid saved history replaced existing records");
                Require(File.ReadAllText(PathName) == before, "Load modified the source file"); return true;
            });
            Check("Restoration never reuses an ID from an earlier stored record", () => {
                long before = NextId; Write(Entry(before + 10), Entry(before + 3)); Invoke("LoadFromDisk");
                var next = Record(); Require(next.Id > before + 10, "ID counter followed the last entry instead of its high-water mark"); return new { before, next = next.Id };
            });
            Check("Failed restoration preserves its source through the next save hook", () => {
                Record(); const string damaged = "{\"records\":[{\"id\":77,"; File.WriteAllText(PathName, damaged);
                Invoke("LoadFromDisk"); Invoke("SaveToDisk"); Require(File.ReadAllText(PathName) == damaged, "Automatic save overwrote failed-load evidence");
                return true;
            });
            Check("Normal fields and old files without new Undo metadata remain readable", () => {
                var legacy = Entry(6000); legacy.Remove("commandFailed"); legacy.Remove("undoSessionId"); legacy.Remove("undoSignature");
                Write(Entry(5999), legacy); Invoke("LoadFromDisk"); var records = MCPActionHistory.GetAll();
                Require(records.Count == 2 && records[0].CommandFailed && records[0].UndoGroup == 17 && records[0].UndoSessionId == "stored-session" && records[0].UndoSignature == "stored-signature", "Current error or Undo metadata changed");
                Require(!records[1].CommandFailed && string.IsNullOrEmpty(records[1].UndoSessionId) && string.IsNullOrEmpty(records[1].UndoSignature), "Legacy missing fields were invented");
                Invoke("SaveToDisk"); string saved = File.ReadAllText(PathName); MCPActionHistory.Clear(); File.WriteAllText(PathName, saved); Invoke("LoadFromDisk");
                Require(MCPActionHistory.Count == 2 && MCPActionHistory.GetAll()[0].TargetInstanceId == "9223372036854775806", "Persistence round trip lost identity"); return true;
            });
            Check("Files above 32 MiB preserve memory and block replacement", () => {
                var existing = Record(); using (var stream = File.Create(PathName)) stream.SetLength(32L * 1024 * 1024 + 1);
                Invoke("LoadFromDisk"); Invoke("SaveToDisk");
                Require(ReferenceEquals(MCPActionHistory.GetAll().Single(), existing), "Oversize file replaced memory");
                Require(new FileInfo(PathName).Length == 32L * 1024 * 1024 + 1 && (bool)Info["saveBlocked"], "Oversize evidence was overwritten"); return Info;
            });
            Check("Output above the byte budget preserves the previous snapshot", () => {
                Record(); Invoke("SaveToDisk"); string before = File.ReadAllText(PathName);
                Record(new string('x', 32 * 1024 * 1024)); Invoke("SaveToDisk");
                Require(File.ReadAllText(PathName) == before && (long)Info["saveFailures"] > 0, "Oversize output damaged the snapshot");
                Require(Directory.GetFiles("Library", "MCPActionHistory.json.*.tmp").Length == 0, "Oversize save left a temporary file"); return Info;
            });
            Check("A locked destination preserves old bytes and cleans its temporary file", () => {
                Record(); Invoke("SaveToDisk"); string before = File.ReadAllText(PathName); Record("newer"); long failures = (long)Info["saveFailures"];
                using (var locked = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read)) Invoke("SaveToDisk");
                Require(File.ReadAllText(PathName) == before && (long)Info["saveFailures"] == failures + 1, "Failed replacement did not preserve the old file");
                Require(Directory.GetFiles("Library", "MCPActionHistory.json.*.tmp").Length == 0, "Failed replacement left a temporary file");
                Invoke("SaveToDisk"); Require(File.ReadAllText(PathName) != before && (string)Info["warning"] == "", "Save did not recover after unlocking"); return true;
            });
            Check("Malformed syntax and invalid field types never replace memory", () => {
                string[] invalid = { "{\"records\":[] } trailing", "{\"records\":{}}", "{\"records\":[{\"id\":true}]}", "{\"records\":[{\"undoGroup\":2147483648}]}", "{\"records\":[{\"commandFailed\":\"false\"}]}", "{\"records\":[{\"targetInstanceId\":1.25}]}", "{\"records\":[{\"id\":9223372036854775807}]}" };
                var existing = Record();
                foreach (string source in invalid) { File.WriteAllText(PathName, source); Invoke("LoadFromDisk"); Invoke("SaveToDisk"); Require(ReferenceEquals(MCPActionHistory.GetAll().Single(), existing) && File.ReadAllText(PathName) == source, "Invalid input was accepted: " + source); }
                return new { rejected = invalid.Length };
            });
            Check("Successful repair and explicit clear both resume saving", () => {
                File.WriteAllText(PathName, "broken"); Invoke("LoadFromDisk"); Require((bool)Info["saveBlocked"], "Failure was hidden");
                Write(Entry(7000)); Invoke("LoadFromDisk"); Require(!(bool)Info["saveBlocked"] && (string)Info["warning"] == "", "Valid repair stayed blocked");
                Record(); Invoke("SaveToDisk"); Require(MCPActionHistory.Count == 2, "Repaired history was not retained");
                File.WriteAllText(PathName, "broken"); Invoke("LoadFromDisk"); MCPActionHistory.Clear(); Record(); Invoke("SaveToDisk");
                Require(!(bool)Info["saveBlocked"] && JsonUtility.FromJson<LegacyFile>(File.ReadAllText(PathName)).records.Length == 1, "Explicit clear did not recover"); return true;
            });
            Check("Saved JSON remains readable by the previous JsonUtility schema", () => {
                var entry = Entry(8000); entry["agentId"] = "François/雪/🚀"; Write(entry); Invoke("LoadFromDisk");
                CultureInfo previous = CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Invoke("SaveToDisk"); }
                finally { CultureInfo.CurrentCulture = previous; }
                var old = JsonUtility.FromJson<LegacyFile>(File.ReadAllText(PathName)).records.Single();
                Require(old.id == 8000 && old.agentId == (string)entry["agentId"] && old.targetInstanceId == "9223372036854775806" && old.undoGroup == 17 && old.commandFailed && old.undoSignature == "stored-signature" && old.executionTimeMs == 23, "Previous schema cannot read the new snapshot");
                Require(DateTime.Parse(old.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Kind == DateTimeKind.Utc, "UTC timestamp lost its kind"); return true;
            });
            Check("UTF-8 and UTF-16 BOM snapshots retain Unicode", () => {
                foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode }) {
                    var entry = Entry(8100); entry["agentId"] = "François/雪/🚀";
                    File.WriteAllText(PathName, MiniJson.Serialize(new Dictionary<string, object> { { "records", new[] { entry } } }), encoding);
                    Invoke("LoadFromDisk"); Require(MCPActionHistory.GetAll().Single().AgentId == (string)entry["agentId"], "BOM text changed");
                } return true;
            });
            Check("Missing legacy Undo groups remain unavailable", () => {
                var entry = Entry(8200); entry.Remove("undoGroup"); Write(entry); Invoke("LoadFromDisk");
                Require(MCPActionHistory.GetAll().Single().UndoGroup == -1, "Missing Undo group became executable"); return true;
            });
            Check("Lowering the cap before a save retains only newest records", () => {
                for (int i = 0; i < 20; i++) Record("record-" + i);
                MCPSettingsManager.ActionHistoryMaxEntries = 3; Invoke("SaveToDisk");
                var saved = JsonUtility.FromJson<LegacyFile>(File.ReadAllText(PathName)).records;
                Require(saved.Length == 3 && MCPActionHistory.Count == 3 && saved[0].agentId == "record-17" && saved[2].agentId == "record-19", "Save ignored the reduced cap"); return true;
            });
            Check("A save cannot publish more values than the loader accepts", () => {
                Record(); Invoke("SaveToDisk"); string before = File.ReadAllText(PathName);
                MCPSettingsManager.ActionHistoryMaxEntries = 40000;
                for (int i = 0; i < 32259; i++) Record(); Invoke("SaveToDisk");
                Require(File.ReadAllText(PathName) == before && ((string)Info["warning"]).Contains("value limit"), "Save published an unreadable value count"); return Info;
            });
        }
        finally
        {
            MCPActionHistory.Clear(); MCPSettingsManager.ActionHistoryMaxEntries = oldLimit; MCPSettingsManager.ActionHistoryPersistence = oldPersistence;
            if (oldFile != null) File.WriteAllBytes(PathName, oldFile); else if (File.Exists(PathName)) File.Delete(PathName);
        }
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpHistoryPersistenceValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
