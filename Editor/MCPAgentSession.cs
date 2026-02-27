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

        private readonly List<string> _actionLog = new List<string>();
        private readonly List<MCPActionRecord> _structuredLog = new List<MCPActionRecord>();

        private const int MaxLogEntries = 100;

        /// <summary>Session is considered active if last activity was within 5 minutes.</summary>
        public bool IsActive => (DateTime.UtcNow - LastActivityAt).TotalSeconds < 300;

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
            LastActivityAt = DateTime.UtcNow;
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
                { "structuredActionCount", _structuredLog.Count },
            };
        }
    }
}
