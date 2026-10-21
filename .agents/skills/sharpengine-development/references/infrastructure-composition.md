# Infrastructure And Composition

Follow the user's pyramid architecture: general infrastructure forms the base;
content adapters and small compositions produce higher-level capabilities.
The authoritative contract and current example are in
[EngineArchitecture.md](../../../../Documentation/EngineArchitecture.md).

Before extending a reusable capability, identify its foundation, adapters,
composition and feature-specific customization. Dependencies point downward.
Splitting files or introducing an interface is insufficient if the base still
requires business state, an asset editor or a World to function.

Verify an independent base use case and isolation between consumers. For 3D
viewports, the base must render content without World/Editor dependencies;
World is attached content, and the observation camera belongs to the viewport.
Input must not edit camera entities or authoritative records in an attached World.
Do not create dummy Worlds or duplicate render/input pipelines to satisfy a
feature. Compose existing capabilities and keep manual maintenance straightforward.

## UI Layout Composition

For a dedicated asset editor, keep feature logic and its layout definition separate.
Use the UI-domain `IUiLayoutPart` contract for referenced child layouts; do not copy
child markup into the parent or create an arbitrary business-object layout container.
The parent owns composition data and event bindings. Layout rendering does not Tick
children. Shared definitions stay immutable; mutable values, slots and Dock instances
belong to each editor. The skeletal-animation example and build/restart boundary are
maintained in `Documentation/AnimationAndTimeline.md`.

Register concrete asset editors in their owning feature Module. A Timeline consumer
belongs above Timeline infrastructure: `Animation.Editor` references
`Timeline.Editor`, never the reverse. Keep animation-specific JSON transactions
and history with animation; reusable Timeline edit builders only return candidates.
Do not move a registration alone while retaining a lower-layer branch for editing
the consuming asset type. Module identities and target filtering are maintained in
`Documentation/ProjectOrganization.md`.

For code-driven UI changes use a per-use `UiLayoutInstance`, never mutate the
shared `UiLayoutTemplate` or save its source implicitly. Parent lookup stops at
child layout and repeat boundaries; customize a referenced feature through its
public API. Mounts borrow `IUiLayoutPart` instances and do not dispose or Tick
them. Route events through `IUiActionHandler` and preserve native widget identity
on refresh. Usage and the real-input validation entry point are maintained in
[RuntimeUiComposition.md](../../../../Documentation/RuntimeUiComposition.md).
