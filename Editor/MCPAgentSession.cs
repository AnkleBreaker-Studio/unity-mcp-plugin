using System;
using System.Collections.Generic;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Tracks a single agent's session: identity, activity, action log, queue stats, and performance metrics.
    /// </summary>
    public class MCPAgentSession
    {
        public string AgentId { get; set; }
        public DateTime ConnectedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public string CurrentAction { get; set; }
        public int TotalActions { get; private set; }

        // Queue and performance tracking
        private int _queuedRequests = 0;
        private int _completedRequests = 0;
        private long _totalResponseTimeMs = 0;
        private int _failedRequests;
        private int _commandErrors;
        private int _timedOutRequests;
        private double _totalQueueWaitMs;
        private double _totalProcessingTimeMs;
        internal long LastActivityTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        internal long Generation;
        internal LinkedListNode<MCPAgentSession> IdleRetentionNode;

        private readonly List<string> _actionLog = new List<string>();
        private readonly List<MCPActionRecord> _structuredLog = new List<MCPActionRecord>();

        private const int MaxLogEntries = 100;

        /// <summary>Queued work remains visible even when its wait exceeds the recent-activity window.</summary>
        public bool IsActive => !IsInactiveAt(System.Diagnostics.Stopwatch.GetTimestamp());

        internal bool IsInactiveAt(long timestamp) => _queuedRequests == 0
            && timestamp - LastActivityTimestamp >= 300L * System.Diagnostics.Stopwatch.Frequency;

        private void Touch()
        {
            LastActivityAt = DateTime.UtcNow;
            LastActivityTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>Number of requests currently queued for this agent.</summary>
        public int QueuedRequests
        {
            get { return _queuedRequests; }
        }

        /// <summary>Total number of requests that have been completed by this agent.</summary>
        public int CompletedRequests
        {
            get { return _completedRequests; }
        }

        /// <summary>Average response time in milliseconds for completed requests.</summary>
        public double AverageResponseTimeMs
        {
            get
            {
                if (_completedRequests == 0)
                    return 0;
                return (double)_totalResponseTimeMs / _completedRequests;
            }
        }

        public void LogAction(string action)
        {
            CurrentAction = action;
            Touch();
            TotalActions++;

            _actionLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {action}");
            if (_actionLog.Count > MaxLogEntries)
                _actionLog.RemoveAt(0);
        }

        /// <summary>
        /// Log a structured action record for this agent session.
        /// </summary>
        public void LogStructuredAction(MCPActionRecord record)
        {
            _structuredLog.Add(record);
            if (_structuredLog.Count > MaxLogEntries)
                _structuredLog.RemoveAt(0);
        }

        /// <summary>Get a copy of the structured action log.</summary>
        public List<MCPActionRecord> GetStructuredLog() => new List<MCPActionRecord>(_structuredLog);

        /// <summary>
        /// Increment the count of queued requests for this agent.
        /// </summary>
        public void IncrementQueuedRequest()
        {
            _queuedRequests++;
        }

        /// <summary>
        /// Decrement the count of queued requests and record the response time.
        /// </summary>
        public void IncrementCompletedRequest(long responseTimeMs)
        {
            if (_queuedRequests > 0)
                _queuedRequests--;

            _completedRequests++;
            if (responseTimeMs >= 0)
                _totalResponseTimeMs += responseTimeMs;
        }

        public List<string> GetLog() => new List<string>(_actionLog);

        internal void RecordCompletion(MCPRequestQueue.RequestTicket ticket)
        {
            IncrementCompletedRequest(ticket.ExecutionTimeMs);
            Touch();
            if (ticket.Status == MCPRequestQueue.RequestStatus.Failed) _failedRequests++;
            if (ticket.CommandFailed) _commandErrors++;
            if (ticket.Status == MCPRequestQueue.RequestStatus.TimedOut) _timedOutRequests++;
            _totalQueueWaitMs += ticket.QueueWaitMs;
            _totalProcessingTimeMs += ticket.ProcessingTimeMs;
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "agentId", AgentId },
                { "connectedAt", ConnectedAt.ToString("O") },
                { "lastActivity", LastActivityAt.ToString("O") },
                { "currentAction", CurrentAction ?? "idle" },
                { "totalActions", TotalActions },
                { "isActive", IsActive },
                { "queuedRequests", QueuedRequests },
                { "completedRequests", CompletedRequests },
                { "averageResponseTimeMs", Math.Round(AverageResponseTimeMs, 2) },
                { "failedRequests", _failedRequests },
                { "commandErrors", _commandErrors },
                { "timedOutRequests", _timedOutRequests },
                { "averageQueueWaitMs", _completedRequests == 0 ? 0 : Math.Round(_totalQueueWaitMs / _completedRequests, 2) },
                { "averageProcessingTimeMs", _completedRequests == 0 ? 0 : Math.Round(_totalProcessingTimeMs / _completedRequests, 2) },
                { "structuredActionCount", _structuredLog.Count },
            };
        }

        internal struct DashboardSnapshot
        {
            internal string AgentId, LatestAction;
            internal int Outstanding, Completed, CommandErrors, Exceptions, Timeouts;
            internal double AverageWaitMs, AverageProcessingMs;

            internal bool Matches(DashboardSnapshot other) => AgentId == other.AgentId && LatestAction == other.LatestAction
                && Outstanding == other.Outstanding && Completed == other.Completed && CommandErrors == other.CommandErrors
                && Exceptions == other.Exceptions && Timeouts == other.Timeouts
                && AverageWaitMs == other.AverageWaitMs && AverageProcessingMs == other.AverageProcessingMs;
        }

        internal DashboardSnapshot GetDashboardSnapshot()
        {
            // The dashboard needs displayed values, not the transport dictionaries and timestamp strings.
            return new DashboardSnapshot {
                AgentId = AgentId, LatestAction = CurrentAction ?? "idle", Outstanding = _queuedRequests,
                Completed = _completedRequests, CommandErrors = _commandErrors, Exceptions = _failedRequests,
                Timeouts = _timedOutRequests,
                AverageWaitMs = _completedRequests == 0 ? 0 : Math.Round(_totalQueueWaitMs / _completedRequests, 2),
                AverageProcessingMs = _completedRequests == 0 ? 0 : Math.Round(_totalProcessingTimeMs / _completedRequests, 2)
            };
        }
    }
}
