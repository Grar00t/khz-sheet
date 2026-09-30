#!/usr/bin/env python3
"""Development-only independent Fraction oracle; never a product dependency."""
import ctypes as C
from fractions import Fraction
import json
from pathlib import Path
import random
import sys

LIMIT = (1 << 63) - 1
class Rational(C.Structure):
    _fields_ = [('num', C.c_int64), ('den', C.c_int64)]

def verify(path):
    lib = C.CDLL(str(Path(path).resolve()))
    for name in ['add', 'sub', 'mul', 'div']:
        fn = getattr(lib, 'khz_rational_' + name)
        fn.argtypes = [Rational, Rational, C.POINTER(Rational)]
        fn.restype = C.c_int
    lib.khz_rational_compare.argtypes = [Rational, Rational, C.POINTER(C.c_int)]
    lib.khz_rational_round.argtypes = [Rational, C.c_int64, C.POINTER(Rational)]
    for name in ['floor', 'ceiling']:
        getattr(lib, 'khz_rational_' + name).argtypes = [Rational, C.POINTER(Rational)]
    lib.khz_simd_sum_i64.argtypes = [C.POINTER(C.c_int64), C.c_size_t, C.POINTER(C.c_int64)]
    lib.khz_simd_sum_scaled.argtypes = [C.POINTER(C.c_int64), C.c_size_t, C.c_int64, C.POINTER(Rational)]
    rng = random.Random(0x4B485A524154)
    checks = 0
    def check(condition, detail):
        nonlocal checks
        checks += 1
        if not condition: raise AssertionError(detail)
    cases = [(Fraction(LIMIT, 2), Fraction(LIMIT, 2)),
             (Fraction(-LIMIT, 2), Fraction(-1, 2)),
             (Fraction(1, 6000000000000000000), Fraction(1, 4000000000000000000))]
    cases.extend([(Fraction(0), Fraction(0)), (Fraction(1), Fraction(0)), (Fraction(0), Fraction(1))])
    for i in range(12000):
        bound = LIMIT if i % 2 else 1000000
        a = Fraction(rng.randint(-bound, bound), rng.randint(1, bound))
        b = Fraction(rng.randint(-bound, bound), rng.randint(1, bound))
        cases.append((a, b))
    for index, (a, b) in enumerate(cases):
        ca, cb = Rational(a.numerator, a.denominator), Rational(b.numerator, b.denominator)
        for name, result in [('add', a+b), ('sub', a-b), ('mul', a*b),
                             ('div', a/b if b else None)]:
            out = Rational(17, 19)
            status = getattr(lib, 'khz_rational_' + name)(ca, cb, C.byref(out))
            expected = -8 if result is None else (0 if abs(result.numerator) <= LIMIT
                                                    and result.denominator <= LIMIT else -7)
            check(status == expected, (name, a, b, status, expected))
            if expected == 0:
                check((out.num, out.den) == (result.numerator, result.denominator),
                      (name, a, b, out.num, out.den, result))
            else:
                check((out.num, out.den) == (17, 19), ('failure mutated output', name, a, b))
        order = C.c_int(71)
        check(lib.khz_rational_compare(ca, cb, C.byref(order)) == 0
              and order.value == ((a > b) - (a < b)), ('compare', a, b, order.value))
        decimals = index % 37 - 18
        scale = Fraction(10 ** decimals) if decimals >= 0 else Fraction(1, 10 ** -decimals)
        scaled = abs(a * scale)
        integer = (scaled.numerator * 2 + scaled.denominator) // (scaled.denominator * 2)
        rounded = Fraction(integer if a >= 0 else -integer) / scale
        for name, expected_value in [('round', rounded), ('floor', Fraction(a.numerator // a.denominator)),
                                     ('ceiling', Fraction(-(-a.numerator // a.denominator)))]:
            out = Rational(17, 19)
            args = [ca, decimals, C.byref(out)] if name == 'round' else [ca, C.byref(out)]
            status = getattr(lib, 'khz_rational_' + name)(*args)
            expected_status = 0 if abs(expected_value.numerator) <= LIMIT and expected_value.denominator <= LIMIT else -7
            check(status == expected_status, (name, a, decimals, status, expected_status))
            pair = (expected_value.numerator, expected_value.denominator) if status == 0 else (17, 19)
            check((out.num, out.den) == pair, (name, a, decimals, pair, out.num, out.den))
    extremes = [-LIMIT-1, -LIMIT, -1, 0, 1, LIMIT]
    for i in range(6000):
        values = [rng.choice(extremes) for _ in range(rng.randrange(65))]
        if i % 3 == 0: values = [LIMIT, LIMIT, -LIMIT]
        rng.shuffle(values)
        array = (C.c_int64 * len(values))(*values)
        total = sum(values)
        out = C.c_int64(71)
        status = lib.khz_simd_sum_i64(array, len(values), C.byref(out))
        expected = 0 if -LIMIT-1 <= total <= LIMIT else -7
        check(status == expected, ('sum', values, status, expected))
        check(out.value == (total if expected == 0 else 71), ('sum value', values, out.value))
        den = rng.randint(1, 1000)
        result = Fraction(total, den)
        scaled = Rational(17, 19)
        status = lib.khz_simd_sum_scaled(array, len(values), den, C.byref(scaled))
        expected = 0 if abs(result.numerator) <= LIMIT else -7
        check(status == expected, ('scaled', values, den, status, expected))
        pair = (result.numerator, result.denominator) if expected == 0 else (17, 19)
        check((scaled.num, scaled.den) == pair, ('scaled value', values, den, pair))
    print(json.dumps({'library': Path(path).name, 'seed': '4B485A524154',
                      'checks': checks, 'failures': 0}), flush=True)

if __name__ == '__main__':
    if len(sys.argv) < 2: raise SystemExit('usage: khz_rational_oracle.py <native-library> [...]')
    for argument in sys.argv[1:]: verify(argument)
