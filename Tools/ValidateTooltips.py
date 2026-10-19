"""Validate shared Editor tooltips with real hover input and matching frame evidence."""

import base64
import ctypes
from ctypes import wintypes
import html
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid


def main():
    root = Path(__file__).resolve().parent.parent
    evidence = root / "Saved/Validation/Tooltips" / uuid.uuid4().hex
    evidence.mkdir(parents=True)
    executable = root / "Binaries/Win64/Debug/Editor/Engine.exe"
    environment = os.environ.copy()
    environment["SHARPENGINE_REGISTER_ON_FIRST_USE"] = "0"
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    subprocess.run(["powershell", "-NoProfile", "-File", str(root / "Tools/SharpEngineProject.ps1"),
                    "-Command", "New", "-Name", "TooltipFixture", "-Directory", str(evidence),
                    "-EngineRoot", str(root)], cwd=root, env=environment, startupinfo=startup,
                   check=True, capture_output=True)
    project = evidence / "TooltipFixture"
    long_title = ("A longer tooltip must wrap inside its background, stay clear of the window edge, "
                  "and escape <b>literal markup</b> & punctuation. " * 3)
    token_title = "Asset_" + "LongAssetIdentifier" * 12
    fixture = (
        '<span style="position: fixed; left: 2px; top: 120px; width: 28px; height: 28px; overflow: hidden;">'
        '<button id="tooltip-left" title="Short" style="width: 25px; height: 25px; padding: 0px;">L</button></span>'
        '<span style="position: fixed; right: 2px; top: 120px; width: 28px; height: 28px; overflow: hidden;">'
        f'<button id="tooltip-long" title="{html.escape(long_title, quote=True)}" '
        'style="width: 25px; height: 25px; padding: 0px;">R</button></span>'
        f'<button id="tooltip-bottom" title="{token_title}" '
        'style="position: fixed; right: 2px; bottom: 2px; width: 25px; height: 20px; padding: 0px;">B</button>'
    )
    extension = '''using SharpEngine.Editor;
namespace TooltipFixture.Editor;
public sealed class ProjectEditorExtension : IEditorExtension
{
    private IDisposable? Registration;
    public void Startup(EditorContext Context)
    {
        Registration = Context.Commands.Register(new EditorCommand("validation.tooltip-fixture", "Tooltip fixture",
            Command =>
            {
                Command.SetMarkup("statusbar", """FIXTURE""");
                Command.SetMarkup("componentInspector", """<div class="field"><label>Asset</label><span id="long-inspector-value">TOKEN</span></div>""");
            }));
    }
    public void Shutdown()
    {
        Registration?.Dispose();
    }
}
'''.replace("FIXTURE", fixture).replace("TOKEN", token_title)
    (project / "Source/TooltipFixture.Editor/ProjectEditorExtension.cs").write_text(extension, encoding="utf-8")
    session = uuid.uuid4().hex
    app_data = ctypes.create_unicode_buffer(32768)
    assert ctypes.windll.shell32.SHGetFolderPathW(None, 0x1C, None, 0, app_data) == 0
    registration = Path(app_data.value) / "SharpEngine/McpSessions" / f"{session}.json"
    transcript = []
    with (evidence / "editor.log").open("w", encoding="utf-8") as log:
        editor = subprocess.Popen([str(executable), "--mcp-isolated", "--mcp-session-id", session,
                                   "--no-splash", "--project", str(project / "TooltipFixture.sharpproject")],
                                  cwd=root, startupinfo=startup, stdout=log, stderr=log)
        bridge = None
        try:
            deadline = time.monotonic() + 30
            while not registration.exists():
                assert editor.poll() is None, f"Editor exited; see {evidence}"
                assert time.monotonic() < deadline, "Editor registration timed out"
                time.sleep(.05)
            bridge = subprocess.Popen([str(executable), "--mcp-stdio"], cwd=root, startupinfo=startup,
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
                                      text=True, encoding="utf-8")
            responses = queue.Queue()

            def read_responses():
                for line in bridge.stdout:
                    responses.put(json.loads(line))

            threading.Thread(target=read_responses, daemon=True).start()
            sequence = 0

            def call(method, parameters=None):
                nonlocal sequence
                sequence += 1
                request = dict(jsonrpc="2.0", id=sequence, method=method, params=parameters or {})
                bridge.stdin.write(json.dumps(request) + "\n")
                bridge.stdin.flush()
                while True:
                    response = responses.get(timeout=30)
                    if "id" in response:
                        break
                assert response["id"] == sequence and "error" not in response, response
                if method != "resources/read":
                    transcript.append(dict(request=request, response=response))
                return response["result"]

            def tool(name, **arguments):
                result = call("tools/call", dict(name=name, arguments=arguments))
                assert "structuredContent" in result, result
                return result["structuredContent"]

            def read():
                return tool("ui.describe")

            def find(snapshot, identifier, host_id="main"):
                host = next(h for h in snapshot["hosts"] if h["hostId"] == host_id)
                return next(e for e in host["elements"] if e["semanticId"] == identifier or identifier in e["actions"])

            def pointer(event, x, y, identifier=None, host_id="main", wheel=0):
                for attempt in range(8):
                    snapshot = read()
                    result = tool("ui.pointer", hostId=host_id, event=event,
                                  button="none" if event in ("move", "wheel") else "left", buttons=[], modifiers=[],
                                  pointerId=0, x=x, y=y, expectedFrameId=snapshot["frameId"],
                                  expectedElementId=identifier, wheelX=0, wheelY=wheel)
                    if result["accepted"]:
                        return
                    assert any(d["code"] in ("UI_FRAME_STALE", "UI_TARGET_CHANGED") for d in result["diagnostics"]), result
                raise AssertionError(result)

            def at(identifier, event="move", host_id="main"):
                item = find(read(), identifier, host_id)
                assert item["visible"] and item["hitTarget"], item
                b = item["bounds"]
                pointer(event, int(b["x"] + b["width"] / 2), int(b["y"] + b["height"] / 2), item["elementId"], host_id)

            def click(identifier):
                at(identifier, "down")
                at(identifier, "up")

            def capture(name, host_id="main"):
                snapshot = read()
                (evidence / f"{name}.json").write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
                uri = f"sharpengine://ui/frames/{snapshot['frameId']}/{host_id}.png"
                png = base64.b64decode(call("resources/read", dict(uri=uri))["contents"][0]["blob"])
                assert png[:8] == b"\x89PNG\r\n\x1a\n" and len(png) > 1000
                (evidence / f"{name}.png").write_bytes(png)
                return snapshot

            def tooltip(identifier, expected, name, host_id="main", wraps=False, above=False):
                before = find(read(), identifier, host_id)["bounds"]
                at(identifier, host_id=host_id)
                snapshot = capture(name, host_id)
                anchor = find(snapshot, identifier, host_id)
                overlay = find(snapshot, "editor-control-tooltip", host_id)
                text = find(snapshot, "editor-control-tooltip-text", host_id)
                host = next(h for h in snapshot["hosts"] if h["hostId"] == host_id)
                b, t, size = overlay["bounds"], text["bounds"], host["bounds"]
                assert anchor["hovered"] and anchor["hitTarget"] and anchor["bounds"] == before
                assert overlay["visible"] and not overlay["hitTarget"] and not overlay["focusable"]
                assert text["text"] == expected and overlay["text"] == expected
                assert b["x"] >= 8 and b["y"] >= 8 and b["x"] + b["width"] <= size["width"] - 8
                assert b["y"] + b["height"] <= size["height"] - 8 and b["width"] <= 320
                assert t["x"] > b["x"] and t["y"] > b["y"]
                assert t["x"] + t["width"] < b["x"] + b["width"]
                assert t["y"] + t["height"] < b["y"] + b["height"]
                if wraps:
                    assert t["height"] > 16, "Long text must wrap"
                if above:
                    assert b["y"] + b["height"] < anchor["bounds"]["y"], "Bottom-edge tooltip must flip above"
                print(f"PASS {name}: {b}", flush=True)

            call("initialize")
            tool("editor.session.attach", sessionId=session)
            deadline = time.monotonic() + 120
            while "validation.tooltip-fixture" not in json.dumps(tool("editor.describe")):
                assert time.monotonic() < deadline, "Fixture module did not load"
                time.sleep(.1)
            tool("editor.action", action="editor.dock-reset-layout")
            click("editor.select-entity:crate")
            revision = tool("components.describe")["revision"]
            # Scroll the footer into view, without invoking its destructive action.
            for attempt in range(16):
                item = find(read(), "editor.component-destroy-entity")
                if item["visible"] and item["hitTarget"]:
                    break
                pane = find(read(), "inspectorContent")["bounds"]
                pointer("wheel", int(pane["x"] + pane["width"] - 20), int(pane["y"] + pane["height"] / 2), wheel=3)
            time.sleep(.4)
            tooltip("editor.component-destroy-entity", "Delete entity", "inspector-delete")
            tooltip("toolbar-tool-mcp", "Configure the MCP port and restart the server", "toolbar-long")
            tooltip("editor.content.clear:content", "Reset to Content root", "content-reset")
            pointer("move", 500, 350)
            assert not find(read(), "editor-control-tooltip")["visible"], "Pointer leave must hide the tooltip"
            tool("editor.action", action="editor.dock-drop:inspector:main:none:unused:detach:0")
            floating = next(h["hostId"] for h in read()["hosts"] if h["kind"] == "floating")
            for attempt in range(16):
                item = find(read(), "editor.component-destroy-entity", floating)
                if item["visible"] and item["hitTarget"]:
                    break
                pane = find(read(), "inspectorContent", floating)["bounds"]
                pointer("wheel", int(pane["x"] + pane["width"] - 20), int(pane["y"] + pane["height"] / 2),
                        host_id=floating, wheel=3)
            time.sleep(.4)
            tooltip("editor.component-destroy-entity", "Delete entity", "floating-inspector", floating)
            tool("editor.action", action="editor.dock-reset-layout")
            tool("editor.action", action="editor.command:validation.tooltip-fixture")
            snapshot = capture("inspector-long-value")
            value = find(snapshot, "long-inspector-value")
            pane = find(snapshot, "inspectorContent")["bounds"]
            assert value["text"] == token_title and value["bounds"]["height"] > 16
            assert value["bounds"]["x"] + value["bounds"]["width"] <= pane["x"] + pane["width"]
            tooltip("tooltip-left", "Short", "left-edge")
            tooltip("tooltip-long", long_title, "wrapped-clipped-parent", wraps=True)
            tooltip("tooltip-bottom", token_title, "bottom-long-token", wraps=True, above=True)

            # Resize only this test's owned main window; hover still traverses native hit testing.
            windows = []
            callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
            def collect(window, _):
                owner = wintypes.DWORD()
                class_name = ctypes.create_unicode_buffer(256)
                ctypes.windll.user32.GetWindowThreadProcessId(window, ctypes.byref(owner))
                ctypes.windll.user32.GetClassNameW(window, class_name, len(class_name))
                if owner.value == editor.pid and class_name.value == "SelfEngineWindow":
                    windows.append(window)
                return True
            ctypes.windll.user32.EnumWindows(callback_type(collect), 0)
            assert len(windows) == 1, windows
            ctypes.windll.user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                                        ctypes.c_int, ctypes.c_int, wintypes.UINT]
            ctypes.windll.user32.SetWindowPos(windows[0], None, 0, 0, 280, 400, 0x0016)
            time.sleep(.1)
            tooltip("tooltip-long", long_title, "narrow-wrapped", wraps=True)
            tooltip("tooltip-bottom", token_title, "narrow-bottom-token", wraps=True, above=True)
            assert tool("components.describe")["revision"] == revision, "Hover and layout must not edit the document"
            print(f"PASS shared tooltip input, sizing, wrapping, clipping and edge placement; evidence: {evidence}")
        finally:
            (evidence / "transcript.json").write_text(json.dumps(transcript, indent=2), encoding="utf-8")
            if bridge is not None:
                bridge.kill()
                bridge.wait(timeout=5)
            editor.kill()
            editor.wait(timeout=5)


if __name__ == "__main__":
    main()
