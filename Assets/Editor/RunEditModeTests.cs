using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace VRZ.EditorTools
{
    /// One-shot runner: executes the EditMode test assembly and logs a summary + every failure.
    /// Exists so tests can be run from tooling (MCP) without the Test Runner window.
    public static class RunEditModeTests
    {
        private class Report : ICallbacks
        {
            public void RunStarted(ITestAdaptor t) => Debug.Log("[Tests] Run started: " + t.FullName);
            public void TestStarted(ITestAdaptor t) { }
            public void TestFinished(ITestResultAdaptor r)
            {
                if (r.Test.IsSuite) return;
                if (r.TestStatus == TestStatus.Passed) return;
                Debug.LogError("[Tests] FAIL " + r.Test.FullName + "\n" + r.Message + "\n" + r.StackTrace);
            }
            public void RunFinished(ITestResultAdaptor r)
            {
                Debug.Log($"[Tests] DONE passed={r.PassCount} failed={r.FailCount} skipped={r.SkipCount} inconclusive={r.InconclusiveCount} in {r.Duration:F2}s");
                _api?.UnregisterCallbacks(this);
            }
        }

        private static TestRunnerApi _api;

        [MenuItem("VRZ/Run EditMode Tests")]
        public static void Run()
        {
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new Report());
            _api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "VRZ.Tests.EditMode" }
            }));
        }
    }
}
