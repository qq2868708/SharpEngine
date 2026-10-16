# Pinned UI dependencies

The editor uses upstream RmlUi 6.1 for RML/RCSS layout, fonts, controls,
focus, clipboard, Win32 IME, clipping, and rendering. This is a real compiled
RmlUi integration, not the older custom XML/GDI draw-list backend.

| Dependency | Source/tag | Checked-out commit | License |
| --- | --- | --- | --- |
| RmlUi | https://github.com/mikke89/RmlUi / 6.1 | 58c751531f4c1a1eb2dc56f409b8e8a323a0c9ad | RmlUi/LICENSE.txt (MIT) |
| FreeType | https://github.com/freetype/freetype / VER-2-13-3 | 42608f77f20749dd6ddc9e0536788eaad70ea4b5 | FreeType/docs/FTL.TXT (FreeType License) |

The source trees and their license files are retained. FreeType is used under
its FreeType License option. RmlUi's bundled Lato fallback font has its own
license at `RmlUi/Samples/assets/LICENSE.txt`; Windows Segoe UI and Microsoft
YaHei are loaded from the operating system and are not redistributed.

The current editor backend is the official RmlUi Win32 + OpenGL 2 backend,
using a WGL double-buffered window. The existing Vulkan source remains in the
engine, but is not initialized by this editor preview. The world viewport is
a custom RmlUi element drawn within RmlUi's paint order, so menus and overlays
can appear above it. C# passes scene vertices and semantic UI state through
the versioned POD host API; no OpenGL or Vulkan types enter the managed API.

## Build

`Tools/BuildUiDependencies.ps1` configures the pinned source through
`External/UiBuild/CMakeLists.txt`, builds the static libraries, and is invoked
before the native engine build. Visual Studio 18 with its C++ CMake tools and
Windows SDK is the current supported toolchain. The script discovers the
installed Visual Studio instance with vswhere rather than embedding its path.

    powershell -NoProfile -ExecutionPolicy Bypass -File Tools/BuildUiDependencies.ps1 -Configuration Debug

The first build compiles RmlUi and FreeType; later builds are incremental.
No dependency download occurs during normal builds. To reproduce the pinned
source checkout, use the source/tag/commit values in the table above.

## UI sources and runtime verification

- `Content/UI/Rml/Editor.rml`: editor document, stable semantic element IDs.
- `Content/UI/Rml/Editor.rcss`: layout, controls, dock theme, hover/focus states.
- `Engine/UI/RmlUiShell.*`: WGL host, Win32 input, Unicode resource IO, scene element.

    x64/Debug/Engine.exe --smoke-test --screenshot D:/SharpEngine/x64/Debug/ui-smoke.bmp

`--smoke-test` renders 10 frames and exits. `--frames N` provides a configurable
bounded run. Omitting these flags leaves the editor running normally.
`--screenshot` writes the actual presented OpenGL framebuffer to a BMP.
The first-frame log reports viewport bounds, actual RmlUi render calls, and
submitted scene primitive counts only after SwapBuffers succeeds.

The New Project dialog selects the full target project directory path and
sends `editor.create-project:<path>`; it does not create a placeholder file.
Open Project selects `project.json` and sends `editor.open-project:<path>`.
The C# editor owns project creation/opening, state changes, and validation.

This is a minimum usable editor shell. Dock dragging, full editor menus,
undo/redo, and property editing are still separate features. Native Win32 IME
is integrated through RmlUi's backend, but full IME and DPI acceptance testing
across systems has not yet been completed.
