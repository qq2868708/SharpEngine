"""Exercise Game UI exclusively through its MCP stdio bridge, with frame evidence."""

import argparse
import base64
import ctypes
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import threading
import time
import uuid
import zlib


def decode_png(data):
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    offset, compressed = 8, bytearray()
    width = height = 0
    while offset + 12 <= len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        assert zlib.crc32(kind + payload) == struct.unpack_from(">I", data, offset + 8 + length)[0]
        if kind == b"IHDR":
            width, height, depth, color = struct.unpack_from(">IIBB", payload)
            assert depth == 8 and color == 6
        if kind == b"IDAT":
            compressed.extend(payload)
        offset += length + 12
    raw = zlib.decompress(compressed)
    stride = width * 4 + 1
    assert len(raw) == stride * height
    assert all(raw[y * stride] == 0 for y in range(height))
    return width, height, b"".join(raw[y * stride + 1:(y + 1) * stride] for y in range(height))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--render-backend", choices=["opengl", "vulkan"], required=True)
    parser.add_argument("--configuration", default="Debug")
    parser.add_argument("--vulkan-validation", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    executable = root / f"Binaries/Win64/{args.configuration}/Game/Engine.exe"
    evidence = root / f"Saved/Validation/GameUiMcp/{args.render_backend}"
    evidence.mkdir(parents=True, exist_ok=True)
    session_id = uuid.uuid4().hex
    local_app_data = ctypes.create_unicode_buffer(32768)
    assert ctypes.windll.shell32.SHGetFolderPathW(None, 0x1C, None, 0, local_app_data) == 0
    registration = Path(local_app_data.value) / "SharpEngine/GameMcpSessions" / f"{session_id}.json"
    transcript = []
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    environment = os.environ.copy()
    if args.vulkan_validation:
        assert args.render_backend == "vulkan", "Vulkan validation requires the Vulkan backend"
        environment["VK_INSTANCE_LAYERS"] = "VK_LAYER_KHRONOS_validation"
        environment["VK_LAYER_VALIDATE_SYNC"] = "1"
    with (evidence / "game.log").open("wb") as log:
        game = subprocess.Popen([str(executable), "--project", str(args.project.resolve(strict=True)),
                                 f"--render-backend={args.render_backend}", "--hidden",
                                 "--mcp-session-id", session_id], cwd=root, startupinfo=startup,
                                stdout=log, stderr=log, env=environment)
        bridge = None
        try:
            deadline = time.monotonic() + 20
            while not registration.exists():
                assert game.poll() is None, f"Game exited {game.returncode}; see {evidence / 'game.log'}"
                assert time.monotonic() < deadline, "Game did not register MCP"
                time.sleep(.05)
            with (evidence / "bridge.log").open("wb") as bridge_log:
                bridge = subprocess.Popen([str(executable), "--mcp-stdio"],
                                          cwd=root, startupinfo=startup, stdin=subprocess.PIPE,
                                          stdout=subprocess.PIPE, stderr=bridge_log, text=True, encoding="utf-8")
                responses = queue.Queue()
                def read_responses():
                    for line in bridge.stdout:
                        responses.put(line)
                threading.Thread(target=read_responses, daemon=True).start()
                counter = 0

                def call(method, parameters=None):
                    nonlocal counter
                    counter += 1
                    request = dict(jsonrpc="2.0", id=counter, method=method, params=parameters or {})
                    bridge.stdin.write(json.dumps(request) + "\n")
                    bridge.stdin.flush()
                    response = json.loads(responses.get(timeout=30))
                    while "id" not in response:
                        transcript.append(dict(notification=response))
                        response = json.loads(responses.get(timeout=30))
                    assert response.get("id") == counter, response
                    transcript.append(dict(request=request, response=response))
                    assert "error" not in response, response
                    return response["result"]

                def tool(name, **arguments):
                    return call("tools/call", dict(name=name, arguments=arguments))["structuredContent"]

                def describe():
                    state = tool("ui.describe")
                    assert state["schemaVersion"] == 1 and state["frameId"] > 0 and not state["diagnostics"], state
                    return state

                def element(state, semantic_id):
                    return next(item for item in state["hosts"][0]["elements"] if item["semanticId"] == semantic_id)

                def image(state, name):
                    uri = f"sharpengine://ui/frames/{state['frameId']}/game.png"
                    resource = call("resources/read", dict(uri=uri))["contents"][0]
                    assert resource["uri"] == uri and resource["mimeType"] == "image/png", resource
                    data = base64.b64decode(resource["blob"])
                    (evidence / f"{name}-{state['frameId']}.png").write_bytes(data)
                    width, height, rgba = decode_png(data)
                    assert width == state["hosts"][0]["bounds"]["width"]
                    assert height == state["hosts"][0]["bounds"]["height"]
                    assert len(set(rgba)) > 32
                    return data, rgba, width

                assert call("initialize")["serverInfo"]["name"] == "sharpengine-game"
                bootstrap = call("tools/list")["tools"]
                assert {item["name"] for item in bootstrap} == {"game.sessions.list", "game.session.attach"}
                sessions = tool("game.sessions.list")
                assert any(item["sessionId"] == session_id for item in sessions["sessions"])
                assert tool("game.session.attach", sessionId=session_id)["attached"]
                tools = {item["name"]: item for item in call("tools/list")["tools"]}
                assert set(tools) == {"ui.describe", "ui.pointer", "ui.key"}
                assert tools["ui.pointer"]["_meta"]["sharpengine/capability"]["effect"] == "runtime"
                assert all("outputSchema" in item and "annotations" in item for item in tools.values())
                assert call("resources/list")["resources"][0]["uri"] == "sharpengine://ui"
                assert call("resources/templates/list")["resourceTemplates"][0]["uriTemplate"].endswith("/{hostId}.png")
                time.sleep(.25)
                initial = describe()
                assert element(initial, "status")["text"] == "CONNECTED"
                original_png, previous_pixels, image_width = image(initial, "connected")
                original_state = initial
                state = initial

                for button, expected in (("continueButton", "RUNNING"), ("settingsButton", "SETTINGS")):
                    control = element(state, button)
                    assert control["visible"] and control["enabled"] and control["hitTarget"] and control["focusable"], control
                    bounds, clip = control["bounds"], control["clip"]
                    x, y = round(bounds["x"] + bounds["width"] / 2), round(bounds["y"] + bounds["height"] / 2)
                    assert clip["x"] <= x < clip["x"] + clip["width"] and clip["y"] <= y < clip["y"] + clip["height"]
                    arguments = dict(hostId="game", x=x, y=y, button="left", expectedElementId=control["elementId"])
                    down = tool("ui.pointer", **arguments, event="down", expectedFrameId=state["frameId"])
                    assert down["accepted"] and down["targetElementId"] == control["elementId"], down
                    assert down["focusAfter"] == control["elementId"] and len(down["eventPath"]) >= 2, down
                    up = tool("ui.pointer", **arguments, event="up", expectedFrameId=down["frameId"])
                    assert up["accepted"] and control["elementId"] in up["eventPath"], up
                    state = describe()
                    assert element(state, "status")["text"] == expected
                    assert state["stateRevision"] > initial["stateRevision"]
                    assert state["hosts"][0]["focusedElementId"] == control["elementId"]
                    _, pixels, width = image(state, expected.lower())
                    status = element(state, "status")["bounds"]
                    # Restrict evidence to the status glyph region; hover/focus
                    # changes elsewhere cannot satisfy the pixel comparison.
                    def region(data):
                        return b"".join(data[(row * width + int(status["x"])) * 4:
                                             (row * width + int(status["x"] + status["width"])) * 4]
                                        for row in range(int(status["y"]), int(status["y"] + status["height"])))
                    assert region(pixels) != region(previous_pixels), "Status pixels did not change"
                    previous_pixels = pixels
                    print(f"PASS {args.render_backend}: MCP click {button} -> {expected}, focus/path/pixels frame={state['frameId']}", flush=True)

                # The observed original image stays pinned after subsequent input.
                assert image(original_state, "pinned-connected")[0] == original_png
                invalid = tool("ui.pointer", hostId="game", event="up", button="left", x=1, y=1,
                               expectedFrameId=state["frameId"])
                assert not invalid["accepted"] and invalid["diagnostics"][0]["code"] == "UI_POINTER_SEQUENCE_INVALID"
                invalid = tool("ui.pointer", hostId="game", event="down", button="left", x=-1, y=1,
                               expectedFrameId=state["frameId"])
                assert not invalid["accepted"] and invalid["diagnostics"][0]["code"] == "UI_COORDINATE_OUT_OF_BOUNDS"
                invalid = tool("ui.pointer", hostId="game", event="move", x=1, y=1, expectedFrameId=2**60)
                assert not invalid["accepted"] and invalid["diagnostics"][0]["code"] == "UI_FRAME_STALE"
                invalid = tool("ui.pointer", hostId="game", event="down", button="left", x=1, y=1,
                               expectedFrameId=state["frameId"], expectedElementId="game/continueButton")
                assert not invalid["accepted"] and invalid["diagnostics"][0]["code"] == "UI_TARGET_CHANGED"
                key = tool("ui.key", hostId="game", event="down", key=16, expectedFrameId=state["frameId"])
                assert key["accepted"] and key["focusAfter"] == "game/settingsButton"
                key_up = tool("ui.key", hostId="game", event="up", key=16, expectedFrameId=key["frameId"])
                assert key_up["accepted"]
                if args.vulkan_validation:
                    diagnostics = (evidence / "game.log").read_text(encoding="utf-8", errors="replace")
                    assert "Validation Error:" not in diagnostics, diagnostics
                print(f"PASS {args.render_backend}: discovery, pinned PNG, invalid-input rejection and focused key input", flush=True)
        finally:
            if bridge is not None:
                bridge.kill()
                bridge.wait(timeout=5)
            game.kill()
            game.wait(timeout=5)
            registration.unlink(missing_ok=True)
            (evidence / "transcript.json").write_text(json.dumps(transcript, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
