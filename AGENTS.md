# AGENTS.md — repo-root agent entry point

Entry point for coding agents. **`CLAUDE.md`** is local/gitignored; use sibling **ModSettingsTool** for the full manifest when needed.

## OpenCode bootstrap (auto-loaded every session)

Repo-root **`opencode.json`** → `instructions` injects **only** this file and
**`.claude/skills/ARCHITECTURE_INVARIANTS.md`**. Read local **`CLAUDE.md`** on demand. Details: **`docs/OPENCODE.md`**.

## Read first

- **`README.md`** — project overview.
- **`.claude/skills/ARCHITECTURE_INVARIANTS.md`** — guardrails.
- Sibling **`ModSettingsTool`** — shared workflow and `CLAUDE.md` reference.
