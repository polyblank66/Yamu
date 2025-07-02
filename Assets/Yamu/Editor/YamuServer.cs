using UnityEngine;
using UnityEditor;
using System.Net;
using System.Threading;
using System.IO;
using System.Text;
using UnityEditor.Compilation;
using System.Collections.Generic;
using System;

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
    }

    [System.Serializable]
    public class ErrorListResponse
    {
        public CompileError[] errors;
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
                        lastCompileTime = _lastCompileTime.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                    responseString = JsonUtility.ToJson(statusResponse);
                }
                else if (request.Url.AbsolutePath == "/errors")
                {
                    var errorResponse = new ErrorListResponse { errors = _errorList.ToArray() };
                    responseString = JsonUtility.ToJson(errorResponse);
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
    }
}
