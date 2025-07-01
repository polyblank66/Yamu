using UnityEngine;
using UnityEditor;
using System.Net;
using System.Threading;
using System.IO;
using System.Text;
using UnityEditor.Compilation;
using System.Collections.Generic;
using System;

namespace YamuHttp
{
    [InitializeOnLoad]
    public static class Server
    {
        static HttpListener _listener;
        static Thread _thread;
        static List<object> _errorList = new List<object>();
        static Queue<Action> _mainThreadActions = new Queue<Action>();
        static bool _isCompiling = false;
        static DateTime _lastCompileTime = DateTime.MinValue;

        static Server()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:8000/");
            _listener.Start();

            _thread = new Thread(Worker);
            _thread.IsBackground = true;
            _thread.Start();

            CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationStarted += OnCompilationStarted;
            EditorApplication.update += OnEditorUpdate;

            EditorApplication.quitting += () =>
            {
                _listener.Stop();
                _thread.Abort();
            };
        }

        static void OnEditorUpdate()
        {
            while (_mainThreadActions.Count > 0)
            {
                _mainThreadActions.Dequeue().Invoke();
            }
        }

        static void OnCompilationStarted(string assemblyPath)
        {
            _isCompiling = true;
        }

        static void OnCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _isCompiling = false;
            _lastCompileTime = DateTime.Now;
            _errorList.Clear();
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    _errorList.Add(new { 
                        file = msg.file, 
                        line = msg.line, 
                        message = msg.message 
                    });
                }
            }
        }

        static void Worker()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    var request = context.Request;
                    var response = context.Response;

                    string responseString = "";
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    response.Headers.Add("Access-Control-Allow-Origin", "*");
                    response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.Url.AbsolutePath == "/compile")
                {
                    lock (_mainThreadActions)
                    {
                        _mainThreadActions.Enqueue(() => CompilationPipeline.RequestScriptCompilation());
                    }
                    responseString = "{\"status\":\"ok\", \"message\":\"Compilation requested.\"}";
                }
                else if (request.Url.AbsolutePath == "/compile-status")
                {
                    var status = _isCompiling ? "compiling" : 
                                EditorApplication.isCompiling ? "compiling" : "idle";
                    responseString = JsonUtility.ToJson(new { 
                        status = status,
                        isCompiling = _isCompiling || EditorApplication.isCompiling,
                        lastCompileTime = _lastCompileTime.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
                else if (request.Url.AbsolutePath == "/errors")
                {
                    responseString = JsonUtility.ToJson(new { errors = _errorList.ToArray() });
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
                catch (Exception ex)
                {
                    Debug.LogError($"YamuHttpServer error: {ex.Message}");
                }
            }
        }
    }
}
