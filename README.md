# Yamu

**Yamu** (Yet Another Minimal MCP server for Unity) is an experimental MCP
(Model Context Protocol) server that enables AI coding agents to interact with
Unity projects.

## Features

- `compile_and_wait` - Triggers Unity Editor compilation, waits for completion,
  and returns compilation results including any errors
- `run_tests` - Executes Unity Test Runner tests (both EditMode and PlayMode)
  with real-time status monitoring and detailed result reporting

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

## Installation

### 1. Install the Package

You can install the Yamu package (`jp.keijiro.yamu`) via the "Keijiro" scoped
registry using the Unity Package Manager. To add the registry to your project,
follow [these instructions].

[these instructions]:
  https://gist.github.com/keijiro/f8c7e8ff29bfe63d86b888901b82644c

### 2. Add the MCP Server to the AI Agent

You can either follow the steps in [`yamu-mcp-setup.md`] manually, or let the
AI agent do it for you. For example, if you're using Gemini CLI:

```
> You're Gemini CLI. Follow yamu-mcp-setup.md
```

The "You're ---" statement is important, as some AI agents don't know what they
are unless explicitly told.

**Note**: You’ll need to update this configuration each time you upgrade Yamu.
You can simply run the same prompt again to refresh it.

[`yamu-mcp-setup.md`]: Packages/jp.keijiro.yamu/yamu-mcp-setup.md
