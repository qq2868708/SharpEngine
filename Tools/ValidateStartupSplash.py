"""Observe real Editor startup, splash pixels, progress and native window lifecycle."""

import argparse
import ctypes as c
from ctypes import wintypes as w
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parent.parent
    parser.add_argument("--executable", type=Path, default=root / "Binaries/Win64/Debug/Editor/Engine.exe")
    parser.add_argument("--project", type=Path)
    parser.add_argument("--vulkan", action="store_true", help="Also validate Vulkan when the host supports backend selection")
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    evidence = root / "Saved/Validation/StartupSplash"
    evidence.mkdir(parents=True, exist_ok=True)
    user = c.WinDLL("user32", use_last_error=True)
    gdi = c.WinDLL("gdi32", use_last_error=True)
    callback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user.EnumWindows.argtypes = [callback, w.LPARAM]
    user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
    user.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
    user.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
    user.IsWindowVisible.argtypes = [w.HWND]
    user.GetClientRect.argtypes = [w.HWND, c.POINTER(w.RECT)]
    user.GetDC.argtypes = [w.HWND]
    user.GetDC.restype = w.HDC
    user.ReleaseDC.argtypes = [w.HWND, w.HDC]
    user.PrintWindow.argtypes = [w.HWND, w.HDC, w.UINT]
    user.SendMessageTimeoutW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM, w.UINT, w.UINT, c.POINTER(c.c_size_t)]
    user.SendMessageTimeoutW.restype = w.LPARAM
    gdi.CreateCompatibleDC.argtypes = [w.HDC]
    gdi.CreateCompatibleDC.restype = w.HDC
    gdi.CreateCompatibleBitmap.argtypes = [w.HDC, c.c_int, c.c_int]
    gdi.CreateCompatibleBitmap.restype = w.HBITMAP
    gdi.SelectObject.argtypes = [w.HDC, w.HGDIOBJ]
    gdi.SelectObject.restype = w.HGDIOBJ
    gdi.GetDIBits.argtypes = [w.HDC, w.HBITMAP, w.UINT, w.UINT, c.c_void_p, c.c_void_p, w.UINT]
    gdi.DeleteObject.argtypes = [w.HGDIOBJ]
    gdi.DeleteDC.argtypes = [w.HDC]

    def windows(pid):
        found = []

        @callback
        def visit(hwnd, unused):
            owner = w.DWORD()
            user.GetWindowThreadProcessId(hwnd, c.byref(owner))
            if owner.value == pid:
                name, title = c.create_unicode_buffer(256), c.create_unicode_buffer(1024)
                user.GetClassNameW(hwnd, name, len(name))
                user.GetWindowTextW(hwnd, title, len(title))
                found.append(dict(handle=hwnd, windowClass=name.value, title=title.value,
                                  visible=bool(user.IsWindowVisible(hwnd))))
            return True

        user.EnumWindows(visit, 0)
        return found

    def capture(hwnd, path):
        bounds = w.RECT()
        user.GetClientRect(hwnd, c.byref(bounds))
        width, height = bounds.right, bounds.bottom
        assert width > 0 and height > 0
        source = user.GetDC(hwnd)
        dc = gdi.CreateCompatibleDC(source)
        bitmap = gdi.CreateCompatibleBitmap(source, width, height)
        previous = gdi.SelectObject(dc, bitmap)
        result = c.c_size_t()
        try:
            assert user.SendMessageTimeoutW(hwnd, 0, 0, 0, 2, 1000, c.byref(result)), "Splash stopped responding"
            assert user.PrintWindow(hwnd, dc, 2), "Splash capture failed"
            gdi.SelectObject(dc, previous)
            pixels = c.create_string_buffer(width * height * 4)
            header = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 32, 0, len(pixels), 0, 0, 0, 0)
            info = c.create_string_buffer(header)
            assert gdi.GetDIBits(source, bitmap, 0, height, pixels, info, 0) == height
            if len(set(pixels.raw)) <= 32:
                return False
            path.write_bytes(struct.pack("<2sIHHI", b"BM", 54 + len(pixels), 0, 0, 54) + header + pixels.raw)
            return True
        finally:
            gdi.SelectObject(dc, previous)
            gdi.DeleteObject(bitmap)
            gdi.DeleteDC(dc)
            user.ReleaseDC(hwnd, source)

    results = []

    def run(name, arguments, visible_splash=False, success=True, host=executable, stdio=False, project_modules=False):
        log = evidence / f"{name}.log"
        observations, captures, last = [], [], None
        started = time.monotonic()
        with log.open("wb") as output:
            process = subprocess.Popen([str(host), *arguments], cwd=root, stdout=output,
                                       stderr=subprocess.STDOUT, stdin=subprocess.PIPE if stdio else subprocess.DEVNULL)
            try:
                if stdio:
                    request = dict(jsonrpc="2.0", id=1, method="initialize", params=dict(
                        protocolVersion="2024-11-05", capabilities={},
                        clientInfo=dict(name="StartupSplashValidation", version="1")))
                    process.stdin.write((json.dumps(request) + "\n").encode())
                    process.stdin.close()
                while process.poll() is None:
                    assert time.monotonic() - started < 30, f"{name}: startup/shutdown timed out"
                    current = windows(process.pid)
                    signature = [(item["windowClass"], item["title"], item["visible"]) for item in current]
                    if signature != last:
                        observations.append(dict(seconds=round(time.monotonic() - started, 3), windows=current))
                        last = signature
                    splashes = [item for item in current if item["windowClass"] == "SharpEngineStartupSplash"]
                    if not visible_splash:
                        assert not splashes, f"{name}: suppressed startup created a splash"
                    if "--mcp-isolated" in arguments:
                        assert not any(item["visible"] and item["windowClass"] in
                                       ("SelfEngineWindow", "SharpEngineDockWindow") for item in current), "Isolated Editor became visible"
                    if splashes and visible_splash and splashes[0]["visible"]:
                        text = log.read_text(encoding="utf-8", errors="replace")
                        editor_windows = [item for item in current if item["visible"] and
                                          item["windowClass"] in ("SelfEngineWindow", "SharpEngineDockWindow")]
                        assert not editor_windows or "100% | Editor | Editor ready" in text, "Editor appeared before it was ready"
                        if len(captures) < 6 and splashes[0]["title"] not in [item["title"] for item in captures]:
                            path = evidence / f"{name}-splash-{len(captures)}.bmp"
                            if capture(splashes[0]["handle"], path):
                                captures.append(dict(title=splashes[0]["title"], path=str(path)))
                    time.sleep(0.01)
                process.wait(timeout=1)
                assert (process.returncode == 0) == success, f"{name}: exit {process.returncode}; see {log}"
                assert not windows(process.pid), f"{name}: orphan native window"
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
        text = log.read_text(encoding="utf-8", errors="replace")
        if name == "vulkan":
            assert "Graphics backend initialized: vulkan" in text, "Host did not select the Vulkan backend"
        if success and not stdio:
            progress = [int(value) for value in re.findall(r"\[Startup\] (\d+)%", text)]
            assert progress and progress == sorted(progress) and progress[-1] == 100
            assert text.index("First frame presented host=main") < text.index("100% | Editor | Editor ready")
            assert "SharpEngine.Timeline.Runtime | Starting engine module" in text
            if project_modules:
                assert "Project code failed to load" not in text
                manifest = json.loads((args.project.resolve().parent / "Binaries/Win64/Debug/Editor/modules.runtime.json").read_text(encoding="utf-8-sig"))
                for module in manifest["modules"]:
                    assert f"| {module['id']} | Loading project module assembly" in text
                    assert f"| {module['id']} | Starting project module" in text
        if success and visible_splash:
            assert captures, f"{name}: splash was never observed"
            assert any(item["visible"] and item["windowClass"] == "SelfEngineWindow"
                       for observation in observations for item in observation["windows"]), "Editor never became visible"
        if stdio:
            response = [json.loads(line) for line in text.splitlines() if line.startswith('{"jsonrpc"')]
            assert any(item.get("id") == 1 and "result" in item for item in response), "MCP initialize failed"
            assert not any(item["windowClass"] in ("SelfEngineWindow", "SharpEngineStartupSplash")
                           for observation in observations for item in observation["windows"])
        results.append(dict(case=name, exitCode=process.returncode, observations=observations, captures=captures))
        (evidence / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(f"PASS {name}", flush=True)

    run("opengl", ["--frames", "30"], visible_splash=True)
    if args.vulkan:
        run("vulkan", ["--render-backend=vulkan", "--frames", "30"], visible_splash=True)
    run("no-splash", ["--no-splash", "--frames", "12"])
    run("isolated", ["--mcp-isolated", "--frames", "12"])
    run("stdio", ["--mcp-stdio"], stdio=True)
    run("invalid-project", ["--project", str(evidence / "Missing.sharpproject")], visible_splash=True, success=False)
    missing_host = evidence / "MissingManaged" / "Engine.exe"
    missing_host.parent.mkdir(exist_ok=True)
    shutil.copy2(executable, missing_host)
    run("missing-managed", [], host=missing_host, visible_splash=True, success=False)
    if args.project:
        run("project", ["--project", str(args.project.resolve(strict=True)), "--frames", "30"],
            visible_splash=True, project_modules=True)
    print(f"PASS {len(results)} startup integration cases; evidence: {evidence}")


if __name__ == "__main__":
    main()
