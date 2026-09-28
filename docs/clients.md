# Connecting MCP clients

Each client can connect to either server in one of two ways:

- **stdio:** the client runs `docker run -i --rm … --stdio` itself. There's no port and no API key,
  but the SmarterMail password has to be in the client's config (VS Code can prompt for it instead).
- **HTTP:** you run the server yourself (see [mcp-user.md](mcp-user.md#http) or
  [`examples/docker-compose.yml`](../examples/docker-compose.yml)), and the client gets only the URL
  and the API key. The password stays on the server.

The examples use the user server. For the admin server, swap in
`ghcr.io/carbonitex/smartermail-mcp-admin:1` and `SMARTERMAIL_ADMIN_USER` /
`SMARTERMAIL_ADMIN_PASSWORD`, and use port 8103 instead of 8102.

Both servers start **read-only**. To allow changes, add `SMARTERMAIL_READ_ONLY=false`. In stdio
configs that means another `"-e", "SMARTERMAIL_READ_ONLY"` in `args` and the value in `env`.

Ready-to-edit files are in [`examples/mcp-configs/`](../examples/mcp-configs/).

## Claude Desktop

`claude_desktop_config.json` (Settings → Developer → Edit Config), stdio:

```json
{
  "mcpServers": {
    "smartermail": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
               "-e", "SMARTERMAIL_URL", "-e", "SMARTERMAIL_USER", "-e", "SMARTERMAIL_PASSWORD",
               "ghcr.io/carbonitex/smartermail-mcp-user:1", "--stdio"],
      "env": {
        "SMARTERMAIL_URL": "https://mail.example.com",
        "SMARTERMAIL_USER": "you@example.com",
        "SMARTERMAIL_PASSWORD": "your-password"
      }
    }
  }
}
```

`-e NAME` with no value passes the variable through from `env`, so the password isn't repeated in
`args`, where it would show up in process listings.

To use an HTTP server from Claude Desktop, run a local bridge such as
[`mcp-remote`](https://www.npmjs.com/package/mcp-remote), which can add the header. (Writing the
header as `Authorization:${AUTH_HEADER}` with no space avoids an argument-quoting problem on Windows.)

```json
{
  "mcpServers": {
    "smartermail": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://mcp.example.com/mcp",
               "--header", "Authorization:${AUTH_HEADER}"],
      "env": { "AUTH_HEADER": "Bearer your-api-key" }
    }
  }
}
```

## Claude Code

stdio:

```bash
claude mcp add smartermail \
  -e SMARTERMAIL_URL=https://mail.example.com \
  -e SMARTERMAIL_USER=you@example.com \
  -e SMARTERMAIL_PASSWORD='your-password' \
  -- docker run -i --rm -e SMARTERMAIL_URL -e SMARTERMAIL_USER -e SMARTERMAIL_PASSWORD \
     ghcr.io/carbonitex/smartermail-mcp-user:1 --stdio
```

HTTP:

```bash
claude mcp add --transport http smartermail http://localhost:8102/mcp \
  --header "Authorization: Bearer $API_KEY"
```

Add `--scope user` to make the server available in every project. `claude mcp list` shows whether
it connected.

## Cursor

`~/.cursor/mcp.json` (all projects) or `.cursor/mcp.json` (one project). stdio:

```json
{
  "mcpServers": {
    "smartermail": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
               "-e", "SMARTERMAIL_URL", "-e", "SMARTERMAIL_USER", "-e", "SMARTERMAIL_PASSWORD",
               "ghcr.io/carbonitex/smartermail-mcp-user:1", "--stdio"],
      "env": {
        "SMARTERMAIL_URL": "https://mail.example.com",
        "SMARTERMAIL_USER": "you@example.com",
        "SMARTERMAIL_PASSWORD": "your-password"
      }
    }
  }
}
```

HTTP:

```json
{
  "mcpServers": {
    "smartermail": {
      "url": "http://localhost:8102/mcp",
      "headers": { "Authorization": "Bearer your-api-key" }
    }
  }
}
```

## VS Code

`.vscode/mcp.json` in a workspace, or **MCP: Open User Configuration** for every workspace. VS Code
can prompt for secrets instead of storing them in the file:

```json
{
  "inputs": [
    { "type": "promptString", "id": "sm-password", "description": "SmarterMail password", "password": true }
  ],
  "servers": {
    "smartermail": {
      "type": "stdio",
      "command": "docker",
      "args": ["run", "-i", "--rm",
               "-e", "SMARTERMAIL_URL", "-e", "SMARTERMAIL_USER", "-e", "SMARTERMAIL_PASSWORD",
               "ghcr.io/carbonitex/smartermail-mcp-user:1", "--stdio"],
      "env": {
        "SMARTERMAIL_URL": "https://mail.example.com",
        "SMARTERMAIL_USER": "you@example.com",
        "SMARTERMAIL_PASSWORD": "${input:sm-password}"
      }
    }
  }
}
```

HTTP:

```json
{
  "inputs": [
    { "type": "promptString", "id": "sm-api-key", "description": "smartermail-mcp API key", "password": true }
  ],
  "servers": {
    "smartermail": {
      "type": "http",
      "url": "http://localhost:8102/mcp",
      "headers": { "Authorization": "Bearer ${input:sm-api-key}" }
    }
  }
}
```

## Generic Streamable HTTP

Any client that speaks MCP Streamable HTTP needs three things:

| | |
|---|---|
| URL | `http(s)://<host>:<port>/mcp` |
| Header | `Authorization: Bearer <API_KEY>` (or `X-API-Key: <API_KEY>`) |
| Accept | `application/json, text/event-stream` (clients normally send this; without it the server answers 406) |

The server is stateless: it doesn't issue an `Mcp-Session-Id`, and `GET /mcp` returns 405. If a
client can only take a URL and no headers, `?apiKey=<API_KEY>` works, but the key will end up in
proxy and access logs.

Quick check with curl:

```bash
curl -s http://localhost:8102/mcp \
  -H "Authorization: Bearer $API_KEY" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
```

The same settings work for the [agent's own `/mcp` endpoint](agent.md#using-it-as-an-mcp-server-multiple-accounts-nothing-to-install),
with an `sma_mcp_…` token as the bearer.

## Trying it out

[MCP Inspector](https://github.com/modelcontextprotocol/inspector) shows the tool list and lets you
call tools by hand:

```bash
npx @modelcontextprotocol/inspector \
  docker run -i --rm -e SMARTERMAIL_URL -e SMARTERMAIL_USER -e SMARTERMAIL_PASSWORD \
  ghcr.io/carbonitex/smartermail-mcp-user:1 --stdio
```

(with the three variables exported in your shell).
