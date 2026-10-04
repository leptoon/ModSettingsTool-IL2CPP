---
name: verify-gates
description: Run FlexCompanion canonical verification gates (Gradle, contract script, whitespace) per docs/agent-workflow-profiles.json. Use before PR push, after implementation, or when running /verify.
---

# Verify gates skill

## Profile selection

Read `docs/agent-workflow-profiles.json` for the active task profile (`docs-only`, `shared-build`, `app-build`, `device-smoke`).

## Typical commands (Windows)

- Docs-only: `node scripts/check-agent-task-contracts.mjs` then `node scripts/check-whitespace.mjs`
- `:shared`-only: `gradlew.bat :shared:test` + both node gates
- App/build: `gradlew.bat build` + both node gates

Git Bash may use `./gradlew` instead of `gradlew.bat`.

## Rules

Do not substitute weaker checks. Do not repair unrelated failures. Report exact commands and exit codes.

Authority: `CLAUDE.md` § Pre-merge gates and § Verification workflow.
