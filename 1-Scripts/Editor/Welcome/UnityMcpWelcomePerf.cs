using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Welcome
{
    internal static class UnityMcpWelcomePerf
    {
        public static bool Enabled { get; set; }
        public static readonly List<Sample> Samples = new List<Sample>();

        [Serializable]
        public struct Sample
        {
            public string name;
            public double milliseconds;
            public long allocatedBytes;
        }

        public struct Scope : IDisposable
        {
            private readonly string _name;
            private readonly long _start;
            private readonly long _allocated;

            public Scope(string name)
            {
                _name = Enabled ? name : null;
                _start = Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                _allocated = Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
                if (_name != null) UnityEngine.Profiling.Profiler.BeginSample("Welcome." + name);
            }

            public void Dispose()
            {
                if (_name == null) return;
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
                UnityEngine.Profiling.Profiler.EndSample();
                if (Samples.Count < 100000)
                    Samples.Add(new Sample { name = _name, milliseconds = (end - _start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, allocatedBytes = allocated });
            }
        }

        public static string[] FindAssets(string filter, string[] folders = null)
        {
            using var scope = new Scope("FindAssets");
            return folders == null ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);
        }
    }
}
