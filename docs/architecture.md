# Architecture

This page is an overview for contributors. For the deeper internals (the agent's HTTP contract,
session model, two-step and remember-me flows), see [CLAUDE.md](../CLAUDE.md).

## The repo

One .NET 10 solution (`SmarterMail.slnx`) that builds three images:

```
src/
  Core/             SmarterMail REST API layer: sign-in, token refresh, the HTTP client (UserContext)
  Tools.Mailbox/    59 mailbox tools        ─┐
  Tools.DomainAdmin/107 domain_* tools      ├─ the only place tools are written
  Tools.SysAdmin/   58 system-admin tools   ─┘
  Mcp.Hosting/      shared MCP host: settings, sign-in, stdio/HTTP, API key, read-only filter
  McpUser/          → image smartermail-mcp-user   (Mailbox + DomainAdmin tools, prompts)
  McpAdmin/         → image smartermail-mcp-admin  (SysAdmin tools)
  Agent/            → image smartermail-agent      (all three libraries, browser UI, /mcp)
tests/
  SmarterMail.Tests/  Core, tool-library guards, Mcp.Hosting, docs/tools.md freshness
  Agent.Tests/        the agent
```

```
             ┌──────────── Tools.Mailbox ───────────┐
             │     Tools.DomainAdmin   Tools.SysAdmin│
             └──────┬──────────────┬─────────────┬───┘
                    │              │             │
   Core ◄───────────┴──────────────┴─────────────┘
    ▲
    ├── Mcp.Hosting ◄── McpUser   (one mailbox account, fixed by env)
    │               ◄── McpAdmin  (one sysadmin account, fixed by env)
    └── Agent                     (any number of accounts, signed in from the browser)
```

## Tools are written once

Every tool is a static method with `[McpServerTool]` in one of the three tool libraries. Each gets a
`UserContext` (an authenticated SmarterMail HTTP client) by dependency injection. Hosts don't define
tools; they only choose which libraries to load:

- **McpUser** loads Tools.Mailbox. It also loads Tools.DomainAdmin when a startup check shows the
  account is a domain admin.
- **McpAdmin** loads Tools.SysAdmin.
- **Agent** loads all three and decides for each signed-in account which tools that account's role
  can use.

A fix to a tool therefore reaches every host at once. The tests pin the tool counts and check that
names are unique. They also check that no shared library, and no hosting code, can start a process.

## Read and write marks

Each tool says whether it only reads (`[McpServerTool(ReadOnly = true)]`). A tool without the mark
counts as a write, so a new tool is treated as a write until someone marks it. Writes that delete,
disable or disconnect are also marked `Destructive = true`. These marks:

- reach MCP clients as `readOnlyHint` / `destructiveHint`;
- decide what a read-only MCP server lists, because `Mcp.Hosting` removes every unmarked tool from
  the server;
- decide what the agent offers a read-only account.

[`docs/tools.md`](tools.md) is generated from the tools themselves, with the marks, by a test. A
stale copy fails the build.

## The MCP servers

`Mcp.Hosting` does, in order:

1. Validates the environment. Configuration errors exit with code 1 before any network call.
2. Signs in to SmarterMail. Temporary failures retry with backoff. A rejection exits with code 2
   and is not retried.
3. Serves MCP, either over **stdio** (the client launches the container; logs go to stderr) or
   over **stateless Streamable HTTP** at `/mcp`, behind an API key. `/health` is anonymous.

Each server is bound to one account for its whole life. The session token is refreshed in the
background, and if the refresh fails the server signs in again from its environment.

## The agent

The agent is a **relay**, not an AI host:

```
browser ──(OpenRouter key, chat loop)──► OpenRouter
   │
   └──(session cookie)──► agent ──(SmarterMail tokens, in memory)──► user's SmarterMail server(s)
```

- SmarterMail sends no CORS headers, so the browser can't call the mail server itself. The agent
  makes those calls on the browser's behalf.
- The OpenRouter key and the conversation stay in the browser. The agent never sees them.
- SmarterMail tokens exist only in the agent's memory for the session. With "Remember me", the
  browser holds an encrypted copy that only the agent's `RESUME_KEY` can open.
- One session can hold several accounts, possibly on different servers. Tool schemas get an
  `account` argument when more than one account could run a tool.
- Every tool call from the UI (`/api/tools/call`) and from MCP clients (`/mcp`, with an `sma_mcp_…`
  token) goes through the same dispatcher. It picks the account, checks the role and read-only
  mode, runs the tool and retries once on an expired token.
- Outbound connections pass an SSRF guard, both before sign-in and again when each connection is
  made.

## Build and release

- Every Dockerfile builds from the repo root and runs the test suites before `dotnet publish`, so a
  red test means no image.
- `.github/workflows/ci.yml` builds, runs the tests (including the frontend's Node tests) and builds
  the images on every push and pull request.
- `.github/workflows/release.yml` publishes multi-arch images to GHCR when a `v*` tag is pushed.
- One version covers the whole repo. It shows up in MCP `serverInfo` and the MCP servers' `/health`.
