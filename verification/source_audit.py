"""Read-only audit of source coverage and original repository integrity."""
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OLD = ROOT / 'apocaraider'
TARGETS = ['womenofwasteland', 'gunplayhud', 'gunplay', 'npcai']

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

def sources(root):
    return [p for p in root.rglob('*.cs') if not any(x in p.parts for x in ['obj', 'bin', '.verification', 'verification'])]

def binds(text):
    return set(re.findall(r'(?:\b\w+\.Bind|\bH)\(\s*"([^"]+)"\s*,\s*"([^"]+)"', text))

def defaults(text):
    pattern = r'(?:\b\w+\.Bind|\bH)\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*(new Color\([^)]*\)|"(?:[^"\\]|\\.)*"|[^,\n]+)\s*,'
    return {(s,k): re.sub(r'\s+', '', value) for s,k,value in re.findall(pattern, text)}

original = (OLD / 'Plugin.cs').read_text(encoding='utf-8-sig')
new = {d: '\n'.join(p.read_text(encoding='utf-8-sig') for p in sources(ROOT / d)) for d in TARGETS}
missing = binds(original) - set().union(*(binds(t) for t in new.values()))
assert not missing, f'Original configuration keys omitted: {missing}'
print(f'PASS all {len(binds(original))} original public/hidden config keys have new owners.')
for key, value in defaults(original).items():
    extracted = [defaults(text)[key] for text in new.values() if key in defaults(text)]
    assert extracted and all(x == value for x in extracted), f'Config default changed: {key}: {value} -> {extracted}'
print('PASS original configuration defaults preserved for every extracted literal setting.')
retired = {'PistolRange', 'SmgRange', 'RifleRange', 'SniperRange', 'ShotgunRange', 'CrossbowRange'}
assert not ({('Tracers', k) for k in retired} & binds(new['npcai'])), 'Editable NPCAI fallback ranges remain.'
print('PASS NPCAI fallback ranges have no configuration bindings; Gunplay remains their configurable owner.')

for rel in ['Models', 'Sounds']:
    for asset in (OLD / rel).rglob('*'):
        if not asset.is_file():
            continue
        relative = asset.relative_to(OLD)
        candidates = [ROOT / d / relative for d in TARGETS]
        owners = [p for p in candidates if p.is_file()]
        assert len(owners) == 1, f'Asset omitted or duplicated: {relative}: {owners}'
        assert sha(asset) == sha(owners[0]), f'Asset changed: {relative}'
print('PASS all original Models/Sounds assets preserved byte-for-byte with one owner.')

snapshot = json.loads((ROOT / 'npcai/source-baseline.json').read_text(encoding='utf-8-sig'))
before = {str(Path(row['Path']).resolve()).lower(): row['Hash'] for row in snapshot}
after = {str(p.resolve()).lower(): sha(p) for p in OLD.rglob('*') if p.is_file() and '.git' not in p.parts}
assert before == after, 'Original repository files changed/added/removed.'
print(f'PASS original {len(before)} non-Git files unchanged (SHA-256 snapshot).')

for d, text in new.items():
    assert 'ProjectReference' not in '\n'.join(p.read_text() for p in (ROOT / d).glob('*.csproj'))
    assert 'DontDestroyOnLoad' in text and 'EnsureRunner' in text, f'Persistent lifecycle missing: {d}'
    assert 'LegacyConfig.Import' in text and 'Apocasetter' in text, f'Config integration missing: {d}'
    assert 'namespace Apocaraider' not in text, f'Legacy namespace collision: {d}'
print('PASS independent project/lifecycle/config identities.')

# Every original patch callback remains with one owning feature; observer hooks are additive.
expected = {
    'womenofwasteland': ['Gungirl.AfterCreateObject'],
    'gunplay': ['Tracers.BeforeRayHit', 'Tracers.BeforeRaycast', 'Tracers.AfterSetFsmFloat', 'Tracers.AfterGetLayer', 'Tracers.AfterSetAudioClip'],
    'npcai': ['Aim.BeforeSendEvent', 'Aim.BeforeRandomWait', 'Brain.BeforeSetVelocity', 'Brain.BeforeRotate', 'Brain.BeforeRaycast', 'Brain.BeforeLookAt', 'Brain.BeforeSmoothLookAt', 'Brain.BeforeSendEvent', 'Brain.BeforeAddForce', 'Brain.AfterLosEnable', 'Brain.AfterRangeEnable', 'Senses.BeforeGetDetections', 'Senses.BeforeLosResult', 'Senses.BeforeAudioPlay', 'Senses.AfterCreateObject', 'Idle.AfterCreateObject', 'Senses.BeforeGetButton', 'Senses.BeforeGetButtonDown', 'Senses.BeforeGetButtonUp'],
}
for owner, callbacks in expected.items():
    for callback in callbacks:
        assert callback in new[owner], f'Original callback not installed: {owner} {callback}'
        for sibling in TARGETS:
            if sibling != owner:
                assert callback not in new[sibling], f'Duplicate original callback: {sibling} {callback}'
print('PASS original patch callbacks preserved with exclusive feature ownership.')

pure = ['Gltf.cs', 'Bindposes.cs', 'Json.cs', 'Level.cs', 'Voice.cs', 'Wav.cs']
for filename in pure:
    original_text = (OLD / filename).read_text(encoding='utf-8-sig').replace('namespace Apocaraider', 'namespace WomenOfWasteland').strip()
    extracted_text = (ROOT / 'womenofwasteland/src' / filename).read_text(encoding='utf-8-sig').strip()
    assert original_text == extracted_text, f'Unexpected rewrite of female helper {filename}'
print('PASS female loader/audio pure helpers unchanged except namespace.')

print('STATIC AUDIT PASSED; this does not verify Unity gameplay or actual Harmony dispatch.')
