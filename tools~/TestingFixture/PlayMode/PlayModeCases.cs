using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityMcpValidation
{
    public class PlayModeCases
    {
        [UnityTest]
        public IEnumerator Pass()
        {
            yield return null;
            Assert.That(Application.isPlaying, Is.True);
        }

        [UnityTest]
        public IEnumerator Slow()
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }
    }

    public class PrebuildFailure : IPrebuildSetup
    {
        public void Setup() { throw new InvalidOperationException("MCP controlled prebuild failure"); }
        [Test] public void NeverStarted() { Assert.Fail("Prebuild failure should prevent test execution"); }
    }
}
