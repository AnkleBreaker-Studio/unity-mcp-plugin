using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace UnityMcpValidation
{
    public class EditModeCases
    {
        [Test] public void Pass() { Assert.Pass(); }
        [Test] public void Fail() { Assert.Fail("MCP controlled assertion failure"); }

        [UnityTest]
        public IEnumerator Slow()
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (EditorApplication.timeSinceStartup < deadline) yield return null;
        }
    }
}
