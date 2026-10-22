# Component Model Contract

## Base Component

Every scene component derives from the engine component base class. The base class exclusively owns and persists:

- `componentId`: stable instance identity.
- `typeId`: stable component type identity, independent of CLR type names.
- schema version and the common serialization envelope.
- owning entity identity.
- revision/change notification integration.

Project code must not override ID generation, forge revisions, or replace the common envelope. It supplies declared state fields, static validation, and behavior through controlled extension points. Follow the current component schema policy rather than adding migration hooks by default.

Address a component with `entityId + componentId`. Never use display names, array positions, object addresses, or registration order as identity. Saving, loading, and reordering preserve IDs; duplication creates new IDs.

## State

Classify each exposed value:

- `authoritative`: the source of truth; serialize every field explicitly, including values equal to defaults.
- `derived`: computed from authoritative state and exposed read-only.
- `transient`: session or runtime state that is observable but not persisted by default.

Component authoring uses serialized data records and generated descriptors. Supported public instance fields participate by default; non-public fields may opt in with `SerializeField`. Properties are not serialized configuration fields by default. Authoring does not construct project components or execute their constructors to discover defaults. The generator derives deterministic defaults and adapters from source.

Authoring edits and Inspector/AI edits use shared validated transactions and change tracking. Project Runtime code may directly write ordinary component fields without a mandatory `SetField` API or per-assignment validation. A complete Runtime Tick is the observation boundary; Runtime Inspector edits and structural operations also establish explicit commit boundaries. Do not validate every pre-existing Runtime field when adding an unrelated component. Runtime business invariants remain the component author's responsibility.

A WorldDocument owned by WorldAuthoringSession rejects direct entity setters. Its public Data and component-record reads are detached copies. Use EditorRuntime.EditWorld or EditorCommandContext.EditSelectedEntity for extension authoring; MarkDirty cannot authorize document writes. Transaction candidates must not remain writable aliases after commit, and callbacks cannot nest commits, history operations, or savepoint changes.

Private caches are allowed only when they are reconstructable and cannot change save/load semantics. Mutable global or singleton state that affects behavior, rendering, or AI decisions must instead live in a declared engine service or serialized state scope.

## Registration And Project Capabilities

Project components may extend the engine only after registering a complete component description. Registration resolves a stable `typeId`, JSON Schema 2020-12 state schema, schema version, defaults, constraints, serialization, and validation. The engine base class or generation system supplies the standard implementation. The pre-stable component contract accepts the registered schema version only and has no component migration hook. Unregistered objects cannot enter the instantiated scene.

All components receive the base engine capabilities. Domain behavior remains project-owned. A project may optionally register component capabilities with MCP; lack of MCP exposure does not invalidate an otherwise valid project component.

When a project capability is exposed to MCP, associate it with a component type or instance and publish its name, purpose, input/output schema, side-effect class, availability, and revision requirements. See the AI runtime contract for the MCP-specific rules.

## Unknown Components

If a component type implementation is missing:

- do not instantiate the component;
- do not show it as an available scene component;
- do not execute it;
- preserve its original serialized record unchanged in the document layer.

Only an explicit redirect, migration, or deletion may replace or remove that record. Saving unrelated edits must not erase it. Cook must fail if included content still contains an unknown component.

A known component with an unsupported schema version or invalid state is a validation failure, not an unknown component to instantiate with defaults.

## References And Deletion

References to entities, components, and assets use stable typed IDs or URIs. Never serialize live object references, memory addresses, array indexes, or name-based guesses.

Project code uses nullable entity/component object references and typed `AssetRef<T>` values; serialized records store stable identities. Validate reference resolution during load, generic patch, hot reload, Authoring-to-Runtime reconstruction, and Cook.

Deleting an entity or component clears corresponding serialized entity/component references. In Authoring, deletion and reference clearing are one reversible transaction; Undo restores both. Ordinary references do not block deletion and do not declare `Restrict`/`Nullify`/`Cascade` policies. Static `RequireComponent` dependencies can prevent component removal; keep that rule separate from ordinary references. Non-serialized caches are the project code's responsibility.

Prefab export validates its reference scope; do not silently clear external references just to make export succeed. Do not generalize entity/component deletion behavior to asset deletion without inspecting the asset contract.

## Authoring And Runtime

Expose `authoring` and `runtime` as separate contexts with separate revisions. A write must name its target context.

- Authoring changes may become dirty, persist on explicit Save, and enter Undo/Redo.
- Runtime changes affect the current simulation and are discarded on Stop. There is currently no Apply Runtime Changes workflow and no simultaneous Authoring editing during Play.

Build the Runtime world from the registered versioned component data contract and validate it again. Do not clone arbitrary managed/native object graphs. Runtime may create handles, caches, and other transient objects after reconstruction, but these never flow back into Authoring implicitly.

## Lifecycle And Threading

The engine defines and owns fixed component lifecycle stages. Do not expose project-configurable `before`, `after`, or dynamic lifecycle dependency graphs. Components in the same stage must not rely on relative execution order or Inspector order. Cross-component work may assume a phase is complete only at an engine-defined boundary where every component has completed that phase.

Construct a complete creation batch, inject identities/state/references, then run initialization, enable, and first-start barriers. Runtime structural requests made inside lifecycle callbacks are queued to a safe boundary. An owned helper's domain evaluation is distinct from engine component lifecycle; nesting a feature must not register or tick the same component twice. See `Documentation/InspectorComponentDesign.md` for the current callbacks, activation, mutation, and exception policy.

Commit authoring edits, external Runtime edits, and structural mutations on the engine world/editor thread. Background tasks may perform IO or computation, but must return a result with its base revision; the world thread revalidates it before committing. Do not confuse this external mutation boundary with ordinary project Runtime field assignment inside engine callbacks.
