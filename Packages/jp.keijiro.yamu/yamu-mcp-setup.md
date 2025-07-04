# MCP Server Setup Instructions for AI Agents

Add an MCP server entry to your **per-project agent settings file**.

Example:

```
{
  "mcpServers": {
    "yamu": {
      "command": "node",
      "args": ["Library/PackageCache/jp.keijiro.yamu@(HASH)/Node/mcp-server.js"]
    }
  }
}
```

`(HASH)` varies between versions, so you should locate it using a shell command.

The location of the **per-project agent settings file** depends on the AI agent
you're using. For Claude Code, the file is usually `.mcp.json` at the project
root. For Gemini CLI, the most common location is `.gemini/settings.json`.

If the file already contains a `yamu` entry in the `mcpServers` section, you
only need to update the `(HASH)` in the `args` field.
