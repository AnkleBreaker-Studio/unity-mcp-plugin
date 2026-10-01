using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static partial class MCPActionHistory
    {
        private const int MaxPersistenceBytes = 32 * 1024 * 1024;
        private static bool _persistenceBlocked;
        private static string _persistenceWarning;
        private static long _persistenceLoadFailures, _persistenceSaveFailures, _lastPersistenceBytes;
        private static int _loadedRecords, _discardedOnLoad;

        private static void ResetPersistenceWarning()
        {
            lock (_lock) { _persistenceBlocked = false; _persistenceWarning = null; }
        }

        private static void PersistenceFailure(string operation, Exception error, bool blockSave)
        {
            string message = "History " + operation + " failed: " + error.GetBaseException().Message;
            if (message.Length > 2048) message = message.Substring(0, 2048);
            lock (_lock)
            {
                _persistenceWarning = message;
                if (blockSave) _persistenceBlocked = true;
                if (operation == "load") _persistenceLoadFailures++;
                if (operation == "save") _persistenceSaveFailures++;
            }
            Debug.LogWarning("[MCP History] " + message);
        }

        internal static Dictionary<string, object> GetPersistenceInfo()
        {
            lock (_lock) return new Dictionary<string, object>
            {
                { "maxFileBytes", MaxPersistenceBytes }, { "saveBlocked", _persistenceBlocked },
                { "warning", _persistenceWarning ?? "" }, { "loadedRecords", _loadedRecords },
                { "discardedOnLoad", _discardedOnLoad }, { "lastFileBytes", _lastPersistenceBytes },
                { "loadFailures", _persistenceLoadFailures }, { "saveFailures", _persistenceSaveFailures },
            };
        }

        private static void SaveToDisk()
        {
            string temporary = null;
            try
            {
                int limit = MCPSettingsManager.ActionHistoryMaxEntries;
                List<MCPActionRecord> snapshot;
                lock (_lock)
                {
                    // Keep a failed-load source available for recovery until a successful load or explicit clear.
                    if (_persistenceBlocked) return;
                    if (_history.Count > limit) { _history.RemoveRange(0, _history.Count - limit); _revision++; }
                    snapshot = new List<MCPActionRecord>(_history);
                }
                var entries = new List<object>(snapshot.Count);
                long values = 3;
                foreach (var record in snapshot)
                {
                    var entry = ToPersistenceEntry(record);
                    values += 1 + 2 * entry.Count;
                    if (values > MCPRequestInput.MaxValues) throw new InvalidDataException("History exceeds the JSON value limit; reduce retained entries before saving.");
                    entries.Add(entry);
                }
                string json = MiniJson.Serialize(new Dictionary<string, object> { { "records", entries } }, MaxPersistenceBytes);
                temporary = PersistencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
                // Publish only a complete same-directory file; never delete the previous snapshot as a fallback.
                if (File.Exists(PersistencePath)) File.Replace(temporary, PersistencePath, null);
                else File.Move(temporary, PersistencePath);
                lock (_lock) { _persistenceWarning = null; _lastPersistenceBytes = new FileInfo(PersistencePath).Length; }
            }
            catch (Exception error) { PersistenceFailure("save", error, false); }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                    try { File.Delete(temporary); }
                    catch (Exception error) { PersistenceFailure("temporary-file cleanup", error, false); }
            }
        }

        private static Dictionary<string, object> ToPersistenceEntry(MCPActionRecord record) => new Dictionary<string, object>
        {
            { "id", record.Id }, { "timestamp", record.Timestamp.ToString("O", CultureInfo.InvariantCulture) },
            { "agentId", record.AgentId ?? "" }, { "actionName", record.ActionName ?? "" },
            { "category", record.Category ?? "" }, { "status", record.Status ?? "" },
            { "commandFailed", record.CommandFailed }, { "executionTimeMs", record.ExecutionTimeMs },
            { "errorMessage", record.ErrorMessage ?? "" }, { "targetInstanceId", record.TargetInstanceId ?? "" },
            { "targetPath", record.TargetPath ?? "" }, { "targetType", record.TargetType ?? "" },
            { "undoGroup", record.UndoGroup }, { "undoSessionId", record.UndoSessionId ?? "" },
            { "undoSignature", record.UndoSignature ?? "" },
        };

        private static void LoadFromDisk()
        {
            if (!File.Exists(PersistencePath)) return;
            try
            {
                string json;
                long fileBytes;
                using (var stream = new FileStream(PersistencePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    fileBytes = stream.Length;
                    if (fileBytes > MaxPersistenceBytes) throw new InvalidDataException("History file exceeds the 32 MiB limit; preserve it separately before clearing history.");
                    // Read one bounded snapshot without charging disk traffic to HTTP diagnostics.
                    var bytes = new byte[(int)fileBytes];
                    int received = 0, count;
                    while (received < bytes.Length && (count = stream.Read(bytes, received, bytes.Length - received)) > 0) received += count;
                    if (received != bytes.Length || stream.ReadByte() != -1) throw new IOException("History file changed while it was being read.");
                    using (var reader = new StreamReader(new MemoryStream(bytes, false), new UTF8Encoding(false, true), true)) json = reader.ReadToEnd();
                }
                var data = MCPRequestInput.ParseObject(json);
                if (!data.TryGetValue("records", out var value) || !(value is List<object> entries))
                    throw new InvalidDataException("History must contain a records array.");
                int limit = MCPSettingsManager.ActionHistoryMaxEntries;
                int start = Math.Max(0, entries.Count - limit);
                var restored = new List<MCPActionRecord>(entries.Count - start);
                long highestId = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!(entries[i] is Dictionary<string, object> entry)) throw new InvalidDataException("History record " + i + " must be an object.");
                    var record = FromPersistenceEntry(entry);
                    if (record.Id == long.MaxValue) throw new InvalidDataException("History record ID leaves no room for a new action.");
                    highestId = Math.Max(highestId, record.Id);
                    if (i >= start) restored.Add(record);
                }
                ClearNotifications();
                lock (_lock)
                {
                    _history.Clear(); _history.AddRange(restored);
                    _nextId = Math.Max(_nextId, highestId); _revision++;
                    _loadedRecords = restored.Count; _discardedOnLoad = start; _lastPersistenceBytes = fileBytes;
                    _persistenceBlocked = false; _persistenceWarning = null;
                }
                Debug.Log("[MCP History] Loaded " + restored.Count + " records from disk (" + start + " older records outside the configured limit).");
            }
            catch (Exception error) { PersistenceFailure("load", error, true); }
        }

        private static MCPActionRecord FromPersistenceEntry(Dictionary<string, object> entry)
        {
            DateTime.TryParse(PersistenceText(entry, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp);
            return new MCPActionRecord
            {
                Id = PersistenceInteger(entry, "id"), Timestamp = timestamp,
                AgentId = PersistenceText(entry, "agentId"), ActionName = PersistenceText(entry, "actionName"),
                Category = PersistenceText(entry, "category"), Status = PersistenceText(entry, "status"),
                CommandFailed = PersistenceBoolean(entry, "commandFailed"), ExecutionTimeMs = PersistenceInteger(entry, "executionTimeMs"),
                ErrorMessage = PersistenceText(entry, "errorMessage"), TargetInstanceId = PersistenceText(entry, "targetInstanceId", true),
                TargetPath = PersistenceText(entry, "targetPath"), TargetType = PersistenceText(entry, "targetType"),
                UndoGroup = checked((int)PersistenceInteger(entry, "undoGroup", -1)),
                UndoSessionId = PersistenceText(entry, "undoSessionId"), UndoSignature = PersistenceText(entry, "undoSignature"),
            };
        }

        private static string PersistenceText(Dictionary<string, object> entry, string key, bool legacyInteger = false)
        {
            if (!entry.TryGetValue(key, out var value) || value == null) return null;
            if (value is string text) return text;
            if (legacyInteger && (value is long || value is int)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            throw new InvalidDataException("History field '" + key + "' must be text.");
        }

        private static long PersistenceInteger(Dictionary<string, object> entry, string key, long fallback = 0)
        {
            if (!entry.TryGetValue(key, out var value) || value == null) return fallback;
            if (value is long number) return number;
            if (value is int integer) return integer;
            if (value is double real && real == Math.Truncate(real)) return checked((long)real);
            throw new InvalidDataException("History field '" + key + "' must be an integer.");
        }

        private static bool PersistenceBoolean(Dictionary<string, object> entry, string key)
        {
            if (!entry.TryGetValue(key, out var value) || value == null) return false;
            if (value is bool boolean) return boolean;
            throw new InvalidDataException("History field '" + key + "' must be a boolean.");
        }
    }
}
