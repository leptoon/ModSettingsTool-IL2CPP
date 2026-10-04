# OpenCode — ModSettingsTool-IL2CPP project setup

## Config layers

| Layer | Path | Role |
|-------|------|------|
| Global (machine) | `%USERPROFILE%\.config\opencode\opencode.jsonc` | Shared plugins (e.g. `@openchamber/opencode-cursor`). |
| Project (git) | `opencode.json` at repo root | Providers, models, `instructions`, `plugin`, `references`, agents, permissions. |
| Cursor (git) | `.cursorrules` | Tiered context + MCP reminders. |

### Project `plugin`

`opencode.json` lists `@openchamber/opencode-cursor` for the Cursor bridge.

### Project `references`

`references.superstructures-sibling` → `C:/Users/Andrew/Documents/SuperStructure` for SMS process patterns only — **this repo's `CLAUDE.md` wins** on conflict. Adjust path on other machines.

### Permissions

Root `permission` matches FlexCompanion rollout template: broad allow for tools (including `task`, `lsp`, `skill`), with **read/edit deny** on `*.env`, keystores, `local.properties`, `google-services.json`, and `secrets/`.

**Restart OpenCode** after editing `opencode.json`.

## What loads every session

1. **`AGENTS.md`**
2. **`.claude/skills/ARCHITECTURE_INVARIANTS.md`**

**`CLAUDE.md` is not auto-injected.** Read on demand per `CLAUDE.md` § Read order.

## External review

Codex or bot reviews are **never** agent-triggered. See **`docs/workflows/CODEX_REVIEW_LOOP.md`**.
