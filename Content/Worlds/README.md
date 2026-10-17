# World and Prefab component data

`.sworld` and `.sprefab` contain strict UTF-8 JSON with `schemaVersion: 3`.
Pre-stable SharpEngine accepts only the current schema. XML/v2 is rejected;
there is no automatic migration or upgrade utility.

A document stores `kind` (`world` or `prefab`), `worldId`, `name`,
`activeCameraId` (an entity ID or null), and an ordered `entities` array.
Each entity stores `entityId`, `name`, `parentId`, `activeSelf`, and `components`.
Each component stores `componentId`, `typeId`, `schemaVersion`, `enabled`, and
`state`. All known fields, including default-valued fields, are explicit.

Every entity has exactly one enabled `engine.transform`. Its `Position`,
`Rotation`, and `Scale` are three-number arrays. Coordinates are right-handed,
meters, Y-up. Rotation is Euler degrees (X pitch, Y yaw, Z roll); scale is
positive. Parent transforms compose local TRS into world matrices. Camera and
directional-light orientation inherit parent rotation but not scale.

Other built-in component types:

| Type ID | State fields |
| --- | --- |
| `engine.mesh-renderer` | `Mesh`, `Material` asset URI or null; RGBA `Color` |
| `engine.camera` | `FieldOfView`, `NearPlane`, `FarPlane` |
| `engine.directional-light` | RGBA `Color`, nonnegative `Intensity` |

Components can coexist on the same entity. Component array order is Inspector
presentation only, including the Transform's position. Instance references use
IDs, never indexes, names, or display order. Entity/component references are
nullable; deleting a target clears serialized references in the same mutation.
Unknown component records survive unrelated saves but are not instantiated or
shown as available components. Cook validation rejects unknown components.

Authoring stores data records. Add/Remove/Reset/Patch/reorder and hierarchy edits
share revision checks and Undo/Redo. Save validates a temporary file before
atomic replacement. Play reconstructs a separate Runtime World from committed
memory, including unsaved edits. Runtime instances execute component callbacks;
Stop discards Runtime changes. Runtime exceptions are logged without rollback
or automatic component deactivation.

Prefab documents contain one root entity and its subtree. The Prefab editor
keeps separate selection, history, and dirty state for each open document.
Export rejects references outside the subtree. Instantiation generates fresh
IDs and remaps internal references before initialization. Instances are deep
copies without persistent override links.

Create a project component with:

```powershell
./Tools/NewSharpComponent.ps1 -ModuleDirectory D:/MyGame/Source/Gameplay -Name HealthComponent -Namespace Gameplay
```

The tool generates a stable type UUID, schema version, and `partial` class.
Public supported instance fields appear automatically; `[SerializeField]`
includes non-public fields. `[NonSerialized]` excludes state, while
`[HideInInspector]` only hides the UI. Runtime Modules receive the component
source generator through the unified build. See
`Documentation/InspectorComponentDesign.md` for the full agreed contract.

Validation:

```powershell
dotnet run --project Tools/ComponentGeneratorValidation/SharpEngine.ComponentGeneratorValidation.csproj
dotnet run --project Tools/ComponentValidation/SharpEngine.ComponentValidation.csproj
dotnet run --project Tools/WorldValidation/SharpEngine.WorldValidation.csproj -- D:/SharpEngine
dotnet run --project Tools/WorldAuthoringValidation/SharpEngine.WorldAuthoringValidation.csproj
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -- D:/SharpEngine
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -- Debug --inspector-only
```
