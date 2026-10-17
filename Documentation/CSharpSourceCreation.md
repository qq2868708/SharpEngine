# C# creation and project code reload

Use **File > New C# Class...** to select an existing project Module, a relative
subdirectory and a type name. Runtime Modules offer Component (the default) and
Plain Class; Editor Modules offer Plain Class. Each operation creates one file
named after its single top-level type. Modules remain under `Source`; assets
remain under `Content`.

The namespace is read-only: evaluated MSBuild `RootNamespace` plus the relative
directory segments. Invalid identifiers, keywords, filesystem-reserved names,
path traversal, directory links, ambiguous Module ownership and existing target
files are rejected. The resulting path must be included in the Module's evaluated
`Compile` items. Failed inclusion checks remove only this operation's unchanged
new file, preserving any concurrent external edit.

Components derive from `SharpComponent`, are public sealed partial classes and
receive a fresh `ComponentTypeId` UUID and `ComponentSchemaVersion(1)`. No empty
lifecycle overrides or second Module entry are generated. UI, MCP and the
`Tools/NewSharpComponent.ps1` compatibility command use the same creation service.
The script accepts `-Directory` for a Module-relative subdirectory. Its optional
legacy `-Namespace` argument must match the derived namespace.

Source files are authoritative external authoring inputs. Creation writes UTF-8
to disk explicitly and is not a World/Prefab Undo operation. The wizard's input
and build status are transient. DLLs, shadow copies and build logs are derived.
MCP exposes versioned `source.modules`, `source.create` (external effect), and
`source.status` capabilities. `editor.describe.projectCode` contains the same
build/reload status. A successful file-creation result does not assert that the
file has compiled or loaded.

While an Editor project is open, a background worker checks source/configuration
content hashes, coalesces edits and invokes the unified ProjectBuild entry point
for the current Editor configuration and platform. It watches project `Source`
C#, project, props and targets files plus root project/props/targets files;
generated `bin`/`obj` files are excluded. Edits outside these roots require
**File > Build Project Code**. Project builds serialize against the existing
ProjectBuild directory lock. Diagnostics are retained in
`Saved/Logs/ProjectBuild.log` and exposed through `source.status`.

Every project generation uses a collectible AssemblyLoadContext and immutable
copies beneath `Intermediate/<Platform>/<Configuration>/Editor/HotReload`.
Engine contract assemblies are shared with the Bootstrap context. Generation
directories are reclaimed after their load contexts become collectible; the
engine does not force garbage collection. Source changes observed during a build
supersede that build for activation.

Activation occurs on the Editor thread only when Stopped and no Inspector input
or gizmo gesture is pending. Playing and Paused
retain the current code and Runtime instances until Stop. This implements Module
replacement, not in-place method-body patching during Play.

Before switching, the new component registry and project settings are staged and
validated against all open World/Prefab documents, save state and Undo/Redo
history. Unsupported field/schema changes reject the candidate without dropping
data. Successful switching preserves document instances, IDs, selection,
revisions, dirty state and history. Project settings are reconstructed using
stable settings IDs, preserving values and history when compatible.

Compilation or preflight failure leaves the current generation available.
Shutdown/Startup failure terminates that reload attempt, cleans the failed
generation's engine-owned registrations, restores the captured authoring state
and disables Play. The Editor remains open. Editing source or choosing Build
Project Code can recover with a subsequent generation; there is no automatic
restart or automatic repeat of a failed lifecycle callback.

Module registration scopes own menu, toolbar, extension, ViewModel and input
registrations made during Startup and scoped callbacks, even when project code
forgets to dispose their handles. Project code remains responsible for external
events, background tasks, native resources and other arbitrary effects outside
these engine APIs; a failed callback cannot undo external side effects.

Validation:

```powershell
dotnet run --project Tools/SourceCreationValidation/SharpEngine.SourceCreationValidation.csproj `
  -c Debug -p:SharpEngineRoot=D:/SharpEngine/Saved/Validation/CodeCreationEngine/ -- D:/SharpEngine
```

The integration fixture compiles real project Modules and checks creation,
schema rejection, Startup failure/retry, deferred activation during Play, source
compile failure/retry, document/history retention and generation collection.
`Tools/McpUiValidation --source-only` exercises the wizard through native pointer
and text input and captures its presented frame; set
`SHARPENGINE_VALIDATION_PROJECT` to a disposable validation project.
