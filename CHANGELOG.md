# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). One version covers all three images.

## [Unreleased]

### Fixed

- `examples/`: `docker compose up <one service>` no longer fails because another service's settings
  are empty, and `.env.example` quotes passwords so a `$` in one survives Compose's interpolation.

## [1.0.0]

First public release.

### Added

- **smartermail-agent**: browser chat for SmarterMail. You bring your own OpenRouter key, and one chat
  can hold several accounts (mailbox, domain admin, system admin). It includes two-factor sign-in,
  "remember me" with an optional passkey lock, and its own `/mcp` endpoint with revocable tokens.
- **smartermail-mcp-user**: MCP server for one mailbox, with 59 tools and 5 prompts. It adds 107
  domain-admin tools when the account is a domain admin.
- **smartermail-mcp-admin**: MCP server for one system-admin account, with 58 tools.
- Both MCP servers run over **stdio** (`--stdio`) or **Streamable HTTP**. They are **read-only by
  default**, and read-only mode leaves write tools out of `tools/list` entirely.
- Every tool carries `readOnlyHint`, and destructive ones carry `destructiveHint`. The tool reference
  in `docs/tools.md` is generated from the code.
- Multi-arch images (`linux/amd64`, `linux/arm64`) on GHCR. They run as a non-root user.

### Security

- The agent checks mail-server addresses against private ranges again at connect time, which stops
  DNS rebinding, and it never follows redirects.
- The agent trusts forwarded client-IP headers only from `TRUSTED_PROXIES`.

### Roadmap

- A log-analysis harness for the admin server that runs in-process.
- NuGet tool packages (`dnx`) and an MCP Registry listing.

[Unreleased]: https://github.com/Carbonitex/smartermail-agent/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Carbonitex/smartermail-agent/releases/tag/v1.0.0
