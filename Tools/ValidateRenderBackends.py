"""Bounded Windows integration checks for both host/backend combinations."""

import argparse
import json
from pathlib import Path
import struct
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--editor-executable", type=Path)
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Development", "Shipping"])
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    project = args.project.resolve(strict=True)
    evidence = root / "Saved/Validation/RenderBackends"
    evidence.mkdir(parents=True, exist_ok=True)
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    results = []

    def run(target, name, arguments, code=0, contains=None):
        executable = (args.editor_executable.resolve(strict=True) if target == "Editor" and args.editor_executable else
                      root / f"Binaries/Win64/{args.configuration}/{target}/Engine.exe")
        completed = subprocess.run([str(executable), *arguments], cwd=root, startupinfo=startup,
                                   capture_output=True, timeout=30)
        output = (completed.stdout + completed.stderr).decode("utf-8", errors="replace")
        (evidence / f"{target}-{name}.log").write_text(output, encoding="utf-8")
        assert completed.returncode == code, f"{target}/{name}: exit {completed.returncode}\n{output}"
        if contains:
            assert contains in output, f"{target}/{name}: missing {contains}\n{output}"
        result = {"target": target, "case": name, "exitCode": completed.returncode}
        results.append(result)
        print(f"PASS {target}/{name}", flush=True)
        return result, output

    for target in ("Editor", "Game"):
        run(target, "invalid", ["--render-backend=invalid"], 2, "RENDER_BACKEND_INVALID")
        run(target, "missing", ["--render-backend"], 2, "RENDER_BACKEND_REQUIRED")
        run(target, "conflict", ["--render-backend=opengl", "--render-backend=vulkan"], 2, "RENDER_BACKEND_CONFLICT")
        base = ["--project", str(project), "--hidden"] if target == "Game" else ["--mcp-isolated"]
        for name, selection, expected in (("opengl", ["--render-backend=opengl"], "opengl"),
                                          ("vulkan", ["--render-backend", "vulkan"], "vulkan"),
                                          ("default", [], "opengl")):
            screenshot = evidence / f"{target}-{name}.bmp"
            screenshot.unlink(missing_ok=True)
            result, output = run(target, name, base + selection + ["--frames", "12", "--screenshot", str(screenshot)],
                                 contains=f"Graphics backend initialized: {expected}")
            assert "Game UI initialized" in output if target == "Game" else "First frame presented" in output
            assert "screenshot failed" not in output.lower()
            data = screenshot.read_bytes()
            width, height, planes, bits = struct.unpack_from("<iiHH", data, 18)
            offset = struct.unpack_from("<I", data, 10)[0]
            assert data[:2] == b"BM" and width > 0 and height > 0 and planes == 1 and bits == 32
            assert len(data) == offset + width * height * 4 and len(set(data[offset:])) > 32
            result.update(backend=expected, width=width, height=height, screenshot=str(screenshot))

    for mode in ("--render-offscreen", "--null-rhi"):
        run("Game", mode[2:] + "-conflict", ["--render-backend=vulkan", mode], 2, "RENDER_BACKEND_CONFLICT")
        run("Game", mode[2:], ["--project", str(project), mode, "--frames", "1"], contains="completed frames=1 submitted=1")
    (evidence / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(f"PASS {len(results)} host/backend integration cases; evidence: {evidence}")


if __name__ == "__main__":
    main()
