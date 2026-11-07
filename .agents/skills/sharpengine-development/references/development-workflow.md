# Development And Delivery Workflow

## Baseline And Ownership

Record HEAD, branch/upstream, staged paths, existing tracked edits, and relevant untracked files before editing. Distinguish the task's files from parallel work. Preserve shared edits and resolve overlapping ownership before changing the same region. Never treat a dirty working-tree build as proof that an isolated subset compiles.

For independently deliverable changes in a shared tree, validate the intended source snapshot, using an isolated checkout when necessary. Include required untracked sources and pinned submodules. Avoid a permanent alternative build pipeline or custom output bypass. Record the tested revision or snapshot, commands, configuration, target, backend, and meaningful result. If source changes after a relevant check, assess whether that evidence still applies.

## Build Authority

Read `Documentation/ProjectOrganization.md` and `Build/README.md` for build/module changes. Use `Tools/SharpEngineBuild.ps1` for engine targets and `Tools/SharpEngineProject.ps1` for project builds, or their documented underlying builders when a focused validation requires them. Solutions, wrappers, launch profiles, and generated component adapters are derived; change their source metadata and regenerate.

Engine build targets are Editor, Game, and Server; tool roles select the Tools output target. Validation suite names are not engine targets. Do not pass `SharpEngineTarget=SpatialValidation` or another suite name into engine dependencies. Ordinary validation executables may keep their existing .NET tool output layout; their fixtures, captures, logs, and reports belong under `Saved/Validation`.

Generated IDE launch Targets bind a fixed Configuration/Target tuple. Keep their
build command, solution ActiveCfg mapping, bootstrap executable path, and private
launchSettings.json aligned. Solution Build enables only the globally selected
tuple; explicitly building a launch Target must retain its own tuple even when
the solution configuration differs. Validate this with
`Tools/ProjectSelectorValidation --launch-configuration-only`.

`Build/SharpEngine.Build.props` owns engine binaries/intermediates. Use `Binaries/<Platform>/<Configuration>/<Target>` and the matching `Intermediate` tuple. Do not redirect engine `OutputPath`, `OutDir`, or intermediate properties to bypass a lock or guard. Inspect the current supported isolation options or build a separate checkout. Do not terminate the user's Editor to unlock binaries.

Run a read-only layout check when changing build metadata, generators, publication, or output paths:

```powershell
./Tools/SharpEngineLayout.ps1 -EngineRoot . -Mode Check -Scope Target -Target Editor -Configuration Debug
```

Select the tuple actually affected. Use `-Scope All` for full-layout claims. Check is distinct from Apply/Clean; a layout failure does not authorize cleanup of unrelated outputs. `Tools/LayoutValidation.ps1` covers the build-layout contract.

Render builds select `OpenGL` or `Both` through `-RenderBackends`, the project's
optional `renderBackends`, and native `SharpEngineRenderBackends`. New templates
select OpenGL; omitted settings preserve Both. Keep runtime `--render-backend`
separate from compiled capabilities. Validate OpenGL without an SDK, Vulkan
rejection in that build, and Both with the SDK when touching this boundary.
Modes share canonical target outputs, so rebuild and restart after switching.
Follow `Documentation/RenderBackends.md`; do not add alternative output roots.

## Module And Plugin Boundaries

`Documentation/ProjectOrganization.md` is authoritative for current ownership, discovery, and target filtering. Runtime must not depend on Editor. Engine feature Modules and reloadable project Modules have different lifetimes. A Plugin is a distribution/discovery container of Modules and content, not another runtime lifecycle or a universal functional-composition unit. Projects explicitly enable Plugins; placement under `Plugins/` alone does not activate them.

Keep evaluated project metadata and manifests authoritative. Do not add hand-maintained parallel project lists. Inspect current project/Plugin build entry points before compiling a Module directly; their output guards require project-aware staging. Verify affected dependency closure, target filtering, and publication failure behavior.

Editor/Game/Server must register module subsystems before creating World components and release World/Instance owners before unloading modules. Server uses a staged DedicatedServerSession followed by StartWorld. A Session with a private Engine advances it; a shared Engine is advanced once by its host. Validate initialization, update and teardown across these ownership modes when changing host startup.

## Editor Sessions And Reload

For interactive MCP use, list sessions and obtain the user's explicit choice of an existing Editor or isolated Editor before binding. Honor a selection already made in the task. Do not auto-attach to the first or most recent Editor. One connection stays bound to one session.

After attachment and `tools/list_changed` / `resources/list_changed`, rediscover the live catalog and schemas. Preserve the selected session identity, actual tool/resource names, registration state, and pending revisions across context handoffs. Do not reconstruct missing schemas from memory or mistake the unbound bootstrap tool list for the attached catalog.

Native `McpUiValidation` modes retain full bootstrap and attached tool schemas,
resource/template catalogs, session descriptors, and attachment notifications
under `Saved/Validation/Mcp/Discovery/<sessionId>`, including focused modes.
Keep these complete discovery records alongside interaction evidence.

Repository validation tools may create their own explicitly targeted isolated sessions as part of authorized tests; that does not authorize attaching to or closing a user's session. They own and clean up only their test processes and fixtures. Read `Documentation/EditorMcpUiObservability.md` for input/frame protocols.

Project code may build in the background, but apply a generation only while Stopped and after pending input/transactions are committed. Playing/Paused retain the active generation. A failed preflight or swap preserves the previous valid generation and open-document state; verify IDs, revisions, dirty state, selection, settings, and Undo/Redo where affected. Plugin configuration changes that require restart must not be presented as a successful code reload. Inspect `EditorModuleReload.cs` and `EditorRuntimePlugins.cs` for the current boundaries.

## Validation Routing

Choose checks from the actual change's blast radius. The following are entry points, not an unconditional full-suite requirement:

| Changed behavior | Focused entry points and required evidence |
| --- | --- |
| Components, serialization, lifecycle | ComponentGeneratorValidation, ComponentValidation, WorldValidation, WorldAuthoringValidation; current-version rejection and Runtime reconstruction |
| Input, control, Root Motion, movement | InputValidation, ControlValidation, AnimationValidation, PrefabMovementValidation, CharacterMovementValidation as affected; UI-first consumption, capture cancellation, motion ownership |
| Timeline | TimelineValidation and affected AnimationValidation; nested evaluation, clocks, seek and output contracts |
| Editor workflows and asset tools | EditorValidation and relevant McpUiValidation modes; real coordinate/key paths and matching rendered frame for interaction claims |
| Project/Plugin discovery, build and startup | ProjectSelectorValidation, ProjectBrowserMcpValidation and actual affected Editor/Game/Server launch; target filtering and project staging |
| Rendering, native interop, windows | Affected native targets and OpenGL/Vulkan paths; GpuSceneValidation covers managed preparation only, not native GPU lifetime |
| Networking/Server | NetworkValidation and RunDedicatedServerValidation.ps1 as affected; real server/client results for transport claims |
| Build layout or manuals | LayoutValidation / SharpEngineLayout Check, or scripts/Validate-EngineManual.ps1; state what each structural check proves |

Inspect each tool's current arguments before running it. Keep compilation, managed semantics, native rendering, interaction, Cook, and packaged delivery as separate evidence. Test changed interfaces together across managed/native consumers; a stale executable does not validate new managed behavior. Run the relevant standard build after isolated diagnosis before claiming that standard build passes.

## Editor Performance Acceptance

Editor performance is part of acceptance, not optional follow-up work. Do not
accept a reproducible increase in frame time, interaction latency, asset-open
time, allocation pressure, or background CPU/I/O relative to the equivalent
baseline. Feature correctness alone does not pass this gate. Investigate noisy
results with repeated matched runs; measurement noise is not permission to
ignore a sustained regression or lower the budget until it passes.

For affected editor paths, retain a baseline and candidate measurement using
the same representative project/assets, build configuration, backend, VSync,
validation flags, debugger state, window sizes/count, and operation. Cover idle
main-window use, affected resource windows, and the actual edit/navigation or
playback gesture; include large assets when work could scale with document size.
Record warm-up, sample duration/count, average and P95 frame time, operation
latency, and relevant allocation/CPU/I/O evidence. VSync-limited FPS alone can
hide lost CPU headroom; use uncapped timing or a focused allocation/CPU check
where necessary. Distinguish profiling overhead from the unprofiled acceptance
run. Set workload-specific budgets before the candidate run and preserve the
raw results, source snapshot, and tested binary identity.

Use `render.describe` for frame/scene timing and GPU/fallback/cache counters,
plus a targeted profile when attribution is unclear. Do not treat the main
scene's timing as total resource-editor UI time, or a single idle sample as
proof of responsive gestures. An unchanged large JSON resource must not trigger
full-document serialization, source formatting, or content-dependent dirty
checks every frame. Cache only derived data; invalidate at successful edits,
Undo/Redo, savepoint changes, reload, and other actual owners' revision boundaries.
Preserve immediate committed observations, complete tool/resource catalogs,
capture/focus behavior, and all authoring semantics.

A failed budget or baseline regression blocks completion. Fix it and rerun the
affected measurement; report any unmeasured boundary instead of declaring the
performance requirement satisfied. Tests own their isolated sessions and must
not replace or terminate the user's selected Editor. For the resource-document
regression, `Tools/EditorValidation --asset-performance-only` covers large
unchanged reads and savepoint/cache correctness; rendered frame and interaction
measurements remain separate requirements.

`Tools/Assets/ValidateEditorPerformance.py --record-baseline --output <directory>`
records an owned native Debug/OpenGL/VSync-on baseline. Run the candidate with
`--baseline <baseline-directory/report.json> --output <new-directory>` after
building the same configuration; `--root` selects a separately built checkout.
The script retains complete catalogs, raw timings, binary hashes, and real native
viewport-wheel snapshots/PNGs. Its workload budget is average <=18 ms and P95
<=25 ms; its 5% comparison tolerance only identifies measurement noise and does
not authorize sustained regressions. Apply other budgets to other measured
workloads rather than treating these numbers as a universal engine guarantee.

Use its `--render-vsync off` option for a matched run without display-sync waits.
`cpuMsPerFrame` divides editor-process CPU time by native submitted frames and
includes worker/GC CPU time. Retain its raw CPU seconds and frame deltas; compare
it separately from wall-clock frame timing, which includes waits. This metric
also participates in the baseline regression check when present in both reports.

## Input, Motion And Native Resource Review

For input/movement work read `Documentation/ControlAndMovement.md` and the actual host routing. RmlUi consumes first; unhandled semantic input reaches its intended route and host. Focus/capture loss clears held state. Do not add a parallel raw project input path. Verify one movement authority commits a target transform at the defined finalize boundary, including Root Motion consumption and conflict diagnostics.

An active IMovementAuthority also suspends same-entity NavAgent writes, with NAV_MOVEMENT_AUTHORITY observation. Verify navigation resumes after authority disable/removal; do not accidentally reinstate a second Transform writer when adding path-following adapters.

For rendering/interop read `Documentation/RenderBackends.md`. Identify the context/device, thread, owner, lifetime, and invalidation boundary of each retained GPU/native resource. Creation, use, release, document replacement, floating-window closure, and shutdown must honor that ownership. OpenGL context ownership and Vulkan device/synchronization requirements need backend-specific evidence; a managed cache test or one screenshot does not establish correct teardown. Preserve the pinned `External/RmlUi` fork workflow; dependency changes require a reproducible parent pin and available dependency commit.

## Review And Git Delivery

For UI visual layout authoring, retain the supported container/slot semantics and
the authored RCSS unit when manipulating geometry. RML/RCSS remain authoritative;
`data-ui-layout` and `data-ui-align-x/y` carry validated literal editing intent,
not a second runtime layout graph. Preview dimensions/zoom are per-instance
transient state. Do not rewrite arbitrary custom styles into presets on open or
selection. Native layout measurements and real transformed hit tests are required
for drag acceptance; a canvas resize does not establish viewport-unit/media-query
parity when the Design canvas still shares its host context.

Before delivery inspect the final diff and run `git diff --check` over the relevant change. Review new/changed source and code examples against `engine-code-style`; preserve required external/serialized identifiers. Do not format unrelated files.

When submission is requested in this workspace, it means commit plus push unless the user narrows it. Stage explicit task-owned content, inspect the staged diff including shared-document hunks and submodule pointers, and verify the pushed remote revision. Analysis or implementation authorization alone is not a request to commit/push. Report the tested snapshot and unverified boundaries; do not transfer test results from unrelated working-tree content to a commit.
