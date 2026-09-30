using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Ticket-based request queue for multi-agent parallel access to Unity Editor.
    /// Implements fair round-robin scheduling across agents, read batching,
    /// and supports both async (ticket polling) and sync (blocking) modes.
    ///
    /// Architecture:
    ///   HTTP request → SubmitRequest (returns ticket immediately)
    ///                → ProcessNextRequests (called from EditorApplication.update on main thread)
    ///                → Agent polls GetTicketStatus for result
    ///
    /// Thread safety:
    ///   - _queueLock protects all queue/ticket/session state
    ///   - Actions execute OUTSIDE the lock on the main thread (prevents deadlocks)
    ///   - Synchronous callers use ManualResetEventSlim per ticket
    /// </summary>
    public static class MCPRequestQueue
    {
        // ═══════════════════════════════════════════════════════
        //  Ticket
        // ═══════════════════════════════════════════════════════

        public enum RequestStatus { Queued, Executing, Completed, Failed, TimedOut }

        public const int ProtocolVersion = 3;
        public const int RetryWindowMs = 120_000;
        public static readonly string SessionId = Guid.NewGuid().ToString("N");
        private static readonly long _sessionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        public static long SessionTimeMs => (long)((System.Diagnostics.Stopwatch.GetTimestamp() - _sessionStarted)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        public class SubmissionResult
        {
            public RequestTicket Ticket;
            public int StatusCode = 202;
            public string Error;
            public string Code;
        }

        private class SubmissionRecord
        {
            public long TicketId;
            public long ExpiresAtMs;
            public string Fingerprint;
        }

        private const int MaxSubmissionRecords = 10_000;
        private static readonly Dictionary<string, SubmissionRecord> _submissions = new Dictionary<string, SubmissionRecord>();

        public class RequestTicket
        {
            public long   TicketId    { get; set; }
            public string AgentId     { get; set; }
            public string ActionName  { get; set; }
            public RequestStatus Status { get; set; }

            // The actual work to execute on the main thread (sync)
            internal Func<object> Action { get; set; }

            // Deferred work whose result arrives via callback (async Unity APIs)
            internal Action<Action<object>> DeferredAction { get; set; }

            // Result / error
            public object Result       { get; set; }
            public string ErrorMessage { get; set; }

            // Timing
            public DateTime  SubmittedAt   { get; set; }
            public DateTime? CompletedAt   { get; set; }
            public DateTime? StartedAt     { get; internal set; }
            public int       QueuePosition { get; set; }

            internal long SubmittedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            internal long? StartedTimestamp;
            internal long? CompletedTimestamp;

            public double QueueWaitMs => ElapsedMs(SubmittedTimestamp,
                StartedTimestamp ?? CompletedTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp());
            public double ProcessingTimeMs => StartedTimestamp.HasValue
                ? ElapsedMs(StartedTimestamp.Value, CompletedTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp()) : 0;

            private static double ElapsedMs(long from, long to) =>
                (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            public long ExecutionTimeMs =>
                CompletedAt.HasValue
                    ? (long)(CompletedAt.Value - SubmittedAt).TotalMilliseconds
                    : -1;
        }

        // ═══════════════════════════════════════════════════════
        //  State
        // ═══════════════════════════════════════════════════════

        // Ticket ID generator (thread-safe via Interlocked)
        private static long _nextTicketId;

        // Per-agent FIFO queues for fair round-robin
        private static readonly Dictionary<string, Queue<RequestTicket>> _agentQueues
            = new Dictionary<string, Queue<RequestTicket>>();

        // Stable round-robin order + index
        private static readonly List<string> _rrOrder = new List<string>();
        private static int _rrIndex;

        // Completed/failed tickets cached for polling
        private static readonly Dictionary<long, RequestTicket> _completedTickets
            = new Dictionary<long, RequestTicket>();

        // In-flight tickets (dequeued, currently executing on main thread)
        // Prevents 404 race condition when polling during slow executions (e.g. execute_code)
        private static readonly Dictionary<long, RequestTicket> _executingTickets
            = new Dictionary<long, RequestTicket>();

        // Polling must not scan every agent's FIFO while the editor is busy.
        private static readonly Dictionary<long, RequestTicket> _pendingTickets
            = new Dictionary<long, RequestTicket>();

        // Synchronous waiters (backward compat)
        private static readonly Dictionary<long, ManualResetEventSlim> _waiters
            = new Dictionary<long, ManualResetEventSlim>();

        // Session tracking
        private static Dictionary<string, MCPAgentSession> _sessions
            = new Dictionary<string, MCPAgentSession>();
        private static int _sessionHighWater;
        private const int SessionRetentionSeconds = 1800;
        private const int MaxInactiveSessions = 256;
        private static long _evictedSessions;
        private static readonly List<MCPAgentSession> _inactiveSessions = new List<MCPAgentSession>();
        private static readonly Comparison<MCPAgentSession> _oldestSessionFirst =
            (left, right) => left.LastActivityTimestamp.CompareTo(right.LastActivityTimestamp);

        // Single lock for all mutable state
        private static readonly object _queueLock = new object();
        private static readonly List<long> _expiredTicketIds = new List<long>();
        private static readonly List<long> _staleExecutingIds = new List<long>();
        private static readonly List<string> _expiredSubmissionKeys = new List<string>();

        // Cleanup cadence
        private static int _frameTick;
        private const int CleanupEveryNFrames        = 100;
        private const int CompletedCacheLifetimeSec   = 60;
        private const int TimedOutCacheLifetimeSec    = 30;
        public const int SyncTimeoutMs                = 30_000;
        private const int MaxReadBatchSize            = 5;

        // ═══════════════════════════════════════════════════════
        //  Public API — Submit
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Submit a request to the queue. Returns a ticket immediately (non-blocking).
        /// The action will be executed on the main thread when its turn comes.
        /// </summary>
        public static RequestTicket SubmitRequest(string agentId, string actionName, Func<object> action)
        {
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";

            var ticket = new RequestTicket
            {
                TicketId    = Interlocked.Increment(ref _nextTicketId),
                AgentId     = agentId,
                ActionName  = actionName,
                Status      = RequestStatus.Queued,
                SubmittedAt = DateTime.UtcNow,
                Action      = action,
            };

            lock (_queueLock)
            {
                // Ensure agent queue exists
                if (!_agentQueues.ContainsKey(agentId))
                {
                    _agentQueues[agentId] = new Queue<RequestTicket>();
                    _rrOrder.Add(agentId);
                }

                ticket.QueuePosition = _agentQueues[agentId].Count;
                _agentQueues[agentId].Enqueue(ticket);
                _pendingTickets[ticket.TicketId] = ticket;

                // Session bookkeeping
                EnsureSession(agentId).LogAction(actionName);
                _sessions[agentId].IncrementQueuedRequest();
            }

            return ticket;
        }

        /// <summary>
        /// Submit a deferred request whose result arrives via callback.
        /// Use for Unity APIs with async callbacks (e.g. TestRunnerApi.RetrieveTestList).
        /// </summary>
        public static RequestTicket SubmitDeferredRequest(string agentId, string actionName,
            Action<Action<object>> deferredAction)
        {
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";

            var ticket = new RequestTicket
            {
                TicketId       = Interlocked.Increment(ref _nextTicketId),
                AgentId        = agentId,
                ActionName     = actionName,
                Status         = RequestStatus.Queued,
                SubmittedAt    = DateTime.UtcNow,
                DeferredAction = deferredAction,
            };

            lock (_queueLock)
            {
                if (!_agentQueues.ContainsKey(agentId))
                {
                    _agentQueues[agentId] = new Queue<RequestTicket>();
                    _rrOrder.Add(agentId);
                }

                ticket.QueuePosition = _agentQueues[agentId].Count;
                _agentQueues[agentId].Enqueue(ticket);
                _pendingTickets[ticket.TicketId] = ticket;

                EnsureSession(agentId).LogAction(actionName);
                _sessions[agentId].IncrementQueuedRequest();
            }

            return ticket;
        }

        /// <summary>
        /// Backward-compatible synchronous mode: submit → wait → return result.
        /// Used by the existing HandleRequest path (direct HTTP calls).
        /// </summary>
        public static object ExecuteWithTracking(string agentId, string actionName, Func<object> action)
        {
            var waiter = new ManualResetEventSlim(false);
            RequestTicket ticket;
            lock (_queueLock)
            {
                // Register before the main thread can complete and signal this ticket.
                ticket = SubmitRequest(agentId, actionName, action);
                _waiters[ticket.TicketId] = waiter;
            }

            try
            {
                bool signaled = waiter.Wait(SyncTimeoutMs);
                lock (_queueLock)
                {
                    if (!signaled)
                    {
                        string detail = ticket.StartedTimestamp.HasValue
                            ? "the operation may already have made changes"
                            : "the operation was removed before execution";
                        TryCompleteTicket(ticket, RequestStatus.TimedOut, null,
                            $"Timed out after {SyncTimeoutMs / 1000}s; {detail}");
                    }
                    if (ticket.Status == RequestStatus.Failed || ticket.Status == RequestStatus.TimedOut)
                        return new Dictionary<string, object>
                        {
                            { "error", ticket.ErrorMessage },
                            { "ticketId", ticket.TicketId },
                        };
                    return ticket.Result;
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _waiters.Remove(ticket.TicketId);
                    waiter.Dispose();
                }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Main-Thread Processing (called from EditorApplication.update)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Dequeue and execute requests on the main thread.
        /// Processes 1 write OR up to 5 reads per call (batching reads).
        /// Also runs periodic cleanup.
        /// </summary>
        public static void ProcessNextRequests()
        {
            // --- Cleanup cadence ---
            if (++_frameTick >= CleanupEveryNFrames)
            {
                _frameTick = 0;
                RunCleanup();
            }

            // --- Dequeue ---
            List<RequestTicket> batch;
            lock (_queueLock)
            {
                batch = DequeueNextBatch();
                if (batch == null || batch.Count == 0) return;
                foreach (var ticket in batch) _pendingTickets.Remove(ticket.TicketId);

                // Drop tickets whose sync waiter already gave up (TimedOut). The client was
                // told the call failed and may have retried; executing the abandoned ticket
                // now would run a non-idempotent action a second time. ExecuteWithTracking
                // sets TimedOut under this same lock, so this check is race-safe.
                batch.RemoveAll(t =>
                {
                    if (t.Status == RequestStatus.TimedOut)
                    {
                        _executingTickets.Remove(t.TicketId);
                        return true;
                    }
                    return false;
                });
                if (batch.Count == 0) return;

                // Mark all as executing and track in-flight
                foreach (var t in batch)
                {
                    t.Status = RequestStatus.Executing;
                    _executingTickets[t.TicketId] = t;
                }
            }

            // --- Execute OUTSIDE lock (main thread) ---
            foreach (var ticket in batch)
            {
                Func<object> action;
                Action<Action<object>> deferredAction;
                lock (_queueLock)
                {
                    // A synchronous waiter can expire while an earlier batched read runs.
                    if (ticket.Status != RequestStatus.Executing) continue;
                    ticket.StartedAt = DateTime.UtcNow;
                    ticket.StartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    action = ticket.Action;
                    deferredAction = ticket.DeferredAction;
                }
                // Give each WRITE action its own named, collapsed Undo group so it can be
                // reverted independently (per-action / per-agent undo via undo/last) and shows
                // up named in Unity's Undo history. Reads and undo/redo ops don't open a group,
                // so they never clutter the history or shift group indices out from under a
                // pending undo. GetCurrentGroup() alone is unreliable — many write ops (e.g.
                // RegisterCreatedObjectUndo) don't advance it — so we open the group explicitly.
                //
                // Deferred actions are EXCLUDED: their completion fires an arbitrary number of
                // frames later, so a CollapseUndoOperations then would fold ANY other agent's
                // interleaved group into this one (corrupting per-action undo bookkeeping). They
                // are already excluded from history recording below, so they need no group.
                bool opensUndoGroup =
                    deferredAction == null
                    && !IsReadOperation(ticket.ActionName)
                    && !(ticket.ActionName != null && ticket.ActionName.StartsWith("undo/"));
                int undoGroup = -1;
                int undoRecordsBefore = -1;
                if (opensUndoGroup)
                {
                    undoRecordsBefore = CountUndoRecords();
                    UnityEditor.Undo.IncrementCurrentGroup();
                    undoGroup = UnityEditor.Undo.GetCurrentGroup();
                    UnityEditor.Undo.SetCurrentGroupName(ticket.ActionName ?? "MCP Action");
                }

                // Deferred actions complete via callback on a future editor frame.
                if (deferredAction != null)
                {
                    try
                    {
                        deferredAction(value => TryCompleteTicket(ticket, RequestStatus.Completed, value, null));
                    }
                    catch (Exception ex)
                    {
                        TryCompleteTicket(ticket, RequestStatus.Failed, null, ex.Message);
                        Debug.LogError($"[Unity MCP Queue] Deferred ticket {ticket.TicketId} ({ticket.ActionName}) failed: {ex.Message}");
                    }
                    continue; // Skip normal completion — callback handles it
                }

                object result = null;
                string error = null;
                var status = RequestStatus.Completed;
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    status = RequestStatus.Failed;
                    error = ex.Message;
                    Debug.LogError($"[Unity MCP Queue] Ticket {ticket.TicketId} ({ticket.ActionName}) failed: {ex.Message}");
                }

                // Fold everything this action registered into its single named group so one
                // undo/last (or a native Ctrl+Z) reverts the whole action as one step.
                if (undoGroup >= 0)
                    UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                TryCompleteTicket(ticket, status, result, error);

                // An action is undoable only if it ACTUALLY registered an undo op. Reads that
                // slip past IsReadOperation, and execute-code that only inspects, open an empty
                // group we must not offer as an undo target (reverting it would be a confusing
                // no-op). Fail open: if the internal record-count API is unavailable, keep the
                // group (old behavior). We can only prove "empty" when both counts are valid.
                bool didRegisterUndo = undoGroup >= 0;
                if (didRegisterUndo && undoRecordsBefore >= 0)
                {
                    int undoRecordsAfter = CountUndoRecords();
                    if (undoRecordsAfter >= 0 && undoRecordsAfter <= undoRecordsBefore)
                        didRegisterUndo = false;
                }

                // Record action in history
                try
                {
                    var record = new MCPActionRecord
                    {
                        Timestamp       = ticket.CompletedAt ?? DateTime.UtcNow,
                        AgentId         = ticket.AgentId,
                        ActionName      = ticket.ActionName,
                        Category        = MCPActionRecord.ExtractCategory(ticket.ActionName),
                        Status          = ticket.Status.ToString(),
                        ExecutionTimeMs = ticket.ExecutionTimeMs,
                        ErrorMessage    = ticket.ErrorMessage,
                        // Only a completed write action that registered an undo op is undoable;
                        // its dedicated group is the revert target for undo/last. Reads, empty
                        // groups, undo-ops and failures stay -1. execute-code is excluded too:
                        // it's an introspection escape hatch whose temp-host churn registers undo
                        // but shouldn't shadow the agent's real edits as an undo/last target
                        // (its group still isolates that churn; native Ctrl+Z still reaches it).
                        UndoGroup       = (ticket.Status == RequestStatus.Completed && didRegisterUndo
                                            && ticket.ActionName != "editor/execute-code") ? undoGroup : -1,
                    };

                    // Try to extract target object info from result
                    if (ticket.Status == RequestStatus.Completed)
                        record.ExtractTargetFromResult(ticket.Result);

                    MCPActionHistory.RecordAction(record);

                    // Also log to the agent session's structured log
                    lock (_queueLock)
                    {
                        if (_sessions.TryGetValue(ticket.AgentId, out var agentSession))
                            agentSession.LogStructuredAction(record);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Unity MCP Queue] Failed to record action history: {ex.Message}");
                }

            }
        }

        // ═══════════════════════════════════════════════════════
        //  Query API
        // ═══════════════════════════════════════════════════════

        /// <summary>Returns ticket info for polling, or null if not found/expired.</summary>
        public static Dictionary<string, object> GetTicketStatus(long ticketId)
        {
            lock (_queueLock)
            {
                // Check completed cache first
                if (_completedTickets.TryGetValue(ticketId, out var done))
                    return TicketToDict(done);

                // Check in-flight (currently executing on main thread)
                if (_executingTickets.TryGetValue(ticketId, out var executing))
                    return TicketToDict(executing);

                if (_pendingTickets.TryGetValue(ticketId, out var pending))
                    return TicketToDict(pending);
            }
            return null;
        }

        /// <summary>Returns overall queue stats.</summary>
        public static Dictionary<string, object> GetQueueInfo()
        {
            lock (_queueLock)
            {
                int totalQueued = 0;
                var perAgent = new Dictionary<string, object>();

                foreach (var kvp in _agentQueues)
                {
                    int c = kvp.Value.Count;
                    perAgent[kvp.Key] = c;
                    totalQueued += c;
                }

                return new Dictionary<string, object>
                {
                    { "totalQueued",          totalQueued },
                    { "activeAgents",         _agentQueues.Count },
                    { "executingCount",       _executingTickets.Count },
                    { "completedCacheSize",   _completedTickets.Count },
                    { "perAgentQueued",        perAgent },
                    { "totalSessionsTracked", _sessions.Count },
                    { "evictedSessions", _evictedSessions },
                    { "sessionRetentionSeconds", SessionRetentionSeconds },
                    { "maxInactiveSessions", MaxInactiveSessions },
                    { "protocolVersion", ProtocolVersion },
                    { "queueSessionId", SessionId },
                    { "queueSessionTimeMs", SessionTimeMs },
                    { "queueRetryWindowMs", RetryWindowMs },
                    { "retryCacheSize", _submissions.Count },
                };
            }
        }

        public static List<Dictionary<string, object>> GetActiveSessions()
        {
            var list = new List<Dictionary<string, object>>();
            lock (_queueLock)
            {
                foreach (var s in _sessions.Values)
                    if (s.IsActive) list.Add(s.ToDict());
            }
            return list;
        }

        public static List<string> GetAgentLog(string agentId)
        {
            lock (_queueLock)
            {
                if (_sessions.TryGetValue(agentId, out var s))
                    return s.GetLog();
            }
            return new List<string>();
        }

        public static int TotalSessionCount
        {
            get { lock (_queueLock) return _sessions.Count; }
        }

        public static int ActiveSessionCount
        {
            get
            {
                int n = 0;
                lock (_queueLock)
                    foreach (var s in _sessions.Values)
                        if (s.IsActive) n++;
                return n;
            }
        }

        public static int TotalQueuedCount
        {
            get
            {
                int n = 0;
                lock (_queueLock)
                    foreach (var q in _agentQueues.Values) n += q.Count;
                return n;
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Internals
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Fair round-robin dequeue. Returns 1 write OR up to MaxReadBatchSize reads.
        /// Must be called under _queueLock.
        /// </summary>
        private static List<RequestTicket> DequeueNextBatch()
        {
            if (_rrOrder.Count == 0) return null;

            // Advance round-robin to find an agent with work
            int startIndex = _rrIndex;
            RequestTicket first = null;
            string firstAgent = null;

            for (int i = 0; i < _rrOrder.Count; i++)
            {
                int idx = (_rrIndex + i) % _rrOrder.Count;
                string agent = _rrOrder[idx];

                if (_agentQueues.TryGetValue(agent, out var q) && q.Count > 0)
                {
                    first = q.Peek();
                    firstAgent = agent;
                    _rrIndex = (idx + 1) % _rrOrder.Count;
                    break;
                }
            }

            if (first == null)
            {
                PurgeEmptyQueues();
                return null;
            }

            var batch = new List<RequestTicket>();

            if (!IsReadOperation(first.ActionName))
            {
                // Single write request
                _agentQueues[firstAgent].Dequeue();
                batch.Add(first);
            }
            else
            {
                // Batch reads across agents (round-robin)
                int collected = 0;
                int scanIdx = (_rrIndex - 1 + _rrOrder.Count) % _rrOrder.Count;

                for (int pass = 0; collected < MaxReadBatchSize && pass < _rrOrder.Count * MaxReadBatchSize; pass++)
                {
                    int idx = (scanIdx + pass) % _rrOrder.Count;
                    string agent = _rrOrder[idx];

                    if (!_agentQueues.TryGetValue(agent, out var q) || q.Count == 0)
                        continue;

                    var peek = q.Peek();
                    if (!IsReadOperation(peek.ActionName))
                        continue;

                    batch.Add(q.Dequeue());
                    collected++;
                }
            }

            PurgeEmptyQueues();
            return batch;
        }

        private static bool IsReadOperation(string actionName) => MCPCommandPolicy.IsReadOnly(actionName);

        // Cached reflection for UnityEditor.Undo.GetRecords(List<string>, List<string>) — the
        // internal API the Undo History window uses. Lets us tell whether an action actually
        // put something on the undo stack (see the undo-group logic in ProcessNextRequests).
        private static System.Reflection.MethodInfo _getUndoRecords;
        private static bool _getUndoRecordsResolved;
        private static readonly List<string> _undoScratchU = new List<string>();
        private static readonly List<string> _undoScratchR = new List<string>();

        /// <summary>Current undo-stack depth, or -1 if the internal API is unavailable.</summary>
        private static int CountUndoRecords()
        {
            try
            {
                if (!_getUndoRecordsResolved)
                {
                    _getUndoRecords = typeof(UnityEditor.Undo).GetMethod(
                        "GetRecords",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                        null,
                        new[] { typeof(List<string>), typeof(List<string>) },
                        null);
                    _getUndoRecordsResolved = true;
                }
                if (_getUndoRecords == null) return -1;
                _undoScratchU.Clear();
                _undoScratchR.Clear();
                _getUndoRecords.Invoke(null, new object[] { _undoScratchU, _undoScratchR });
                return _undoScratchU.Count;
            }
            catch { return -1; }
        }

        private static void PurgeEmptyQueues()
        {
            for (int i = _rrOrder.Count - 1; i >= 0; i--)
            {
                string agent = _rrOrder[i];
                if (!_agentQueues.ContainsKey(agent) || _agentQueues[agent].Count == 0)
                {
                    _agentQueues.Remove(agent);
                    _rrOrder.RemoveAt(i);
                    if (_rrIndex > i) _rrIndex = Math.Max(0, _rrIndex - 1);
                }
            }
            if (_rrOrder.Count > 0 && _rrIndex >= _rrOrder.Count)
                _rrIndex = 0;
        }

        private static MCPAgentSession EnsureSession(string agentId)
        {
            if (!_sessions.TryGetValue(agentId, out var session))
            {
                session = new MCPAgentSession
                {
                    AgentId     = agentId,
                    ConnectedAt = DateTime.UtcNow,
                };
                _sessions[agentId] = session;
            }
            return session;
        }

        private static bool TryCompleteTicket(RequestTicket ticket, RequestStatus status, object result, string error)
        {
            lock (_queueLock)
            {
                // Late callbacks and racing waiters must not overwrite a terminal outcome.
                if (ticket.Status != RequestStatus.Queued && ticket.Status != RequestStatus.Executing)
                    return false;

                ticket.Status = status;
                ticket.Result = result;
                ticket.ErrorMessage = error;
                ticket.CompletedAt = DateTime.UtcNow;
                ticket.CompletedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                ticket.Action = null;
                ticket.DeferredAction = null;

                if (_pendingTickets.Remove(ticket.TicketId)
                    && _agentQueues.TryGetValue(ticket.AgentId, out var queue))
                {
                    // Expired work must leave both indexes without changing the surviving FIFO order.
                    int count = queue.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var queued = queue.Dequeue();
                        if (queued.TicketId != ticket.TicketId) queue.Enqueue(queued);
                    }
                    PurgeEmptyQueues();
                }
                _executingTickets.Remove(ticket.TicketId);
                _completedTickets[ticket.TicketId] = ticket;
                if (_sessions.TryGetValue(ticket.AgentId, out var session))
                    session.RecordCompletion(ticket);
                if (_waiters.TryGetValue(ticket.TicketId, out var waiter))
                    waiter.Set();
                return true;
            }
        }

        private static void RunCleanup()
        {
            lock (_queueLock)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                CleanupSubmissions(SessionTimeMs);
                CleanupSessions(now);
                _expiredTicketIds.Clear();

                foreach (var kvp in _completedTickets)
                {
                    var t = kvp.Value;
                    if (!t.CompletedTimestamp.HasValue) continue;

                    double age = (now - t.CompletedTimestamp.Value) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (t.Status == RequestStatus.TimedOut && age > TimedOutCacheLifetimeSec)
                        _expiredTicketIds.Add(t.TicketId);
                    else if (age > CompletedCacheLifetimeSec)
                        _expiredTicketIds.Add(t.TicketId);
                }

                foreach (var id in _expiredTicketIds)
                    _completedTickets.Remove(id);
                _expiredTicketIds.Clear();

                // Queue wait does not consume an asynchronous operation's execution deadline.
                _staleExecutingIds.Clear();
                foreach (var kvp in _executingTickets)
                {
                    if (!kvp.Value.StartedTimestamp.HasValue) continue;
                    double age = (now - kvp.Value.StartedTimestamp.Value) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (age > 120)
                        _staleExecutingIds.Add(kvp.Key);
                }
                foreach (var id in _staleExecutingIds)
                    TryCompleteTicket(_executingTickets[id], RequestStatus.TimedOut, null,
                        "Timed out after 120s of execution; the operation may already have made changes");
                _staleExecutingIds.Clear();
            }
        }

        private static void CleanupSessions(long now)
        {
            _sessionHighWater = Math.Max(_sessionHighWater, _sessions.Count);
            _inactiveSessions.Clear();
            foreach (var session in _sessions.Values)
                if (session.IsInactiveAt(now)) _inactiveSessions.Add(session);

            int excess = Math.Max(0, _inactiveSessions.Count - MaxInactiveSessions);
            if (excess > 0) _inactiveSessions.Sort(_oldestSessionFirst);
            for (int i = 0; i < _inactiveSessions.Count; i++)
            {
                var session = _inactiveSessions[i];
                if (i < excess || now - session.LastActivityTimestamp >= SessionRetentionSeconds * (long)System.Diagnostics.Stopwatch.Frequency)
                {
                    _sessions.Remove(session.AgentId);
                    _evictedSessions++;
                }
            }
            // Scratch storage must not keep evicted sessions and their logs alive.
            _inactiveSessions.Clear();
            if (_inactiveSessions.Capacity > 4096) _inactiveSessions.Capacity = MaxInactiveSessions;
            // Long-lived editors must also release capacity left by a burst of agent identities.
            if (_sessionHighWater >= 1024 && _sessions.Count <= _sessionHighWater / 2)
            {
                _sessions = new Dictionary<string, MCPAgentSession>(_sessions);
                _sessionHighWater = _sessions.Count;
            }
        }

        private static Dictionary<string, object> TicketToDict(RequestTicket t)
        {
            var dict = new Dictionary<string, object>
            {
                { "ticketId",        t.TicketId },
                { "agentId",         t.AgentId },
                { "actionName",      t.ActionName },
                { "status",          t.Status.ToString() },
                { "queuePosition",   t.QueuePosition },
                { "submittedAt",     t.SubmittedAt.ToString("O") },
                { "executionTimeMs", t.ExecutionTimeMs },
                { "queueWaitMs",     t.QueueWaitMs },
                { "processingTimeMs", t.ProcessingTimeMs },
                { "errorMessage",    t.ErrorMessage ?? "" },
            };

            if (t.CompletedAt.HasValue)
                dict["completedAt"] = t.CompletedAt.Value.ToString("O");
            if (t.StartedAt.HasValue)
                dict["startedAt"] = t.StartedAt.Value.ToString("O");

            // Include result for completed tickets
            if (t.Status == RequestStatus.Completed || t.Status == RequestStatus.Failed)
                dict["result"] = t.Result;

            return dict;
        }

        // Admission and replay lookup share the lock so duplicate callers cannot enqueue twice.
        public static SubmissionResult SubmitOnce(string agentId, string actionName, string body, string requestId,
            string sessionId, long expiresAtMs, Func<RequestTicket> submit)
        {
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";
            if (!Guid.TryParseExact(requestId, "N", out var parsedId))
                return SubmissionError(400, "invalid_request_id", "requestId must be a 32-character GUID");
            if (!string.Equals(sessionId, SessionId, StringComparison.Ordinal))
                return SubmissionError(409, "queue_session_changed", "Queue session changed; submission was not accepted");

            string fingerprint;
            string key;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                string content = actionName.Length + ":" + actionName + (body ?? "");
                fingerprint = Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content)));
                key = Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId))) + ":" + parsedId.ToString("N");
            }
            lock (_queueLock)
            {
                long now = SessionTimeMs;
                if (expiresAtMs <= now)
                    return SubmissionError(410, "request_expired", "Submission retry window expired; inspect the original outcome");
                if (expiresAtMs - now > RetryWindowMs)
                    return SubmissionError(400, "invalid_expiration", "Expiration exceeds the supported retry window");

                if (_submissions.TryGetValue(key, out var previous))
                {
                    if (previous.Fingerprint != fingerprint || previous.ExpiresAtMs != expiresAtMs)
                        return SubmissionError(409, "request_conflict", "requestId was already used with a different payload or expiration");
                    if (_pendingTickets.TryGetValue(previous.TicketId, out var ticket)
                        || _executingTickets.TryGetValue(previous.TicketId, out ticket)
                        || _completedTickets.TryGetValue(previous.TicketId, out ticket))
                        return new SubmissionResult { Ticket = ticket };
                    return SubmissionError(410, "result_expired", "Original result expired; the request will not execute again");
                }

                if (_submissions.Count >= MaxSubmissionRecords) CleanupSubmissions(now);
                if (_submissions.Count >= MaxSubmissionRecords)
                    return SubmissionError(429, "retry_cache_full", "Submission retry cache is full; request was not accepted");

                var accepted = submit();
                _submissions.Add(key, new SubmissionRecord
                {
                    TicketId = accepted.TicketId, ExpiresAtMs = expiresAtMs, Fingerprint = fingerprint
                });
                return new SubmissionResult { Ticket = accepted };
            }
        }

        private static SubmissionResult SubmissionError(int status, string code, string error) =>
            new SubmissionResult { StatusCode = status, Code = code, Error = error };

        private static void CleanupSubmissions(long now)
        {
            _expiredSubmissionKeys.Clear();
            foreach (var entry in _submissions)
                if (entry.Value.ExpiresAtMs <= now) _expiredSubmissionKeys.Add(entry.Key);
            foreach (var key in _expiredSubmissionKeys) _submissions.Remove(key);
            _expiredSubmissionKeys.Clear();
        }
    }
}
