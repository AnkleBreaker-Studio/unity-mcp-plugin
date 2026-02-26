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

        public class RequestTicket
        {
            public long   TicketId    { get; set; }
            public string AgentId     { get; set; }
            public string ActionName  { get; set; }
            public RequestStatus Status { get; set; }

            // The actual work to execute on the main thread
            internal Func<object> Action { get; set; }

            // Result / error
            public object Result       { get; set; }
            public string ErrorMessage { get; set; }

            // Timing
            public DateTime  SubmittedAt   { get; set; }
            public DateTime? CompletedAt   { get; set; }
            public int       QueuePosition { get; set; }

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

        // Synchronous waiters (backward compat)
        private static readonly Dictionary<long, ManualResetEventSlim> _waiters
            = new Dictionary<long, ManualResetEventSlim>();

        // Session tracking
        private static readonly Dictionary<string, MCPAgentSession> _sessions
            = new Dictionary<string, MCPAgentSession>();

        // Single lock for all mutable state
        private static readonly object _queueLock = new object();

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

                // Session bookkeeping
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
            var ticket = SubmitRequest(agentId, actionName, action);

            var waiter = new ManualResetEventSlim(false);
            lock (_queueLock) { _waiters[ticket.TicketId] = waiter; }

            try
            {
                if (!waiter.Wait(SyncTimeoutMs))
                {
                    // Timed out — mark ticket
                    lock (_queueLock)
                    {
                        ticket.Status       = RequestStatus.TimedOut;
                        ticket.ErrorMessage = $"Timed out after {SyncTimeoutMs / 1000}s waiting for main thread";
                        ticket.CompletedAt  = DateTime.UtcNow;
                        _completedTickets[ticket.TicketId] = ticket;
                    }
                    return new Dictionary<string, object>
                    {
                        { "error", ticket.ErrorMessage },
                        { "ticketId", ticket.TicketId },
                    };
                }

                // Signaled — grab result
                lock (_queueLock)
                {
                    if (_completedTickets.TryGetValue(ticket.TicketId, out var done))
                    {
                        if (done.Status == RequestStatus.Failed)
                            return new Dictionary<string, object>
                            {
                                { "error", done.ErrorMessage },
                                { "ticketId", done.TicketId },
                            };
                        return done.Result;
                    }
                }
                return null;
            }
            finally
            {
                waiter.Dispose();
                lock (_queueLock) { _waiters.Remove(ticket.TicketId); }
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

                // Mark all as executing
                foreach (var t in batch) t.Status = RequestStatus.Executing;
            }

            // --- Execute OUTSIDE lock (main thread) ---
            foreach (var ticket in batch)
            {
                try
                {
                    ticket.Result = ticket.Action();
                    ticket.Status = RequestStatus.Completed;
                }
                catch (Exception ex)
                {
                    ticket.Status       = RequestStatus.Failed;
                    ticket.ErrorMessage = ex.Message;
                    Debug.LogError($"[Unity MCP Queue] Ticket {ticket.TicketId} ({ticket.ActionName}) failed: {ex.Message}");
                }
                ticket.CompletedAt = DateTime.UtcNow;
                ticket.Action      = null; // Free the closure

                // Move to completed cache and signal waiters
                lock (_queueLock)
                {
                    _completedTickets[ticket.TicketId] = ticket;

                    if (_waiters.TryGetValue(ticket.TicketId, out var waiter))
                        waiter.Set();

                    if (_sessions.TryGetValue(ticket.AgentId, out var session))
                        session.IncrementCompletedRequest(ticket.ExecutionTimeMs);
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

                // Check active queues
                foreach (var q in _agentQueues.Values)
                    foreach (var t in q)
                        if (t.TicketId == ticketId)
                            return TicketToDict(t);
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
                    { "completedCacheSize",   _completedTickets.Count },
                    { "perAgentQueued",        perAgent },
                    { "totalSessionsTracked", _sessions.Count },
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

        private static bool IsReadOperation(string actionName)
        {
            if (string.IsNullOrEmpty(actionName)) return false;

            // Match API path patterns that are read-only
            string lower = actionName.ToLower();
            return lower == "ping"
                || lower.EndsWith("/info")
                || lower.EndsWith("/list")
                || lower.EndsWith("/log")
                || lower.EndsWith("/stats")
                || lower.EndsWith("/get")
                || lower.StartsWith("search/")
                || lower.StartsWith("agents/")
                || lower.StartsWith("queue/")
                || lower == "scene/info"
                || lower == "scene/hierarchy"
                || lower == "editor/state"
                || lower == "project/info"
                || lower == "console/log"
                || lower.StartsWith("profiler/")
                || lower.StartsWith("debugger/")
                || lower.StartsWith("selection/get")
                || lower.StartsWith("selection/find")
                || lower.Contains("/info")
                || lower.Contains("/list")
                || lower.Contains("/get-")
                || lower.Contains("/status");
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

        private static void RunCleanup()
        {
            lock (_queueLock)
            {
                var now  = DateTime.UtcNow;
                var kill = new List<long>();

                foreach (var kvp in _completedTickets)
                {
                    var t = kvp.Value;
                    if (!t.CompletedAt.HasValue) continue;

                    double age = (now - t.CompletedAt.Value).TotalSeconds;
                    if (t.Status == RequestStatus.TimedOut && age > TimedOutCacheLifetimeSec)
                        kill.Add(t.TicketId);
                    else if (age > CompletedCacheLifetimeSec)
                        kill.Add(t.TicketId);
                }

                foreach (var id in kill)
                    _completedTickets.Remove(id);
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
                { "errorMessage",    t.ErrorMessage ?? "" },
            };

            if (t.CompletedAt.HasValue)
                dict["completedAt"] = t.CompletedAt.Value.ToString("O");

            // Include result for completed tickets
            if (t.Status == RequestStatus.Completed || t.Status == RequestStatus.Failed)
                dict["result"] = t.Result;

            return dict;
        }
    }
}
