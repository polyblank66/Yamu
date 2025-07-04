using UnityEngine;
using UnityEditor;
using System.Net;
using System.Threading;
using System.IO;
using System.Text;
using UnityEditor.Compilation;
using System.Collections.Generic;
using System;
using UnityEditor.TestTools.TestRunner.Api;
using System.Linq;

namespace Yamu
{
    [System.Serializable]
    public class CompileError
    {
        public string file;
        public int line;
        public string message;
    }

    [System.Serializable]
    public class CompileStatusResponse
    {
        public string status;
        public bool isCompiling;
        public string lastCompileTime;
        public CompileError[] errors;
    }

    [System.Serializable]
    public class TestResult
    {
        public string name;
        public string outcome;
        public string message;
        public double duration;
    }

    [System.Serializable]
    public class TestResults
    {
        public int totalTests;
        public int passedTests;
        public int failedTests;
        public int skippedTests;
        public double duration;
        public TestResult[] results;
    }

    [System.Serializable]
    public class TestStatusResponse
    {
        public string status;
        public bool isRunning;
        public string lastTestTime;
        public TestResults testResults;
        public string testRunId;
    }


    [InitializeOnLoad]
    public static class Server
    {
        static HttpListener _listener;
        static Thread _thread;
        static List<CompileError> _errorList = new();
        static Queue<Action> _mainThreadActions = new();
        static bool _isCompiling;
        static DateTime _lastCompileTime = DateTime.MinValue;
        static DateTime _compileRequestTime = DateTime.MinValue;
        static volatile bool _shouldStop;

        internal static bool _isRunningTests;
        internal static DateTime _lastTestTime = DateTime.MinValue;
        internal static TestResults _testResults;
        internal static string _currentTestRunId = null;
        static TestCallbacks _testCallbacks;

        static Server()
        {
            Cleanup();

            _shouldStop = false;
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:17932/");
            _listener.Start();

            _thread = new(Worker);
            _thread.IsBackground = true;
            _thread.Start();

            CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            EditorApplication.update += OnEditorUpdate;

            _testCallbacks = new TestCallbacks();

            EditorApplication.quitting += Cleanup;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        static void Cleanup()
        {
            _shouldStop = true;

            if (_listener?.IsListening == true)
            {
                try
                {
                    _listener.Stop();
                }
                catch { }
            }

            if (_thread?.IsAlive == true)
            {
                if (!_thread.Join(1000))
                {
                    try
                    {
                        _thread.Abort();
                    }
                    catch { }
                }
            }
        }


        static void OnEditorUpdate()
        {
            while (_mainThreadActions.Count > 0)
                _mainThreadActions.Dequeue().Invoke();
        }

        static void OnCompilationStarted(object obj) => _isCompiling = true;

        static void OnCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _isCompiling = false;
            _lastCompileTime = DateTime.Now;
            _errorList.Clear();
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                    _errorList.Add(new CompileError
                    {
                        file = msg.file,
                        line = msg.line,
                        message = msg.message
                    });
            }
        }

        static void Worker()
        {
            while (!_shouldStop && _listener?.IsListening == true)
            {
                try
                {
                    var context = _listener.GetContext();
                    var request = context.Request;
                    var response = context.Response;

                    var responseString = "";
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    response.Headers.Add("Access-Control-Allow-Origin", "*");
                    response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.Url.AbsolutePath == "/compile-and-wait")
                {
                    // Record request time and request compilation
                    _compileRequestTime = DateTime.Now;
                    lock (_mainThreadActions)
                    {
                        _mainThreadActions.Enqueue(() => CompilationPipeline.RequestScriptCompilation());
                    }

                    // Wait for compilation to actually start or timeout
                    var waitStart = DateTime.Now;
                    var timeout = TimeSpan.FromSeconds(5);

                    while ((DateTime.Now - waitStart) < timeout)
                    {
                        if (_isCompiling || EditorApplication.isCompiling)
                        {
                            responseString = "{\"status\":\"ok\", \"message\":\"Compilation started.\"}";
                            break;
                        }

                        // Check if compilation already completed (very fast compile)
                        if (_lastCompileTime > _compileRequestTime)
                        {
                            responseString = "{\"status\":\"ok\", \"message\":\"Compilation completed quickly.\"}";
                            break;
                        }

                        Thread.Sleep(50); // Small delay to avoid busy waiting
                    }

                    // If we get here without breaking, compilation didn't start
                    if (string.IsNullOrEmpty(responseString))
                    {
                        responseString = "{\"status\":\"warning\", \"message\":\"Compilation may not have started.\"}";
                    }
                }
                else if (request.Url.AbsolutePath == "/compile-status")
                {
                    var status = _isCompiling ? "compiling" :
                                EditorApplication.isCompiling ? "compiling" : "idle";
                    var statusResponse = new CompileStatusResponse
                    {
                        status = status,
                        isCompiling = _isCompiling || EditorApplication.isCompiling,
                        lastCompileTime = _lastCompileTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        errors = _errorList.ToArray()
                    };
                    responseString = JsonUtility.ToJson(statusResponse);
                }
                else if (request.Url.AbsolutePath == "/run-tests")
                {
                    var query = request.Url.Query ?? "";
                    var mode = "EditMode";
                    var filter = "";

                    if (query.Contains("mode="))
                    {
                        var modeStart = query.IndexOf("mode=") + 5;
                        var modeEnd = query.IndexOf("&", modeStart);
                        mode = modeEnd == -1 ? query.Substring(modeStart) : query.Substring(modeStart, modeEnd - modeStart);
                        mode = Uri.UnescapeDataString(mode);
                    }

                    if (query.Contains("filter="))
                    {
                        var filterStart = query.IndexOf("filter=") + 7;
                        var filterEnd = query.IndexOf("&", filterStart);
                        filter = filterEnd == -1 ? query.Substring(filterStart) : query.Substring(filterStart, filterEnd - filterStart);
                        filter = Uri.UnescapeDataString(filter);
                    }

                    lock (_mainThreadActions)
                    {
                        _mainThreadActions.Enqueue(() => StartTestExecution(mode, filter));
                    }

                    responseString = "{\"status\":\"ok\", \"message\":\"Test execution started.\"}";
                }
                else if (request.Url.AbsolutePath == "/test-status")
                {
                    var status = _isRunningTests ? "running" : "idle";
                    var statusResponse = new TestStatusResponse
                    {
                        status = status,
                        isRunning = _isRunningTests,
                        lastTestTime = _lastTestTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        testResults = _testResults,
                        testRunId = _currentTestRunId
                    };
                    responseString = JsonUtility.ToJson(statusResponse);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    responseString = "{\"status\":\"error\", \"message\":\"Not Found\"}";
                }

                    var buffer = Encoding.UTF8.GetBytes(responseString);
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_shouldStop)
                        Debug.LogError($"YamuServer error: {ex.Message}");
                }
            }
        }

        static void StartTestExecution(string mode, string filter)
        {
            if (_isRunningTests)
            {
                Debug.LogWarning("Test execution already in progress");
                return;
            }

            // Generate unique test run ID
            _currentTestRunId = Guid.NewGuid().ToString();
            _isRunningTests = true;
            _testResults = null;

            Debug.Log($"Starting test execution with ID: {_currentTestRunId}");

            try
            {
                var testMode = mode == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode;
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();

                var filterObj = new Filter
                {
                    testMode = testMode
                };

                if (!string.IsNullOrEmpty(filter))
                {
                    filterObj.testNames = new[] { filter };
                }

                Debug.Log($"Starting test execution with mode: {testMode}, filter: '{filter}'");
                api.RegisterCallbacks(_testCallbacks);
                api.Execute(new ExecutionSettings(filterObj));
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start test execution: {ex.Message}");
                _isRunningTests = false;
                _testResults = new TestResults
                {
                    totalTests = 0,
                    passedTests = 0,
                    failedTests = 1,
                    skippedTests = 0,
                    duration = 0,
                    results = new[] { new TestResult { name = "TestExecution", outcome = "Failed", message = ex.Message, duration = 0 } }
                };
            }
        }
    }

    class TestCallbacks : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun)
        {
            Debug.Log($"Test run started with {testsToRun.Children?.Count() ?? 0} test suites");
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            Debug.Log($"Test run finished with status: {result.TestStatus}, ID: {Server._currentTestRunId}");

            var results = new List<TestResult>();
            CollectTestResults(result, results);

            Debug.Log($"Collected {results.Count} test results for run ID: {Server._currentTestRunId}");

            // Update results first, then mark as complete
            Server._testResults = new TestResults
            {
                totalTests = results.Count,
                passedTests = results.Count(r => r.outcome == "Passed"),
                failedTests = results.Count(r => r.outcome == "Failed"),
                skippedTests = results.Count(r => r.outcome == "Skipped"),
                duration = result.Duration,
                results = results.ToArray()
            };

            Server._lastTestTime = DateTime.Now;
            // Mark as complete LAST to ensure results are available
            Server._isRunningTests = false;
        }

        public void TestStarted(ITestAdaptor test)
        {
            Debug.Log($"Test started: {test.FullName}");
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            Debug.Log($"Test finished: {result.Test.FullName} - {result.TestStatus}");
        }

        void CollectTestResults(ITestResultAdaptor result, List<TestResult> results)
        {
            if (result.Test.IsTestAssembly)
            {
                foreach (var child in result.Children)
                    CollectTestResults(child, results);
            }
            else if (result.Test.IsSuite)
            {
                foreach (var child in result.Children)
                    CollectTestResults(child, results);
            }
            else
            {
                results.Add(new TestResult
                {
                    name = result.Test.FullName,
                    outcome = result.TestStatus.ToString(),
                    message = result.Message ?? "",
                    duration = result.Duration
                });
            }
        }
    }
}
