#!/usr/bin/env node

const http = require('http');

class MCPServer {
    constructor() {
        this.unityServerUrl = 'http://localhost:8000';
        this.capabilities = {
            tools: {
                compile: {
                    description: "Request Unity Editor to compile C# scripts",
                    inputSchema: {
                        type: "object",
                        properties: {},
                        required: []
                    }
                },
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
                get_errors: {
                    description: "Get compilation errors from Unity Editor",
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
                            name: 'YamuHttpServer',
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
                case 'compile':
                    return await this.callCompile(id);
                case 'compile_and_wait':
                    return await this.callCompileAndWait(id, args.timeout || 30);
                case 'get_errors':
                    return await this.getErrors(id);
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

    async callCompile(id) {
        try {
            const response = await this.makeHttpRequest('/compile');
            return {
                jsonrpc: '2.0',
                id,
                result: {
                    content: [{
                        type: 'text',
                        text: `Compilation requested successfully. Status: ${response.status}`
                    }]
                }
            };
        } catch (error) {
            throw new Error(`Failed to request compilation: ${error.message}`);
        }
    }

    async callCompileAndWait(id, timeoutSeconds) {
        try {
            // Start compilation
            await this.makeHttpRequest('/compile');
            
            // Wait for completion with polling
            const startTime = Date.now();
            const timeoutMs = timeoutSeconds * 1000;
            
            while (Date.now() - startTime < timeoutMs) {
                const statusResponse = await this.makeHttpRequest('/compile-status');
                
                if (statusResponse.status === 'idle') {
                    // Compilation completed, get errors
                    const errorResponse = await this.makeHttpRequest('/errors');
                    const errorText = errorResponse.errors && errorResponse.errors.length > 0 
                        ? `Compilation completed with errors:\n${errorResponse.errors.map(err => `${err.file}:${err.line} - ${err.message}`).join('\n')}`
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
            }
            
            // Timeout reached
            throw new Error(`Compilation timeout after ${timeoutSeconds} seconds`);
            
        } catch (error) {
            throw new Error(`Failed to compile and wait: ${error.message}`);
        }
    }

    async getErrors(id) {
        try {
            const response = await this.makeHttpRequest('/errors');
            const errorText = response.errors && response.errors.length > 0 
                ? response.errors.map(err => `${err.file}:${err.line} - ${err.message}`).join('\n')
                : 'No compilation errors found.';
            
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
        } catch (error) {
            throw new Error(`Failed to get errors: ${error.message}`);
        }
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

            req.setTimeout(5000, () => {
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