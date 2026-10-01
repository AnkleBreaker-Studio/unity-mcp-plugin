using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEditor;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    internal static class MCPHttpDiagnostics
    {
        private const string ReloadKey = "UnityMCP_HttpReload";
        private static readonly object Gate = new object();
        private static Snapshot totals;
        [ThreadStatic] private static Observation current;

        internal struct Observation
        {
            internal bool Active, ResponseCompleted, InputRejected, Aborted;
            internal int Status, SerializationFailures;
            internal long Started, InputBytes, OutputBytes;
        }

        internal struct Snapshot
        {
            internal long Revision, Received, Completed, Active, PeakActive;
            internal long Responses2xx, Responses4xx, Responses5xx, OtherResponses, Incomplete, Aborted;
            internal long InputRejected, InputBytes, OutputBytes, SerializationFailures;
            internal long BodyReaders, PeakBodyReaders, ReservedBodyBytes, PeakReservedBodyBytes, BodyReadTimeouts, BodyAdmissionRefusals;
            internal double TotalDurationMs, MaxDurationMs, LastReloadMs;
            internal int DomainReloads;
            internal long ActiveAtLastReload;
            internal string StartedAtUtc;
            internal double AverageDurationMs => Completed == 0 ? 0 : TotalDurationMs / Completed;

            internal Dictionary<string, object> ToDict() => new Dictionary<string, object>
            {
                { "startedAtUtc", StartedAtUtc }, { "receivedRequests", Received },
                { "completedRequests", Completed }, { "activeRequests", Active }, { "peakActiveRequests", PeakActive },
                { "responses2xx", Responses2xx }, { "responses4xx", Responses4xx }, { "responses5xx", Responses5xx },
                { "otherResponses", OtherResponses }, { "incompleteRequests", Incomplete }, { "abortedRequests", Aborted },
                { "inputRejectedRequests", InputRejected }, { "inputBytesRead", InputBytes }, { "outputBytesWritten", OutputBytes },
                { "responseSerializationFailures", SerializationFailures },
                { "activeBodyReaders", BodyReaders }, { "peakBodyReaders", PeakBodyReaders },
                { "reservedBodyBytes", ReservedBodyBytes }, { "peakReservedBodyBytes", PeakReservedBodyBytes },
                { "bodyReadTimeouts", BodyReadTimeouts }, { "bodyAdmissionRefusals", BodyAdmissionRefusals },
                { "maxBodyReaders", MCPRequestInput.MaxConcurrentBodyReads }, { "maxReservedBodyBytes", MCPRequestInput.MaxReservedBodyBytes },
                { "bodyReadTimeoutMs", MCPRequestInput.BodyReadTimeoutMs },
                { "averageDurationMs", AverageDurationMs }, { "maxDurationMs", MaxDurationMs },
                { "domainReloadCount", DomainReloads }, { "lastDomainReloadMs", LastReloadMs },
                { "activeRequestsAtLastReload", ActiveAtLastReload }
            };
        }

        static MCPHttpDiagnostics()
        {
            totals.StartedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            totals.DomainReloads = SessionState.GetInt(ReloadKey + "Count", 0);
            long.TryParse(SessionState.GetString(ReloadKey + "Active", "0"), out totals.ActiveAtLastReload);
            if (long.TryParse(SessionState.GetString(ReloadKey + "Started", ""), out long started))
                totals.LastReloadMs = Math.Max(0, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            SessionState.EraseString(ReloadKey + "Started");
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
        }

        private static void BeforeReload()
        {
            var snapshot = Read();
            SessionState.SetInt(ReloadKey + "Count", snapshot.DomainReloads + 1);
            SessionState.SetString(ReloadKey + "Active", snapshot.Active.ToString(CultureInfo.InvariantCulture));
            SessionState.SetString(ReloadKey + "Started", Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture));
        }

        internal static Snapshot Read() { lock (Gate) return totals; }

        internal static bool TryBeginBody(long reservation)
        {
            lock (Gate)
            {
                totals.Revision++;
                if (totals.BodyReaders >= MCPRequestInput.MaxConcurrentBodyReads
                    || reservation > MCPRequestInput.MaxReservedBodyBytes - totals.ReservedBodyBytes)
                { totals.BodyAdmissionRefusals++; return false; }
                totals.BodyReaders++;
                totals.ReservedBodyBytes += reservation;
                totals.PeakBodyReaders = Math.Max(totals.PeakBodyReaders, totals.BodyReaders);
                totals.PeakReservedBodyBytes = Math.Max(totals.PeakReservedBodyBytes, totals.ReservedBodyBytes);
                return true;
            }
        }

        internal static void EndBody(long reservation)
        {
            lock (Gate) { totals.BodyReaders--; totals.ReservedBodyBytes -= reservation; totals.Revision++; }
        }

        internal static void BodyTimedOut() { lock (Gate) { totals.BodyReadTimeouts++; totals.Revision++; } }

        internal static Observation Begin()
        {
            var previous = current;
            current = new Observation { Active = true, Started = Stopwatch.GetTimestamp() };
            lock (Gate)
            {
                totals.Received++;
                totals.Active++;
                if (totals.Active > totals.PeakActive) totals.PeakActive = totals.Active;
                totals.Revision++;
            }
            return previous;
        }

        internal static void Finish(Observation previous)
        {
            var request = current;
            current = previous;
            if (!request.Active) return;
            double elapsed = (Stopwatch.GetTimestamp() - request.Started) * 1000.0 / Stopwatch.Frequency;
            lock (Gate)
            {
                totals.Active--;
                totals.Completed++;
                totals.InputBytes += request.InputBytes;
                totals.OutputBytes += request.OutputBytes;
                totals.SerializationFailures += request.SerializationFailures;
                if (request.InputRejected) totals.InputRejected++;
                if (request.Aborted) totals.Aborted++;
                if (!request.ResponseCompleted) totals.Incomplete++;
                else if (request.Status >= 200 && request.Status < 300) totals.Responses2xx++;
                else if (request.Status >= 400 && request.Status < 500) totals.Responses4xx++;
                else if (request.Status >= 500 && request.Status < 600) totals.Responses5xx++;
                else totals.OtherResponses++;
                totals.TotalDurationMs += elapsed;
                if (elapsed > totals.MaxDurationMs) totals.MaxDurationMs = elapsed;
                totals.Revision++;
            }
        }

        // The bridge handles each request synchronously on one worker; no payload or agent data is retained.
        internal static void ReadBytes(int count) { if (current.Active) current.InputBytes += count; }
        internal static void WroteBytes(int count) { if (current.Active) current.OutputBytes += count; }
        internal static void RejectInput() { if (current.Active) current.InputRejected = true; }
        internal static void Abort() { if (current.Active) current.Aborted = true; }
        internal static void SerializationFailed() { if (current.Active) current.SerializationFailures++; }
        internal static void ResponseCompleted(int status)
        {
            if (!current.Active) return;
            current.Status = status;
            current.ResponseCompleted = true;
        }
    }
}
