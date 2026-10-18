# Tool contract fixtures

The real `GET /api/tools` response (`[{ name, description, inputSchema, category, scope, write, destructive }]`),
produced by `ToolCatalog.List` from the real tool registration, with fake accounts. All accounts are read-write.

| File | Session |
|---|---|
| `tools-user.json` | one `User` (`user@example.com`): the mailbox tools |
| `tools-domainadmin.json` | one `DomainAdmin` (`domainadmin@example.com`): mailbox + `domain_*` tools |
| `tools-sysadmin.json` | one `SysAdmin` (`sysadmin:admin@mail.example.com`): the system admin tools |
| `tools-multi.json` | a `User` + a `SysAdmin`: both sets, each schema with the injected `account` enum |

## Regenerate

After a tool is added or changed, from the repo root:

```sh
DUMP_TOOL_CONTRACT=$PWD/src/Agent/wwwroot/dev/local-model/fixtures \
  dotnet test tests/Agent.Tests --filter ToolContractDump
```

The test (`tests/Agent.Tests/ToolContractDumpTests.cs`) does nothing without the variable.
`TOOLS=full node wwwroot/dev/stub-server.mjs` serves these files.

## Size

`chars` is the length of the OpenAI-style `tools` array as `toolsToOpenAI` (`js/llm.js`) builds it,
as compact JSON. Tokens are approximate (chars / 3.5); a real tokenizer will differ.

| Fixture | Tools | Chars | ~Tokens |
|---|---|---|---|
| user | 59 | 37,599 | 10,700 |
| domainadmin | 166 | 104,338 | 29,800 |
| sysadmin | 58 | 19,574 | 5,600 |
| multi | 117 | 78,080 | 22,300 |
