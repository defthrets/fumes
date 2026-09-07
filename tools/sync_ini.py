# -*- coding: utf-8 -*-
"""
Copy settings the source Fumes.ini has and a deployed one does not, with their comments.

build.ps1 -Deploy KEEPS the installed ini so your tuning survives an update, which is right --
and means every new setting has to be carried across by hand or it silently runs on its C#
default. This does that carrying, and touches nothing that already exists: values you have
tuned are never overwritten.
"""
import io, os, re, sys

SRC = r'C:\projects\fumes\Fumes.ini'
INSTALLS = [
    r'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\scripts\Fumes.ini',
    r'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V Enhanced\scripts\Fumes.ini',
]

KEY = re.compile(r'^\s*([A-Za-z]\w*)\s*=', re.M)


def blocks(text):
    """Every key, with the comment block and blank lines immediately above it."""
    lines = text.split('\n')
    out, section, pending = {}, '', []

    for line in lines:
        s = line.strip()
        if s.startswith('[') and s.endswith(']'):
            section, pending = s, []
            continue
        m = re.match(r'^\s*([A-Za-z]\w*)\s*=', line)
        if m:
            out[m.group(1)] = (section, pending + [line])
            pending = []
        elif s.startswith(';') or s == '':
            pending.append(line)
        else:
            pending = []
    return out


src = io.open(SRC, encoding='utf-8-sig').read()
srcb = blocks(src)
srckeys = list(srcb.keys())

for path in INSTALLS:
    if not os.path.exists(path):
        print("missing: %s" % path)
        continue

    text = io.open(path, encoding='utf-8-sig').read()
    have = set(KEY.findall(text))
    missing = [k for k in srckeys if k not in have]

    print("=" * 74)
    print(os.path.dirname(path))
    print("  installed %d keys, source %d, missing %d" % (len(have), len(srckeys), len(missing)))

    if not missing:
        print("  nothing to do")
        continue

    for k in missing:
        section, chunk = srcb[k]

        # Land it under the same [Section] heading, at that section's end.
        if section:
            m = re.search(r'^\s*%s\s*$' % re.escape(section), text, re.M)
        else:
            m = None

        if m:
            nxt = re.compile(r'^\s*\[[^\]]+\]\s*$', re.M).search(text, m.end())
            at = nxt.start() if nxt else len(text)
        else:
            at = len(text)
            print("      (%s has no %s heading; appended at the end)" % (k, section or '[?]'))

        text = text[:at].rstrip('\n') + '\n\n' + '\n'.join(l for l in chunk).strip('\n') + '\n\n' + text[at:].lstrip('\n')
        print("      + %-22s into %s" % (k, section or '(end)'))

    io.open(path, 'w', encoding='utf-8', newline='').write(text)

    check = io.open(path, encoding='utf-8-sig').read()
    still = [k for k in srckeys if k not in set(KEY.findall(check))]
    print("  now %d keys, still missing: %s"
          % (len(set(KEY.findall(check))), ', '.join(still) if still else 'none'))
    if still:
        sys.exit(1)
