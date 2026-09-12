# -*- coding: utf-8 -*-
"""
Build every data/lang/*.json from the tr_*.py dictionaries, and refuse anything that
would misbehave in the game:

  * a colour code (~y~, ~s~, ~b~...) present in the English and missing from the
    translation, or added -- the game would draw a literal tilde or lose a colour;
  * leading or trailing whitespace that differs -- the mod glues fragments together
    on exactly those spaces;
  * a key written twice in the same file -- a Python dict keeps the second, silently;
  * a required key with no translation -- reported, not fatal, so a partial file can
    still ship, but the count is printed so it is a decision and not an accident.

pt-BR: the community file is read first and wins; tr_ptgap only fills what it lacks.
"""
import io, os, re, sys, json, importlib.util, collections

SP = os.path.dirname(os.path.abspath(__file__))
OUT = r'C:\projects\fumes\data\lang'
sys.path.insert(0, SP)

required = list(json.load(io.open(os.path.join(SP, 'uikeys.json'), encoding='utf-8')).keys())
# hooked after the inventory was taken, plus the reasons glued onto "The nozzle was "
required += ['standard', '% further', ' loaded.  Press ~b~', '~s~ for settings.', ' - by ',
             ' stopped~s~ - see Fumes.log.', 'pulled out of your hands', 'the pump went away',
             'the pump went up', 'the vehicle went away', 'you moved away from the filler',
             'you are out of money', 'you were on fire', 'there was a fire at the pump',
             'the player is not available']
IDENT = re.compile(r'^[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+$')      # AffectBoats and friends: ini keys, not text
required = [k for k in required if not IDENT.match(k) and k not in ('/L', '.~s~', 'HUD', 'SHIFT', 'ALT', 'AUTO',
            'CONTROL', 'ENGLISH', 'ENGLISHUS', 'PORTUGUESEBR', 'SPANISH', 'FRENCH', 'GERMAN', 'RUSSIAN',
            'POLISH', 'HINDI', 'CHINESESIMPLIFIED')]      # Language values show through Lang.NameOf, not T

CODES = re.compile(r'~[a-z_]+~')


def load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(SP, name + '.py'))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def duplicates(name):
    text = io.open(os.path.join(SP, name + '.py'), encoding='utf-8').read()
    seen, dupes = collections.Counter(), []
    for m in re.finditer(r'^\s*"((?:[^"\\]|\\.)*)"\s*:', text, re.M):
        seen[m.group(1)] += 1
    for m in re.finditer(r'(?:,|\{)\s*"((?:[^"\\]|\\.)*)"\s*:', text):
        seen[m.group(1)] += 0   # inline pairs already counted by the line regex when first on a line
    text2 = re.sub(r'#.*', '', text)
    for m in re.finditer(r'"((?:[^"\\]|\\.)*)"\s*:\s*"', text2):
        pass
    counts = collections.Counter(re.findall(r'"((?:[^"\\]|\\.)*)"\s*:\s*"', text2))
    return [k for k, n in counts.items() if n > 1]


def check(name, table, spaces=True):
    bad = []
    for e, t in table.items():
        if sorted(CODES.findall(e)) != sorted(CODES.findall(t)):
            bad.append(('codes', e, t))
        if spaces and ((len(e) - len(e.lstrip())) != (len(t) - len(t.lstrip())) or (len(e) - len(e.rstrip())) != (len(t) - len(t.rstrip()))):
            bad.append(('space', e, t))
        if not t.strip():
            bad.append(('empty', e, t))
    return bad


def write(code, language, by, table, note=None):
    doc = collections.OrderedDict([('language', language), ('code', code), ('by', by)])
    doc['note'] = note or ('English on the left, ' + language + ' on the right. Keep the ~y~ colour codes and '
                           'the leading and trailing spaces exactly as they are: the mod glues fragments '
                           'together. Anything not listed here shows in English.')
    doc['strings'] = collections.OrderedDict(table)
    path = os.path.join(OUT, code + '.json')
    with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
        f.write('\n')
    json.load(io.open(path, encoding='utf-8'))
    return path


fatal = False
print("%-8s %8s %9s %8s  %s" % ('file', 'strings', 'required', 'missing', ''))

# ---- the six authored languages + US English ---------------------------------------------
for name in ('tr_enus', 'tr_es', 'tr_fr', 'tr_de', 'tr_ru', 'tr_pl', 'tr_hi', 'tr_zh'):
    m = load(name)
    table = collections.OrderedDict((k, v) for k, v in m.T.items() if k != v)
    d = duplicates(name)
    bad = check(name, table, getattr(m, 'SPACES', True))
    missing = [k for k in required if k not in m.T]
    if d or bad:
        fatal = True
        for k in d: print("  DUPLICATE in %s: %r" % (name, k))
        for why, e, t in bad: print("  BAD %-5s in %s: %r -> %r" % (why, name, e, t))
    note = None
    if m.CODE == 'zh-CN':
        note = ('English on the left, Chinese on the right. Keep the ~y~ colour codes and the leading and '
                'trailing spaces as they are. GTA V only has CJK glyphs when the game itself is set to Chinese; '
                'in any other game language these strings draw as nothing. Anything not listed shows in English.')
    if m.CODE == 'en-US':
        missing = []      # a spelling overlay, deliberately sparse
    write(m.CODE, m.LANGUAGE, m.BY, table, note)
    print("%-8s %8d %9d %8d  %s" % (m.CODE, len(table), len(required), len(missing),
                                    ('; '.join(repr(x)[:38] for x in missing[:4]) + (' ...' if len(missing) > 4 else '')) if missing else ''))

# ---- pt-BR: community first, gaps second -------------------------------------------------
community = json.load(io.open(os.path.join(OUT, 'pt-BR.json'), encoding='utf-8'))
ct = collections.OrderedDict(community['strings'])
gap = load('tr_ptgap')
added = 0
for k, v in gap.T.items():
    if k not in ct and k != v:
        ct[k] = v; added += 1
bad = check('pt-BR', ct); d = duplicates('tr_ptgap')
if bad or d:
    fatal = True
    for k in d: print("  DUPLICATE in tr_ptgap: %r" % k)
    for why, e, t in bad: print("  BAD %-5s in pt-BR: %r -> %r" % (why, e, t))
missing = [k for k in required if k not in ct]
write('pt-BR', gap.LANGUAGE, gap.BY, ct)
print("%-8s %8d %9d %8d  (+%d filled in)  %s" % ('pt-BR', len(ct), len(required), len(missing), added,
                                                 '; '.join(repr(x)[:38] for x in missing[:4])))

if fatal:
    print("\nFAILED"); sys.exit(1)
print("\nall language files written and re-parsed as strict JSON")
