# -*- coding: utf-8 -*-
"""
Every string a PLAYER can read, pulled from the source so a translation can be
complete. Second attempt: a balanced-parenthesis scanner for the menu rows, since
the lambdas inside them defeat any regex, and no dropping of upper-case words,
which is where ON, OFF and every enum value went last time.

Log.* is not collected. The log stays English by design.
"""
import re, io, os, json, glob, collections

ROOT = r'C:\projects\fumes\src\Fumes'
OUT = r'C:\Users\mmidd\AppData\Local\Temp\claude\C--\427f7b77-478f-4049-8eb6-541ed5fbfb49\scratchpad\uikeys.json'
STR = re.compile(r'"((?:[^"\\]|\\.)*)"')

keys = collections.OrderedDict()


def add(s, where):
    s = s.replace('\\"', '"')
    if s and s.strip() and s not in keys:
        keys[s] = where


def call_args(text, start):
    """text[start] is '(' -- return the argument text up to its matching ')'."""
    depth, i, in_str = 0, start, False
    while i < len(text):
        c = text[i]
        if in_str:
            if c == '\\': i += 1
            elif c == '"': in_str = False
        elif c == '"': in_str = True
        elif c == '(': depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0: return text[start + 1:i]
        i += 1
    return text[start + 1:]


def strings_in(args):
    # only literals at the top level of the argument list -- not inside nested calls/lambdas
    out, depth, i, in_str, cur = [], 0, 0, False, None
    while i < len(args):
        c = args[i]
        if in_str:
            if c == '\\': cur.append(c); i += 1; cur.append(args[i])
            elif c == '"': in_str = False; out.append((depth, ''.join(cur)))
            else: cur.append(c)
        elif c == '"': in_str = True; cur = []
        elif c in '([{': depth += 1
        elif c in ')]}': depth -= 1
        i += 1
    return out


# ---- menu rows -------------------------------------------------------------------------
menu = io.open(os.path.join(ROOT, 'UI', 'Menu.cs'), encoding='utf-8').read()
body = menu[menu.index('private void Build()'):]
for m in re.finditer(r'\b(Toggle|Number|Whole|Choice|Action_)\(', body):
    args = call_args(body, m.end() - 1)
    lits = [s for d, s in strings_in(args) if d == 0]
    if not lits: continue
    add(lits[0], 'label')
    if m.group(1) == 'Action_' and len(lits) >= 2: add(lits[-1], 'hint')
    elif len(lits) >= 3: add(lits[-1], 'hint')        # section, key, hint -> last is the hint
for m in re.finditer(r'Add\("([A-Z ]+)",\s*"[^"]+\.png"\)', menu):
    add(m.group(1), 'page')
for s in STR.findall(menu):
    if s.startswith(('TAB page', 'LB RB page', 'ARROWS move', 'Menu, prompts')): add(s, 'legend')
add('ON', 'value'); add('OFF', 'value'); add('ENTER', 'legend')

# ---- enum values shown upper-cased by Choice rows --------------------------------------
for f in ('Core/Settings.cs', 'Core/Lang.cs'):
    src = io.open(os.path.join(ROOT, f), encoding='utf-8').read()
    for m in re.finditer(r'internal enum (\w+)\s*\{(.*?)\n    \}', src, re.S):
        clean = re.sub(r'///.*|//.*', '', m.group(2))
        for v in re.findall(r'^\s*([A-Z][A-Za-z0-9]*)\s*[,=]?\s*$', clean, re.M):
            add(v.upper(), 'enum ' + m.group(1))

# ---- prompts, notices, button labels, meter words, blip --------------------------------
SINK = re.compile(r'\b(?:Draw\.)?(Help|Notify|Offer\w*|Prompt\w*|Toast|Subtitle|Say|Announce)\s*\(')
for path in glob.glob(os.path.join(ROOT, '**', '*.cs'), recursive=True):
    base = os.path.basename(path)
    if base in ('Log.cs', 'Menu.cs'): continue
    text = io.open(path, encoding='utf-8').read()
    for m in SINK.finditer(text):
        # skip the definitions themselves
        if re.match(r'\s*(private|public|internal|static)', text[max(0, m.start() - 40):m.start()].split('\n')[-1]): continue
        for d, s in strings_in(call_args(text, m.end() - 1)):
            add(s, base[:-3] + '.' + m.group(1))
    for m in re.finditer(r'Buttons?\.(?:Add|Show|Row|Set)\s*\(', text):
        for d, s in strings_in(call_args(text, m.end() - 1)): add(s, base[:-3] + '.button')
    for m in re.finditer(r'Hud\.Text\(\s*(?:Lang\.T\()?"([^"]+)"', text): add(m.group(1), base[:-3] + '.meter')
    for m in re.finditer(r'Lang\.T\("([^"]+)"\)', text): add(m.group(1), base[:-3] + '.T')

# ---- not text ---------------------------------------------------------------------------
drop = re.compile(r'^(?:[0-9.#]+|~[a-z]~|[a-z_]+\.(?:png|json|ini)|\W{1,3}|\s*|[A-Z0-9_]+_[A-Z0-9_]+|STRING|[a-z]+)$')
keys = collections.OrderedDict((k, w) for k, w in keys.items() if not drop.match(k) and len(k.strip()) >= 2)

io.open(OUT, 'w', encoding='utf-8').write(json.dumps(keys, ensure_ascii=False, indent=1))
by = collections.Counter(w.split(' ')[0] if w.startswith('enum') else w for w in keys.values())
print("%d player-visible strings" % len(keys))
for k, n in sorted(by.items(), key=lambda x: -x[1]): print("   %-22s %d" % (k, n))
