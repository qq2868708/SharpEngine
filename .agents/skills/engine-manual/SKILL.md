---
name: engine-manual
description: Maintain SharpEngine's versioned HTML engine manual when an engine feature, user workflow, public API, asset contract, schema, editor command, MCP surface, or runtime behavior changes.
---

# Engine Manual

Use this skill for every SharpEngine change that changes an observable engine capability or a public contract. A source-only refactor, test-only change, or comment-only change does not need a manual edit unless it changes behavior, supported usage, diagnostics, or an API contract.

## Documentation gate

The HTML manual under `Documentation/EngineManual` is part of the feature's
deliverable. Do not report a functional change as complete while its applicable
manual pages remain stale.

1. Read `Documentation/EngineManual/manual.json` and the current version entry
   before editing source. The current version is the version in `engine.json`.
2. Update the current version's `user/index.html` for user-observable behavior:
   workflows, editor controls, runtime behavior, asset authoring, supported
   configuration, diagnostics, examples, and validation boundaries.
3. Update the current version's `api/index.html` for public contracts: C# types
   and methods, stable IDs, component and asset schemas, JSON fields and
   versions, commands, MCP capabilities/resources, native API versions, and
   migration or compatibility rules.
4. Keep the two audiences separate. The user page explains what a user can do
   and how to verify it; the API page is an index of stable integration points
   and links to the authoritative source or existing detailed documentation.
5. Add a concise entry to the `本版更新` section of each affected page. State
   the observable change and, for API work, the contract/version impact. Do not
   claim compile, Editor, Runtime, Cook, or production validation that was not
   actually run.
6. Run `scripts/Validate-EngineManual.ps1` from the repository root. This is the
   single implementation for manifest, entry-point, heading, and local-link
   checks; the skill's script entry delegates to it. It is a structural check,
   not evidence that an API example compiles or a CLI example executes.
7. When changing a command, public API, schema policy, or development entry
   point, inspect affected examples and linked detailed documentation. Validate
   changed examples with a suitable build/evaluation or bounded execution when
   applicable. Keep commands aligned with current supported targets and output
   guards. Report example execution separately from structural manual validation.

## Versioning

The manual version is release-facing and follows `engine.json`'s `version`.
While that version is a development version, update its pages in place. For a
new release or an incompatible documentation/API contract, copy the previous
version directory to a new version directory, update `manual.json`, the root
version index, and the new pages; preserve released version directories as
read-only history. Never silently replace an older version's HTML.

Use the existing HTML structure and stylesheet. Keep links relative so a
version directory can be archived or served from a static file host. Existing
Markdown files under `Documentation/` may remain detailed source references,
but the versioned HTML pages must contain the user-facing summary and API index
needed to navigate the feature.

## Scope and evidence

When a change crosses user behavior and API behavior, update both pages. For a
private implementation change with no observable or contract effect, record no
manual change and explain that boundary in the final report. Manual edits do not
replace focused tests or runtime evidence; report those validation boundaries
separately.
