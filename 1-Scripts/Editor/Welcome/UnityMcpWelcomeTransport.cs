using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace UnityMCP.Editor.Welcome
{
    internal static class UnityMcpWelcomeTransport
    {
        // Only framework/Unity types cross stamped assemblies; incompatible protocols use another key.
        private const string Key = "AnkleBreaker.Welcome.Transport.v1";
        private static Dictionary<string, object[]> Requests
        {
            get
            {
                var requests = AppDomain.CurrentDomain.GetData(Key) as Dictionary<string, object[]>;
                if (requests != null) return requests;
                requests = new Dictionary<string, object[]>();
                AppDomain.CurrentDomain.SetData(Key, requests);
                return requests;
            }
        }

        public static UnityWebRequest Acquire(string url, int timeout, bool bounded)
        {
            var requests = Requests;
            string key = timeout + ":" + url;
            if (requests.TryGetValue(key, out var entry))
            {
                entry[1] = (int)entry[1] + 1;
                return (UnityWebRequest)entry[0];
            }
            if (bounded && requests.Count >= 2) return null;
            UnityWebRequest request = null;
            try
            {
                using var perf = new UnityMcpWelcomePerf.Scope("Request.Start");
                request = UnityWebRequest.Get(url);
                request.timeout = timeout;
                request.SendWebRequest();
                requests.Add(key, new object[] { request, 1 });
                return request;
            }
            catch
            {
                request?.Dispose();
                throw;
            }
        }

        public static void Release(ref UnityWebRequest request)
        {
            if (request == null) return;
            var requests = Requests;
            string key = null;
            foreach (var pair in requests)
                if (ReferenceEquals(pair.Value[0], request)) { key = pair.Key; break; }
            if (key != null)
            {
                var entry = requests[key];
                int remaining = (int)entry[1] - 1;
                if (remaining > 0) { entry[1] = remaining; request = null; return; }
                requests.Remove(key);
            }
            if (!request.isDone) request.Abort();
            request.Dispose();
            request = null;
        }
    }
}
