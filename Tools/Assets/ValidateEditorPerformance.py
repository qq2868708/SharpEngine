"""Measure an owned native Editor session and retain complete MCP catalogs.

Run once with --record-baseline before the change, then --baseline <report.json>.
Only this script's Editor and bridge processes are terminated on cleanup.
"""
import argparse
import base64
import hashlib
import json
import os
import pathlib
import queue
import socket
import statistics
import subprocess
import threading
import time
import uuid

Arguments = argparse.ArgumentParser()
Arguments.add_argument('--root', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2])
Arguments.add_argument('--output', type=pathlib.Path, required=True)
Arguments.add_argument('--baseline', type=pathlib.Path)
Arguments.add_argument('--record-baseline', action='store_true')
Arguments.add_argument('--configuration', default='Debug')
Options = Arguments.parse_args()
Root = Options.root.resolve()
Output = Options.output.resolve()
Output.mkdir(parents=True, exist_ok=False)
Executable = Root / 'Binaries/Win64' / Options.configuration / 'Editor/Engine.exe'
Session = uuid.uuid4().hex
Registration = pathlib.Path(os.environ['LOCALAPPDATA']) / 'SharpEngine/McpSessions' / (Session + '.json')
Project = Output / 'Project'
(Project / 'Content/Assets').mkdir(parents=True)
(Project / 'Content/Worlds').mkdir()
(Project / 'Content/Assets/assets.json').write_text('{"schemaVersion":2,"assets":[]}', encoding='utf-8')
(Project / 'Content/Worlds/Main.sworld').write_bytes((Root / 'Content/Worlds/Main.sworld').read_bytes())
Manifest = Project / 'Performance.sharpproject'
Manifest.write_text(json.dumps({'schemaVersion': 3, 'projectId': 'editor-performance-validation',
    'displayName': 'Editor Performance Validation', 'engineVersion': '0.2.0-dev', 'modules': [],
    'engine': {'id': 'SharpEngine', 'version': '0.2.0-dev', 'association': 'performance-validation', 'manifest': 'engine.json'},
    'world': {'startup': 'Content/Worlds/Main.sworld'}}), encoding='utf-8')
with socket.socket() as PortProbe:
    PortProbe.bind(('127.0.0.1', 0))
    Port = PortProbe.getsockname()[1]
(Project / 'Config').mkdir()
(Project / 'Config/Mcp.json').write_text(json.dumps({'schemaVersion': 1, 'port': Port}), encoding='utf-8')
Environment = os.environ.copy()
Environment['SHARPENGINE_ROOT'] = str(Root)
Log = (Output / 'editor.log').open('w', encoding='utf-8')
BridgeLog = (Output / 'bridge.log').open('w', encoding='utf-8')
Editor = subprocess.Popen([str(Executable), '--project', str(Manifest), '--mcp-isolated', '--mcp-visible',
    '--mcp-session-id', Session, '--render-backend=opengl', '--render-vsync=on'], cwd=Root,
    env=Environment, stdout=Log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
Bridge = None
Responses = queue.Queue()
Identifier = 0
Report = {'schemaVersion': 1, 'sessionId': Session, 'configuration': Options.configuration,
    'backend': 'opengl', 'vsync': True, 'debugger': False,
    'executableSha256': hashlib.sha256(Executable.read_bytes()).hexdigest(),
    'editorAssemblySha256': hashlib.sha256((Executable.parent / 'SharpEngine.Editor.dll').read_bytes()).hexdigest(),
    'budgets': {'frameAverageMs': 18, 'frameP95Ms': 25, 'baselineNoiseRatio': 1.05},
    'assetId': 'engine://meshes/uefn-mannequin', 'measurements': {}, 'failures': [], 'status': 'running'}

def Save(Name, Value):
    (Output / Name).write_text(json.dumps(Value, ensure_ascii=False, indent=2), encoding='utf-8')
    return Value

def ReadBridge():
    for Line in Bridge.stdout:
        Responses.put(json.loads(Line))

def Call(Method, Parameters=None):
    global Identifier
    Identifier += 1
    Request = {'jsonrpc': '2.0', 'id': Identifier, 'method': Method, 'params': Parameters or {}}
    Bridge.stdin.write(json.dumps(Request) + '\n')
    Bridge.stdin.flush()
    while True:
        Response = Responses.get(timeout=30)
        if Response.get('method', '').startswith('notifications/'):
            with (Output / 'notifications.ndjson').open('a', encoding='utf-8') as Notifications:
                Notifications.write(json.dumps(Response) + '\n')
        if Response.get('id') == Identifier:
            if 'error' in Response:
                raise RuntimeError(Response['error'])
            return Response['result']

def Tool(Name, **Values):
    Result = Call('tools/call', {'name': Name, 'arguments': Values})
    if Result.get('isError'):
        raise RuntimeError(Result)
    Content = Result.get('structuredContent')
    if Content is None:
        Content = next(Item['text'] for Item in Result['content'] if Item['type'] == 'text')
    return json.loads(Content) if isinstance(Content, str) else Content

def Measure(Name):
    # Each observation is preceded by more than 240 frames at the acceptance budget.
    Samples = []
    for Index in range(3):
        time.sleep(5)
        Samples.append(Save(f'{Name}-{Index}.json', Tool('render.describe')))
    Timing = [Sample['timingMilliseconds'] for Sample in Samples]
    Result = {'frameAverageMs': statistics.median(Item['frameAverage'] for Item in Timing),
        'frameP95Ms': statistics.median(Item['frameP95'] for Item in Timing),
        'sceneAverageMs': statistics.median(Item['sceneAverage'] for Item in Timing)}
    Report['measurements'][Name] = Result
    print(Name, json.dumps(Result), flush=True)
    return Result

def ResourceEditor():
    State = Tool('editor.describe')
    return next(Item for Item in State['assetTools']['editors'] if Item['assetId'] == Report['assetId'])

def Capture(Name, HostId):
    Snapshot = Save(Name + '-ui.json', Tool('ui.describe'))
    Frame = Call('resources/read', {'uri': f'sharpengine://ui/frames/{Snapshot["frameId"]}/{HostId}.png'})
    (Output / (Name + '.png')).write_bytes(base64.b64decode(Frame['contents'][0]['blob']))
    return Snapshot

try:
    Deadline = time.monotonic() + 45
    while not Registration.exists():
        if Editor.poll() is not None or time.monotonic() > Deadline:
            raise RuntimeError('Owned Editor failed to start; inspect editor.log')
        time.sleep(0.1)
    Bridge = subprocess.Popen([str(Executable), '--mcp-stdio'], cwd=Root, env=Environment,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=BridgeLog,
        text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
    threading.Thread(target=ReadBridge, daemon=True).start()
    Save('initialize.json', Call('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
        'clientInfo': {'name': 'editor-performance-validation', 'version': '1'}}))
    Save('bootstrap-tools.json', Call('tools/list'))
    Save('sessions.json', Tool('editor.sessions.list'))
    Save('attached-session.json', Tool('editor.session.attach', sessionId=Session))
    Catalog = Save('attached-tools.json', Call('tools/list'))
    Save('resources.json', Call('resources/list'))
    Save('resource-templates.json', Call('resources/templates/list'))
    CatalogNames = {Item['name'] for Item in Catalog['tools']}
    if not {'render.describe', 'editor.action', 'ui.pointer', 'ui.describe'} <= CatalogNames:
        raise RuntimeError('Required live capabilities unavailable')
    Measure('idle')
    OpenTimes = []
    for Index in range(3):
        Started = time.perf_counter()
        Save(f'open-result-{Index}.json', Tool('editor.action', action='editor.open-asset:' + Report['assetId']))
        OpenTimes.append(time.perf_counter() - Started)
        if Index < 2:
            Opened = ResourceEditor()
            Tool('editor.action', action=f'editor.host-action:{Opened["rootId"]}:editor.asset-close')
    Report['openSeconds'] = statistics.median(OpenTimes)
    Report['openSamplesSeconds'] = OpenTimes
    State = Save('resource-editor.json', ResourceEditor())
    HostId = State['rootId']
    # Flush the 240-frame timing ring even in the slower baseline.
    time.sleep(15)
    Measure('resource')
    Snapshot = Capture('before-navigation', HostId)
    Host = next(Item for Item in Snapshot['hosts'] if Item['hostId'] == HostId)
    Viewport = max((Item for Item in Host['elements'] if Item.get('role') == 'viewport' and Item.get('visible')),
        key=lambda Item: Item['bounds']['width'] * Item['bounds']['height'])
    Bounds = Viewport['bounds']
    Latencies = []
    for Index in range(6):
        Started = time.perf_counter()
        Result = Tool('ui.pointer', hostId=HostId, event='wheel',
            x=int(Bounds['x'] + Bounds['width'] * 0.5), y=int(Bounds['y'] + Bounds['height'] * 0.5),
            button='none', buttons=[], modifiers=[], pointerId=0, expectedFrameId=Snapshot['frameId'],
            expectedElementId=Viewport['elementId'], wheelX=0, wheelY=1 if Index % 2 == 0 else -0.5)
        Latencies.append((time.perf_counter() - Started) * 1000)
        Save(f'navigation-{Index}.json', Result)
        if not Result.get('accepted'):
            raise RuntimeError('Native viewport navigation was rejected')
        Snapshot['frameId'] = Result['frameId']
    Report['navigationRpcMedianMs'] = statistics.median(Latencies)
    Report['navigationRpcSamplesMs'] = Latencies
    Capture('after-navigation', HostId)
    StateAfter = Save('resource-after-navigation.json', ResourceEditor())
    if StateAfter['dirty'] != State['dirty']:
        Report['failures'].append('Navigation changed document dirty state')
    if StateAfter['state']['viewport']['distance'] == State['state']['viewport']['distance']:
        Report['failures'].append('Native wheel events did not move the preview camera')
    Tool('editor.action', action=f'editor.host-action:{HostId}:editor.asset-close')
    time.sleep(5)
    Save('recovered.json', Tool('render.describe'))
    if not Options.record_baseline:
        for Name, Value in Report['measurements'].items():
            if Value['frameAverageMs'] > Report['budgets']['frameAverageMs'] or Value['frameP95Ms'] > Report['budgets']['frameP95Ms']:
                Report['failures'].append(Name + ' exceeds frame budget')
        if Options.baseline:
            Baseline = json.loads(Options.baseline.read_text(encoding='utf-8'))
            for Setting in ('configuration', 'backend', 'vsync', 'debugger', 'assetId'):
                if Baseline[Setting] != Report[Setting]:
                    Report['failures'].append('Incompatible baseline setting: ' + Setting)
            for Name, Value in Report['measurements'].items():
                for Metric in ('frameAverageMs', 'frameP95Ms'):
                    if Value[Metric] > Baseline['measurements'][Name][Metric] * Report['budgets']['baselineNoiseRatio']:
                        Report['failures'].append(Name + ' regresses baseline ' + Metric)
            for Metric in ('openSeconds', 'navigationRpcMedianMs'):
                if Report[Metric] > Baseline[Metric] * 1.10:
                    Report['failures'].append('Operation latency regresses baseline: ' + Metric)
    Report['status'] = 'failed' if Report['failures'] else ('baseline' if Options.record_baseline else 'passed')
    Save('report.json', Report)
    if Report['failures']:
        raise RuntimeError('; '.join(Report['failures']))
    print('RECORDED BASELINE' if Options.record_baseline else 'PASS: Editor performance and native navigation', flush=True)
except Exception as Error:
    Report['status'] = 'failed'
    if str(Error) not in Report['failures']:
        Report['failures'].append(str(Error))
    raise
finally:
    Save('report.json', Report)
    for OwnedProcess in (Bridge, Editor):
        if OwnedProcess is not None and OwnedProcess.poll() is None:
            OwnedProcess.terminate()
            try:
                OwnedProcess.wait(timeout=10)
            except subprocess.TimeoutExpired:
                OwnedProcess.kill()
    Log.close()
    BridgeLog.close()
