# Serialization And Cook Contract

## Source Of Truth

Editor assets are readable text and are the authoritative source. A special suffix, container, or encryption layer may make a file appear binary, but the editor contract remains a structured, readable text representation before Cook.

AI-facing contracts use versioned JSON. Current World/Prefab source documents use strict v3 JSON. Other domain storage, including RML/RCSS, may remain in its deterministic text format when its adapters are lossless for authoritative semantics. Do not infer support for legacy World XML from this storage flexibility.

Do not serialize native handles, GPU objects, threads, delegates, open streams, or caches. Serialize stable descriptors, IDs, URIs, settings, and provenance needed to rebuild them.

## Versions And Migration

Every root object carries `schemaVersion`. Determine the supported versions from that format's current reader and confirmed design before changing it. The pre-stable World/Prefab contract accepts v3 only; components accept their registered schema version only. Do not add v2 compatibility, automatic repair, or component migration hooks to these contracts. Other formats can have explicit supported legacy readers; preserve those within their documented scope. Compatibility is a per-format decision, not an engine-wide default.

Reject unknown future versions, unknown required types, invalid known fields, and failed migrations with structured diagnostics. Never hide incompatibility with silent defaults or field deletion.

Preserve stable component type IDs, asset IDs, and other serialized identities when semantics have not changed. An identity change requires an explicitly designed content/reference update or supported redirect; a schema version bump alone does not repair references. Do not invent compatibility paths for a current-only format.

Save every authoritative field explicitly, including values equal to current defaults. Defaults create new state and assist migration; they must not reinterpret an existing saved asset after an engine upgrade.

The same semantic state produces deterministic serialized output. Preserve business-significant ordering explicitly. Avoid environment-specific paths, timestamps, locale formatting, unstable dictionary iteration, and incidental registration order.

## Save And Hot Reload

State edits commit to live memory and mark their authoritative document dirty. They do not save automatically. Explicit Save serializes to a temporary file, validates the result, and atomically replaces the target.

Detect external edits to authoritative text assets. Hot reload uses:

1. parse into a staging representation;
2. apply only the format's explicitly supported conversions and completely validate it;
3. atomically replace the live state on the world/editor thread;
4. update revisions and diagnostics.

On failure, retain the previous valid live state. Never expose a partially written or partially parsed asset.

Unknown component records remain verbatim in the document layer during unrelated saves, even though they are not instantiated. Known schema violations fail validation rather than being silently discarded.

## Asset Identity And Dependencies

Separate stable `assetId` from file path and display name. Moves and renames preserve identity. Asset URIs resolve through the stable ID. Changing an ID requires redirect/migration support and upgrades old references on an explicit successful save.

All Runtime asset dependencies must be discoverable for Cook. Prefer serialized stable asset URIs. Dynamic selection must declare an enumerable asset set, tag query, or deterministic dependency generator. Reject arbitrary untracked path construction when it prevents dependency discovery.

AI snapshots describe asset identity, type, schema version, source, content hash, dependencies, import settings, and available capabilities. Large content may be read through resource URIs instead of being embedded into a scene snapshot.

## Cook

Cooked files are derived, disposable, reproducible artifacts. They never become editor authority and never overwrite source assets. A Cook manifest records inputs, configuration, provenance, diagnostics, and output hashes.

Cook fails when included content contains:

- unknown components;
- incomplete or failed migrations;
- missing required references;
- undiscoverable required dynamic dependencies;
- invalid schema data.

Only explicitly Editor-only content may be removed, and the Cook report records each removal.

Keep three concepts separate:

- `contentHash` is computed from the actual Cook output bytes.
- `cookInputFingerprint` covers source hashes, Cook configuration, target platform, and only the Cooker/Schema versions that can affect that output.
- the complete engine version is manifest metadata and does not automatically invalidate the fingerprint or fail Cook.

The same effective inputs and relevant tool versions should produce identical content and manifest hashes. Exclude timestamps, absolute paths, random iteration order, and other environment noise. If a platform toolchain cannot be fully deterministic, record the exact nondeterministic source in the manifest rather than claiming reproducibility.

## Engine Validation

For engine-owned formats and built-in components, test applicable invariants:

- default and non-default values serialize explicitly;
- save/load and Authoring-to-Runtime round trips preserve authoritative semantics;
- repeated serialization is deterministic;
- current-only formats reject unsupported versions; explicitly supported old versions read or migrate correctly;
- invalid and unknown required data produces structured diagnostics;
- external hot reload is atomic and retains the prior valid state on failure;
- revisions, dirty state, transactions, Undo/Redo, and explicit Save behave independently;
- Cook rejects invalid dependencies and is deterministic for the same fingerprint.

Do not require project authors to duplicate the complete engine test suite. Project components still pass engine registration/load checks. Test only the MCP contracts a project elects to expose.
