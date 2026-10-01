using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace UnityMCP.Editor
{
    internal static class MCPUndoState
    {
        internal sealed class Group
        {
            internal int Id;
            internal string Name;
            internal int Operations;
            internal string Signature;
            internal readonly List<string> Names = new List<string>();
            internal Dictionary<string, object> Describe() => new Dictionary<string, object>
            { { "undoGroup", Id }, { "name", Name }, { "operations", Operations } };
        }

        internal sealed class Snapshot
        {
            internal bool Available;
            internal readonly Dictionary<int, Group> Groups = new Dictionary<int, Group>();
            internal string Revision;

            internal string State(MCPActionRecord record)
            {
                if (record.UndoGroup < 0 || record.Status != "Completed") return "unavailable";
                if (!Available || record.UndoSessionId != SessionId || string.IsNullOrEmpty(record.UndoSignature)) return "unverified";
                if (!Groups.TryGetValue(record.UndoGroup, out var group)) return "not_on_stack";
                return group.Signature == record.UndoSignature ? "available" : "changed";
            }
        }

        private static readonly Action<List<string>, List<string>> GetRecords;
        private static readonly Func<int, int> GetGroup;
        private static readonly List<string> Undos = new List<string>();
        private static readonly List<string> Redos = new List<string>();
        internal static readonly string SessionId;

        static MCPUndoState()
        {
            const string key = "UnityMCP.UndoSessionId";
            SessionId = SessionState.GetString(key, "");
            if (string.IsNullOrEmpty(SessionId))
            {
                SessionId = Guid.NewGuid().ToString("N");
                SessionState.SetString(key, SessionId);
            }
            try
            {
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var records = typeof(Undo).GetMethod("GetRecords", flags, null, new[] { typeof(List<string>), typeof(List<string>) }, null);
                var group = typeof(Undo).GetMethod("GetGroupFromStack", flags, null, new[] { typeof(int) }, null);
                if (records != null && group != null)
                {
                    GetRecords = (Action<List<string>, List<string>>)Delegate.CreateDelegate(typeof(Action<List<string>, List<string>>), records);
                    GetGroup = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), group);
                }
            }
            catch { }
        }

        private static string Signature(List<string> names, int start, int end)
        {
            var text = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                string name = names[i] ?? "";
                text.Append(name.Length).Append(':').Append(name).Append(';');
            }
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        internal static bool TryGetLatestGroup(out int group, out string signature)
        {
            group = -1;
            signature = null;
            try
            {
                if (GetRecords == null || GetGroup == null) return false;
                Undos.Clear(); Redos.Clear(); GetRecords(Undos, Redos);
                if (Undos.Count > 0)
                {
                    group = GetGroup(Undos.Count - 1);
                    int start = Undos.Count - 1;
                    while (start > 0 && GetGroup(start - 1) == group) start--;
                    signature = Signature(Undos, start, Undos.Count);
                }
                return true;
            }
            catch { return false; }
            finally { Undos.Clear(); Redos.Clear(); }
        }

        internal static Snapshot Read()
        {
            var snapshot = new Snapshot();
            try
            {
                if (GetRecords == null || GetGroup == null) return snapshot;
                Undo.FlushUndoRecordObjects();
                Undos.Clear(); Redos.Clear(); GetRecords(Undos, Redos);
                var revision = new StringBuilder();
                for (int i = 0; i < Undos.Count; i++)
                {
                    int id = GetGroup(i);
                    if (id < 0) return new Snapshot();
                    string name = Undos[i] ?? "";
                    if (!snapshot.Groups.TryGetValue(id, out var group))
                        snapshot.Groups[id] = group = new Group { Id = id, Name = name };
                    group.Operations++;
                    group.Names.Add(name);
                    revision.Append(id).Append(':').Append(name.Length).Append(':').Append(name).Append(';');
                }
                foreach (var group in snapshot.Groups.Values)
                {
                    group.Signature = Signature(group.Names, 0, group.Names.Count);
                    group.Names.Clear();
                }
                snapshot.Revision = revision.ToString();
                snapshot.Available = true;
            }
            catch { return new Snapshot(); }
            finally { Undos.Clear(); Redos.Clear(); }
            return snapshot;
        }
    }
}
