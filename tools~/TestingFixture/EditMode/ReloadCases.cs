using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace UnityMcpValidation
{
    public class ReloadCases
    {
        [Test, Order(1)] public void PassBeforeReload() { Assert.Pass(); }
        [Test, Order(2)] public void FailBeforeReload() { Assert.Fail("MCP failure before reload"); }

        [UnityTest, Order(3)]
        public IEnumerator ReloadAndPass()
        {
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (EditorApplication.timeSinceStartup < deadline) yield return null;
            SessionState.SetInt("McpValidation_ReloadRequests", SessionState.GetInt("McpValidation_ReloadRequests", 0) + 1);
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
            Assert.That(SessionState.GetInt("McpValidation_ReloadRequests", 0), Is.EqualTo(1), "The test was replayed after reload");
        }
    }
}
