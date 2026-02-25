using System;
using System.Collections.Generic;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Manages agent sessions and provides a global execution lock to prevent
    /// concurrent modifications from multiple agents.
    /// </summary>
    public static class MCPRequestQueue
    {
        private static readonly Dictionary<string, MCPAgentSession> _sessions =
            new Dictionary<string, MCPAgentSession>();

        private static readonly object _globalLock = new object();

        /// <summary>
        /// Execute an action with agent tracking. The action itself is NOT wrapped
        /// in the global lock — Unity API calls are already serialized via
        /// ExecuteOnMainThread (main thread queue). Holding the lock during execution
        /// would cause deadlocks when main-thread code accesses session data.
        /// </summary>
        public static object ExecuteWithTracking(string agentId, string actionName, Func<object> action)
        {
            if (string.IsNullOrEmpty(agentId))
                agentId = "anonymous";

            // Track the session (lock only for session bookkeeping)
            lock (_globalLock)
            {
                if (!_sessions.ContainsKey(agentId))
                {
                    _sessions[agentId] = new MCPAgentSession
                    {
                        AgentId = agentId,
                        ConnectedAt = DateTime.UtcNow,
                    };
                }
                _sessions[agentId].LogAction(actionName);
            }

            // Execute WITHOUT holding the lock — prevents deadlocks
            return action();
        }

        /// <summary>Returns info for all active agent sessions.</summary>
        public static List<Dictionary<string, object>> GetActiveSessions()
        {
            var result = new List<Dictionary<string, object>>();
            lock (_globalLock)
            {
                foreach (var session in _sessions.Values)
                {
                    if (session.IsActive)
                        result.Add(session.ToDict());
                }
            }
            return result;
        }

        /// <summary>Returns the action log for a specific agent.</summary>
        public static List<string> GetAgentLog(string agentId)
        {
            lock (_globalLock)
            {
                if (_sessions.ContainsKey(agentId))
                    return _sessions[agentId].GetLog();
            }
            return new List<string>();
        }

        /// <summary>Returns total count of all sessions (including inactive).</summary>
        public static int TotalSessionCount
        {
            get { lock (_globalLock) { return _sessions.Count; } }
        }

        /// <summary>Returns count of currently active sessions.</summary>
        public static int ActiveSessionCount
        {
            get
            {
                int count = 0;
                lock (_globalLock)
                {
                    foreach (var s in _sessions.Values)
                        if (s.IsActive) count++;
                }
                return count;
            }
        }
    }
}
