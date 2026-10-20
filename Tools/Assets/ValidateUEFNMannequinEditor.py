"""Exercise the imported animations in a user-authorized isolated native Editor.

Preserves complete MCP bootstrap/attached tool catalogs and frame evidence.
Only the Editor and bridge processes created by this script are closed.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid

Root = Path(__file__).resolve().parents[2]
Parser = argparse.ArgumentParser()
Parser.add_argument('--backend', choices=['opengl', 'vulkan'], required=True)
Mode = Parser.add_mutually_exclusive_group()
Mode.add_argument('--main-scene', action='store_true', dest='main_scene', default=True)
Mode.add_argument('--asset-editor', action='store_false', dest='main_scene')
Arguments = Parser.parse_args()
Output = Root / 'Saved/Validation/SkeletalReimport-20261020' / Arguments.backend
if Arguments.main_scene:
    Output = Output / 'main-scene'
Output.mkdir(parents=True, exist_ok=True)
Executable = Root / 'Binaries/Win64/Debug/Editor/Engine.exe'
Session = uuid.uuid4().hex
Registration = Path(os.environ['LOCALAPPDATA']) / 'SharpEngine/McpSessions' / (Session + '.json')
EditorLog = (Output / 'editor.log').open('w', encoding='utf-8')
BridgeLog = (Output / 'bridge.log').open('w', encoding='utf-8')
CreationFlags = subprocess.CREATE_NO_WINDOW
Editor = subprocess.Popen([str(Executable), '--mcp-isolated', '--mcp-session-id', Session,
    '--render-backend=' + Arguments.backend, '--render-validation=off'], cwd=Root,
    stdout=EditorLog, stderr=subprocess.STDOUT, creationflags=CreationFlags)
Bridge = None
Report = {'schemaVersion': 1, 'backend': Arguments.backend, 'sessionId': Session,
    'editorProcessId': Editor.pid, 'animations': []}
Replies = queue.Queue()
RequestId = 0

def Save(Name, Value):
    (Output / Name).write_text(json.dumps(Value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def ReadBridge():
    try:
        for Line in Bridge.stdout:
            Replies.put(json.loads(Line))
    except Exception as Error:
        Replies.put(Error)

def Call(Method, Params=None):
    global RequestId
    RequestId += 1
    Identifier = RequestId
    Bridge.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': Identifier,
        'method': Method, 'params': Params or {}}) + '\n')
    Bridge.stdin.flush()
    while True:
        try:
            Response = Replies.get(timeout=40)
        except queue.Empty as Error:
            raise TimeoutError(f'MCP {Method} request {Identifier} timed out; evidence: {Output}') from Error
        if isinstance(Response, Exception):
            raise Response
        if Response.get('id') != Identifier:
            continue
        if 'error' in Response:
            raise RuntimeError(Response['error'])
        return Response['result']

def Tool(Name, **Arguments):
    Result = Call('tools/call', {'name': Name, 'arguments': Arguments})
    if Result.get('isError'):
        raise RuntimeError(Name + ': ' + json.dumps(Result))
    Structured = Result.get('structuredContent')
    if Structured is None:
        Structured = json.loads(next(Item['text'] for Item in Result.get('content', []) if Item['type'] == 'text'))
    if isinstance(Structured, str):
        Structured = json.loads(Structured)
    return Structured

def Action(Value):
    return Tool('editor.action', action=Value)

def EditorState(AssetId):
    State = Tool('editor.describe')
    return next(Editor for Editor in State['assetTools']['editors'] if Editor['assetId'] == AssetId)

def Capture(Name, HostId):
    Snapshot = Tool('ui.describe')
    Host = next(Host for Host in Snapshot['hosts'] if Host['hostId'] == HostId)
    Viewport = max((Element for Element in Host['elements'] if Element.get('role') == 'viewport'
        and Element['bounds']['width'] > 250 and Element['bounds']['height'] > 150),
        key=lambda Element: Element['bounds']['width'] * Element['bounds']['height'])
    Frame = Call('resources/read', {'uri': f'sharpengine://ui/frames/{Snapshot["frameId"]}/{HostId}.png'})
    Png = base64.b64decode(Frame['contents'][0]['blob'])
    (Output / (Name + '.png')).write_bytes(Png)
    Save(Name + '-ui.json', Snapshot)
    return Png, Viewport['bounds']

def ClickPlay(HostId, Needle='editor.asset-command:play'):
    Snapshot = Tool('ui.describe')
    Host = next(Host for Host in Snapshot['hosts'] if Host['hostId'] == HostId)
    Play = next(Element for Element in Host['elements'] if Element.get('visible') and Element.get('enabled')
        and Element.get('hitTarget') and any(Needle in Value for Value in Element.get('actions', [])))
    Bounds = Play['bounds']
    X = int(Bounds['x'] + Bounds['width'] / 2)
    Y = int(Bounds['y'] + Bounds['height'] / 2)
    Down = Tool('ui.pointer', hostId=HostId, event='down', x=X, y=Y,
        button='left', buttons=['left'], modifiers=[], pointerId=0,
        expectedFrameId=Snapshot['frameId'], expectedElementId=Play['elementId'])
    if not Down.get('accepted'):
        raise RuntimeError('Play pointer down rejected: ' + json.dumps(Down))
    Up = Tool('ui.pointer', hostId=HostId, event='up', x=X, y=Y,
        button='left', buttons=[], modifiers=[], pointerId=0,
        expectedFrameId=Down['frameId'], expectedElementId=Play['elementId'])
    if not Up.get('accepted'):
        raise RuntimeError('Play pointer up rejected: ' + json.dumps(Up))

def Patch(EntityId, ComponentId, State):
    Snapshot = Tool('components.describe')
    return Tool('components.edit', context=Snapshot['context'], expectedRevision=Snapshot['revision'],
        operation='patch', entityId=EntityId, componentId=ComponentId, state=State)

def MainScene():
    Initial = Tool('components.describe')
    Save('components-initial.json', Initial)
    Entities = Initial['document']['entities']
    Player = next(Entity for Entity in Entities if any(Component['typeId'] == 'engine.skeletal-mesh' for Component in Entity['components']))
    Animator = next(Component for Component in Player['components'] if Component['typeId'] == 'engine.animator')
    Transform = next(Component for Component in Player['components'] if Component['typeId'] == 'engine.transform')
    Camera = next(Entity for Entity in Entities if any(Component['typeId'] == 'engine.camera' for Component in Entity['components']))
    CameraTransform = next(Component for Component in Camera['components'] if Component['typeId'] == 'engine.transform')
    Patch(Player['entityId'], Transform['componentId'], {'Position': [0, 0, 0], 'Rotation': [0, 0, 0]})
    Patch(Camera['entityId'], CameraTransform['componentId'], {'Position': [0, 0.85, -3.2], 'Rotation': [0, 180, 0]})
    for Entity in Entities:
        for Component in Entity['components']:
            if Component['typeId'] == 'engine.sky-light':
                Patch(Entity['entityId'], Component['componentId'], {'Intensity': 0.8})
    for Name, Duration in {'Idle': 2.0, 'Walk': 32/30, 'Run': 20/30, 'Jump': 1.4}.items():
        Patch(Player['entityId'], Animator['componentId'], {'Controller': None, 'MovementSource': None,
            'Clip': 'engine://animations/uefn-mannequin-' + Name.lower(), 'Time': 0,
            'Playing': True, 'Loop': False, 'StateId': ''})
        ClickPlay('main', 'core.play-pause')
        time.sleep(0.18)
        Playing = Tool('components.describe')
        Save(Name + '-playing.json', Playing)
        if Playing['context'] != 'runtime':
            raise RuntimeError('The real Play click did not create a Runtime World')
        Animation = next(Item for Item in Playing['animation']['animators'] if Item['componentId'] == Animator['componentId'])
        Save(Name + '-animation.json', Animation)
        if Animation['code'] != 'ANIMATOR_READY' or Animation['sampleTime'] <= 0:
            raise RuntimeError(Name + ': Runtime animation did not advance correctly')
        Action('editor.pause')
        Patch(Player['entityId'], Animator['componentId'], {'Playing': False, 'Time': 0})
        Action('editor.select-scene')
        time.sleep(0.12)
        Before, Bounds = Capture(Name + '-start', 'main')
        SampleTime = Duration * (0.25 if Name == 'Idle' else 0.48)
        Patch(Player['entityId'], Animator['componentId'], {'Playing': False, 'Time': SampleTime})
        Action('editor.select-scene')
        time.sleep(0.12)
        Middle, Bounds = Capture(Name + '-middle', 'main')
        Snapshot = Tool('components.describe')
        Save(Name + '-sampled.json', Snapshot)
        Skeleton = next(Item for Item in Snapshot['animation']['skeletons'] if Item['componentId'] ==
            next(Component['componentId'] for Component in Player['components'] if Component['typeId'] == 'engine.skeletal-mesh'))
        if Skeleton['code'] != 'SKELETON_READY':
            raise RuntimeError(Name + ': runtime skeleton was not ready')
        if abs(Skeleton['localPose']['time'] - SampleTime) > 1e-6:
            raise RuntimeError(Name + ': rendered skeleton did not use the committed pose time')
        if Before == Middle:
            raise RuntimeError(Name + ': rendered frame did not change')
        Report['animations'].append({'name': Name, 'playClicked': True,
            'advancedTime': Animation['sampleTime'],
            'capturedTime': SampleTime, 'skeletonCode': Skeleton['code'],
            'previewBounds': Bounds, 'frameChanged': Before != Middle})
        Action('editor.stop')
        print('[EditorReimport]', Arguments.backend, Name, 'runtime played and GPU frame captured', flush=True)

try:
    Deadline = time.monotonic() + 25
    while not Registration.exists():
        if Editor.poll() is not None:
            raise RuntimeError('Isolated Editor exited; see editor.log')
        if time.monotonic() > Deadline:
            raise TimeoutError('Isolated Editor did not register')
        time.sleep(0.1)
    Bridge = subprocess.Popen([str(Executable), '--mcp-stdio'], cwd=Root,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=BridgeLog,
        text=True, encoding='utf-8', creationflags=CreationFlags)
    threading.Thread(target=ReadBridge, daemon=True).start()
    Call('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
        'clientInfo': {'name': 'skeletal-reimport-validation', 'version': '1'}})
    Save('bootstrap-tools.json', Call('tools/list'))
    Sessions = Tool('editor.sessions.list')
    Save('sessions.json', Sessions)
    Attached = Tool('editor.session.attach', sessionId=Session)
    if not Attached.get('attached'):
        raise RuntimeError('Explicit isolated-session attachment failed')
    Save('attached-tools.json', Call('tools/list'))
    Save('resource-templates.json', Call('resources/templates/list'))
    InitialRender = Tool('render.describe')
    Save('render-initial.json', InitialRender)
    if not InitialRender['gpuScene']['supported']:
        raise RuntimeError('The native backend does not support the retained GPU scene')
    if InitialRender['gpuScene']['submittedFrames'] == 0:
        raise RuntimeError('The native backend did not submit the imported scene on the GPU')

    if Arguments.main_scene:
        MainScene()
    else:
        Durations = {'Idle': 2.0, 'Walk': 32 / 30, 'Run': 20 / 30, 'Jump': 1.4}
        for Name, Duration in Durations.items():
            AssetId = 'engine://animations/uefn-mannequin-' + Name.lower()
            Open = Action('editor.open-asset:' + AssetId)
            Save(Name + '-open.json', Open)
            State = EditorState(AssetId)
            HostId = State['rootId']
            Prefix = f'editor.host-action:{HostId}:'
            Action(Prefix + 'editor.asset-command:seek-start')
            time.sleep(0.12)
            Before, Bounds = Capture(Name + '-start', HostId)
            ClickPlay(HostId)
            time.sleep(0.16)
            Playing = EditorState(AssetId)
            Save(Name + '-playing.json', Playing)
            if Playing['state'].get('currentTime', 0) <= 0:
                raise RuntimeError(Name + ': clicking Play did not advance time')
            Action(Prefix + 'editor.asset-command:pause')
            Action(Prefix + 'editor.timeline-seek:' + str(Duration * 0.48))
            time.sleep(0.12)
            Middle, Bounds = Capture(Name + '-middle', HostId)
            if Before == Middle:
                raise RuntimeError(Name + ': the rendered frame did not change')
            State = EditorState(AssetId)
            Save(Name + '-middle-state.json', State)
            Report['animations'].append({'name': Name, 'assetId': AssetId, 'playClicked': True,
                'advancedTime': Playing['state']['currentTime'], 'capturedTime': State['state']['currentTime'],
                'previewBounds': Bounds, 'frameChanged': Before != Middle})
            Action(Prefix + 'window.close-requested')
            print('[EditorReimport]', Arguments.backend, Name, 'played and captured', flush=True)
    FinalRender = Tool('render.describe')
    Save('render-final.json', FinalRender)
    if FinalRender['gpuScene']['submittedFrames'] <= InitialRender['gpuScene']['submittedFrames']:
        raise RuntimeError('Native GPU submissions did not advance during animation validation')
    if FinalRender['gpuScene']['cpuFallbackFrames'] != InitialRender['gpuScene']['cpuFallbackFrames']:
        raise RuntimeError('Native scene rendering fell back to CPU during validation')
    Report['render'] = FinalRender
    Report['success'] = True
    Save('result.json', Report)
    print('[EditorReimport] PASS', Arguments.backend, 'four Actions, real Play clicks, rendered frames, GPU submission', flush=True)
finally:
    if Bridge is not None:
        Bridge.terminate()
        Bridge.wait(timeout=10)
    if Editor.poll() is None:
        Editor.terminate()
        Editor.wait(timeout=10)
    EditorLog.close()
    BridgeLog.close()
