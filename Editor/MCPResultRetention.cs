using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnityMCP.Editor
{
    internal static class MCPResultRetention
    {
        internal const long MaxCost = 256L * 1024 * 1024;
        private const int MaxDepth = 64, MaxVisited = 65536;

        // Accounting weights bound retained polling results, not the managed or native heap.
        // Read stored fields only: observing a result must never call user getters or enumerators.
        internal static long Measure(object result, long metadataCost)
        {
            try
            {
                var counter = new Counter { Cost = metadataCost };
                counter.Visit(result, 0);
                return counter.Cost > MaxCost ? MaxCost + 1 : counter.Uncertain ? MaxCost : counter.Cost;
            }
            catch (System.Threading.ThreadAbortException) { throw; }
            catch { return MaxCost; }
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            internal static readonly IdentityComparer Instance = new IdentityComparer();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private sealed class Counter
        {
            internal long Cost;
            internal bool Uncertain;
            private int visited;
            private HashSet<object> references;

            private static bool Scalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);

            private static int ScalarSize(Type type)
            {
                if (type.IsEnum) type = Enum.GetUnderlyingType(type);
                if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
                if (type == typeof(char) || type == typeof(short) || type == typeof(ushort)) return 2;
                if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
                return 16;
            }

            internal void Visit(object value, int depth)
            {
                if (Cost > MaxCost || Uncertain) return;
                Cost += 64;
                if (value == null) return;
                if (++visited > MaxVisited || depth > MaxDepth) { Uncertain = true; return; }
                if (value is string text) { Cost += 2L * text.Length; return; }
                Type type = value.GetType();
                if (Scalar(type)) return;
                if (!type.IsValueType)
                {
                    if (references == null) references = new HashSet<object>(IdentityComparer.Instance);
                    if (!references.Add(value)) return;
                }
                // Native object memory and independently owned Unity resources are outside this budget.
                if (value is UnityEngine.Object) return;
                if (type.IsArray)
                {
                    var array = (Array)value;
                    if (Scalar(type.GetElementType())) { Cost += ScalarSize(type.GetElementType()) * array.LongLength; return; }
                    foreach (object item in array)
                    {
                        Visit(item, depth + 1);
                        if (Cost > MaxCost || Uncertain) return;
                    }
                    return;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var list = (IList)value;
                    var storage = type.GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) as Array;
                    if (storage == null) { Uncertain = true; return; }
                    if (Scalar(type.GetGenericArguments()[0])) { Cost += (long)ScalarSize(type.GetGenericArguments()[0]) * storage.Length; return; }
                    Cost += 8L * storage.Length;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Visit(list[i], depth + 1);
                        if (Cost > MaxCost || Uncertain) return;
                    }
                    return;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    var storageField = type.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? type.GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (storageField == null) { Uncertain = true; return; }
                    var storage = storageField.GetValue(value) as Array;
                    Cost += 32L * (storage?.Length ?? 0);
                    foreach (DictionaryEntry entry in (IDictionary)value)
                    {
                        Visit(entry.Key, depth + 1); Visit(entry.Value, depth + 1);
                        if (Cost > MaxCost || Uncertain) return;
                    }
                    return;
                }
                for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
                {
                    foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        Visit(field.GetValue(value), depth + 1);
                        if (Cost > MaxCost || Uncertain) return;
                    }
                }
            }
        }
    }
}
