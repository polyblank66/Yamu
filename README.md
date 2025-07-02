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
