using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    internal static class MCPAssetPreviewScheduler
    {
        private sealed class PendingPreview
        {
            public Func<Texture2D> Preview, Fallback;
            public Func<bool> Loading, IsActive;
            public Action<Texture2D> Resolve;
            public Action<Exception> Fail;
            public double Deadline, NextPoll;
        }

        private const int MaxPending = 64;
        private static readonly List<PendingPreview> Pending = new List<PendingPreview>();
        private static int _cursor;
        private static bool _subscribed;

        static MCPAssetPreviewScheduler()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
        }

        internal static void Schedule(Func<Texture2D> preview, Func<bool> loading, Func<Texture2D> fallback,
            Action<Texture2D> resolve, Action<Exception> fail, Func<bool> isActive, double seconds)
        {
            if (!isActive()) return;
            if (Pending.Count >= MaxPending) { fail(new InvalidOperationException("Asset preview queue is full; retry after pending previews finish.")); return; }
            Pending.Add(new PendingPreview { Preview = preview, Loading = loading, Fallback = fallback,
                Resolve = resolve, Fail = fail, IsActive = isActive, Deadline = EditorApplication.timeSinceStartup + seconds });
            if (_subscribed) return;
            EditorApplication.update += Update;
            _subscribed = true;
        }

        private static void Update() => Progress(EditorApplication.timeSinceStartup);

        internal static void Progress(double now)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int remaining = Pending.Count, polls = 0;
            while (Pending.Count > 0 && remaining-- > 0 && polls < 4)
            {
                if (_cursor >= Pending.Count) _cursor = 0;
                var operation = Pending[_cursor];
                bool remove = false;
                Texture2D preview = null;
                Exception failure = null;
                try
                {
                    if (!operation.IsActive()) { Pending.RemoveAt(_cursor); continue; }
                    if (now < operation.NextPoll) { _cursor++; continue; }
                    polls++;
                    operation.NextPoll = now + 0.05;
                    preview = operation.Preview();
                    if (!operation.IsActive()) { Pending.RemoveAt(_cursor); continue; }
                    remove = preview != null || now >= operation.Deadline || !operation.Loading();
                    if (remove && preview == null) preview = operation.Fallback();
                }
                catch (Exception error) { remove = true; failure = error; }
                if (remove)
                {
                    Pending.RemoveAt(_cursor);
                    if (failure != null) operation.Fail(failure);
                    else if (operation.IsActive())
                    {
                        try { operation.Resolve(preview); }
                        catch (Exception error) { operation.Fail(error); }
                    }
                }
                else _cursor++;
                // Native preview/readback calls cannot be preempted; avoid starting more work after this frame's allowance.
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 5) break;
            }
            if (Pending.Count == 0) Clear();
        }

        internal static void Clear()
        {
            Pending.Clear(); _cursor = 0;
            if (!_subscribed) return;
            EditorApplication.update -= Update;
            _subscribed = false;
        }
    }
}
