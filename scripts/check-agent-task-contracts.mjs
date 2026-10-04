#!/usr/bin/env node

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const TAG = 'docs:agent-tasks:check';
const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..');
const contractsDir = resolve(repoRoot, 'docs/agent_tasks');
const schemaPath = resolve(repoRoot, 'docs/agent-task.schema.json');
const profilesPath = resolve(repoRoot, 'docs/agent-workflow-profiles.json');
const indexPath = resolve(contractsDir, 'INDEX.md');
const errors = [];

const ALLOWED_STATUSES = new Set(['proposed', 'accepted', 'in_progress', 'blocked', 'completed', 'withdrawn']);
const CONTRACT_KEYS = new Set([
  'schemaVersion',
  'id',
  'title',
  'status',
  'summary',
  'scope',
  'outOfScope',
  'changeTypes',
  'blastRadius',
  'repositories',
  'requiredReads',
  'expectedTouchedPaths',
  'requiredCompanions',
  'outOfScopePaths',
  'verificationProfile',
  'extraCommands',
  'acceptanceCriteria',
  'stopConditions',
  'knownAllowedFailures',
  'environmentBlockers',
  'humanDecisions',
  'discoveredFacts',
  'openQuestions',
  'risks',
  'prPolicy',
  'verificationResults'
]);
const ALLOWED_CHANGE_TYPES = new Set([
  'docs',
  'tooling',
  'framework',
  'expansion-api',
  'harmony-patch',
  'il2cpp-interop',
  'unity-assets',
  'legacy',
  'bugfix',
  'refactor',
  'workflow',
  'release-ops'
]);
const BLAST_RADIUS_KEYS = [
  'framework',
  'expansionApi',
  'harmonyPatches',
  'il2cppInterop',
  'unityAssets',
  'docs',
  'tooling',
  'legacy'
];
const ALLOWED_CRITERION_STATUSES = new Set(['pending', 'met', 'blocked', 'not_applicable']);
const ALLOWED_PATH_KINDS = new Set(['file', 'directory', 'glob']);
const ALLOWED_PATH_STATUSES = new Set(['exists', 'to_create', 'external']);

// Allowed-key sets mirror the schema's `additionalProperties: false` declarations.
// The validator rejects unknown keys at every entry level so typos like `reson`
// instead of `reason` fail the gate instead of silently slipping through.
const PATH_ENTRY_KEYS = new Set(['path', 'kind', 'status', 'reason']);
const COMMAND_ENTRY_KEYS = new Set(['cwd', 'command', 'reason']);
const ACCEPTANCE_KEYS = new Set(['id', 'description', 'verification', 'status']);
const STOP_KEYS = new Set(['id', 'condition']);
const KNOWN_ALLOWED_FAILURE_KEYS = new Set(['command', 'reason', 'evidence']);
const ENVIRONMENT_BLOCKER_KEYS = new Set(['tool', 'impact', 'fallback']);
const HUMAN_DECISION_KEYS = new Set(['id', 'decision', 'decidedAt']);
const DISCOVERED_FACT_KEYS = new Set(['id', 'fact', 'source']);
const OPEN_QUESTION_KEYS = new Set(['id', 'question', 'blocking', 'owner', 'status']);
const RISK_KEYS = new Set(['id', 'risk', 'mitigation']);
const PR_POLICY_KEYS = new Set(['draftPrRequired', 'baseBranch', 'mergeRequiresUserRequest', 'requiredBodyNotes']);
const VERIFICATION_RESULT_KEYS = new Set(['command', 'outcome', 'notes']);
const REPOSITORY_KEYS = new Set(['id', 'path', 'role', 'verificationProfile', 'extraCommands']);
const PROFILE_KEYS = new Set(['description', 'defaultRequiredReads', 'verificationCommands', 'defaultStopConditions']);

function toDisplayPath(path) {
  return relative(repoRoot, path).replaceAll('\\', '/');
}

function fail(message) {
  errors.push(`[${TAG}] ${message}`);
}

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (err) {
    fail(`${toDisplayPath(path)} is not valid JSON: ${err instanceof Error ? err.message : String(err)}`);
    return null;
  }
}

function isObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function rejectUnknownKeys(obj, allowedKeys, context) {
  if (!isObject(obj)) return;
  for (const key of Object.keys(obj)) {
    if (!allowedKeys.has(key)) {
      fail(`${context} contains unknown key ${key}.`);
    }
  }
}

function requireString(obj, key, context) {
  if (typeof obj[key] !== 'string' || obj[key].trim() === '') {
    fail(`${context}.${key} must be a non-empty string.`);
    return '';
  }

  return obj[key];
}

function requireArray(obj, key, context) {
  if (!Array.isArray(obj[key])) {
    fail(`${context}.${key} must be an array.`);
    return [];
  }

  return obj[key];
}

function requireObject(obj, key, context) {
  if (!isObject(obj[key])) {
    fail(`${context}.${key} must be an object.`);
    return {};
  }

  return obj[key];
}

function validateUniqueIds(items, fieldName, context) {
  const seen = new Set();

  for (const item of items) {
    if (!isObject(item)) {
      fail(`${context}.${fieldName} entries must be objects.`);
      continue;
    }

    const id = item.id;
    if (typeof id !== 'string' || id.trim() === '') {
      fail(`${context}.${fieldName} entry is missing a non-empty id.`);
      continue;
    }

    if (seen.has(id)) {
      fail(`${context}.${fieldName} has duplicate id ${id}.`);
    }

    seen.add(id);
  }
}

function validateStringArray(items, fieldName, context, options = {}) {
  if (!Array.isArray(items)) {
    fail(`${context}.${fieldName} must be an array.`);
    return;
  }

  if (options.minItems && items.length < options.minItems) {
    fail(`${context}.${fieldName} must contain at least ${options.minItems} item(s).`);
  }

  for (const [index, item] of items.entries()) {
    if (typeof item !== 'string' || item.trim() === '') {
      fail(`${context}.${fieldName}[${index}] must be a non-empty string.`);
    }
  }
}

function resolveContractPath(rawPath) {
  return resolve(repoRoot, rawPath);
}

function globBase(rawPath) {
  const wildcardIndex = rawPath.search(/[*?[{]/);
  if (wildcardIndex === -1) {
    return rawPath;
  }

  const prefix = rawPath.slice(0, wildcardIndex).replace(/[\\/]+$/, '');
  return prefix === '' ? '.' : prefix;
}

function isInsideRepoRoot(absolutePath) {
  // Reject paths that resolve outside repoRoot. Uses POSIX-style separator-safe
  // comparison so a sibling like `<repoRoot>-other` does not look like a child.
  if (absolutePath === repoRoot) return true;
  const prefix = repoRoot.endsWith(sep) ? repoRoot : repoRoot + sep;
  return absolutePath.startsWith(prefix);
}

function ensurePathWithinRepoRoot(rawPath, context, outsideHint) {
  // Reject absolute paths and paths that resolve outside repo root.
  // `outsideHint` names the field's dedicated outside-repo channel: path entries
  // use `status "external"`, repositories use `role "external"`. Catches
  // accidental `../sibling-repo/...` and absolute paths like `/etc/passwd`.
  if (typeof rawPath !== 'string' || rawPath === '') return true;
  if (isAbsolute(rawPath)) {
    fail(`${context} path "${rawPath}" is absolute; use a repo-relative path or ${outsideHint}.`);
    return false;
  }
  const absolute = resolve(repoRoot, rawPath);
  if (!isInsideRepoRoot(absolute)) {
    fail(`${context} path "${rawPath}" resolves outside repo root; use ${outsideHint} for outside references.`);
    return false;
  }
  return true;
}

function validateRepoContainment(entry, context) {
  // For `exists` / `to_create` statuses, the path must stay inside the repo root.
  // Outside references must use `status: "external"`. Globs are checked against
  // the literal prefix before the first wildcard.
  const candidate = entry.kind === 'glob' ? globBase(entry.path) : entry.path;
  return ensurePathWithinRepoRoot(candidate, context, 'status "external"');
}

function validateExistingPath(entry, context) {
  const rawPath = entry.path;
  const kind = entry.kind;
  const absolute = resolveContractPath(kind === 'glob' ? globBase(rawPath) : rawPath);

  if (!existsSync(absolute)) {
    fail(`${context} references missing path ${rawPath}. Use status "to_create" only for planned outputs or "external" for outside references.`);
    return;
  }

  if (kind === 'file' && !statSync(absolute).isFile()) {
    fail(`${context} expected file path but found non-file ${rawPath}.`);
  }

  if (kind === 'directory' && !statSync(absolute).isDirectory()) {
    fail(`${context} expected directory path but found non-directory ${rawPath}.`);
  }
}

function validatePathEntries(entries, fieldName, context, options = {}) {
  if (!Array.isArray(entries)) {
    fail(`${context}.${fieldName} must be an array.`);
    return;
  }

  if (options.minItems && entries.length < options.minItems) {
    fail(`${context}.${fieldName} must contain at least ${options.minItems} item(s).`);
  }

  for (const [index, entry] of entries.entries()) {
    const entryContext = `${context}.${fieldName}[${index}]`;
    if (!isObject(entry)) {
      fail(`${entryContext} must be an object.`);
      continue;
    }

    rejectUnknownKeys(entry, PATH_ENTRY_KEYS, entryContext);
    const rawPath = requireString(entry, 'path', entryContext);
    const kind = requireString(entry, 'kind', entryContext);
    const status = requireString(entry, 'status', entryContext);

    if (!ALLOWED_PATH_KINDS.has(kind)) {
      fail(`${entryContext}.kind must be one of ${[...ALLOWED_PATH_KINDS].join(', ')}.`);
    }

    if (!ALLOWED_PATH_STATUSES.has(status)) {
      fail(`${entryContext}.status must be one of ${[...ALLOWED_PATH_STATUSES].join(', ')}.`);
    }

    if (options.noPlannedOutputs && status === 'to_create') {
      fail(`${entryContext} cannot use status "to_create"; this field must reference existing or external paths.`);
    }

    if (options.noGlobs && kind === 'glob') {
      fail(`${entryContext} cannot use kind "glob".`);
    }

    // Repo-root containment: exists / to_create paths must stay inside the repo.
    // Outside references must use status "external".
    let withinRepo = true;
    if ((status === 'exists' || status === 'to_create') && rawPath !== '') {
      withinRepo = validateRepoContainment(entry, entryContext);
    }

    if (withinRepo && status === 'exists' && rawPath !== '') {
      validateExistingPath(entry, entryContext);
    }
  }
}

function validateCommandEntries(commands, fieldName, context) {
  if (!Array.isArray(commands)) {
    fail(`${context}.${fieldName} must be an array.`);
    return;
  }

  for (const [index, commandEntry] of commands.entries()) {
    const entryContext = `${context}.${fieldName}[${index}]`;
    if (!isObject(commandEntry)) {
      fail(`${entryContext} must be an object.`);
      continue;
    }

    rejectUnknownKeys(commandEntry, COMMAND_ENTRY_KEYS, entryContext);
    requireString(commandEntry, 'cwd', entryContext);
    requireString(commandEntry, 'command', entryContext);
  }
}

function validateProfiles(profilesData) {
  if (!isObject(profilesData)) {
    fail('docs/agent-workflow-profiles.json must contain an object.');
    return new Set();
  }

  if (profilesData.profilesVersion !== 1) {
    fail('docs/agent-workflow-profiles.json profilesVersion must be 1.');
  }

  const profiles = requireObject(profilesData, 'profiles', 'profiles');
  const profileIds = new Set(Object.keys(profiles));

  for (const [profileId, profile] of Object.entries(profiles)) {
    const context = `profiles.${profileId}`;

    if (!/^[a-z][a-z0-9-]*$/.test(profileId)) {
      fail(`${context} id must be lowercase kebab-case.`);
    }

    if (!isObject(profile)) {
      fail(`${context} must be an object.`);
      continue;
    }

    rejectUnknownKeys(profile, PROFILE_KEYS, context);
    requireString(profile, 'description', context);
    validatePathEntries(profile.defaultRequiredReads, 'defaultRequiredReads', context, {
      noPlannedOutputs: true,
      noGlobs: true
    });
    validateCommandEntries(profile.verificationCommands, 'verificationCommands', context);

    const stopConditions = requireArray(profile, 'defaultStopConditions', context);
    if (stopConditions.length === 0) {
      fail(`${context}.defaultStopConditions must not be empty.`);
    }
    for (const [index, condition] of stopConditions.entries()) {
      if (typeof condition !== 'string' || condition.trim() === '') {
        fail(`${context}.defaultStopConditions[${index}] must be a non-empty string.`);
      }
    }
  }

  return profileIds;
}

function validateBlastRadius(blastRadius, context) {
  for (const key of BLAST_RADIUS_KEYS) {
    if (typeof blastRadius[key] !== 'boolean') {
      fail(`${context}.blastRadius.${key} must be a boolean.`);
    }
  }

  for (const key of Object.keys(blastRadius)) {
    if (!BLAST_RADIUS_KEYS.includes(key)) {
      fail(`${context}.blastRadius contains unknown key ${key}.`);
    }
  }
}

function validateRepositories(repositories, profileIds, context) {
  const repoIds = new Set();

  for (const [index, repo] of repositories.entries()) {
    const repoContext = `${context}.repositories[${index}]`;
    if (!isObject(repo)) {
      fail(`${repoContext} must be an object.`);
      continue;
    }

    rejectUnknownKeys(repo, REPOSITORY_KEYS, repoContext);
    const id = requireString(repo, 'id', repoContext);
    const repoPath = requireString(repo, 'path', repoContext);
    const role = requireString(repo, 'role', repoContext);
    const verificationProfile = requireString(repo, 'verificationProfile', repoContext);

    if (repoIds.has(id)) {
      fail(`${repoContext}.id duplicates repository id ${id}.`);
    }
    repoIds.add(id);

    if (!['primary', 'companion', 'external'].includes(role)) {
      fail(`${repoContext}.role must be primary, companion, or external.`);
    }

    if (!profileIds.has(verificationProfile)) {
      fail(`${repoContext}.verificationProfile references unknown profile ${verificationProfile}.`);
    }

    // Repo-root containment: non-`external` entries (primary / companion) are
    // in-scope repositories that agents will treat as authoritative. They must
    // stay inside the SuperDecor repo root; an absolute path or `../sibling-repo`
    // would silently mark outside work as in-scope. Outside repos must use
    // `role: "external"`, which is exempt from the containment + existence checks.
    if (role !== 'external' && repoPath !== '') {
      const withinRepo = ensurePathWithinRepoRoot(repoPath, `${repoContext}.path`, 'role "external"');
      if (withinRepo && !existsSync(resolve(repoRoot, repoPath))) {
        fail(`${repoContext}.path references missing repository path ${repoPath}.`);
      }
    }

    validateCommandEntries(repo.extraCommands, 'extraCommands', repoContext);
  }
}

function validateAcceptanceCriteria(criteria, contract, context) {
  if (criteria.length < 1) {
    fail(`${context}.acceptanceCriteria must contain at least 1 item.`);
  }
  validateUniqueIds(criteria, 'acceptanceCriteria', context);

  for (const [index, criterion] of criteria.entries()) {
    const criterionContext = `${context}.acceptanceCriteria[${index}]`;
    if (!isObject(criterion)) {
      continue;
    }

    rejectUnknownKeys(criterion, ACCEPTANCE_KEYS, criterionContext);
    if (!/^AC-[0-9]{2}$/.test(String(criterion.id))) {
      fail(`${criterionContext}.id must match AC-00 style.`);
    }
    requireString(criterion, 'description', criterionContext);
    requireString(criterion, 'verification', criterionContext);
    if (!ALLOWED_CRITERION_STATUSES.has(criterion.status)) {
      fail(`${criterionContext}.status must be one of ${[...ALLOWED_CRITERION_STATUSES].join(', ')}.`);
    }
  }

  if (contract.status === 'completed') {
    for (const criterion of criteria) {
      if (isObject(criterion) && !['met', 'not_applicable'].includes(criterion.status)) {
        fail(`${context} is completed, but ${criterion.id} has status ${criterion.status}.`);
      }
    }
  }
}

function validateStopConditions(stopConditions, context) {
  validateUniqueIds(stopConditions, 'stopConditions', context);

  if (stopConditions.length === 0) {
    fail(`${context}.stopConditions must include at least one stop condition.`);
  }

  for (const [index, stopCondition] of stopConditions.entries()) {
    const stopContext = `${context}.stopConditions[${index}]`;
    if (!isObject(stopCondition)) {
      continue;
    }

    rejectUnknownKeys(stopCondition, STOP_KEYS, stopContext);
    if (!/^SC-[0-9]{2}$/.test(String(stopCondition.id))) {
      fail(`${stopContext}.id must match SC-00 style.`);
    }
    requireString(stopCondition, 'condition', stopContext);
  }
}

function validateOpenQuestions(openQuestions, contract, context) {
  validateUniqueIds(openQuestions, 'openQuestions', context);

  const blockingOpen = [];
  for (const [index, question] of openQuestions.entries()) {
    const questionContext = `${context}.openQuestions[${index}]`;
    if (!isObject(question)) {
      continue;
    }

    rejectUnknownKeys(question, OPEN_QUESTION_KEYS, questionContext);
    if (!/^OQ-[0-9]{2}$/.test(String(question.id))) {
      fail(`${questionContext}.id must match OQ-00 style.`);
    }
    requireString(question, 'question', questionContext);
    requireString(question, 'owner', questionContext);
    if (typeof question.blocking !== 'boolean') {
      fail(`${questionContext}.blocking must be a boolean.`);
    }
    if (!['open', 'answered', 'not_applicable'].includes(question.status)) {
      fail(`${questionContext}.status must be open, answered, or not_applicable.`);
    }

    if (question.blocking === true && question.status === 'open') {
      blockingOpen.push(question.id);
    }
  }

  if (['accepted', 'in_progress', 'completed'].includes(contract.status) && blockingOpen.length > 0) {
    fail(`${context} has status ${contract.status}, but blocking open questions remain: ${blockingOpen.join(', ')}.`);
  }

  return blockingOpen;
}

function validateSimpleIdArray(items, fieldName, context, idPattern) {
  validateUniqueIds(items, fieldName, context);

  for (const [index, item] of items.entries()) {
    const entryContext = `${context}.${fieldName}[${index}]`;
    if (!isObject(item)) {
      fail(`${entryContext} must be an object.`);
      continue;
    }

    if (!idPattern.test(String(item.id))) {
      fail(`${entryContext}.id has an invalid format.`);
    }
  }
}

function validateHumanDecisions(items, context) {
  validateSimpleIdArray(items, 'humanDecisions', context, /^HD-[0-9]{2}$/);

  for (const [index, item] of items.entries()) {
    const itemContext = `${context}.humanDecisions[${index}]`;
    if (!isObject(item)) {
      continue;
    }

    rejectUnknownKeys(item, HUMAN_DECISION_KEYS, itemContext);
    requireString(item, 'decision', itemContext);
    const decidedAt = requireString(item, 'decidedAt', itemContext);
    if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/.test(decidedAt)) {
      fail(`${itemContext}.decidedAt must be YYYY-MM-DD.`);
    }
  }
}

function validateDiscoveredFacts(items, context) {
  validateSimpleIdArray(items, 'discoveredFacts', context, /^DF-[0-9]{2}$/);

  for (const [index, item] of items.entries()) {
    const itemContext = `${context}.discoveredFacts[${index}]`;
    if (!isObject(item)) {
      continue;
    }

    rejectUnknownKeys(item, DISCOVERED_FACT_KEYS, itemContext);
    requireString(item, 'fact', itemContext);
    requireString(item, 'source', itemContext);
  }
}

function validateRisks(items, context) {
  validateSimpleIdArray(items, 'risks', context, /^R-[0-9]{2}$/);

  for (const [index, item] of items.entries()) {
    const itemContext = `${context}.risks[${index}]`;
    if (!isObject(item)) {
      continue;
    }

    rejectUnknownKeys(item, RISK_KEYS, itemContext);
    requireString(item, 'risk', itemContext);
    requireString(item, 'mitigation', itemContext);
  }
}

function validateContract(contractPath, profileIds) {
  const contract = readJson(contractPath);
  if (!contract) {
    return;
  }

  const relativePath = toDisplayPath(contractPath);
  const context = relativePath;
  const basename = relativePath.split('/').pop().replace(/\.json$/, '');

  for (const key of Object.keys(contract)) {
    if (!CONTRACT_KEYS.has(key)) {
      fail(`${context} contains unknown top-level key ${key}.`);
    }
  }

  if (contract.schemaVersion !== 1) {
    fail(`${context}.schemaVersion must be 1.`);
  }

  const id = requireString(contract, 'id', context);
  if (!/^TASK-[0-9]{4}-[0-9]{2}-[0-9]{2}-[a-z0-9]+(?:-[a-z0-9]+)*$/.test(id)) {
    fail(`${context}.id must match TASK-YYYY-MM-DD-slug.`);
  }
  if (id !== `TASK-${basename}`) {
    fail(`${context}.id must match filename ${basename}.json.`);
  }

  requireString(contract, 'title', context);
  requireString(contract, 'summary', context);

  if (!ALLOWED_STATUSES.has(contract.status)) {
    fail(`${context}.status must be one of ${[...ALLOWED_STATUSES].join(', ')}.`);
  }

  validateStringArray(contract.scope, 'scope', context, { minItems: 1 });
  validateStringArray(contract.outOfScope, 'outOfScope', context);

  const changeTypes = requireArray(contract, 'changeTypes', context);
  if (changeTypes.length < 1) {
    fail(`${context}.changeTypes must contain at least 1 item.`);
  }
  for (const changeType of changeTypes) {
    if (!ALLOWED_CHANGE_TYPES.has(changeType)) {
      fail(`${context}.changeTypes contains unknown value ${changeType}.`);
    }
  }
  if (new Set(changeTypes).size !== changeTypes.length) {
    fail(`${context}.changeTypes must not contain duplicates.`);
  }

  validateBlastRadius(requireObject(contract, 'blastRadius', context), context);

  const verificationProfile = requireString(contract, 'verificationProfile', context);
  if (!profileIds.has(verificationProfile)) {
    fail(`${context}.verificationProfile references unknown profile ${verificationProfile}.`);
  }

  const repositories = requireArray(contract, 'repositories', context);
  if (repositories.length < 1) {
    fail(`${context}.repositories must contain at least 1 item.`);
  }
  validateRepositories(repositories, profileIds, context);
  validatePathEntries(contract.requiredReads, 'requiredReads', context, {
    noPlannedOutputs: true,
    noGlobs: true
  });
  validatePathEntries(contract.expectedTouchedPaths, 'expectedTouchedPaths', context, {
    minItems: 1
  });
  validatePathEntries(contract.requiredCompanions, 'requiredCompanions', context);
  validatePathEntries(contract.outOfScopePaths, 'outOfScopePaths', context, {
    noPlannedOutputs: true
  });
  validateCommandEntries(contract.extraCommands, 'extraCommands', context);
  validateAcceptanceCriteria(requireArray(contract, 'acceptanceCriteria', context), contract, context);
  validateStopConditions(requireArray(contract, 'stopConditions', context), context);

  const knownAllowedFailures = requireArray(contract, 'knownAllowedFailures', context);
  for (const [index, failure] of knownAllowedFailures.entries()) {
    const failureContext = `${context}.knownAllowedFailures[${index}]`;
    if (!isObject(failure)) {
      fail(`${failureContext} must be an object.`);
      continue;
    }
    rejectUnknownKeys(failure, KNOWN_ALLOWED_FAILURE_KEYS, failureContext);
    requireString(failure, 'command', failureContext);
    requireString(failure, 'reason', failureContext);
  }

  const environmentBlockers = requireArray(contract, 'environmentBlockers', context);
  for (const [index, blocker] of environmentBlockers.entries()) {
    const blockerContext = `${context}.environmentBlockers[${index}]`;
    if (!isObject(blocker)) {
      fail(`${blockerContext} must be an object.`);
      continue;
    }
    rejectUnknownKeys(blocker, ENVIRONMENT_BLOCKER_KEYS, blockerContext);
    requireString(blocker, 'tool', blockerContext);
    requireString(blocker, 'impact', blockerContext);
    requireString(blocker, 'fallback', blockerContext);
  }

  const humanDecisions = requireArray(contract, 'humanDecisions', context);
  validateHumanDecisions(humanDecisions, context);
  if (['accepted', 'in_progress', 'completed'].includes(contract.status) && humanDecisions.length === 0) {
    fail(`${context} has status ${contract.status}, but no humanDecisions entries.`);
  }

  validateDiscoveredFacts(requireArray(contract, 'discoveredFacts', context), context);
  validateRisks(requireArray(contract, 'risks', context), context);
  const blockingOpen = validateOpenQuestions(requireArray(contract, 'openQuestions', context), contract, context);

  const prPolicy = requireObject(contract, 'prPolicy', context);
  rejectUnknownKeys(prPolicy, PR_POLICY_KEYS, `${context}.prPolicy`);
  if (typeof prPolicy.draftPrRequired !== 'boolean') {
    fail(`${context}.prPolicy.draftPrRequired must be a boolean.`);
  }
  requireString(prPolicy, 'baseBranch', `${context}.prPolicy`);
  if (typeof prPolicy.mergeRequiresUserRequest !== 'boolean') {
    fail(`${context}.prPolicy.mergeRequiresUserRequest must be a boolean.`);
  }
  // The schema requires each requiredBodyNotes entry to be a non-empty string —
  // these notes force PRs to surface the task contract path and verification
  // results, so invalid entries (`[{}]`, `['']`) would silently drop that signal.
  validateStringArray(prPolicy.requiredBodyNotes, 'requiredBodyNotes', `${context}.prPolicy`);

  const verificationResults = requireArray(contract, 'verificationResults', context);
  for (const [index, result] of verificationResults.entries()) {
    const resultContext = `${context}.verificationResults[${index}]`;
    if (!isObject(result)) {
      fail(`${resultContext} must be an object.`);
      continue;
    }
    rejectUnknownKeys(result, VERIFICATION_RESULT_KEYS, resultContext);
    requireString(result, 'command', resultContext);
    requireString(result, 'notes', resultContext);
    if (!['passed', 'failed', 'blocked', 'not_run'].includes(result.outcome)) {
      fail(`${resultContext}.outcome must be passed, failed, blocked, or not_run.`);
    }
  }

  if (contract.status === 'completed' && verificationResults.length === 0) {
    fail(`${context} is completed but has no verificationResults.`);
  }

  if (contract.status === 'blocked' && environmentBlockers.length === 0 && blockingOpen.length === 0) {
    fail(`${context} is blocked but has no environmentBlockers or blocking openQuestions.`);
  }
}

function validateIndex(contractPaths) {
  if (!existsSync(indexPath)) {
    fail('docs/agent_tasks/INDEX.md is missing.');
    return;
  }

  const indexText = readFileSync(indexPath, 'utf8');
  const expectedContractRefs = contractPaths.map((path) => toDisplayPath(path));

  for (const contractRef of expectedContractRefs) {
    if (!indexText.includes(contractRef)) {
      fail(`docs/agent_tasks/INDEX.md does not reference ${contractRef}.`);
    }
  }

  const mentionedContracts = [...indexText.matchAll(/docs\/agent_tasks\/[0-9]{4}-[0-9]{2}-[0-9]{2}-[a-z0-9-]+\.json/g)].map((match) => match[0]);
  const knownContracts = new Set(expectedContractRefs);

  for (const mentioned of mentionedContracts) {
    if (!knownContracts.has(mentioned)) {
      fail(`docs/agent_tasks/INDEX.md references missing contract ${mentioned}.`);
    }
  }

  // Cross-check the INDEX status column against each contract JSON's status field.
  // CLAUDE.md directs agents to read INDEX before substantial work; a stale status
  // there can make agents miss an accepted contract and draft against the wrong scope.
  // INDEX rows look like: | <status> | `docs/agent_tasks/<file>.json` | <objective> |
  const indexStatusPattern = /^\|\s*([a-z_]+)\s*\|\s*`(docs\/agent_tasks\/[0-9]{4}-[0-9]{2}-[0-9]{2}-[a-z0-9-]+\.json)`\s*\|/gm;
  const indexStatusByPath = new Map();
  let statusMatch;
  while ((statusMatch = indexStatusPattern.exec(indexText)) !== null) {
    indexStatusByPath.set(statusMatch[2], statusMatch[1]);
  }

  for (const contractPath of contractPaths) {
    const relPath = toDisplayPath(contractPath);
    const indexStatus = indexStatusByPath.get(relPath);
    if (indexStatus === undefined) {
      // Either the row is missing entirely (already caught above) or the row uses
      // a non-standard table layout — flag the latter case.
      if (expectedContractRefs.includes(relPath) && indexText.includes(relPath)) {
        fail(`docs/agent_tasks/INDEX.md mentions ${relPath} but no row of the form \`| <status> | \\\`${relPath}\\\` | ... |\` was found; the status cross-check needs the canonical table shape.`);
      }
      continue;
    }

    if (!ALLOWED_STATUSES.has(indexStatus)) {
      fail(`docs/agent_tasks/INDEX.md status "${indexStatus}" for ${relPath} is not one of ${[...ALLOWED_STATUSES].join(', ')}.`);
      continue;
    }

    let contractJson;
    try {
      contractJson = JSON.parse(readFileSync(contractPath, 'utf8'));
    } catch {
      // The earlier per-contract pass already reported the JSON parse error.
      continue;
    }

    if (contractJson && indexStatus !== contractJson.status) {
      fail(`docs/agent_tasks/INDEX.md status "${indexStatus}" for ${relPath} does not match contract JSON status "${contractJson.status}". Update INDEX.md or the contract so the two agree.`);
    }
  }
}

if (!existsSync(schemaPath)) {
  fail('docs/agent-task.schema.json is missing.');
} else {
  const schema = readJson(schemaPath);
  if (schema && schema.title !== 'Mod Settings Tool Agent Task Contract') {
    fail('docs/agent-task.schema.json has an unexpected title.');
  }
}

if (!existsSync(profilesPath)) {
  fail('docs/agent-workflow-profiles.json is missing.');
}

const profileIds = existsSync(profilesPath) ? validateProfiles(readJson(profilesPath)) : new Set();

if (!existsSync(contractsDir)) {
  fail('docs/agent_tasks directory is missing.');
} else {
  const contractPaths = readdirSync(contractsDir)
    .filter((name) => extname(name) === '.json')
    .map((name) => join(contractsDir, name))
    .sort();

  if (contractPaths.length === 0) {
    // Bootstrap phase: a fresh repo (M0) has no contracts yet. Validate the schema,
    // profiles, and INDEX, but do not fail on an empty ledger. The first contract
    // (and this notice's removal, if desired) lands with the first build milestone.
    console.log(`[${TAG}] No task contracts yet (empty ledger); validated schema, profiles, and INDEX only.`);
  }

  for (const contractPath of contractPaths) {
    validateContract(contractPath, profileIds);
  }

  validateIndex(contractPaths);
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(error);
  }
  process.exit(1);
}

console.log(`[${TAG}] OK: task contract schema, profiles, index, and contracts are valid.`);
