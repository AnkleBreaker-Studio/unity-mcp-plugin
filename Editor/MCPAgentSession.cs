using System;
using System.Collections.Generic;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Tracks a single agent's session: identity, activity, and action log.
    /// </summary>
    public class MCPAgentSession
    {
        public string AgentId { get; set; }
        public DateTime ConnectedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public string CurrentAction { get; set; }
        public int TotalActions { get; private set; }
        private readonly List<string> _actionLog = new List<string>();

        private const int MaxLogEntries = 100;

        /// <summary>Session is considered active if last activity was within 5 minutes.</summary>
        public bool IsActive => (DateTime.UtcNow - LastActivityAt).TotalSeconds < 300;

        public void LogAction(string action)
        {
            CurrentAction = action;
            LastActivityAt = DateTime.UtcNow;
            TotalActions++;

            _actionLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {action}");
            if (_actionLog.Count > MaxLogEntries)
                _actionLog.RemoveAt(0);
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
            };
        }
    }
}
