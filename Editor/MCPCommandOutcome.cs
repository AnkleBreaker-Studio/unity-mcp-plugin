using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    internal static class MCPCommandOutcome
    {
        private const int MaxDiagnosticLength = 2048;

        internal static bool TryGetError(object result, out string message)
        {
            try { return InspectResult(result, out message); }
            catch (Exception)
            {
                // Monitoring must not prevent a custom result from completing its original request.
                message = null;
                return false;
            }
        }

        private static bool InspectResult(object result, out string message)
        {
            message = null;
            object success = ReadValue(result, "success");
            object ok = ReadValue(result, "ok");
            // Status commands may report success alongside an unrelated diagnostic.
            if ((success is bool successFlag && successFlag) || (ok is bool okFlag && okFlag)) return false;

            object error = ReadValue(result, "error");
            string detail = error as string;
            string structuredDetail = ReadValue(error, "message") as string;
            bool explicitFailure = (success is bool failedSuccess && !failedSuccess) || (ok is bool failedOk && !failedOk);
            if (!explicitFailure && string.IsNullOrEmpty(detail) && structuredDetail == null) return false;

            if (string.IsNullOrEmpty(detail)) detail = structuredDetail;
            if (string.IsNullOrEmpty(detail)) detail = "Command reported failure.";
            message = detail.Length <= MaxDiagnosticLength ? detail : detail.Substring(0, MaxDiagnosticLength);
            return true;
        }

        private static object ReadValue(object value, string name)
        {
            if (value == null || value is string || value is Array || value is UnityEngine.Object) return null;
            if (value.GetType().IsPrimitive) return null;
            if (value is Dictionary<string, object> dictionary)
                return dictionary.TryGetValue(name, out var entry) ? entry : null;

            // Inspect stored fields only: a result getter can invoke Unity APIs or arbitrary user code.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = value.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public)
                    ?? type.GetField("<" + name + ">k__BackingField", flags)
                    ?? type.GetField("<" + name + ">i__Field", flags);
                if (field != null) return field.GetValue(value);
            }
            return null;
        }
    }
}
