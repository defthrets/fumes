# -*- coding: utf-8 -*-
"""
What is in the release zip, and what must never be.

    python tools\verify_zip.py [release\Fumes-x.y.z.zip]      defaults to the newest zip

Run before anything is uploaded. It caught 1.0.0's first zip going out with no
language files in it, which is the whole point: the machine that builds the zip is
the one machine on which a missing file cannot be noticed by playing, because the
files are already in place from being deployed.
"""
import zipfile, re, sys, os, glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE = os.path.join(ROOT, 'release')

if len(sys.argv) > 1:
    ZIP = sys.argv[1]
else:
    zips = [z for z in glob.glob(os.path.join(RELEASE, 'Fumes-*.zip')) if 'DO-NOT-UPLOAD' not in z]
    ZIP = sorted(zips, key=os.path.getmtime)[-1]

# Shipping any of these leaks this machine's own play state to every downloader.
FORBIDDEN = ['tanks.json', 'can.json', 'stations.local.json', 'models.local.json', 'Fumes.log', '.bak', '.tmp', '.pdb']

REQUIRED = [
    'Fumes.dll', 'Fumes.ini', 'stations.json', 'models.json',
    'icons/drop.png', 'icons/fuel_bar.png', 'icons/fuel.png', 'icons/icon_hose.png',
    'icons/icon_hud.png', 'icons/icon_station.png', 'icons/label_fuel.png', 'icons/logo.png',
    'README.txt', 'CHANGES.txt', 'LICENCE.txt',
]
# Every language the code can offer must be in the zip; see Lang.FileFor.
REQUIRED += ['lang/' + f for f in ('en-US.json', 'pt-BR.json', 'es.json', 'fr.json',
                                     'de.json', 'ru.json', 'pl.json', 'hi.json', 'zh-CN.json')]

SEP = chr(92)
z = zipfile.ZipFile(ZIP)
names = z.namelist()
flat = [n.replace(SEP, '/') for n in names]

print("%s  --  %d entries\n" % (os.path.basename(ZIP), len(names)))
for n in sorted(flat):
    print("    %-46s %7d" % (n, z.getinfo(n if n in names else n.replace('/', SEP)).file_size))

print("\nREQUIRED")
missing = []
for r in REQUIRED:
    hit = any(f.endswith(r) for f in flat)
    if not hit:
        missing.append(r)
    print("    %-4s %s" % ('ok' if hit else 'MISS', r))

print("\nMUST NOT BE PRESENT")
leaked = []
for bad in FORBIDDEN:
    hit = [f for f in flat if bad.lower() in f.lower()]
    if hit:
        leaked.extend(hit)
    print("    %-4s %-22s %s" % ('LEAK' if hit else 'ok', bad, ', '.join(hit) if hit else ''))

# The ini in the zip must be the shipped defaults, with every key the source ini has.
ini = None
for n in names:
    if n.replace(SEP, '/').endswith('Fumes.ini'):
        ini = z.read(n).decode('utf-8-sig')
print("\nSHIPPED INI")
if ini is None:
    print("    MISSING")
    missing.append('Fumes.ini body')
else:
    KEY = re.compile(r'^\s*[A-Za-z]\w*\s*=', re.M)
    keys = len(KEY.findall(ini))
    src = len(KEY.findall(open(os.path.join(ROOT, 'Fumes.ini'), encoding='utf-8-sig').read()))
    for k, want in (('RememberCan', 'true'), ('DropEmptyCan', 'true'), ('Language', 'English'),
                    ('SputterFraction', '0.06'), ('SputterLitres', '0.4')):
        m = re.search(r'^\s*%s\s*=\s*(\S+)' % k, ini, re.M)
        got = m.group(1) if m else '(absent)'
        ok = got == want
        print("    %-4s %-18s %s" % ('ok' if ok else 'BAD', k, got))
        if not ok:
            missing.append(k)
    print("    %d settings (source has %d)" % (keys, src))
    if keys != src:
        missing.append('ini key count %d != source %d' % (keys, src))

# Every language file must parse, and hold a strings object.
print("\nLANGUAGE FILES")
import json
for n in names:
    f = n.replace(SEP, '/')
    if '/lang/' in f and f.endswith('.json'):
        try:
            doc = json.loads(z.read(n).decode('utf-8-sig'))
            count = len(doc.get('strings', {}))
            ok = count > 0
            print("    %-4s %-14s %4d strings" % ('ok' if ok else 'BAD', os.path.basename(f), count))
            if not ok:
                missing.append(f)
        except Exception as ex:
            print("    BAD  %-14s %s" % (os.path.basename(f), ex))
            missing.append(f)

# Version must agree everywhere in the zip.
print("\nVERSION IN THE ZIP")
seen = set()
for n in names:
    f = n.replace(SEP, '/')
    if f.endswith('README.txt') or f.endswith('CHANGES.txt'):
        t = z.read(n).decode('utf-8', 'replace')
        m = re.search(r'\d+\.\d+\.\d+', t)
        v = m.group(0) if m else '??'
        seen.add(v)
        print("    %-22s %s" % (os.path.basename(f), v))
zipv = re.search(r'Fumes-(\d+\.\d+\.\d+)', os.path.basename(ZIP))
if zipv:
    seen.add(zipv.group(1))
    print("    %-22s %s" % ('zip name', zipv.group(1)))
if len(seen) > 1:
    missing.append('version disagreement %s' % sorted(seen))

print()
if leaked or missing:
    print("FAILED: leaked=%s missing=%s" % (leaked, missing))
    sys.exit(1)
print("zip is clean")
