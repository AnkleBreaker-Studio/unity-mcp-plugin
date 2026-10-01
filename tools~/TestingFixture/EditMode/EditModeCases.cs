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
        [Test] public void Skip() { Assert.Ignore("MCP controlled ignored test"); }
        [Test] public void Inconclusive() { Assert.Inconclusive("MCP controlled inconclusive test"); }
        [TestCase(1), TestCase(2)] public void Parameterized(int value) { Assert.That(value, Is.GreaterThan(0)); }

        [UnityTest]
        public IEnumerator Slow()
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (EditorApplication.timeSinceStartup < deadline) yield return null;
        }
    }

    public class SetupFailureCases
    {
        [OneTimeSetUp] public void Setup() { Assert.Fail("MCP controlled fixture setup failure"); }
        [Test] public void First() { Assert.Fail("Setup failure should prevent execution"); }
        [Test] public void Second() { Assert.Fail("Setup failure should prevent execution"); }
    }
}
