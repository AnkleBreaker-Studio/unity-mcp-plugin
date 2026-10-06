using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityMCP.Editor.Welcome
{
    internal static class UnityMcpWelcomeStartup
    {
        private const string Key = "AnkleBreaker.Welcome.Startup.v1";
        public static void Enqueue(Action action)
        {
            var state = AppDomain.CurrentDomain.GetData(Key) as object[];
            if (state == null)
            {
                state = new object[] { new Queue<Action>(), (EditorApplication.CallbackFunction)Tick };
                AppDomain.CurrentDomain.SetData(Key, state);
            }
            var queue = (Queue<Action>)state[0];
            if (!queue.Contains(action)) queue.Enqueue(action);
            EditorApplication.update -= (EditorApplication.CallbackFunction)state[1];
            EditorApplication.update += (EditorApplication.CallbackFunction)state[1];
        }

        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var state = (object[])AppDomain.CurrentDomain.GetData(Key);
            var queue = (Queue<Action>)state[0];
            try { if (queue.Count > 0) queue.Dequeue()(); }
            finally { if (queue.Count == 0) EditorApplication.update -= (EditorApplication.CallbackFunction)state[1]; }
        }
    }
}
