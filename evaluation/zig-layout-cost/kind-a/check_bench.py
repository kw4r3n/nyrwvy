"""Parity and timing of the Nyr body against the hand-written layoutFrameFlat declaration, through the C export.
Usage: python check_bench.py OUT_DIR CASES_DIR [processes]     (OUT_DIR from build.mjs; CASES_DIR holds layoutFrame-*.json)
Variants: A base ny_layoutFrame (Nyr body), B base ny_layoutFrameFlat (Nyr body behind the wrapper), C kinda ny_layoutFrameFlat (declaration).
Timing is per individual ctypes call (about 1 us of call overhead included), 100 warm-up calls, fresh process per run.
"""
import ctypes as c, json, pathlib, random, statistics, subprocess, sys, time
P = c.POINTER
def load(path, symbol):
    f = c.CDLL(str(pathlib.Path(path).resolve()))[symbol]
    f.argtypes = [P(c.c_int64), P(c.c_size_t), c.c_size_t, c.c_int64, c.c_int64, P(c.c_int64), c.c_size_t, P(c.c_size_t), P(c.c_size_t), c.c_size_t, P(c.c_size_t)]
    f.restype = c.c_int
    return f
class Frame:
    def __init__(self, rows):
        flat = [v for r in rows for v in r]; n = len(rows)
        self.n = n
        self.data = (c.c_int64 * max(1, len(flat)))(*flat); self.lens = (c.c_size_t * max(1, n))(*map(len, rows))
        self.out = (c.c_int64 * (len(flat) + n + 16))(); self.outlens = (c.c_size_t * max(1, n))()
        self.olen = c.c_size_t(); self.orows = c.c_size_t()
    def call(self, f, narrow, wide):
        return f(self.data, self.lens, self.n, narrow, wide, self.out, len(self.out), c.byref(self.olen), self.outlens, self.n, c.byref(self.orows))
    def result(self):
        o, at = [], 0
        for i in range(self.orows.value):
            k = self.outlens[i]; o.append(list(self.out[at:at + k])); at += k
        return o
def run(f, rows, narrow, wide):
    fr = Frame(rows); status = fr.call(f, narrow, wide); assert status == 0, status
    return fr.result()
PATTERNS = {'narrow': [1], 'wide': [1, 2, 0, 1, 1, 2, 0, 1], 'table': [1, 2, 0, 1, 3, 1, 1, 1]}
def pattern_rows(name, cols, rows):
    if name == 'frame':  # mostly plain wide text, one five-row table block in every twenty rows
        return [[PATTERNS['table'][(i + r) % 8] if 5 <= r % 20 < 10 else PATTERNS['wide'][(i + r) % 8] for i in range(cols)] for r in range(rows)]
    p = PATTERNS[name]; return [[p[(i + r) % len(p)] for i in range(cols)] for r in range(rows)]
def child(base, kinda, variant, process_id):
    f = load(kinda if variant == 'C' else base, 'ny_layoutFrame' if variant == 'A' else 'ny_layoutFrameFlat')
    res = {}
    for cols, nrows in [(80, 24), (300, 100)]:
        for name in ['narrow', 'wide', 'frame', 'table']:
            fr = Frame(pattern_rows(name, cols, nrows)); times = []
            for _ in range(100): assert fr.call(f, 2, 3) == 0
            samples = 400 if cols * nrows > 10000 else 3000
            if name == 'table' and cols == 300: samples = 150
            for _ in range(samples):
                t = time.perf_counter_ns(); s = fr.call(f, 2, 3); times.append((time.perf_counter_ns() - t) / 1000); assert s == 0
            times.sort(); res[f'{cols}x{nrows} {name}'] = [statistics.median(times), times[int(len(times) * 0.99) - 1]]
    print(json.dumps(res))
def parity(base, kinda, cases):
    A, B, C = load(base, 'ny_layoutFrame'), load(base, 'ny_layoutFrameFlat'), load(kinda, 'ny_layoutFrameFlat')
    saved = 0
    for path in sorted(pathlib.Path(cases).glob('layoutFrame-*.json')):
        a = json.loads(path.read_text())['arguments']; rows, grid = a['arguments']; n, w = grid['narrow'], grid['wide']
        for name, f in [('A', A), ('B', B), ('C', C)]:
            assert run(f, rows, n, w) == a['expected'], (path.name, name)
        saved += 1
    print('saved cases matching expected on A, B and C:', saved)
    rng = random.Random(52965); checks = 0
    def check(rows, n, w):
        nonlocal checks
        ra = run(A, rows, n, w); assert run(B, rows, n, w) == ra and run(C, rows, n, w) == ra, (rows, n, w); checks += 1
    for rows in [[], [[]], [[], []], [[3]], [[3, 3, 3]], [[1, 3, 1], [1, 1], [2, 0, 3, 1]], [[1, 3, 1], [3, 1, 3, 1, 3], [1, 3]]]:
        for n, w in [(1, 2), (2, 3), (0, 0)]: check(rows, n, w)
    for _ in range(3000):
        rows = []
        table_bias = rng.random()
        for _ in range(rng.randrange(1, 17)):
            sep = rng.choice([0.0, 0.05, 0.3, 0.6]) if table_bias < 0.8 else 1.0
            rows.append([3 if rng.random() < sep else rng.choice([0, 1, 1, 1, 2, 2, 5, -1]) for _ in range(rng.randrange(0, 41))])
        for n, w in [(1, 2), (2, 3), (3, 5), (0, 0), (2 ** 62, 2 ** 62), (-1, 4)]: check(rows, n, w)
    for cols, nrows in [(80, 24), (300, 100)]:
        for name in ['narrow', 'wide', 'frame', 'table']: check(pattern_rows(name, cols, nrows), 2, 3)
    print('random and pattern frames identical on A, B and C:', checks)
if __name__ == '__main__':
    if sys.argv[1] == 'child': child(*sys.argv[2:6]); sys.exit()
    out, cases = sys.argv[1:3]; runs = int(sys.argv[3]) if len(sys.argv) > 3 else 5
    base, kinda = f'{out}/base/liblayout.so', f'{out}/kinda/liblayout.so'
    parity(base, kinda, cases)
    table = {}
    for variant in 'ABC':
        for k in range(runs):
            table.setdefault(variant, []).append(json.loads(subprocess.run([sys.executable, __file__, 'child', base, kinda, variant, str(k)], capture_output=True, text=True, check=True).stdout))
    print(f'median over {runs} processes of per-process p50 / p99, microseconds')
    print('case'.ljust(18), *[f'{v}: p50 / p99'.rjust(20) for v in 'ABC'])
    for key in table['A'][0]:
        print(key.ljust(18), *[f"{statistics.median(r[key][0] for r in table[v]):9.1f} / {statistics.median(r[key][1] for r in table[v]):8.1f}".rjust(20) for v in 'ABC'])
