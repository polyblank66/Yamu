# Yamu

**Yamu** (Yet Another Minimal MCP server for Unity) is an experimental MCP
(Model Context Protocol) server that enables AI coding agents to interact with
Unity projects.

## Features

- `compile_and_wait` - Triggers Unity Editor compilation and waits for
  completion
- `get_errors` - Retrieves compilation errors from the last build

## Purpose

This proof-of-concept demonstrates how AI coding agents (Claude Code, Gemini
CLI, etc.) can autonomously iterate through edit-compile-debug cycles in Unity
development when provided with these basic compilation feedback mechanisms via
MCP.

## Prerequisites

- **Platform**: macOS (only tested platform)
- **Node.js**: Required to run the intermediate server
  ```bash
  brew install node
  ```

## Quick Start

1. Open this project in Unity Editor
2. Run Claude Code or Gemini CLI
3. Check MCP server connection with `/mcp` command
4. Order the agent to trigger compilation and retrieve errors
5. Order the agent to add incorrect code to `TestScript.cs` and trigger
   compilation
6. Order the agent to fix the issues until compilation succeeds

## Setup with Your Own Project

### 1. Install Package

Copy `Packages/jp.keijiro.yamu` to your Unity project.

### 2. Configure AI Client

#### For Gemini CLI
Copy `.gemini/settings.json` to your project directory.

If you already have a `.gemini/settings.json` file, add the `Yamu` entry to
your existing configuration.

#### For Claude Code
Copy `.mcp.json` to your project directory.

If you already have a `.mcp.json` file, add the `Yamu` entry to your existing
configuration.
