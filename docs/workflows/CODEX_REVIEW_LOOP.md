# Codex / external review loop (owner-initiated only)

This doc aligns with **`CLAUDE.md`** § No unsolicited code reviews and § Merge safety gate. If anything here conflicts with `CLAUDE.md`, **`CLAUDE.md` wins**.

## What agents must never do

- Comment `@codex review` or equivalent on a PR.
- Mark a draft ready **for the purpose of** triggering a bot review.
- Re-request, poll for, or wait on a Codex (or other external) review signal the owner did not ask for in the current session.
- Treat a missing 👍 or absent review as a merge blocker when the owner gave an explicit merge instruction.

## What CI does automatically

GitHub Actions (`.github/workflows/ci.yml`, when present on the branch) runs **Gradle and repo script gates** on push/PR. That is **not** permission for agents to solicit Codex or other cloud reviews.

## When the owner asks for a reviewed PR

Follow the owner's scope only. Batch local fixes, run required gates, inventory open review threads (including outdated), then push once per approved batch (`CLAUDE.md` § PR review and push discipline).

## Merge on owner instruction

Before merging, perform **passive** checks only:

1. Glance at open PR threads — if an unresolved P0/P1 or privacy / `FLEX-RULE` thread is visible, name it in one sentence and confirm.
2. **Contract-status gate** — if the PR ships contract work, the contract file must be `status: "completed"` with evidence in `verificationResults`.

Both pass → merge immediately. Resolved threads, absent reviews, and missing 👍 are **not** blockers.

## Handoff to a stronger model

Use OpenCode `/handoff` or an owner-directed escalation — not an unsolicited review request.
