import { readFileSync } from 'node:fs';

// .github/commit-scopes.json lists each scope and what it covers. CONTRIBUTING.md points at it
// rather than restating it, so a new scope is one edit. The path resolves against this file,
// so the list is found however this module is loaded.
const vocabularyPath = new URL('.github/commit-scopes.json', import.meta.url);
const scopes = JSON.parse(readFileSync(vocabularyPath, 'utf8')).map((entry) => entry.scope);

// scope-enum accepts every scope when handed an empty list, so a vocabulary
// that failed to load would read as a passing gate.
if (scopes.length === 0) {
  throw new Error(`${vocabularyPath.href} must list at least one scope.`);
}

export default {
  extends: ['@commitlint/config-conventional'],
  rules: {
    'scope-enum': [2, 'always', scopes],
    // 72 keeps a subject readable in `git log --oneline` inside an 80-column
    // terminal, with room for the hash and any ref decoration.
    'header-max-length': [2, 'always', 72],
    // The same width for the body, so a message reads the same in a terminal
    // as it does on GitHub. A line holding a URL is exempt by the rule.
    'body-max-line-length': [2, 'always', 72],
  },
};
