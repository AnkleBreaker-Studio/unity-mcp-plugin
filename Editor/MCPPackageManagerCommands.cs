using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing Unity packages via Package Manager.
    /// </summary>
    public static class MCPPackageManagerCommands
    {
        private sealed class Operation
        {
            public Func<Func<bool>> Start;
            public Func<bool> IsCompleted;
            public Func<object> Result;
            public Action<object> Resolve;
            public Func<bool> IsActive;
        }

        private static readonly Queue<Operation> Pending = new Queue<Operation>();
        private static Operation _active;
        private static bool _subscribed;

        static MCPPackageManagerCommands()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
        }

        private static void Enqueue<T>(Func<T> start, Func<T, object> result, Action<object> resolve, Func<bool> isActive)
            where T : Request
        {
            T request = null;
            Schedule(() =>
            {
                request = start();
                return () => request.IsCompleted;
            }, () => result(request), resolve, isActive);
        }

        internal static void Schedule(Func<Func<bool>> start, Func<object> result, Action<object> resolve, Func<bool> isActive)
        {
            Pending.Enqueue(new Operation { Start = start, Result = result, Resolve = resolve, IsActive = isActive });
            if (_subscribed) return;
            EditorApplication.update += Progress;
            _subscribed = true;
        }

        private static void Progress()
        {
            int pendingCount = Pending.Count;
            for (int i = 0; i < pendingCount; i++)
            {
                var pending = Pending.Dequeue();
                if (pending.IsActive()) Pending.Enqueue(pending);
            }

            if (_active != null)
            {
                // Unity cannot cancel a started request; retain its slot even after the caller's ticket expires.
                if (_active.Resolve != null && !_active.IsActive())
                {
                    _active.Resolve = null;
                    _active.Result = null;
                    _active.IsActive = null;
                }
                object result = null;
                try
                {
                    if (!_active.IsCompleted()) return;
                    if (_active.Resolve != null) result = _active.Result();
                }
                catch (Exception error)
                {
                    Finish(new { error = error.Message });
                    UpdateSubscription();
                    return;
                }
                Finish(result);
            }

            // Client operations must remain sequential on the supported older Unity versions.
            if (_active == null && Pending.Count > 0)
            {
                _active = Pending.Dequeue();
                try { _active.IsCompleted = _active.Start(); }
                catch (Exception error) { Finish(new { error = error.Message }); }
                if (_active != null) _active.Start = null;
            }
            UpdateSubscription();
        }

        private static void Finish(object result)
        {
            var operation = _active;
            _active = null;
            operation.Resolve?.Invoke(result);
        }

        private static void UpdateSubscription()
        {
            if (_active != null || Pending.Count != 0 || !_subscribed) return;
            EditorApplication.update -= Progress;
            _subscribed = false;
        }

        private static void Clear()
        {
            Pending.Clear();
            _active = null;
            UpdateSubscription();
        }

        // ─── List Installed Packages ───

        public static void ListPackages(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            Enqueue(() => Client.List(true), listRequest =>
            {
                if (listRequest.Status == StatusCode.Failure)
                    return new { error = listRequest.Error?.message ?? "Failed to list packages" };

                var packages = new List<Dictionary<string, object>>();
                foreach (var pkg in listRequest.Result)
                {
                    packages.Add(new Dictionary<string, object>
                    {
                        { "name", pkg.name },
                        { "displayName", pkg.displayName },
                        { "version", pkg.version },
                        { "source", pkg.source.ToString() },
                        { "description", pkg.description ?? "" },
                    });
                }

                return new Dictionary<string, object>
                {
                    { "count", packages.Count },
                    { "packages", packages },
                };
            }, resolve, isActive);
        }

        // ─── Add Package ───

        public static void AddPackage(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            string identifier = args.ContainsKey("identifier") ? args["identifier"]?.ToString() : "";
            if (string.IsNullOrEmpty(identifier))
            {
                resolve(new { error = "identifier is required (e.g. 'com.unity.cinemachine' or 'com.unity.cinemachine@3.0.0')" });
                return;
            }

            Enqueue(() => Client.Add(identifier), addRequest =>
            {
                if (addRequest.Status == StatusCode.Failure)
                    return new { error = addRequest.Error?.message ?? "Failed to add package" };

                var pkg = addRequest.Result;
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "name", pkg.name },
                    { "displayName", pkg.displayName },
                    { "version", pkg.version },
                };
            }, resolve, isActive);
        }

        // ─── Remove Package ───

        public static void RemovePackage(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            string name = args.ContainsKey("name") ? args["name"]?.ToString() : "";
            if (string.IsNullOrEmpty(name))
            {
                resolve(new { error = "name is required (e.g. 'com.unity.cinemachine')" });
                return;
            }

            Enqueue(() => Client.Remove(name), removeRequest =>
            {
                if (removeRequest.Status == StatusCode.Failure)
                    return new { error = removeRequest.Error?.message ?? "Failed to remove package" };

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "removed", name },
                };
            }, resolve, isActive);
        }

        // ─── Search Package ───

        public static void SearchPackage(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            string query = args.ContainsKey("query") ? args["query"]?.ToString() : "";
            if (string.IsNullOrEmpty(query))
            {
                resolve(new { error = "query is required" });
                return;
            }

            Enqueue(() => Client.Search(query), searchRequest =>
            {
                if (searchRequest.Status == StatusCode.Failure)
                    return new { error = searchRequest.Error?.message ?? "Search failed" };

                var results = new List<Dictionary<string, object>>();
                foreach (var pkg in searchRequest.Result)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", pkg.name },
                        { "displayName", pkg.displayName },
                        { "version", pkg.version },
                        { "description", pkg.description ?? "" },
                    });
                }

                return new Dictionary<string, object>
                {
                    { "query", query },
                    { "count", results.Count },
                    { "results", results },
                };
            }, resolve, isActive);
        }

        // ─── Get Package Info ───

        public static void GetPackageInfo(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            string name = args.ContainsKey("name") ? args["name"]?.ToString() : "";
            if (string.IsNullOrEmpty(name))
            {
                resolve(new { error = "name is required" });
                return;
            }

            Enqueue(() => Client.List(true), listRequest =>
            {
                if (listRequest.Status == StatusCode.Failure)
                    return new { error = "Failed to list packages" };

                foreach (var pkg in listRequest.Result)
                {
                    if (pkg.name == name)
                    {
                        var versions = new List<string>();
                        if (pkg.versions != null && pkg.versions.compatible != null)
                            versions.AddRange(pkg.versions.compatible);

                        return new Dictionary<string, object>
                        {
                            { "name", pkg.name },
                            { "displayName", pkg.displayName },
                            { "version", pkg.version },
                            { "source", pkg.source.ToString() },
                            { "description", pkg.description ?? "" },
                            { "category", pkg.category ?? "" },
                            { "documentationUrl", pkg.documentationUrl ?? "" },
                            { "compatibleVersions", versions },
                            { "dependencies", pkg.dependencies?.Select(d => d.name + "@" + d.version).ToList() ?? new List<string>() },
                        };
                    }
                }

                return new { error = $"Package '{name}' not found" };
            }, resolve, isActive);
        }
    }
}
