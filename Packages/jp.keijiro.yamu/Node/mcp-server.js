#!/usr/bin/env node

const http = require('http');

// Custom error classes for Unity-specific issues
class UnityUnavailableError extends Error {
    constructor(message, data) {
        super(message);
        this.name = 'UnityUnavailableError';
        this.data = data;
    }
}

class UnityRestartingError extends Error {
    constructor(message, data) {
        super(message);
        this.name = 'UnityRestartingError';
        this.data = data;
    }
}

class MCPServer {
    constructor() {
        this.unityServerUrl = 'http://localhost:17932';
        this.capabilities = {
            tools: {
                compile_and_wait: {
                    description: "Request Unity Editor to compile C# scripts and wait for completion. Returns compilation status and any errors. IMPORTANT: For structural changes (new/deleted/moved files), call refresh_assets first (use force=true for deletions), wait for MCP responsiveness, then call this tool. Without refresh, Unity may not detect file changes. LLM HINTS: If you get Error -32603 with 'Unity HTTP server restarting', this is normal during compilation - wait 3-5 seconds and retry. If you get 'Unity Editor HTTP server unavailable', verify Unity Editor is running with YAMU project open.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            timeout: {
                                type: "number",
                                description: "Timeout in seconds (default: 30). LLM HINT: Use longer timeouts (45-60s) for large projects or complex compilation tasks.",
                                default: 30
                            }
                        },
                        required: []
                    }
                },
                run_tests: {
                    description: "Execute Unity Test Runner tests and wait for completion. Returns test results including pass/fail counts and detailed failure information. Supports both EditMode (editor tests) and PlayMode (runtime tests) execution. LLM HINTS: EditMode tests run faster but only test editor functionality. PlayMode tests simulate actual game runtime but take longer. If tests fail to start, Unity Test Runner may need initialization - wait and retry.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            test_mode: {
                                type: "string",
                                description: "Test mode: EditMode or PlayMode (default: PlayMode). LLM HINT: Use EditMode for quick verification of basic functionality, PlayMode for comprehensive runtime testing.",
                                enum: ["EditMode", "PlayMode"],
                                default: "PlayMode"
                            },
                            test_filter: {
                                type: "string",
                                description: "Test filter pattern (optional). The full name of the tests to match the filter, including namespace and fixture. This is usually in the format Namespace.FixtureName.TestName. If the test has test arguments, then include them in parenthesis. E.g. MyProject.Tests.MyTestClass2.MyTestWithMultipleValues(1). Use pipe '|' to separate different test names.",
                                default: ""
                            },
                            test_filter_regex: {
                                type: "string",
                                description: "Test filter regex pattern (optional). Use .NET Regex syntax to match test names by pattern. This is mapped to Unity's Filter.groupNames property for flexible test selection.",
                                default: ""
                            },
                            timeout: {
                                type: "number",
                                description: "Timeout in seconds (default: 60). LLM HINT: PlayMode tests typically need 60-120s, EditMode tests usually complete within 30s.",
                                default: 60
                            }
                        },
                        required: []
                    }
                },
                refresh_assets: {
                    description: "Force Unity to refresh the asset database. CRITICAL for file operations - Unity may not detect file system changes without this. Regular refresh works for new files, but force=true is required for deletions to prevent CS2001 'Source file could not be found' errors. Workflow: 1) Make file changes, 2) Call refresh_assets (force=true for deletions), 3) Wait for MCP responsiveness, 4) Call compile_and_wait. LLM HINTS: Always call this after creating/deleting/moving files in Unity project. Unity HTTP server will restart during refresh - expect temporary -32603 errors that resolve automatically.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            force: {
                                type: "boolean",
                                description: "Use ImportAssetOptions.ForceUpdate for stronger refresh. Set to true when deleting files to prevent Unity CS2001 errors. False is sufficient for new file creation. LLM HINT: Use force=true when deleting files, force=false when creating new files.",
                                default: false
                            }
                        },
                        required: []
                    }
                },
                editor_status: {
                    description: "Get current Unity Editor status including compilation state, test execution state, and play mode state. Returns real-time information about what the editor is currently doing.",
                    inputSchema: {
                        type: "object",
                        properties: {},
                        required: []
                    }
                }
            }
        };
    }

    async handleRequest(request) {
        const { method, params, id } = request;

        switch (method) {
            case 'initialize':
                const clientVersion = params?.protocolVersion;
                if (!clientVersion) {
                    return {
                        jsonrpc: '2.0',
                        id,
                        error: {
                            code: -32602,
                            message: 'Invalid params: protocolVersion is required'
                        }
                    };
                }

                return {
                    jsonrpc: '2.0',
                    id,
                    result: {
                        protocolVersion: '2024-11-05',
                        capabilities: this.capabilities,
                        serverInfo: {
                            name: 'YamuServer',
                            version: '1.0.0'
                        }
                    }
                };

            case 'tools/list':
                return {
                    jsonrpc: '2.0',
                    id,
                    result: {
                        tools: Object.entries(this.capabilities.tools).map(([name, tool]) => ({
                            name,
                            description: tool.description,
                            inputSchema: tool.inputSchema
                        }))
                    }
                };

            case 'tools/call':
                return await this.handleToolCall(params, id);

            default:
                return {
                    jsonrpc: '2.0',
                    id,
                    error: {
                        code: -32601,
                        message: 'Method not found'
                    }
                };
        }
    }

    async handleToolCall(params, id) {
        const { name, arguments: args } = params;

        try {
            switch (name) {
                case 'compile_and_wait':
                    return await this.callCompileAndWait(id, args.timeout || 30);
                case 'run_tests':
                    return await this.callRunTests(id, args.test_mode || 'PlayMode', args.test_filter || '', args.test_filter_regex || '', args.timeout || 60);
                case 'refresh_assets':
                    return await this.callRefreshAssets(id, args.force || false);
                case 'editor_status':
                    return await this.callEditorStatus(id);
                default:
                    return {
                        jsonrpc: '2.0',
                        id,
                        error: {
                            code: -32602,
                            message: `Unknown tool: ${name}`
                        }
                    };
            }
        } catch (error) {
            // Enhanced error handling with LLM-friendly instructions
            if (error instanceof UnityUnavailableError || error instanceof UnityRestartingError) {
                return {
                    jsonrpc: '2.0',
                    id,
                    error: {
                        code: -32603,
                        message: error.message,
                        data: error.data
                    }
                };
            } else {
                // Generic error fallback
                return {
                    jsonrpc: '2.0',
                    id,
                    error: {
                        code: -32603,
                        message: `Tool execution failed: ${error.message}`
                    }
                };
            }
        }
    }

    async callCompileAndWait(id, timeoutSeconds) {
        try {

            // Start compilation
            const compileResponse = await this.makeHttpRequest('/compile-and-wait');

            // C# side now ensures compilation has started, so we can immediately begin polling

            // Wait for completion with polling
            const startTime = Date.now();
            const timeoutMs = timeoutSeconds * 1000;

            // Wait for compilation to complete
            while (Date.now() - startTime < timeoutMs) {
                try {
                    const statusResponse = await this.makeHttpRequest('/compile-status');

                    if (statusResponse.status === 'idle') {

                        // Compilation completed, errors are included in status response
                        const errorText = statusResponse.errors && statusResponse.errors.length > 0
                            ? `Compilation completed with errors:\n${statusResponse.errors.map(err => `${err.file}:${err.line} - ${err.message}`).join('\n')}`
                            : 'Compilation completed successfully with no errors.';

                        return {
                            jsonrpc: '2.0',
                            id,
                            result: {
                                content: [{
                                    type: 'text',
                                    text: errorText
                                }]
                            }
                        };
                    }

                    // Wait 1 second before next poll
                    await new Promise(resolve => setTimeout(resolve, 1000));
                } catch (pollError) {
                    // Continue polling despite individual request failures
                    await new Promise(resolve => setTimeout(resolve, 2000));
                    continue;
                }
            }

            // Timeout reached
            throw new Error(`Compilation timeout after ${timeoutSeconds} seconds`);

        } catch (error) {
            throw new Error(`Failed to compile and wait: ${error.message}`);
        }
    }

    async callRunTests(id, testMode, testFilter, testFilterRegex, timeoutSeconds) {
        try {
            // Get initial status to capture current test run ID (if any)
            const initialStatus = await this.makeHttpRequest('/test-status');
            const initialTestRunId = initialStatus.testRunId;

            // Start test execution
            const runResponse = await this.makeHttpRequest(`/run-tests?mode=${testMode}&filter=${encodeURIComponent(testFilter)}&filter_regex=${encodeURIComponent(testFilterRegex)}`);

            // Wait for test execution to actually start and get new test run ID
            const startTime = Date.now();
            const timeoutMs = timeoutSeconds * 1000;
            let currentTestRunId = initialTestRunId;

            // First, wait for test execution to start (new test run ID)
            let testStarted = false;
            const startCheckTimeout = 10000; // 10 seconds timeout for test start

            while (Date.now() - startTime < startCheckTimeout) {
                try {
                    const statusResponse = await this.makeHttpRequest('/test-status');

                    if (statusResponse.testRunId && statusResponse.testRunId !== initialTestRunId) {
                        currentTestRunId = statusResponse.testRunId;
                        testStarted = true;
                        break;
                    }

                    // Wait 200ms before next poll for test start
                    await new Promise(resolve => setTimeout(resolve, 200));
                } catch (pollError) {
                    await new Promise(resolve => setTimeout(resolve, 500));
                    continue;
                }
            }

            if (!testStarted) {
                throw new Error('Test execution failed to start - no new test run ID detected');
            }

            // Now wait for completion with the specific test run ID
            while (Date.now() - startTime < timeoutMs) {
                try {
                    const statusResponse = await this.makeHttpRequest('/test-status');

                    // Check if this is the same test run and it's completed
                    if (statusResponse.testRunId === currentTestRunId && statusResponse.status === 'idle') {
                        // Test execution completed for our specific test run
                        const resultText = this.formatTestResults(statusResponse);

                        return {
                            jsonrpc: '2.0',
                            id,
                            result: {
                                content: [{
                                    type: 'text',
                                    text: resultText
                                }]
                            }
                        };
                    }

                    // Wait 1 second before next poll
                    await new Promise(resolve => setTimeout(resolve, 1000));
                } catch (pollError) {
                    // Continue polling despite individual request failures
                    await new Promise(resolve => setTimeout(resolve, 2000));
                    continue;
                }
            }

            // Timeout reached
            throw new Error(`Test execution timeout after ${timeoutSeconds} seconds`);

        } catch (error) {
            throw new Error(`Failed to run tests: ${error.message}`);
        }
    }

    async callRefreshAssets(id, force = false) {
        try {
            // Call Unity refresh endpoint with force parameter
            const refreshResponse = await this.makeHttpRequest(`/refresh-assets?force=${force}`);

            return {
                jsonrpc: '2.0',
                id,
                result: {
                    content: [{
                        type: 'text',
                        text: refreshResponse.message || 'Asset database refresh completed.'
                    }]
                }
            };

        } catch (error) {
            throw new Error(`Failed to refresh assets: ${error.message}`);
        }
    }

    async callEditorStatus(id) {
        try {
            // Call Unity editor-status endpoint
            const statusResponse = await this.makeHttpRequest('/editor-status');

            return {
                jsonrpc: '2.0',
                id,
                result: {
                    content: [{
                        type: 'text',
                        text: JSON.stringify(statusResponse)
                    }]
                }
            };

        } catch (error) {
            throw new Error(`Failed to get editor status: ${error.message}`);
        }
    }

    formatTestResults(statusResponse) {
        if (!statusResponse.testResults) {
            return 'Test execution completed but no results available.';
        }

        const { totalTests, passedTests, failedTests, skippedTests, duration, results } = statusResponse.testResults;

        let resultText = `Test Results:\n`;
        resultText += `Total: ${totalTests}, Passed: ${passedTests}, Failed: ${failedTests}, Skipped: ${skippedTests}\n`;
        resultText += `Duration: ${duration}s\n\n`;

        if (failedTests > 0 && results) {
            resultText += 'Failed Tests:\n';
            results.filter(test => test.outcome === 'Failed').forEach(test => {
                resultText += `- ${test.name}: ${test.message}\n`;
            });
        }

        return resultText;
    }

    async makeHttpRequest(path) {
        return new Promise((resolve, reject) => {
            const req = http.request(`${this.unityServerUrl}${path}`, { method: 'GET' }, (res) => {
                let data = '';
                res.on('data', chunk => data += chunk);
                res.on('end', () => {
                    try {
                        const parsed = JSON.parse(data);
                        resolve(parsed);
                    } catch (error) {
                        reject(new Error(`Invalid JSON response: ${data}`));
                    }
                });
            });

            req.on('error', (error) => {
                // Enhanced error detection for Unity server issues
                reject(this.createUnityServerError(error));
            });

            req.setTimeout(15000, () => {
                req.destroy();
                reject(this.createUnityTimeoutError());
            });

            req.end();
        });
    }

    createUnityServerError(error) {
        // Detect Unity server unavailability vs temporary restart
        if (error.code === 'ECONNREFUSED') {
            // Unity Editor not running or HTTP server not started
            return new UnityUnavailableError(
                `Unity Editor HTTP server unavailable at ${this.unityServerUrl}`,
                {
                    errorType: 'unity_server_unavailable',
                    instructions: 'INSTRUCTIONS FOR LLM: 1) Verify Unity Editor is running and YAMU project is open 2) Check if Unity HTTP server is active (should start automatically) 3) Test with: curl http://localhost:17932/compile-status 4) If Unity is running but server is down, restart Unity Editor',
                    retryable: false,
                    originalError: error.message
                }
            );
        } else if (error.code === 'ECONNRESET' || error.code === 'EPIPE') {
            // Unity HTTP server restarting (temporary)
            return new UnityRestartingError(
                'Unity HTTP server restarting during compilation/asset refresh',
                {
                    errorType: 'unity_server_restarting',
                    instructions: 'INSTRUCTIONS FOR LLM: This is normal behavior during Unity compilation. Wait 3-5 seconds and retry the operation. Unity automatically restarts HTTP server during script compilation and asset database refresh.',
                    retryable: true,
                    originalError: error.message
                }
            );
        } else {
            // Other HTTP errors
            return new Error(`HTTP request failed: ${error.message}`);
        }
    }

    createUnityTimeoutError() {
        return new UnityRestartingError(
            'Unity HTTP server timeout - likely restarting during compilation',
            {
                errorType: 'unity_server_restarting',
                instructions: 'INSTRUCTIONS FOR LLM: Unity server timeout usually indicates compilation or asset refresh in progress. Wait 3-5 seconds and retry the operation.',
                retryable: true,
                originalError: 'Request timeout after 15 seconds'
            }
        );
    }

    async checkUnityServerHealth() {
        try {
            const response = await this.makeHttpRequest('/compile-status');
            return { available: true, response };
        } catch (error) {
            return { available: false, error };
        }
    }

    start() {
        process.stdin.setEncoding('utf8');
        process.stdin.on('readable', () => {
            const chunk = process.stdin.read();
            if (chunk !== null) {
                this.processInput(chunk);
            }
        });

        process.stdin.on('end', () => {
            process.exit(0);
        });
    }

    async processInput(input) {
        const lines = input.trim().split('\n');

        for (const line of lines) {
            if (line.trim() === '') continue;

            try {
                const request = JSON.parse(line);
                const response = await this.handleRequest(request);
                process.stdout.write(JSON.stringify(response) + '\n');
            } catch (error) {
                const errorResponse = {
                    jsonrpc: '2.0',
                    id: null,
                    error: {
                        code: -32700,
                        message: 'Parse error'
                    }
                };
                process.stdout.write(JSON.stringify(errorResponse) + '\n');
            }
        }
    }
}

const server = new MCPServer();
server.start();