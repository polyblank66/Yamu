//
// YamuServer.cs - Yamu MCP (Model Context Protocol) Server
//
// This file implements an HTTP server that enables external tools to interact with Unity Editor
// for compilation and test execution via MCP protocol. The server runs on a background thread
// and provides REST API endpoints for:
//
// - Script compilation triggering and status monitoring
// - PlayMode and EditMode test execution
// - Real-time compilation error reporting
// - Test result collection with detailed status
//
// Key features:
// - Automatic domain reload handling via [InitializeOnLoad]
// - PlayMode test execution without domain reload (preserves server state)
// - Thread-safe communication between background HTTP server and Unity main thread
// - Graceful shutdown on Unity exit or domain reload
//
// Note: Currently, PlayMode tests work by temporarily modifying Enter Play Mode settings
// to disable domain reload, which prevents server state loss. However, this approach
// may not be ideal and should potentially be replaced with a more robust solution that
// doesn't rely on changing Unity's global editor settings.
//

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
    // ============================================================================
    // CONFIGURATION AND CONSTANTS
    // ============================================================================
    
    // Configuration constants for the Yamu MCP server
    static class Constants
    {
        public const int ServerPort = 17932;
        public const int CompileTimeoutSeconds = 5;
        public const int ThreadSleepMilliseconds = 50;
        public const int ThreadJoinTimeoutMilliseconds = 1000;

        public static class Endpoints
        {
            public const string CompileAndWait = "/compile-and-wait";
            public const string CompileStatus = "/compile-status";
            public const string RunTests = "/run-tests";
            public const string TestStatus = "/test-status";
        }

        public static class JsonResponses
        {
            public const string CompileStarted = "{\"status\":\"ok\", \"message\":\"Compilation started.\"}";
            public const string TestStarted = "{\"status\":\"ok\", \"message\":\"Test execution started.\"}";
        }
    }

    // ============================================================================
    // DATA TRANSFER OBJECTS (DTOs)
    // ============================================================================
    // These classes define the JSON structure for API requests and responses
    
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

    // ============================================================================
    // MAIN HTTP SERVER CLASS
    // ============================================================================
    // Handles HTTP server lifecycle, request routing, and Unity integration
    
    [InitializeOnLoad]
    public static class Server
    {
        // ========================================================================
        // STATE VARIABLES
        // ========================================================================
        
        // HTTP server components
        static HttpListener _listener;
        static Thread _thread;
        
        // Compilation tracking
        static List<CompileError> _compilationErrors = new();
        static bool _isCompiling;
        static DateTime _lastCompileTime = DateTime.MinValue;  // When last compilation finished
        static DateTime _compileRequestTime = DateTime.MinValue;  // When compilation was requested
        
        // Unity main thread action queue (required for Unity API calls)
        static Queue<Action> _mainThreadActionQueue = new();
        
        // Shutdown coordination
        static volatile bool _shouldStop;

        // Test execution state
        internal static bool _isRunningTests;
        internal static DateTime _lastTestTime = DateTime.MinValue;
        internal static TestResults _testResults;
        internal static string _currentTestRunId = null;  // Unique ID to track test runs across domain reloads
        static TestCallbacks _testCallbacks;

        static Server()
        {
            Cleanup();

            _shouldStop = false;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Constants.ServerPort}/");
            _listener.Start();

            _thread = new(HttpRequestProcessor);
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
                if (!_thread.Join(Constants.ThreadJoinTimeoutMilliseconds))
                {
                    try
                    {
                        _thread.Abort();
                    }
                    catch { }
                }
            }
        }

        // ========================================================================
        // UNITY EVENT HANDLERS
        // ========================================================================
        
        static void OnEditorUpdate()
        {
            while (_mainThreadActionQueue.Count > 0)
                _mainThreadActionQueue.Dequeue().Invoke();
        }

        static void OnCompilationStarted(object obj) => _isCompiling = true;

        static void OnCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _isCompiling = false;
            _lastCompileTime = DateTime.Now;
            _compilationErrors.Clear();
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                    _compilationErrors.Add(new CompileError
                    {
                        file = msg.file,
                        line = msg.line,
                        message = msg.message
                    });
            }
        }

        // ========================================================================
        // HTTP SERVER INFRASTRUCTURE
        // ========================================================================
        
        static void HttpRequestProcessor()
        {
            while (!_shouldStop && _listener?.IsListening == true)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessHttpRequest(context);
                }
                catch (Exception ex)
                {
                    HandleHttpException(ex);
                }
            }
        }

        static void ProcessHttpRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            SetupResponseHeaders(response);

            var responseString = RouteRequest(request, response);
            SendResponse(response, responseString);
        }

        static void SetupResponseHeaders(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        static string RouteRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            return request.Url.AbsolutePath switch
            {
                Constants.Endpoints.CompileAndWait => HandleCompileAndWaitRequest(),
                Constants.Endpoints.CompileStatus => HandleCompileStatusRequest(),
                Constants.Endpoints.RunTests => HandleRunTestsRequest(request),
                Constants.Endpoints.TestStatus => HandleTestStatusRequest(),
                _ => HandleNotFoundRequest(response)
            };
        }

        static string HandleCompileAndWaitRequest()
        {
            _compileRequestTime = DateTime.Now;
            lock (_mainThreadActionQueue)
            {
                _mainThreadActionQueue.Enqueue(() => CompilationPipeline.RequestScriptCompilation());
            }

            var (success, message) = WaitForCompilationToStart(_compileRequestTime, TimeSpan.FromSeconds(Constants.CompileTimeoutSeconds));
            return success ? Constants.JsonResponses.CompileStarted : $"{{\"status\":\"warning\", \"message\":\"{message}\"}}";
        }

        static (bool success, string message) WaitForCompilationToStart(DateTime requestTime, TimeSpan timeout)
        {
            var waitStart = DateTime.Now;

            while ((DateTime.Now - waitStart) < timeout)
            {
                if (_isCompiling || EditorApplication.isCompiling)
                    return (true, "Compilation started.");

                if (_lastCompileTime > requestTime)
                    return (true, "Compilation completed quickly.");

                Thread.Sleep(Constants.ThreadSleepMilliseconds);
            }

            return (false, "Compilation may not have started.");
        }

        static string HandleCompileStatusRequest()
        {
            var status = _isCompiling || EditorApplication.isCompiling ? "compiling" : "idle";
            var statusResponse = new CompileStatusResponse
            {
                status = status,
                isCompiling = _isCompiling || EditorApplication.isCompiling,
                lastCompileTime = _lastCompileTime.ToString("yyyy-MM-dd HH:mm:ss"),
                errors = _compilationErrors.ToArray()
            };
            return JsonUtility.ToJson(statusResponse);
        }

        static string HandleRunTestsRequest(HttpListenerRequest request)
        {
            var query = request.Url.Query ?? "";
            var mode = ExtractQueryParameter(query, "mode") ?? "EditMode";
            var filter = ExtractQueryParameter(query, "filter");

            lock (_mainThreadActionQueue)
            {
                _mainThreadActionQueue.Enqueue(() => StartTestExecution(mode, filter));
            }

            return Constants.JsonResponses.TestStarted;
        }

        static string ExtractQueryParameter(string query, string paramName)
        {
            if (!query.Contains($"{paramName}="))
                return null;

            var paramStart = query.IndexOf($"{paramName}=") + paramName.Length + 1;
            var paramEnd = query.IndexOf("&", paramStart);
            var value = paramEnd == -1 ? query.Substring(paramStart) : query.Substring(paramStart, paramEnd - paramStart);
            return Uri.UnescapeDataString(value);
        }

        static string HandleTestStatusRequest()
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
            return JsonUtility.ToJson(statusResponse);
        }

        static string HandleNotFoundRequest(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return "{\"status\":\"error\", \"message\":\"Not Found\"}";
        }

        static void SendResponse(HttpListenerResponse response, string content)
        {
            var buffer = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        static void HandleHttpException(Exception ex)
        {
            if (ex is HttpListenerException || ex is ThreadAbortException)
                return;

            if (!_shouldStop)
                Debug.LogError($"YamuServer error: {ex.Message}");
        }

        // ========================================================================
        // TEST EXECUTION COORDINATION
        // ========================================================================
        
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

                // Override Enter Play Mode settings for PlayMode tests to avoid domain reload
                var originalEnterPlayModeOptionsEnabled = false;
                var originalEnterPlayModeOptions = EnterPlayModeOptions.None;

                if (testMode == TestMode.PlayMode)
                {
                    originalEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
                    originalEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;

                    EditorSettings.enterPlayModeOptionsEnabled = true;
                    EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

                    Debug.Log("Overriding Enter Play Mode settings to disable domain reload for PlayMode tests");
                }

                var api = ScriptableObject.CreateInstance<TestRunnerApi>();

                var filterObj = new Filter
                {
                    testMode = testMode
                };

                if (!string.IsNullOrEmpty(filter))
                    filterObj.testNames = new[] { filter };


                // Store original settings in test callbacks for restoration
                _testCallbacks.SetOriginalPlayModeSettings(testMode == TestMode.PlayMode, originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);

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

    // ============================================================================
    // TEST EXECUTION MANAGEMENT
    // ============================================================================
    // Handles Unity Test Runner callbacks and result collection
    
    class TestCallbacks : ICallbacks
    {
        bool _shouldRestorePlayModeSettings;
        bool _originalEnterPlayModeOptionsEnabled;
        EnterPlayModeOptions _originalEnterPlayModeOptions;

        public void SetOriginalPlayModeSettings(bool shouldRestore, bool originalEnabled, EnterPlayModeOptions originalOptions)
        {
            _shouldRestorePlayModeSettings = shouldRestore;
            _originalEnterPlayModeOptionsEnabled = originalEnabled;
            _originalEnterPlayModeOptions = originalOptions;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            Debug.Log($"Test run finished with status: {result.TestStatus}, ID: {Server._currentTestRunId}");

            var results = new List<TestResult>();
            CollectTestResults(result, results);


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

            // Restore original Enter Play Mode settings if they were overridden
            if (_shouldRestorePlayModeSettings)
            {
                EditorSettings.enterPlayModeOptionsEnabled = _originalEnterPlayModeOptionsEnabled;
                EditorSettings.enterPlayModeOptions = _originalEnterPlayModeOptions;
                Debug.Log("Restored original Enter Play Mode settings after PlayMode test completion");
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }

        void CollectTestResults(ITestResultAdaptor result, List<TestResult> results)
        {
            // Recursively collect test results from Unity's test hierarchy
            if (result.Test.IsTestAssembly)
            {
                // Assembly level - recurse into child test suites
                foreach (var child in result.Children)
                    CollectTestResults(child, results);
            }
            else if (result.Test.IsSuite)
            {
                // Test suite level - recurse into individual tests
                foreach (var child in result.Children)
                    CollectTestResults(child, results);
            }
            else
            {
                // Individual test - add to results
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
