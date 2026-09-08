# -*- coding: utf-8 -*-
"""
State-consistency audit over the Fumes source.

Each check targets a bug CLASS that has actually shipped this week, not a style rule:
  1. held-vs-owned      CanLitres/Weapons.Current used where the owned can was meant
  2. one-shot-per-tick   spawners (CreateProp/GIVE_WEAPON/ADD_ROPE) reachable every frame, unguarded
  3. per-frame-keepup    per-frame natives asserted in some nozzle-out stages and not others
  4. save-restore        fields captured from a live read and written back later
  5. setting-vs-state    a _cfg bool deciding cleanup of a resource that has its own existence
  6. duplicated-init     two methods assigning the same 3+ fields (drift risk)
  7. exists-guard        cached entity fields dereferenced with no Exists() nearby
  8. delete-no-null      .Delete() on a field not followed by = null
  9. log-once-keys       Log.Once key reused for different messages
 10. stage-exits         every _stage = assignment, and what the leaving stage owned
"""
import io, os, re, glob, collections

ROOT = r'C:\projects\fumes\src\Fumes'
files = {}
for p in glob.glob(os.path.join(ROOT, '**', '*.cs'), recursive=True):
    files[os.path.relpath(p, ROOT)] = io.open(p, encoding='utf-8').read()


def strip_comments(s):
    s = re.sub(r'/\*.*?\*/', lambda m: ' ' * len(m.group(0)), s, flags=re.S)
    return '\n'.join(re.sub(r'//.*$', '', l) for l in s.split('\n'))


code = {k: strip_comments(v) for k, v in files.items()}

METHOD = re.compile(r'^\s{8}(?:private|public|internal|protected)\s[^\n{;=]*?\b(\w+)\s*\([^)]*\)\s*(?:\n\s*)?\{', re.M)


def methods(src):
    """[(name, start, end, body)] by brace matching from the signature."""
    out = []
    for m in METHOD.finditer(src):
        i = src.index('{', m.end() - 1)
        depth, j = 0, i
        while j < len(src):
            c = src[j]
            if c == '{': depth += 1
            elif c == '}':
                depth -= 1
                if depth == 0: break
            j += 1
        out.append((m.group(1), m.start(), j, src[i:j + 1]))
    return out


def line_of(src, pos):
    return src.count('\n', 0, pos) + 1


def enclosing(meths, pos):
    for n, s, e, _ in meths:
        if s <= pos <= e: return n
    return '?'


findings = collections.defaultdict(list)

# ---------------------------------------------------------------- per file
for fname, src in code.items():
    meths = methods(src)
    raw = files[fname]

    # 1. held vs owned -------------------------------------------------------
    for m in re.finditer(r'\bCanLitres\(|\bWeapons\.Current\b', src):
        meth = enclosing(meths, m.start())
        ln = line_of(src, m.start())
        # A prompt is allowed to ask what he is holding. Anything that persists,
        # guards, restores or writes back is not.
        body = next((b for n, s, e, b in meths if n == meth), '')
        writes = bool(re.search(r'Remember\(|SetCan|_canOwn|JsonFile\.Write|SET_PED_AMMO|\.Ammo\s*=', body))
        if writes:
            findings['1 held-vs-owned'].append("%s:%d in %s()  held-read feeds a write/guard" % (fname, ln, meth))

    # 2. one-shot spawners reachable per tick -----------------------------------
    SPAWN = r'World\.CreateProp|World\.CreatePed|World\.CreateVehicle|Hash\.ADD_ROPE|GIVE_WEAPON_TO_PED|Scaleform\.RequestMovie|Blip\b.*=.*\.AddBlip|World\.CreateBlip'
    spawners = {}
    for n, s, e, b in meths:
        if re.search(SPAWN, b):
            # guarded if it returns/continues on an existing handle before spawning
            first_spawn = re.search(SPAWN, b).start()
            head = b[:first_spawn]
            guarded = bool(re.search(r'!=\s*null[^;]*(return|continue)|\.Exists\(\)[^;]*return|if\s*\(_\w+\s*!=\s*null\)', head))
            spawners[n] = guarded
    TICKY = {'Update', 'Tick', 'OnTick', 'AtRest', 'Carrying', 'Filling', 'FillingCan', 'Siphoning',
             'Pouring', 'Choosing', 'Poses', 'Render', 'Draw', 'Idlers', 'Traffic', 'Burn', 'Watch'}
    for n, s, e, b in meths:
        if n in TICKY:
            for callee, guarded in spawners.items():
                if re.search(r'\b%s\s*\(' % callee, b) and not guarded and callee != n:
                    findings['2 one-shot-per-tick'].append(
                        "%s: %s() calls %s() every tick; %s has no existing-handle guard" % (fname, n, callee, callee))
    for callee, guarded in spawners.items():
        if not guarded:
            findings['2b unguarded-spawner'].append("%s: %s() spawns with no existing-handle guard" % (fname, callee))

    # 3. per-frame keep-up spread across stages --------------------------------
    KEEP = ['DisableControlThisFrame', 'SET_PED_CURRENT_WEAPON_VISIBLE', 'SET_IK_TARGET',
            'DISABLE_CONTROL_ACTION', 'SET_PED_CAN_SWITCH_WEAPON', 'HIDE_HUD']
    stage_bodies = {n: b for n, s, e, b in meths if n in ('Carrying', 'Filling', 'FillingCan', 'Siphoning', 'Pouring', 'Choosing', 'Poses')}
    if stage_bodies:
        for k in KEEP:
            has = [n for n, b in stage_bodies.items() if k in b]
            # LockHands wraps the control disables; count it as present
            if k == 'DisableControlThisFrame':
                has = [n for n, b in stage_bodies.items() if k in b or 'LockHands()' in b]
            if 0 < len(has) < len(stage_bodies) - 1:
                missing = [n for n in stage_bodies if n not in has and n != 'Poses']
                findings['3 per-frame-keepup'].append("%s: %s asserted in %s, not in %s" % (fname, k, has, missing))

    # 4. save-then-restore ------------------------------------------------------
    for m in re.finditer(r'(_\w+)\s*=\s*(?:Function\.Call<[^>]+>\(Hash\.GET_[A-Z_]+|me\.Weapons\.Current|\w+\.Ammo\b)', src):
        f = m.group(1)
        restores = [line_of(src, r.start()) for r in re.finditer(r'(SET_PED_AMMO|SET_CURRENT_PED_WEAPON|\.Ammo\s*=)[^;]*\b%s\b' % re.escape(f), src)]
        if restores:
            findings['4 save-restore'].append("%s:%d captures %s from a live read; restored at line(s) %s  -- does anything move the real value in between?"
                                              % (fname, line_of(src, m.start()), f, restores))

    # 5. setting decides cleanup of a live resource ----------------------------
    for n, s, e, b in meths:
        for m in re.finditer(r'if\s*\(\s*!?_cfg\.(\w+)\s*\)\s*return;', b):
            after = b[m.end():]
            if re.search(r'\.Delete\(\)|\.Detach\(\)|REMOVE_WEAPON_FROM_PED|STOP_ANIM_TASK|Retract\(\)', after):
                findings['5 setting-vs-state'].append("%s:%d %s() bails on _cfg.%s before tearing down a resource -- should it ask the resource instead?"
                                                      % (fname, line_of(src, s + m.start()), n, m.group(1)))

    # 6. duplicated initialisation ---------------------------------------------
    assigns = {}
    for n, s, e, b in meths:
        fields = set(re.findall(r'^\s*(_\w+)\s*=\s*[^=]', b, re.M))
        if len(fields) >= 3: assigns[n] = fields
    names = list(assigns)
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            common = assigns[names[i]] & assigns[names[j]]
            if len(common) >= 4:
                findings['6 duplicated-init'].append("%s: %s() and %s() both assign %s" % (fname, names[i], names[j], sorted(common)))

    # 7. cached entity used without Exists() -----------------------------------
    ents = set(re.findall(r'^\s+private\s+(?:Prop|Vehicle|Ped|Rope|Entity)\s+(_\w+)\s*;', src, re.M))
    for f in ents:
        for n, s, e, b in meths:
            uses = [m for m in re.finditer(r'\b%s\.(?!Exists)\w+' % re.escape(f), b)]
            if uses and not re.search(r'%s\s*(?:==|!=)\s*null|%s\.Exists\(\)|%s\s*!=\s*null' % (re.escape(f), re.escape(f), re.escape(f)), b):
                findings['7 exists-guard'].append("%s:%d %s() dereferences %s with no null/Exists check in the method"
                                                  % (fname, line_of(src, s + uses[0].start()), n, f))

    # 8. Delete without nulling --------------------------------------------------
    for m in re.finditer(r'(_\w+)\.Delete\(\);', src):
        f = m.group(1)
        tail = src[m.end():m.end() + 400]
        if not re.search(r'%s\s*=\s*null' % re.escape(f), tail):
            findings['8 delete-no-null'].append("%s:%d %s.Delete() not followed by %s = null within 400 chars" % (fname, line_of(src, m.start()), f, f))

    # 9. Log.Once key reuse -------------------------------------------------------
    keys = collections.defaultdict(set)
    for m in re.finditer(r'Log\.Once\(\s*"([^"]+)"\s*,\s*"([^"]{0,40})', src):
        keys[m.group(1)].add(m.group(2))
    for k, msgs in keys.items():
        if len(msgs) > 1:
            findings['9 log-once-keys'].append("%s: Log.Once key \"%s\" used for %d different messages" % (fname, k, len(msgs)))

    # 10. stage exits -------------------------------------------------------------
    if 'Refuel' in fname:
        for m in re.finditer(r'_stage\s*=\s*Stage\.(\w+);', src):
            findings['10 stage-exits'].append("%s:%d -> %s  (in %s)" % (fname, line_of(src, m.start()), m.group(1), enclosing(meths, m.start())))

# ---------------------------------------------------------------- report
total = 0
for k in sorted(findings):
    items = findings[k]
    print("=" * 78)
    print("%s   (%d)" % (k, len(items)))
    for it in items:
        print("  " + it)
    total += len(items)
print("=" * 78)
print("%d findings across %d files" % (total, len(files)))
