using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace UnityMCP.Editor
{
    internal static class MCPCodeExecutionSupport
    {
        private const int MaxReferences = 512;
        private const long MaxReferenceImageBytes = 128L * 1024 * 1024;
        private static readonly Dictionary<string, ReferenceEntry> References = new Dictionary<string, ReferenceEntry>(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private static readonly Queue<string> ReferenceOrder = new Queue<string>();
        private static long _referenceImageBytes;
        private static long _referenceHits;
        private static long _referenceMisses;
        private static long _loadedSnippets;

        private sealed class ReferenceEntry
        {
            internal object Reference;
            internal long Length;
            internal DateTime LastWriteUtc;
        }

        internal static IEnumerable<Assembly> GetAssemblies()
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }

        internal static string GetAssemblyPath(Assembly assembly)
        {
#if UNITY_6000_6_OR_NEWER
            return assembly.GetLoadedAssemblyPath();
#else
            return assembly.Location;
#endif
        }

        internal static Assembly LoadAssembly(string path)
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.LoadFromPath(path);
#else
            return Assembly.LoadFrom(path);
#endif
        }

        internal static Assembly LoadSnippet(byte[] bytes)
        {
            // Unity's loader keeps generated code in the context that its script reload will unload.
#if UNITY_6000_6_OR_NEWER
            var assembly = UnityEngine.Assemblies.CurrentAssemblies.LoadFromBytes(bytes);
#else
            var assembly = Assembly.Load(bytes);
#endif
            _loadedSnippets++;
            return assembly;
        }

        internal static object GetReference(string path, Func<string, object> create)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            long length = file.Length;
            DateTime lastWriteUtc = file.LastWriteTimeUtc;
            if (References.TryGetValue(path, out var cached))
            {
                if (cached.Length == length && cached.LastWriteUtc == lastWriteUtc)
                {
                    _referenceHits++;
                    return cached.Reference;
                }
                // A rebuilt file must not reuse metadata from its previous contents.
                _referenceImageBytes -= cached.Length;
                References.Remove(path);
                int count = ReferenceOrder.Count;
                for (int i = 0; i < count; i++)
                {
                    string key = ReferenceOrder.Dequeue();
                    if (!References.Comparer.Equals(key, path)) ReferenceOrder.Enqueue(key);
                }
            }
            _referenceMisses++;
            object reference = create(path);
            if (length > MaxReferenceImageBytes) return reference;
            while (References.Count >= MaxReferences || _referenceImageBytes + length > MaxReferenceImageBytes)
            {
                string oldest = ReferenceOrder.Dequeue();
                _referenceImageBytes -= References[oldest].Length;
                References.Remove(oldest);
            }
            References.Add(path, new ReferenceEntry { Reference = reference, Length = length, LastWriteUtc = lastWriteUtc });
            ReferenceOrder.Enqueue(path);
            _referenceImageBytes += length;
            return reference;
        }

        internal static object GetDiagnostics()
        {
            return new {
                metadataCacheEntries = References.Count,
                metadataReferenceImageBytes = _referenceImageBytes,
                maxMetadataCacheEntries = MaxReferences,
                maxMetadataReferenceImageBytes = MaxReferenceImageBytes,
                metadataCacheHits = _referenceHits,
                metadataCacheMisses = _referenceMisses,
                loadedSnippetAssembliesSinceReload = _loadedSnippets
            };
        }
    }
}
