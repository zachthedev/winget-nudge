import { readFileSync } from "node:fs";

// .github/commit-scopes.json lists each scope and what it covers. CONTRIBUTING.md points at it
// rather than restating it, so a new scope is one edit.
const scopes = JSON.parse(
    readFileSync(new URL(".github/commit-scopes.json", import.meta.url), "utf8"),
).map((entry) => entry.scope);

export default {
    extends: ["@commitlint/config-conventional"],
    rules: {
        "scope-enum": [2, "always", scopes],
        "header-max-length": [2, "always", 72],
        "body-max-line-length": [2, "always", 72],
    },
};
