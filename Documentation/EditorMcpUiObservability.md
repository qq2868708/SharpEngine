# Editor MCP UI Observability and Session Contract

Status: implemented schema version 1

## Purpose

SharpEngine is intended to be self-describing. An AI client must be able to
inspect and operate the editor through engine-owned MCP capabilities without
controlling the user's desktop or inferring behavior from source code alone.

The MCP surface describes committed scene, asset, content, editor, and active
RmlUi state. Semantic UI snapshots and pinned rendered frames allow MCP-only
verification of visibility, hit testing, focus, clipping, z-order, pointer
capture, Dock drag previews, and other transient UI behavior.

This document defines the implemented contract. It is intentionally limited to
engine-owned editor UI observability and input. It does not redesign scene
authoring, project capabilities, or the Dock layout file format.

## Current Implementation

The embedded server in `Source/SharpEngine.Editor/Editor/McpEndpoint.cs`
currently exposes:

- `scene.describe`
- `assets.describe`
- `content.describe`
- `editor.describe`
- `scene.reparent`
- `viewport.input`
- `viewport.key`
- `viewport.tick`
- `editor.action`
- `ui.describe`
- `ui.pointer`
- `ui.key`
- `sharpengine://scene`
- `sharpengine://assets`
- `sharpengine://content`
- `sharpengine://editor`
- `sharpengine://capabilities`
- `sharpengine://ui`
- `sharpengine://ui/frames/{frameId}/{hostId}.png`

`editor.describe` in `Source/SharpEngine.Editor/Editor/EditorRuntime.cs`
includes the committed Dock tree and links to the capability and UI resources.
The full computed RmlUi tree remains in `sharpengine://ui` rather than being
duplicated into the editor snapshot.

`viewport.input` calls managed viewport navigation and gizmo logic directly. It
does not enter the native RmlUi hit-test, focus, pointer-capture, or event
bubbling path. `editor.action` dispatches a semantic action directly and is not
interaction evidence.

The native shell pins BGRA pixels for snapshot frame IDs. Managed code encodes
those exact pixels as PNG for the MCP resource without exposing native handles.

## Session Selection and Transport

The right side of the Editor toolbar always shows `MCP : <port>`. Click it to
open MCP Settings, edit the port, and choose **Confirm & Restart**. Confirmation
atomically saves the independent `Config/Mcp.json` file and immediately restarts
the HTTP endpoint. Cancel discards the draft. Confirming the current port also
restarts the endpoint. Port conflicts and save failures leave the previous valid
configuration and service intact and display a diagnostic in the window.

The file belongs to the open project, or to the engine workspace when no project
is open. It is created on first launch if absent. `SHARPENGINE_MCP_CONFIG` may
select a separate absolute configuration path for isolated sessions and tests.

```json
{
  "schemaVersion": 1,
  "port": 17700
}
```

The default connection URL is `http://127.0.0.1:17700/mcp`. This is a local-only,
stateless MCP Streamable HTTP endpoint: JSON-RPC POST returns JSON, notifications
return 202, and GET returns 405 because no SSE stream is provided. HTTP requests
share the existing capability dispatch and run on the Editor thread. Restarting
disconnects HTTP clients; the stdio bridge and its named-pipe session remain
available. Reconnect HTTP clients to the new port after a change.

`editor.mcp.describe` exposes schema version 1, configuration path, configured and
active ports, endpoint, running state, configuration revision and diagnostics.
`editor.describe.mcp.transport` carries the same state. The external capability
`editor.mcp.configure` accepts `port` and `expectedRevision` and shares the UI's
save/restart operation. These are editor preferences, outside scene Undo/Redo.
External file edits are checked once per second; invalid versions, unknown fields
or unavailable ports retain the last valid live service and report an error.

Run `dotnet run --project Tools/McpUiValidation -c Debug -- Debug --mcp-only`
after building the Editor to validate the rendered toolbar and settings window,
coordinate typing/clicks, HTTP transport, port changes, cancellation, occupied
ports, save failure, revision conflicts and external reload. The fixture uses
its own configuration and saves matching PNG/semantic evidence under
`Saved/Validation/Mcp`.

`--mcp-stdio` starts a sessionless bridge. It does not initialize an
`EditorRuntime`, load a World, or execute project Modules. Before attachment it
exposes only:

- `editor.sessions.list`
- `editor.session.attach`
- `editor.session.launchIsolated`
- `sharpengine://sessions`

Every windowed Editor publishes a per-launch UUID `sessionId` and a
current-user-only named pipe. The user explicitly selects either an existing
Editor or a newly launched isolated Editor. Isolated Editors still create the
full native RmlUi and rendering contexts, but hide their main OS window.

One stdio connection may attach to exactly one Editor session. Switching target
Editors requires a new MCP connection so frame IDs, revisions, pointer capture,
and pending transactions cannot cross session boundaries. Attachment emits MCP
tool- and resource-list changed notifications.

## State Classification

The contract must keep these state classes separate:

| State | Classification | Owner | Persistence |
| --- | --- | --- | --- |
| Saved main-window Dock tree and split ratios | Authoritative editor preference | Editor | Explicit `Save Layout` |
| Floating Dock hosts | Transient session state | Editor runtime | Never persisted |
| Computed element bounds and clip rectangles | Derived | RmlUi runtime | Never persisted |
| Visibility, enabled state, z-order, focus, hover, and hit targets | Transient | RmlUi runtime | Never persisted |
| Pointer capture and active Dock drag/resize preview | Transient | RmlUi runtime | Never persisted |
| Presented framebuffer | Derived transient evidence | Renderer | Retained only for a bounded frame cache |

Dock layout operations are editor-preference operations, not scene Authoring
transactions. They increment the layout revision and dirty the layout, but do
not enter scene Undo/Redo history. A resize preview is not a committed layout
operation.

## Required Invariants

1. An MCP UI snapshot represents one coherent, fully updated RmlUi state.
2. Every AI-observable transient UI change increments `stateRevision`.
3. A committed Dock layout change increments `layoutRevision` exactly once.
4. Hover, pointer movement, capture, and resize preview do not increment
   `layoutRevision` or dirty the saved layout.
5. A semantic snapshot and rendered frame used as evidence share the same
   `frameId`.
6. Coordinate input enters the same native RmlUi visibility, clipping, z-order,
   hit-test, focus, pointer-capture, and event-bubbling path as user input.
7. Direct `editor.action` or handler invocation is never accepted as evidence
   that a visible control is operable.
8. Native handles, pointers, OpenGL resources, and process-local addresses are
   never exposed through MCP.
9. Snapshot ordering and identifiers are deterministic for the same semantic
   state.
10. Invalid or stale input fails with structured diagnostics; it is never
    silently redirected to a similar element.

## UI MCP Surface

### `ui.describe`

Side effect: `read`

Returns the latest coherent semantic UI snapshot. It must be equivalent to
reading `sharpengine://ui`.

### `sharpengine://ui`

MIME type: `application/json`

Provides the bounded root snapshot described below. The resource must describe
all visible editor hosts, including owned floating Dock windows.

### `ui.pointer`

Maximum side effect: `external`

Sends coordinate pointer input to one logical editor host. It must call the
native RmlUi context input functions and allow RmlUi to resolve the target. It
must not call `InvokeUiAction`, `HandleDockAction`, or viewport behavior
directly.

Although pointer movement and resize preview are transient, a coordinate input
can activate Save, project launch, native dialogs, or another externally
effectful control. Discovery therefore declares the maximum effect as
`external`; registration is not authorization.

Required input fields:

```json
{
  "hostId": "main",
  "event": "move",
  "x": 245,
  "y": 420,
  "button": "none",
  "buttons": [],
  "modifiers": [],
  "pointerId": 0,
  "expectedFrameId": 120
}
```

`event` is one of `move`, `down`, `up`, `cancel`, or `wheel`. `button` is one
of `none`, `left`, `right`, or `middle`. A wheel event also requires finite
`wheelX` and `wheelY` values.

`expectedFrameId` must identify a retained observation frame. If unrelated
semantic state changed after that frame, `expectedElementId` allows input only
when current native hit testing still resolves the same stable element. Without
that target precondition the request fails stale. Once native pointer capture
has begun, move, up, and cancel continue against that capture rather than being
invalidated by unrelated visible text changes.

The result includes:

```json
{
  "accepted": true,
  "beforeStateRevision": 40,
  "afterStateRevision": 41,
  "frameId": 121,
  "targetElementId": "host/main/split/main.root/handle",
  "eventPath": [
    "host/main/split/main.root/handle",
    "host/main/split/main.root",
    "host/main"
  ],
  "capture": {
    "pointerId": 0,
    "elementId": "host/main/split/main.root/handle"
  },
  "diagnostics": []
}
```

### `ui.key`

Maximum side effect: `external`

Sends key up/down and text input through the focused RmlUi host. The response
reports the target, event path, focus before/after, state revisions, and frame.
This is separate from `viewport.key`, which is a viewport-specific semantic
capability.

### Rendered frame resource

The server must support a resource template equivalent to:

```text
sharpengine://ui/frames/{frameId}/{hostId}.png
```

The resource MIME type is `image/png`. It returns the exact presented host
frame identified by `frameId`. The engine retains a bounded recent-frame cache
and returns a structured `UI_FRAME_EXPIRED` diagnostic when a frame is no
longer available.

`resources/read` returns the image through the standard MCP base64 `blob`
content field. Image bytes never enter the semantic UI snapshot and no custom
binary framing is introduced on the JSON-RPC transport.

## UI Snapshot Schema

The root object uses JSON Schema 2020-12 and starts at `schemaVersion: 1`.

```json
{
  "schemaVersion": 1,
  "stateRevision": 41,
  "layoutRevision": 7,
  "documentRevision": 18,
  "frameId": 121,
  "hosts": [],
  "diagnostics": []
}
```

Revision meanings:

- `stateRevision`: increments for every observable UI change, including
  hover, focus, capture, preview bounds, and visibility.
- `layoutRevision`: increments only after a committed Dock layout operation.
- `documentRevision`: remains the scene Authoring revision. UI-only changes do
  not create scene write conflicts.
- `frameId`: identifies one completed presentation. It is not a mutation
  revision.

Each host contains:

```json
{
  "hostId": "main",
  "rootId": "main",
  "kind": "main-editor",
  "bounds": { "x": 0, "y": 0, "width": 1440, "height": 900 },
  "contentScale": 1.0,
  "visible": true,
  "focusedElementId": "host/main/pane/viewport/canvas",
  "hoveredElementId": "host/main/split/main.root/handle",
  "pointerCapture": null,
  "elements": [],
  "interaction": null
}
```

Top-level asset editors use `kind: "top-level-editor"` and set `rootId` equal
to their own stable host ID. Owner-scoped detached panes use
`kind: "floating-tab"`; their `rootId` remains the owning main or top-level
editor root. Dock hit testing rejects targets whose `rootId` differs. No HWND
or native window address is included.

Each exposed element contains at least:

```json
{
  "elementId": "host/main/split/main.root/handle",
  "parentId": "host/main/split/main.root",
  "role": "splitter",
  "semanticId": "main.root",
  "bounds": { "x": 243, "y": 80, "width": 4, "height": 795 },
  "clip": { "x": 0, "y": 80, "width": 1440, "height": 795 },
  "visible": true,
  "enabled": true,
  "focusable": false,
  "focused": false,
  "hovered": true,
  "selected": false,
  "checked": false,
  "expanded": false,
  "zOrder": 80,
  "hitTarget": true,
  "text": null,
  "value": null,
  "actions": ["resize"],
  "children": []
}
```

Required roles include `host`, `split`, `splitter`, `tab-group`, `tab`, `pane`,
`button`, `input`, `menu`, `menu-item`, `viewport`, and `text`. Additional roles
may be added without changing the meaning of existing roles.

The snapshot may omit non-semantic decorative elements. It must not omit an
element that can receive input, owns clipping, establishes a relevant z-order,
or changes the interpretation of a visible control.

## Stable Element Identity

Element IDs must be deterministic and must not depend on DOM child indexes,
memory addresses, or generated native handles.

Preferred identities are constructed from existing stable engine IDs:

- `host/{hostId}`
- `host/{hostId}/split/{nodeId}`
- `host/{hostId}/split/{nodeId}/handle`
- `host/{hostId}/group/{groupId}`
- `host/{hostId}/pane/{paneId}`
- `host/{hostId}/pane/{paneId}/tab`
- explicit RML element IDs under the owning pane

Elements without a semantic identity may use a deterministic author-assigned
RML ID. Arbitrary DOM paths are not a stable public contract.

Generated menu and toolbar controls receive IDs derived from their surface,
category/tool ID, and registered command ID. Reusing one command in several UI
surfaces therefore does not produce duplicate element IDs.

## Dock Resize Interaction Contract

Dock resize is the first required end-to-end scenario.

### Pointer down

1. `ui.pointer` receives coordinates tied to an observed `frameId`.
2. Native RmlUi hit testing resolves a visible `splitter` element.
3. The event bubbles through the actual RmlUi path.
4. Pointer capture begins.
5. `stateRevision` increments.
6. `layoutRevision` and layout dirty state do not change.

### Pointer move while captured

1. The active splitter boundary follows the pointer within validated limits.
2. Every DockGroup touching that boundary resizes in the preview.
3. Other splitters with the same orientation retain their absolute screen
   positions.
4. Perpendicular splitters retain their axis position and may change only in
   span as their containing group changes size.
5. The snapshot exposes the preview bounds and all affected split ratios.
6. `stateRevision` and `frameId` advance.
7. `layoutRevision`, persistent layout data, and dirty state remain unchanged.

The host interaction object is:

```json
{
  "kind": "splitter-resize",
  "phase": "preview",
  "pointerId": 0,
  "sourceElementId": "host/main/split/main.root/handle",
  "targetNodeId": "main.root",
  "orientation": "horizontal",
  "initialBoundary": 245,
  "previewBoundary": 350,
  "affectedGroups": [
    "main.hierarchy.tabs",
    "main.content.tabs",
    "main.viewport.tabs",
    "main.console.tabs"
  ],
  "preservedSplitters": ["main.body"],
  "previewRatios": [
    { "nodeId": "main.root", "ratio": 0.243056 },
    { "nodeId": "main.body", "ratio": 0.727273 }
  ]
}
```

### Pointer up

1. The complete preview ratio set is validated against the current layout.
2. Ratios commit atomically as one layout operation.
3. `layoutRevision` increments exactly once and the layout becomes dirty.
4. No scene Authoring Undo/Redo entry is created.
5. The transient interaction clears and `stateRevision` advances.
6. The next frame and snapshot show the committed geometry.

### Cancel or capture loss

1. All preview geometry returns to the last committed layout.
2. No ratio commits.
3. `layoutRevision` and dirty state remain unchanged.
4. The transient interaction clears and `stateRevision` advances.

## Native and Managed Ownership

The native RmlUi shell owns facts that only the live UI can know:

- computed bounds and clip rectangles;
- render and hit-test ordering;
- hover, focus, and pointer capture;
- resolved event targets and bubbling paths;
- active Dock drag/resize preview state;
- completed framebuffer images and `frameId`.

The managed editor owns:

- committed Dock layout state and `layoutRevision`;
- editor preference dirty/save behavior;
- scene `documentRevision`;
- MCP schema construction and structured diagnostics.

The versioned native API must be extended so `McpEndpoint` can request a
coherent UI snapshot, send native UI input, and read a captured frame. Managed
code must not reconstruct RmlUi rectangles from the serialized Dock tree.

Snapshot and input requests execute on the editor thread. If MCP transport work
arrives on another thread, it must queue to the editor thread and return only
after the requested consistency boundary completes.

## Diagnostics

Every failure returns a structured diagnostic with `code`, `severity`,
`objectId`, `fieldPath`, `expected`, `actual`, `stateRevision`, and `retryable`.

Minimum codes:

| Code | Meaning |
| --- | --- |
| `UI_HOST_NOT_FOUND` | `hostId` is unknown or no longer live |
| `UI_FRAME_STALE` | The observation frame expired or no longer has a safe current target relation |
| `UI_FRAME_EXPIRED` | Requested rendered frame left the bounded cache |
| `UI_TARGET_CHANGED` | Current hit testing does not resolve `expectedElementId` |
| `UI_COORDINATE_OUT_OF_BOUNDS` | Input coordinate is outside the host |
| `UI_TARGET_NOT_INTERACTIVE` | Hit element cannot accept the requested input |
| `UI_POINTER_CAPTURE_CONFLICT` | Another pointer or host owns capture |
| `UI_POINTER_SEQUENCE_INVALID` | Down/move/up ordering is invalid |
| `UI_LAYOUT_REVISION_CONFLICT` | Resize release targets a changed layout |
| `UI_SNAPSHOT_UNAVAILABLE` | No coherent updated RmlUi state can be captured |
| `UI_RENDER_UNAVAILABLE` | A semantic snapshot exists but no matching frame was presented |

Failures never fall back to `editor.action`, viewport input, a nearby element,
or desktop automation.

## Capability Discovery

`tools/list` descriptors and `sharpengine://capabilities` declare:

- stable capability ID and schema version;
- side-effect class (`read`, `edit`, `runtime`, or `external`);
- whether a live visible host is required;
- coordinate space and content scale;
- required revision or frame preconditions;
- current `available` state;
- structured unavailability reason.

`editor.describe` should link to `sharpengine://ui` and report the current
`stateRevision` and `frameId`, but it should not duplicate the full UI tree.

## Validation Matrix

Implementation is incomplete until automated validation covers all of the
following:

1. `tools/list`, `resources/list`, and resource-template discovery expose the
   declared schemas and side effects.
2. Two reads of unchanged UI state serialize identically.
3. Stable IDs survive layout recomputation and frame presentation.
4. A pointer down at a splitter coordinate resolves the expected element and
   event path through native RmlUi.
5. Pointer move with capture changes preview DockGroup bounds, advances
   `stateRevision`, and leaves `layoutRevision` unchanged.
6. Groups touching the dragged boundary resize while every other same-axis
   splitter retains its absolute position.
7. A semantic snapshot and PNG for one `frameId` agree on host dimensions and
   visible preview geometry.
8. Pointer up commits all affected ratios atomically, advances
   `layoutRevision` once, and creates no scene Undo/Redo entry.
9. Pointer cancel and capture loss restore the committed layout without dirtying
   it.
10. Hidden, clipped, disabled, and occluded elements cannot be reported as the
    accepted hit target.
11. Focus and keyboard events traverse the focused RmlUi path.
12. Main and floating hosts use independent coordinate spaces and stable host
    IDs.
13. Stale-frame, invalid-sequence, and revision-conflict diagnostics are
    deterministic and non-mutating.
14. Validation runs without desktop control, OS-level mouse injection, or a
    user's foreground window.

Source review, direct action dispatch, a post-release layout snapshot, or a
standalone screenshot does not satisfy these acceptance criteria.

## Implemented Architecture

1. Native `RmlUiShell` owns `stateRevision`, frame-set IDs, semantic extraction,
   input dispatch, and bounded pinned frame pixels.
2. Managed native API version 9 exposes snapshot, pointer, key, and frame reads.
3. A single capability catalog produces tool schemas, standard annotations,
   effects, availability, and the capability resource.
4. A sessionless stdio bridge performs explicit session discovery and selection;
   live requests are serialized onto the selected Editor thread.
5. `Tools/McpUiValidation` launches a hidden rendered Editor and validates the
   protocol without desktop control or OS-level input injection.

## Non-Goals

- Exposing HWNDs, native pointers, GL objects, or the complete private DOM.
- Treating MCP as the only way a user can operate the editor.
- Replacing semantic authoring commands with fabricated UI clicks.
- Making project-owned internal features implement MCP capabilities by default.
- Persisting hover, focus, pointer capture, floating hosts, or preview geometry.
- Using desktop automation as a substitute for missing engine observability.

## Current Validation Boundary

`Tools/McpUiValidation` currently validates session discovery and attachment,
capability metadata, stable element identity, deterministic snapshots,
splitter hit testing and capture, transient preview invariants, exact preview
pixels in a pinned PNG, atomic layout commit, cancel, stale and invalid input,
focused text input, and main/floating host frame resources. Direct
`editor.action`, `viewport.input`, or source review remains invalid evidence for
visible UI operability.
