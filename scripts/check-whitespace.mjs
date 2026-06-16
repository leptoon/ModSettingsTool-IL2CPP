#!/usr/bin/env node
// Base-aware whitespace gate: checks the committed diff, not just the worktree.
//
// Why this wrapper exists:
// - Plain `git diff --check` only inspects the worktree, not the committed PR diff,
//   so a post-commit invocation returns success even if the diff introduced trailing
//   whitespace or mixed indent.
// - `git diff --check origin/main...HEAD` is base-aware but hard-fails with
//   `fatal: ambiguous argument` on a fresh agent checkout that has no `origin` remote.
//
// This wrapper picks the best available base:
//   1. `origin/main` if the remote-tracking ref is present (normal GitHub clone)
//   2. `main` if a local main branch exists (worktree-only or fork without origin/main)
//   3. Worktree-only fallback (`git diff --check`): limited but better than a hard fail
//      on a repo without either ref.

import { spawnSync } from 'node:child_process';
import process from 'node:process';

const TAG = 'check-whitespace';

function run(cmd, args) {
  return spawnSync(cmd, args, { encoding: 'utf8' });
}

function refExists(ref) {
  return run('git', ['rev-parse', '--verify', '--quiet', ref]).status === 0;
}

function pickBase() {
  if (refExists('origin/main')) return 'origin/main';
  if (refExists('main')) return 'main';
  return null;
}

function runDiffCheck(range) {
  const args = ['diff', '--check'];
  if (range) args.push(range);
  const result = run('git', args);
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  return result.status === 0;
}

const base = pickBase();

if (!base) {
  console.log(`[${TAG}] No base ref (origin/main or main) found; checking worktree only.`);
  console.log(`[${TAG}] To gate the committed PR diff, fetch a base ref:`);
  console.log(`[${TAG}]   git fetch origin main   # or   git branch main <sha>`);
  if (!runDiffCheck(null)) process.exit(1);
  console.log(`[${TAG}] OK: no whitespace errors in worktree (committed diff NOT covered).`);
  process.exit(0);
}

console.log(`[${TAG}] Checking ${base}...HEAD for whitespace errors.`);
if (!runDiffCheck(`${base}...HEAD`)) process.exit(1);
console.log(`[${TAG}] OK: no whitespace errors in PR diff.`);
