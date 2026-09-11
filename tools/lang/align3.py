# -*- coding: utf-8 -*-
"""
Alignment, second pass: the DP again, but it can no longer pair an identifier
with a sentence, and it knows enough Portuguese to notice when Boats has been
handed Aeronaves.

  1. An English string with no space that is CamelCase, snake_case or lowercase
     -- an ini key, a model name, a variable -- never pairs. Neither does a
     Portuguese one.
  2. A small bilingual list of the domain's nouns scores a pair up when both
     halves carry the same word, and DOWN when the English carries one of them
     and the Portuguese carries a DIFFERENT one. That is the off-by-one detector.
  3. After the DP, short labels still unpaired on both sides are matched by the
     dictionary alone, ignoring order, because their build shuffled one file.
  4. Only keys that exist verbatim in the 0.1.9 heap are kept: every entry in the
     shipped file must do something.
"""
import json, io, re, difflib

SP = r'C:\Users\mmidd\AppData\Local\Temp\claude\C--\427f7b77-478f-4049-8eb6-541ed5fbfb49\scratchpad'
en = [s or '' for s in json.load(io.open(SP + r'\en.json', encoding='utf-8'))]
pt_all = [s or '' for s in json.load(io.open(SP + r'\pt.json', encoding='utf-8'))]
now = set(s for s in json.load(io.open(SP + r'\en019.json', encoding='utf-8')) if s)

sm = difflib.SequenceMatcher(None, en, pt_all, autojunk=False)
tag, i1, i2, j1, j2 = sm.get_opcodes()[-1]
pt = pt_all[j1:j2]

HARD = re.compile(r'~[a-z]~|\{\d+\}|\d+(?:\.\d+)?|\[[A-Z][A-Za-z]+\]|[A-Za-z_]+\.(?:log|json|ini|png|dll|cs)'
                  r'|[A-Z_]{4,}|[A-Za-z]+_[A-Za-z_]+|Fumes|Shift|Enter|tanks|stations')
IDENT = re.compile(r'^(?:[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+|[a-z][a-z0-9_]*|[A-Za-z]+_[A-Za-z_]+|[a-z0-9_]+\d)$')

# English word -> Portuguese stems (lowercase, accent-free) that a translation of it would carry.
LEX = {
    'boat': ['barco'], 'aircraft': ['aeronave'], 'plane': ['aviao', 'aeronave'],
    'litre': ['litro'], 'gallon': ['galao'], 'opacity': ['opacidade'], 'unit': ['unidade'],
    'width': ['largura'], 'height': ['altura'], 'rope': ['corda'], 'fuel': ['combustivel'],
    'pump': ['bomba'], 'hose': ['mangueira'], 'station': ['posto'], 'tank': ['tanque'],
    'gauge': ['medidor', 'barra'], 'price': ['preco'], 'sound': ['som'], 'icon': ['icone'],
    'map': ['mapa'], 'stop': ['parar', 'interromp'], 'traffic': ['trafego', 'transito'],
    'percentage': ['porcentagem'], 'reserve': ['reserva'], 'length': ['comprimento'],
    'sag': ['folga'], 'nozzle': ['bico'], 'vehicle': ['veiculo', 'carro'], 'engine': ['motor'],
    'empty': ['vazio'], 'dry': ['seco'], 'full': ['cheio', 'completo'], 'low': ['baixo'],
    'free': ['gratis', 'gratuito'], 'loaded': ['carregad'], 'removed': ['removid'],
    'blip': ['blip', 'icone'], 'learn': ['aprend'], 'consumption': ['consumo'],
    'prompt': ['comando', 'aviso', 'mensagem'], 'menu': ['menu'], 'total': ['total'],
    'volume': ['volume'], 'save': ['salv', 'grava'], 'cleanup': ['limpeza'],
    'failed': ['falha', 'falhou'], 'could not': ['nao foi possivel', 'nao conseguiu'],
    'can': ['galao'], 'grade': ['tipo', 'grau', 'combustivel'], 'distance': ['distancia'],
    'hand': ['mao'], 'left': ['esquerd'], 'right': ['direit'], 'animation': ['animacao'],
    'colour': ['cor'], 'color': ['cor'], 'thickness': ['espessura'], 'speed': ['velocidade'],
    'money': ['dinheiro'], 'money': ['dinheiro', 'cobrar'], 'fire': ['fogo', 'incend'],
    'light': ['luz'], 'key': ['tecla'], 'button': ['botao'], 'text': ['texto'],
    'screen': ['tela'], 'size': ['tamanho'], 'position': ['posicao'], 'number': ['numero'],
    'idle': ['ocios', 'parado', 'marcha'], 'vertical': ['vertical'], 'horizontal': ['horizontal'],
    'liquid': ['liquid'], 'label': ['rotulo', 'etiqueta'], 'wave': ['onda'],
}


def fold(s):
    import unicodedata
    return ''.join(c for c in unicodedata.normalize('NFD', s.lower()) if unicodedata.category(c) != 'Mn')


def lex_hits(e, p):
    """+1 for each English domain word whose Portuguese stem is present; -1 for each whose stem is absent
    while some OTHER listed stem is present (a substitution, not just a paraphrase)."""
    fe, fp = fold(e), fold(p)
    present = [w for w in LEX if re.search(r'\b' + w, fe)]
    if not present:
        return 0, 0
    good = bad = 0
    others = set(st for w2 in LEX if w2 not in present for st in LEX[w2])
    for w in present:
        if any(st in fp for st in LEX[w]):
            good += 1
        elif any(st in fp for st in others):
            bad += 1
    return good, bad


def is_ident(s):
    t = s.strip()
    return bool(t) and ' ' not in t and (bool(IDENT.match(t)) or t.lower() == t and len(t) <= 12 and t.isalpha())


def score(e, p):
    if len(e.strip()) < 3 or len(p.strip()) < 3:
        return -9
    if is_ident(e) or is_ident(p):
        return -9
    r = len(p) / float(max(1, len(e)))
    if r < 0.45 or r > 2.4:
        return -9
    se, sp = sorted(HARD.findall(e)), sorted(HARD.findall(p))
    s = 0.0
    if se and se == sp:
        s += 4
    elif se and sp:
        s += 2 * len(set(se) & set(sp)) - len(set(se) ^ set(sp))
    elif not se and not sp:
        s += 1
    else:
        s -= 1
    good, bad = lex_hits(e, p)
    s += 3 * good - 4 * bad
    if e[:1].isspace() == p[:1].isspace(): s += 0.5
    if e.rstrip()[-1:] == p.rstrip()[-1:]: s += 0.5
    if (' - ' in e) == (' - ' in p): s += 0.3
    return s


GAP = -0.6
n, m = len(en), len(pt)
NEG = float('-inf')
dp = [[NEG] * (m + 1) for _ in range(n + 1)]
bt = [[None] * (m + 1) for _ in range(n + 1)]
dp[0][0] = 0
for i in range(n + 1):
    for j in range(m + 1):
        if i == 0 and j == 0: continue
        best, arg = NEG, None
        if i > 0 and dp[i - 1][j] + GAP > best: best, arg = dp[i - 1][j] + GAP, 'del'
        if j > 0 and dp[i][j - 1] + GAP > best: best, arg = dp[i][j - 1] + GAP, 'ins'
        if i > 0 and j > 0:
            v = dp[i - 1][j - 1] + score(en[i - 1], pt[j - 1])
            if v > best: best, arg = v, 'pair'
        dp[i][j], bt[i][j] = best, arg

pairs, used_e, used_p = [], set(), set()
i, j = n, m
while i > 0 or j > 0:
    a = bt[i][j]
    if a == 'pair':
        s = score(en[i - 1], pt[j - 1])
        if s >= 1.0:
            pairs.append((en[i - 1], pt[j - 1], s, 'dp'))
            used_e.add(i - 1); used_p.add(j - 1)
        i -= 1; j -= 1
    elif a == 'del': i -= 1
    else: j -= 1
pairs.reverse()

# Pass 3: short labels by dictionary alone, order ignored.
for jj, p in enumerate(pt):
    if jj in used_p or len(p.strip()) > 28 or is_ident(p): continue
    fp = fold(p)
    cands = []
    for ii, e in enumerate(en):
        if ii in used_e or len(e.strip()) > 28 or is_ident(e) or e not in now: continue
        good, bad = lex_hits(e, p)
        if good and not bad and abs(len(e) - len(p)) <= 14:
            # every English domain word must be accounted for
            words = [w for w in LEX if re.search(r'\b' + w, fold(e))]
            if all(any(st in fp for st in LEX[w]) for w in words):
                cands.append((good, ii, e))
    if len(cands) == 1 or (cands and len(set(c[2] for c in cands)) == 1):
        good, ii, e = cands[0]
        pairs.append((e, p, 2.0 + good, 'lex'))
        used_e.add(ii); used_p.add(jj)

# Pass 4: only keys the current build still has, no version-stamped strings.
kept = [(e, p, s, how) for e, p, s, how in pairs if e in now and '0.1.1' not in e and e != p]
dropped_stale = len(pairs) - len(kept)

print("dp pairs %d + lex pairs %d = %d; kept (key exists in 0.1.9, not stale) %d"
      % (sum(1 for x in pairs if x[3] == 'dp'), sum(1 for x in pairs if x[3] == 'lex'), len(pairs), len(kept)))

out = io.open(SP + r'\pairs.json', 'w', encoding='utf-8')
json.dump([{'en': e, 'pt': p, 'score': s, 'how': how} for e, p, s, how in kept], out, ensure_ascii=False, indent=1)
out.close()
rev = io.open(SP + r'\pairs_review.txt', 'w', encoding='utf-8')
for k, (e, p, s, how) in enumerate(kept):
    rev.write("%03d %-3s %4.1f  %r\n             %r\n" % (k, how, s, e, p))
rev.close()
print("written pairs.json / pairs_review.txt")
