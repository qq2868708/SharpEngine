# Editor startup

The native Editor shows a centered, DPI-scaled splash before project resolution,
graphics initialization or CoreCLR startup. It uses the existing SharpEngine
brand icon and a separate Win32 message loop, so a synchronous module startup
does not stop repainting or the elapsed-time display.

Progress is a weighted estimate of completed startup stages, not a prediction of
remaining time. It only advances when the actual startup path reports a stage:

| Range | Work |
| --- | --- |
| 0–28% | Project resolution, native window, graphics/RmlUi and CoreCLR |
| 36–66% | Editor services, assets, layout, initial world, panels and input |
| 70–77% | Engine module startup, with each module's actual ID |
| 78–94% | Project/plugin verification, assembly loading, components and module startup |
| 95–98% | Build/reload services and the first editor frame |
| 100% | The first frame has successfully presented |

The main window and restored floating Dock Hosts stay hidden until that first
frame. Then the Editor shows them and closes/joins the splash thread. Startup
failures also close/join it through native scope cleanup. Project module failure
keeps the existing recovery behavior: the editor opens with project code
unavailable and its build error visible.

This is transient editor presentation state. It does not modify project assets,
World revisions, Undo/Redo, module identity or Cook outputs. Progress diagnostics
use the existing `Startup` log category and versioned engine log envelope. The
live native window title also contains the current percentage and module.
The optional native API v10 callback is used synchronously during initialization
and is never retained by modules; managed code also supports older native hosts.

`--no-splash` suppresses the splash. `--mcp-stdio` never creates it or an editor
window, and `--mcp-isolated` keeps the splash and editor windows hidden. Game
Targets do not include the splash implementation.

After building Editor, run the Windows integration validation:

```powershell
python Tools/ValidateStartupSplash.py
```

Pass `--executable` to validate a named native build, or `--project` to additionally
check real project/plugin module IDs. Logs, observed native window transitions,
and splash frame captures are saved under `Saved/Validation/StartupSplash`.
For hosts that support backend selection, `--vulkan` also checks Vulkan startup
and verifies that the requested backend was actually selected.
