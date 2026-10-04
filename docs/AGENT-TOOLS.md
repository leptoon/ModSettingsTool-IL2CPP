# Agent tools — OpenCode MCP and Cursor bridge

FlexCompanion is developed on **Windows** with **OpenCode** (CLI/TUI or Cursor via `@openchamber/opencode-cursor`). Parameter names differ from native Cursor tools.

## Repo root

- Primary clone: `C:\Users\Andrew\Documents\FlexCompanion`
- In an **isolated worktree**, use the shell working directory — do not assume `/workspace/` or a stale path.

## OpenCode MCP tool parameters

| Tool | Use | Parameter notes |
|------|-----|-----------------|
| `read` | Read a file | `filePath` (not `path`) |
| `glob` | Find files by pattern | `pattern` |
| `grep` | Search file contents | `pattern`, optional `path` |
| `bash` | Shell (PowerShell on this box) | `command` |
| `write` / `edit` | Create or patch files | `filePath`, content or old/new strings |
| `skill` | Load a platform or repo skill | `name` |
| `opencode_task` | Delegate to a configured subagent (OpenCode `task` bridge) | **`description`** (3–5 words), **`prompt`** (worker body), **`subagent_type`** (`explore`, `scout`, `review`, `tooling-writer`, …). Optional: `task_id`, `background`. Skill: `parallel-contract-orchestration`. |

## When to use what

- **Repository edits and verification:** OpenCode `bash`, `grep`, `glob`, `read`.
- **Subagents:** `opencode_task` (native Cursor `task` does not return results to OpenCode sessions).
- **Architecture invariants:** always-on via `instructions`; deeper policy in `CLAUDE.md` on demand.
- **Built-in OpenCode skills:** `customize-opencode`, `find-skills`, etc. via `skill`.
- **Repo skills:** `task-contract`, `verify-gates`, `privacy-review`, `parallel-contract-orchestration` under `.claude/skills/*/SKILL.md`.

## Windows verify commands

| Gate | Command |
|------|---------|
| Full build | `gradlew.bat build` (PowerShell) or `./gradlew build` (Git Bash) |
| Shared tests only | `gradlew.bat :shared:test` when diff is `:shared`-only |
| Contract gate | `node scripts/check-agent-task-contracts.mjs` |
| Whitespace gate | `node scripts/check-whitespace.mjs` |

Docs-only / workflow PRs: the two `node` gates only.

See also `docs/OPENCODE.md` and `docs/agent-workflow-profiles.json`.
