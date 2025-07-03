#!/usr/bin/env node

const http = require('http');

class MCPServer {
    constructor() {
        this.unityServerUrl = 'http://localhost:17932';
        this.capabilities = {
            tools: {
                compile_and_wait: {
                    description: "Request Unity Editor to compile C# scripts and wait for completion",
                    inputSchema: {
                        type: "object",
                        properties: {
                            timeout: {
                                type: "number",
                                description: "Timeout in seconds (default: 30)",
                                default: 30
                            }
                        },
                        required: []
                    }
                },
                run_tests: {
                    description: "Execute Unity tests and wait for completion",
                    inputSchema: {
                        type: "object",
                        properties: {
                            test_mode: {
                                type: "string",
                                description: "Test mode: EditMode or PlayMode (default: PlayMode)",
                                enum: ["EditMode", "PlayMode"],
                                default: "PlayMode"
                            },
                            test_filter: {
                                type: "string",
                                description: "Test filter pattern (optional)",
                                default: ""
                            },
                            timeout: {
                                type: "number",
                                description: "Timeout in seconds (default: 60)",
                                default: 60
                            }
                        },
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
                    return await this.callRunTests(id, args.test_mode || 'PlayMode', args.test_filter || '', args.timeout || 60);
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

    async callRunTests(id, testMode, testFilter, timeoutSeconds) {
        try {
            // Start test execution
            const runResponse = await this.makeHttpRequest(`/run-tests?mode=${testMode}&filter=${encodeURIComponent(testFilter)}`);

            // Wait for completion with polling
            const startTime = Date.now();
            const timeoutMs = timeoutSeconds * 1000;

            while (Date.now() - startTime < timeoutMs) {
                try {
                    const statusResponse = await this.makeHttpRequest('/test-status');

                    if (statusResponse.status === 'idle') {

                        // Test execution completed
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
                reject(new Error(`HTTP request failed: ${error.message}`));
            });

            req.setTimeout(15000, () => {
                req.destroy();
                reject(new Error('Request timeout'));
            });

            req.end();
        });
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