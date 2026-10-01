using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpRequestShutdownValidation
{
    public static void Run()
    {
        var loggedErrors = new List<string>();
        Application.LogCallback capture = (message, stack, type) => {
            if (type == LogType.Error && message.StartsWith("[AB-UMCP] Request failed:"))
                lock (loggedErrors) loggedErrors.Add(message);
        };
        var listener = new HttpListener();
        Thread worker = null, client = null;
        string failure = null;
        string clientResult = null, workerFailure = null;
        bool waiterObserved = false;
        Application.logMessageReceivedThreaded += capture;
        try
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            string url = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(url); listener.Start();
            var handler = (Action<HttpListenerContext>)Delegate.CreateDelegate(typeof(Action<HttpListenerContext>),
                typeof(MCPBridgeServer).GetMethod("HandleRequest", BindingFlags.NonPublic | BindingFlags.Static));
            worker = new Thread(() => { try { handler(listener.GetContext()); } catch (ThreadAbortException) { } catch (Exception error) { workerFailure = error.ToString(); } });
            worker.IsBackground = true; worker.Start();
            client = new Thread(() => {
                try { var request = WebRequest.CreateHttp(url + "api/ping"); request.Method = "POST"; request.ContentLength = 0; request.Proxy = null; request.Timeout = 5000;
                    using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) clientResult = reader.ReadToEnd(); }
                catch (WebException error) { clientResult = error.Message; }
            });
            client.IsBackground = true; client.Start();
            var queue = typeof(MCPBridgeServer).Assembly.GetType("UnityMCP.Editor.MCPRequestQueue", true);
            var waiters = (IDictionary)queue.GetField("_waiters", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            object gate = queue.GetField("_queueLock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do { lock (gate) waiterObserved = waiters.Count > 0; if (!waiterObserved) Thread.Sleep(10); }
            while (!waiterObserved && DateTime.UtcNow < deadline);
            if (!waiterObserved) throw new InvalidOperationException("HTTP worker did not reach its main-thread wait");
            worker.Abort();
            if (!worker.Join(5000)) throw new InvalidOperationException("Aborted HTTP worker did not exit");
        }
        catch (Exception error) { failure = error.GetBaseException().Message; }
        finally
        {
            listener.Close();
            if (worker != null && worker.IsAlive) { worker.Abort(); worker.Join(5000); }
            client?.Join(6000);
            Application.logMessageReceivedThreaded -= capture;
            bool passed = failure == null && waiterObserved && loggedErrors.Count == 0;
            File.WriteAllText("Library/UnityMcpRequestShutdownValidation.json", MiniJson.Serialize(new {
                unityVersion = Application.unityVersion, passed, waiterObserved, requestErrorLogs = loggedErrors.Count, failure, clientResult, workerFailure,
                scope = "Owned loopback HTTP worker aborted while waiting for the editor; no domain reload or native tests"
            }));
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }
}
