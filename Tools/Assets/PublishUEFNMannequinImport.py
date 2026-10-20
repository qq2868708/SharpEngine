"""Publish the six staged assets only after independent engine validation.

The Engine must be stopped for this offline import. Files use atomic replaces;
the manifest is published last, with rollback of this import on failure.
Unrelated manifest records and their original formatting are preserved.
"""
import hashlib
import ctypes
import json
import os
from pathlib import Path
import re
import uuid

Root = Path(__file__).resolve().parents[2]
Sessions = Path(os.environ['LOCALAPPDATA']) / 'SharpEngine/McpSessions'
Kernel = ctypes.WinDLL('kernel32', use_last_error=True)
Kernel.OpenProcess.restype = ctypes.c_void_p
Kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
Kernel.CloseHandle.argtypes = [ctypes.c_void_p]
for SessionPath in Sessions.glob('*.json'):
    Session = json.loads(SessionPath.read_text(encoding='utf-8'))
    if Path(Session['engineRoot']).resolve() != Root.resolve():
        continue
    Handle = Kernel.OpenProcess(0x00100000, False, Session['processId'])
    if Handle:
        try:
            if Kernel.WaitForSingleObject(Handle, 0) == 0x00000102:
                raise RuntimeError('Close Editors for this engine before offline publication: ' + Session['sessionId'])
        finally:
            Kernel.CloseHandle(Handle)
Evidence = Root / 'Saved/Validation/SkeletalReimport-20261020'
Stage = Evidence / 'Stage'
Import = json.loads((Evidence / 'import.json').read_text())
ReportPath = Evidence / 'engine-comparison.json'
Report = json.loads(ReportPath.read_text())
if Report['samples'] <= 0 or Report['maximumVertexErrorMetres'] > Report['vertexToleranceMetres']:
    raise ValueError('A successful complete engine comparison is required')
if hashlib.sha256((Root / Import['sourceBlend']).read_bytes()).hexdigest().upper() != Import['sourceSha256']:
    raise ValueError('The Blender source changed after staging')
if any((Stage / Relative).stat().st_mtime > ReportPath.stat().st_mtime for Relative in Import['files']):
    raise ValueError('Staged assets changed after engine comparison')
ManifestPath = Root / 'Content/Assets/assets.json'
OriginalText = ManifestPath.read_text(encoding='utf-8')
StageManifest = json.loads((Stage / 'Content/Assets/assets.json').read_text())
Changes = {Record['id']: Record for Record in StageManifest['assets']
    if Record['path'] in Import['files']}
Decoder = json.JSONDecoder()
Position = re.search(r'"assets"\s*:\s*\[', OriginalText).end()
Replacements = []
Found = set()
while True:
    while OriginalText[Position].isspace() or OriginalText[Position] == ',':
        Position += 1
    if OriginalText[Position] == ']':
        break
    Record, End = Decoder.raw_decode(OriginalText, Position)
    if Record['id'] in Changes:
        Proposed = Changes[Record['id']]
        if Record['path'] != Proposed['path'] or Record['type'] != Proposed['type']:
            raise ValueError('Reimport must preserve stable IDs, paths, and types')
        Fragment = OriginalText[Position:End]
        Match = re.search(r'"metadata"\s*:\s*', Fragment)
        if Match is None:
            raise ValueError('Expected existing source metadata')
        Start = Position + Match.end()
        Previous, MetadataEnd = Decoder.raw_decode(OriginalText, Start)
        Previous.update(Proposed['metadata'])
        Replacements.append((Start, MetadataEnd, json.dumps(Previous, ensure_ascii=False)))
        Found.add(Record['id'])
    Position = End
if Found != set(Changes) or len(Changes) != 6:
    raise ValueError('All six existing assets must participate in this import')
NewText = OriginalText
for Start, End, Replacement in sorted(Replacements, reverse=True):
    NewText = NewText[:Start] + Replacement + NewText[End:]
json.loads(NewText)
Files = [(Root / Relative, (Stage / Relative).read_bytes()) for Relative in Import['files']]
Files.append((ManifestPath, NewText.encode('utf-8')))
Before = {Path: Path.read_bytes() for Path, Data in Files}
for Path, Data in Before.items():
    Backup = Evidence / 'Before' / Path.relative_to(Root)
    Backup.parent.mkdir(parents=True, exist_ok=True)
    if not Backup.exists():
        Backup.write_bytes(Data)

def Replace(Path, Data):
    Temporary = Path.with_name(Path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        Temporary.write_bytes(Data)
        Temporary.replace(Path)
    finally:
        if Temporary.exists():
            Temporary.unlink()

Changed = []
try:
    for Path, Data in Files:
        Replace(Path, Data)
        Changed.append(Path)
except Exception:
    for Path in reversed(Changed):
        Replace(Path, Before[Path])
    raise
Publication = {'schemaVersion': 1, 'sourceSha256': Import['sourceSha256'],
    'assets': {str(Path.relative_to(Root)).replace('\\', '/'): hashlib.sha256(Data).hexdigest().upper()
        for Path, Data in Files}}
(Evidence / 'publication.json').write_text(json.dumps(Publication, indent=2) + '\n', encoding='utf-8')
print('[UEFNReimport] published six validated assets and source metadata')
