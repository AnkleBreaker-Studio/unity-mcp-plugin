using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing Unity packages via Package Manager.
    /// </summary>
    public static class MCPPackageManagerCommands
    {
        // ─── List Installed Packages ───

        public static object ListPackages(Dictionary<string, object> args)
        {
            var listRequest = Client.List(true);
            while (!listRequest.IsCompleted)
                System.Threading.Thread.Sleep(10);

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
        }

        // ─── Add Package ───

        public static object AddPackage(Dictionary<string, object> args)
        {
            string identifier = args.ContainsKey("identifier") ? args["identifier"].ToString() : "";
            if (string.IsNullOrEmpty(identifier))
                return new { error = "identifier is required (e.g. 'com.unity.cinemachine' or 'com.unity.cinemachine@3.0.0')" };

            var addRequest = Client.Add(identifier);
            while (!addRequest.IsCompleted)
                System.Threading.Thread.Sleep(10);

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
        }

        // ─── Remove Package ───

        public static object RemovePackage(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "";
            if (string.IsNullOrEmpty(name))
                return new { error = "name is required (e.g. 'com.unity.cinemachine')" };

            var removeRequest = Client.Remove(name);
            while (!removeRequest.IsCompleted)
                System.Threading.Thread.Sleep(10);

            if (removeRequest.Status == StatusCode.Failure)
                return new { error = removeRequest.Error?.message ?? "Failed to remove package" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "removed", name },
            };
        }

        // ─── Search Package ───

        public static object SearchPackage(Dictionary<string, object> args)
        {
            string query = args.ContainsKey("query") ? args["query"].ToString() : "";
            if (string.IsNullOrEmpty(query))
                return new { error = "query is required" };

            var searchRequest = Client.Search(query);
            while (!searchRequest.IsCompleted)
                System.Threading.Thread.Sleep(10);

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
        }

        // ─── Get Package Info ───

        public static object GetPackageInfo(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "";
            if (string.IsNullOrEmpty(name))
                return new { error = "name is required" };

            var listRequest = Client.List(true);
            while (!listRequest.IsCompleted)
                System.Threading.Thread.Sleep(10);

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
        }
    }
}
