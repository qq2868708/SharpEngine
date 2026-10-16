# Component Model Contract

## Base Component

Every scene component derives from the engine component base class. The base class exclusively owns and persists:

- `componentId`: stable instance identity.
- `typeId`: stable component type identity, independent of CLR type names.
- schema version and the common serialization envelope.
- owning entity identity.
- revision/change notification integration.

Project code must not override ID generation, forge revisions, or replace the common envelope. It supplies declared state fields, validation and migration hooks, and behavior through controlled extension points.

Address a component with `entityId + componentId`. Never use display names, array positions, object addresses, or registration order as identity. Saving, loading, and reordering preserve IDs; duplication creates new IDs.

## State

Classify each exposed value:

- `authoritative`: the source of truth; serialize every field explicitly, including values equal to defaults.
- `derived`: computed from authoritative state and exposed read-only.
- `transient`: session or runtime state that is observable but not persisted by default.

Authoritative fields use engine-managed properties, generated accessors, descriptors, or a controlled state container. Writes must pass through engine validation and change tracking. Arbitrary publicly writable fields are not valid authoritative state.

Private caches are allowed only when they are reconstructable and cannot change save/load semantics. Mutable global or singleton state that affects behavior, rendering, or AI decisions must instead live in a declared engine service or serialized state scope.

## Registration And Project Capabilities

Project components may extend the engine only after registering a complete component description. Registration resolves a stable `typeId`, JSON Schema 2020-12 state schema, schema version, defaults, constraints, serialization, validation, and migration behavior. The engine base class or generation system supplies the standard implementation; project code adds custom validation or migrations only when its semantics or version history require them. Unregistered objects cannot enter the instantiated scene.

All components receive the base engine capabilities. Domain behavior remains project-owned. A project may optionally register component capabilities with MCP; lack of MCP exposure does not invalidate an otherwise valid project component.

When a project capability is exposed to MCP, associate it with a component type or instance and publish its name, purpose, input/output schema, side-effect class, availability, and revision requirements. See the AI runtime contract for the MCP-specific rules.

## Unknown Components

If an implementation or migration is missing:

- do not instantiate the component;
- do not show it as an available scene component;
- do not execute it;
- preserve its original serialized record unchanged in the document layer.

Only an explicit redirect, migration, or deletion may replace or remove that record. Saving unrelated edits must not erase it. Cook must fail if included content still contains an unknown component.

## References And Deletion

References to entities, components, and assets use stable typed IDs or URIs. Never serialize live object references, memory addresses, array indexes, or name-based guesses.

Validate references during load, generic patch, hot reload, Authoring-to-Runtime reconstruction, and Cook. Missing required references are errors; missing optional references are explicitly `null` or `unresolved`.

Each reference field declares an `onDelete` policy:

- `restrict` is the default and rejects deletion while referenced.
- `nullify` is valid only for optional references.
- `cascade` requires an explicit declaration and a completely reversible transaction.

Compute all deletion effects before commit and apply them atomically.

## Authoring And Runtime

Expose `authoring` and `runtime` as separate contexts with separate revisions. A write must name its target context.

- Authoring changes may become dirty, persist on explicit Save, and enter Undo/Redo.
- Runtime changes affect the current simulation and are discarded on Stop unless an explicit supported workflow converts them into Authoring edits.

Build the Runtime world from the registered versioned component data contract and validate it again. Do not clone arbitrary managed/native object graphs. Runtime may create handles, caches, and other transient objects after reconstruction, but these never flow back into Authoring implicitly.

## Lifecycle And Threading

The engine defines and owns fixed component lifecycle stages. Do not expose project-configurable `before`, `after`, or dynamic lifecycle dependency graphs. Components in the same stage must not rely on relative execution order. Cross-component work may assume a phase is complete only at an engine-defined boundary where every component has completed that phase.

Commit authoritative state only on the engine world/editor thread. Background tasks may perform IO or computation, but must return a result with its base revision; the world thread revalidates it before committing. Serialization, transaction commit, revision updates, and lifecycle callbacks share this consistency boundary.
