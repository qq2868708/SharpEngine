---
name: sharpengine-development
description: Design, implement, or review SharpEngine engine code and project-side engine extensions under its AI-first component, serialization, live-state, MCP, runtime-input, and Cook contracts. Use for SharpEngine architecture, components, editor/runtime state, asset formats, commands, MCP surfaces, hot reload, or Cook changes; do not invoke for ordinary scene assembly, parameter tuning, or content authoring that does not extend the engine.
---

# SharpEngine Development

Treat SharpEngine as an AI-first component engine. Every semantic state an AI can observe, create, or edit must have a versioned serialized representation and be readable from the latest committed live state. Native handles, GPU resources, threads, delegates, caches, and similar process objects are not semantic state; represent their meaning with stable IDs or descriptors and rebuild them from serialized state.

## Work From The Contract

Follow the root `AGENTS.md` and `.editorconfig` naming policy: PascalCase for C#
members, private fields, parameters and locals, without private-field prefixes.
Keep serialized names and external API contracts stable. New project templates
must carry the same defaults in their own `AGENTS.md` and `.editorconfig`.

Before designing or changing code:

1. Inspect the current implementation and trace the actual read, mutation, save, runtime, and Cook paths involved. Do not infer behavior from UI labels or documentation alone.
2. Classify affected state as `authoritative`, `derived`, or `transient`. State which context owns it: `authoring` or `runtime`.
3. Identify the stable IDs, schema version, revisions, transaction boundary, diagnostics, and AI discovery surface affected by the change.
4. Surface a conflict before implementation when a request would introduce hidden state, bypass transactions, make UI the only control path, silently lose data, or make Cook non-reproducible. Ask one decision question at a time only when code inspection cannot resolve it.
5. Keep implementation narrow. Project MCP exposure is optional; do not force an internal project feature to implement an MCP contract merely because it uses engine components.
6. Verify observable invariants, not wording or implementation shape.

## Universal Invariants

- AI-facing data uses versioned JSON contracts. Domain storage may remain XML, RML/RCSS, or another deterministic text format when a lossless adapter exists.
- `authoritative` state round-trips and may be patched. `derived` state is read-only. `transient` state is live-readable but is not persisted by default.
- Snapshots describe the latest fully committed state. They never expose a half-written file, partial transaction, or partially reloaded graph.
- A mutation of engine-managed authoritative state runs on the engine world/editor thread, validates completely, commits atomically, updates revisions and dirty state, and participates in Undo/Redo.
- UI and MCP share semantic engine commands for authoring operations. Runtime interaction acceptance uses the real input, hit-test, focus, clipping, z-order, and event-bubbling path.
- Text source assets are authoritative. Cook outputs are derived, replaceable artifacts and never overwrite source state.
- Unknown or invalid data is never silently defaulted away or dropped. Preserve unknown component records at the document layer, but do not instantiate, display, or run them.
- Secrets never enter assets, snapshots, diagnostics, logs, or transaction history. Store only secret references.

## Reference Routing

- Read [references/component-model.md](references/component-model.md) for component APIs, component state, identity, references, lifecycle, threading, or Authoring/Runtime work.
- Read [references/ai-runtime-contract.md](references/ai-runtime-contract.md) for snapshots, revisions, transactions, commands, MCP, AI discovery, runtime input, UI observability, or deterministic automation.
- Read [references/serialization-and-cook.md](references/serialization-and-cook.md) for asset formats, schema evolution, save/load, hot reload, migration, dependency discovery, or Cook work.
- Read [references/asset-naming.md](references/asset-naming.md) when adding or migrating a resource file type or file extension.

Read every reference whose domain the requested change crosses. A cross-cutting feature may require all four.

## Asset File Naming

New resource source files must use a lowercase `s` prefix in the type suffix:
use `.sskeletalmesh`, `.sskeleton`, `.sskeletalanimation`, `.sprefab`, or
`.sworld` patterns as applicable. Do not introduce a new `.sharp...` resource
extension. This rule applies to the resource file suffix, including the type
part of a compound suffix such as `.sskeletalmesh.json`.

Existing suffixes that do not follow this rule, including bare `.mesh`,
`.staticmesh`, `.skeletalmesh`, `.skeleton`, `.skeletalanimation`, and
`.sharp...` forms, are legacy compatibility inputs. Keep them readable and
registered until an explicit migration is designed; do not mass-rename them as
part of an unrelated feature. A migration must preserve asset identity, support
old references, update the manifest atomically, and document the old and new
suffixes.

Do not apply the file-suffix rule to other contracts: manifest `type` values,
stable asset IDs, importer names such as `SharpEngine.*`, and C# asset type names
retain their established semantic naming unless that contract is separately
versioned and migrated.

## Completion Standard

For engine-owned behavior, add or update focused validation for the applicable contracts. New or changed built-in components require coverage for default and non-default serialization, round-trip fidelity, deterministic output, migration, invalid input diagnostics, Authoring-to-Runtime reconstruction, and revision/transaction behavior. Project components must satisfy engine registration and serialization checks, but the engine does not require project authors to provide the same test suite. Project MCP tests apply only to MCP capabilities the project actually exposes.

Any change that adds, removes, fixes, or changes an observable feature or
public contract must also use the repository `engine-manual` skill and update
the current version under `Documentation/EngineManual`. Update the user-manual
page for behavior and workflows, the API-index page for public contracts, and
run its manual validator before declaring the change complete. A private
implementation-only refactor may omit the manual, but state that boundary when
reporting the work.

Report any validation boundary honestly. A source review, direct semantic command test, or mocked action does not prove that a runtime UI control is visible and clickable.

## Infrastructure Pyramid

For reusable engine capabilities, follow
[Infrastructure And Composition](references/infrastructure-composition.md).
General infrastructure must function independently; higher-level capabilities
combine it with content adapters and small customizations. Use the confirmed
contract in `Documentation/EngineArchitecture.md` and validate isolation.
