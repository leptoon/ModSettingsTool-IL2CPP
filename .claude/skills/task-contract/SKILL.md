---
name: task-contract
description: Draft, revise, or validate FlexCompanion task contracts (docs/agent_tasks, INDEX.md, agent-task.schema.json). Use when starting substantial work, accepting a contract, or running the contract gate.
---

# Task contract skill

## When to use

- Substantial work (reader, data model/Redactor, Data Layer, signing, new modules, CI, broad docs).
- Before editing `docs/agent_tasks/*.json` or `docs/agent_tasks/INDEX.md`.

## Steps

1. Read `docs/agent_tasks/INDEX.md` for an accepted contract.
2. If none, draft `status: "proposed"` per `docs/agent-task.schema.json` and pick a profile from `docs/agent-workflow-profiles.json`.
3. Add an INDEX row; run `node scripts/check-agent-task-contracts.mjs`.
4. Stop for owner acceptance before product code changes.

## Authority

Full policy: `CLAUDE.md` § Task contracts. Narrow fixes may skip a contract with a one-line `Not substantial` reason in the PR body.
