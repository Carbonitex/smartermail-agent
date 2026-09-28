# Contributing

Thanks for helping. Bug reports, SmarterMail version quirks and small focused pull requests are the most
useful. For anything large (a new tool family, a new host), open an issue first so we can agree on the
shape.

## Getting set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Node 22+ is needed for the agent's
frontend tests, and Docker for image builds.

```bash
dotnet build SmarterMail.slnx                          # warnings are errors
dotnet test SmarterMail.slnx                           # offline, a few seconds
node --test 'src/Agent/wwwroot/dev/test/*.test.mjs'   # agent frontend
scripts/build-images.sh all                            # local Docker images (runs the tests too)
```

The agent's frontend can be developed without a mail server or an AI key: see
`src/Agent/wwwroot/dev/README.md`.

`CLAUDE.md` (and the `CLAUDE.md` in each `src/` project) describes the internals in detail: the tool
libraries, the read/write rules, the agent's session and security model. Read the relevant part
before changing it.

## Adding or changing a tool

Tools live only in `src/Tools.Mailbox`, `src/Tools.DomainAdmin` and `src/Tools.SysAdmin`. The MCP
servers and the agent all load them from there.

1. Add the method with `[McpServerTool]` and a clear `[Description]`. Domain-admin tools are named
   `domain_*`.
2. **Mark reads:** `[McpServerTool(ReadOnly = true)]` if the tool changes nothing. Leave it unmarked if
   it writes (an unmarked tool is a write everywhere). Add `Destructive = true` if it deletes, disables
   or disconnects something.
3. If it is a new class, give it a scope and category in `src/Agent/Mcp/ToolPolicy.cs` (`Groups`), or
   the agent will refuse to start.
4. Update the counts in `tests/SmarterMail.Tests/ToolLibraryTests.cs` and `ToolAnnotationTests.cs`, and
   the pinned read/write lists in `tests/Agent.Tests/ToolPolicyTests.cs`.
5. Regenerate the tool reference:
   `UPDATE_TOOL_DOCS=1 dotnet test tests/SmarterMail.Tests --filter ToolReferenceTests`.

Tool results go to the user's model provider, so redact secrets (passwords, keys, tokens) in anything a
tool returns. Shared code must not start processes; a test enforces this.

## Pull requests

- Keep each PR to one change, with tests where it makes sense. `dotnet test` and the node tests must pass.
- Don't commit credentials, server addresses or real mail data, including in tests and fixtures. Use
  `example.com` and fake data.
- Describe how you tested it, and against which SmarterMail build if you used a real server.
- User-visible changes get a line under **Unreleased** in `CHANGELOG.md`.

By contributing you agree that your work is released under the MIT License.

## Maintainer notes

The canonical repository is a private Forgejo instance that push-mirrors to GitHub. A pull request
merged on GitHub would be overwritten by the next mirror sync, so PRs are merged locally and pushed to
the canonical remote:

```bash
git fetch github pull/<n>/head:pr-<n>
git checkout main && git merge --no-ff pr-<n>
git push forgejo main               # the mirror updates GitHub and closes the PR as merged
```

Releasing: move **Unreleased** in `CHANGELOG.md` under a new version heading, commit, then tag `vX.Y.Z`
and push the tag. `release.yml` builds and publishes the images and the GitHub release. Try a
`vX.Y.Z-rc.N` tag first for anything risky.
