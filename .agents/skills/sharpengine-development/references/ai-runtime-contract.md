# AI And Runtime Contract

## Live Snapshots

An AI read returns the latest fully committed in-memory state, not a stale disk cache. Capture a coherent snapshot at an engine-controlled consistency boundary.

Use two revision concepts:

- `stateRevision` increases for every AI-observable change, including transient UI state.
- `documentRevision` is scoped to each authoritative scene or resource and changes when that authoritative document changes.

Combined snapshots expose both. Writes compare the relevant `documentRevision`, so unrelated hover, selection, or runtime observation changes do not create authoring conflicts.

Serialization is deterministic for the same semantic state. Define stable ordering and unique representations for fields, entities, components, capabilities, enums, floating-point values, nulls, and defaults. Business-significant order must be explicit data, not an accident of dictionary or registration iteration.

Large states may use resource-level reads and deterministic pagination. A root snapshot must expose stable IDs, types, revisions, summaries, and child resource URIs. Bind pagination cursors to one snapshot revision so a complete enumeration has no gaps or duplicates. Keep a complete snapshot convenience endpoint for bounded scenes when practical.

Do not promise one global millisecond latency. Each resource type may define a performance budget. A slow operation must expose metrics or an asynchronous resource rather than return stale state and call it live.

## Schema And Discovery

Use JSON Schema 2020-12 for component state and MCP inputs/outputs. Add engine semantics only through clearly named `x-sharpengine-*` extension keywords; never redefine standard keyword behavior.

The engine indexes exposed component capabilities. A component snapshot lets the AI discover each capability's stable namespaced ID, purpose, schemas, side-effect class, revision requirements, current `available` state, and structured unavailability reason. Recheck availability immediately before execution; availability predicates must be fast and side-effect free.

Reserve engine namespaces such as `engine.*`, `editor.*`, and `scene.*`. Project capabilities use a registered project/plugin namespace and cannot replace engine capabilities. Breaking capability changes require a new version or an explicit redirect/migration.

## Commands And Transactions

UI and AI authoring operations converge on a shared semantic command layer. MCP calls the command directly; it does not fabricate clicks or require a visible control.

The engine provides generic schema-driven component read and atomic patch operations. Use these for direct authoritative field changes. A patch is an AI-decided engine operation, not AI reasoning and not a direct file edit. It identifies the target object and context, includes `expectedRevision`, and carries only allowed changes. The engine validates type, constraints, references, permissions, and current revision before an all-or-nothing commit.

Use project MCP capabilities for domain operations whose invariants exceed a generic field patch. Project MCP exposure is optional. Internal project behavior does not need MCP schemas or MCP result envelopes. Authoring and external Inspector/AI edits use engine tracking; ordinary project Runtime field writes follow the complete-Tick observation boundary described in the component model.

Successful Authoring edits:

- update the relevant revisions and dirty state;
- join the shared user/AI Undo/Redo history;
- record transaction ID, actor/source, revisions, changed objects, and reversible data;
- treat a multi-field or cascading operation as one transaction.

Patch does not automatically save. Save is explicit and separate. Runtime-only or external operations may be non-reversible, but their capability descriptions must say so and must not present them as normal authoring edits.

Runtime edits have their own revisions and must not dirty Authoring, enter its Undo/Redo history, or save back implicitly.

Engine mutation results include transaction ID, before/after revisions, affected objects and fields, created/deleted IDs, diagnostics, and dirty state. Large diffs may be a summary plus a readable diff resource URI.

For project MCP capabilities, detailed changed paths and diff resources are optional. At minimum, exposed project capabilities declare side effects and return structured success/failure plus relevant before/after revisions. If they mutate authoritative state, the engine transaction context generates the authoritative change record so project code does not have to construct it manually.

## Capability Effects And Authorization

Classify exposed capabilities:

- `read`: observes state only.
- `edit`: changes reversible engine authoritative state.
- `runtime`: changes the current simulation without persistence.
- `external`: changes files, network, processes, or systems outside engine-owned state.

Registration is discovery, not authorization. External and irreversible effects still require explicit authority from the caller.

Never serialize credentials or return them through MCP. Store secret reference IDs and resolve them through a credential provider. Redact values in snapshots, diagnostics, logs, and transaction history.

## Diagnostics

Serialization, migration, validation, command, and capability failures return structured diagnostics. Include at least a stable code, severity, object ID, field path, expected constraint, safe actual-value summary, relevant revision, and retryability. Human-readable messages supplement these fields; they do not replace them.

Do not silently default invalid data, discard fields, bind by a similar name, or report a partial operation as success.

## Runtime Input And Visual Evidence

Direct runtime semantic capabilities may set up state, debug logic, or test non-interaction behavior. They do not prove that a user can operate the UI.

Any acceptance claim about clicking, typing, focus, or visibility must begin with real coordinate/device input and traverse visibility, clipping, z-order, hit testing, focus, and event bubbling before reaching the handler. Directly invoking an action or handler is not valid evidence for UI interaction.

Expose both:

- a semantic UI snapshot containing IDs, layout rectangles, visibility, clipping, z-order, focus, and hit targets;
- the actual rendered frame image.

Associate both with the same `frameId` or another unambiguous frame relation.

## Deterministic Automation

AI test mode supports pause, single-step or fixed-delta advancement, and setting/reading random seeds. Record input events with frame and revision information.

Project code may use time, network, randomness, and external files, but deterministic tests must access relevant nondeterministic sources through replaceable services. Allow tests to inject clocks, seeds, responses, and resource versions. If an uncontrolled source participates, mark the result non-deterministic; do not claim exact replay.

Keep Undo/Redo and the operation log session-scoped by default. The log is serializable and exportable for audit/replay, but does not enter authoritative project assets unless the user explicitly exports it.
