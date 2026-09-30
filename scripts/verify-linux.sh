#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
build_root="${1:-$repo_root/build/linux-verify}"
evidence="$repo_root/build/evidence-linux"
mkdir -p "$evidence"
run_check() {
    local name="$1"
    shift
    local start=$SECONDS code=0
    "$@" > "$evidence/$name.log" 2>&1 || code=$?
    python3 - "$evidence" "$repo_root" "$name" "$code" "$((SECONDS-start))" "$@" <<'PY'
import hashlib,json,pathlib,re,sys
directory,root,name,code,seconds,*command=sys.argv[1:]
p=pathlib.Path(directory);log=p/(name+'.log')
text=log.read_text(errors='replace').replace(root,'<repo>')
log.write_text(text)
step={'name':name,'command':command,'exitCode':int(code),'seconds':int(seconds),
      'compilerWarnings':len(re.findall(r'(?im)^.*\bwarning:',text)),
      'log':log.name,'sha256':hashlib.sha256(log.read_bytes()).hexdigest()}
with (p/'steps.jsonl').open('a') as f:f.write(json.dumps(step)+'\n')
print(name+' exit='+code,flush=True)
if code!='0': print(text[-10000:]);raise SystemExit(int(code))
if step['compilerWarnings']: raise SystemExit('compiler warnings')
PY
}
: > "$evidence/steps.jsonl"
run_check gcc-version gcc --version
run_check clang-version clang --version
run_check gcc-configure cmake -S "$repo_root" -B "$build_root/gcc" -DCMAKE_C_COMPILER=gcc -DCMAKE_BUILD_TYPE=Release -DKHZ_ENABLE_LEDGER=ON -DKHZ_STRICT_WARNINGS=ON -DKHZ_BUILD_TESTS=ON -DKHZ_BUILD_SHARED=ON
run_check gcc-build cmake --build "$build_root/gcc" --parallel 4 --clean-first
run_check gcc-ctest ctest --test-dir "$build_root/gcc" --output-on-failure -V --output-junit "$evidence/gcc-ctest.xml"
run_check sanitizer-configure cmake -S "$repo_root" -B "$build_root/sanitize" -DCMAKE_C_COMPILER=clang -DCMAKE_BUILD_TYPE=Debug -DKHZ_ENABLE_LEDGER=OFF -DKHZ_STRICT_WARNINGS=ON -DKHZ_BUILD_TESTS=ON -DKHZ_BUILD_SHARED=OFF -DKHZ_ENABLE_ASAN=ON -DKHZ_ENABLE_UBSAN=ON
run_check sanitizer-build cmake --build "$build_root/sanitize" --parallel 4 --clean-first
run_check sanitizer-ctest env ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 ctest --test-dir "$build_root/sanitize" --output-on-failure -V --output-junit "$evidence/sanitizer-ctest.xml"
python3 - "$evidence" <<'PY'
import json,pathlib,sys,xml.etree.ElementTree as E
p=pathlib.Path(sys.argv[1]); steps=[json.loads(s) for s in (p/'steps.jsonl').read_text().splitlines()]
receipt={'schema':1,'platform':'Linux','steps':steps,'compilerWarnings':sum(s['compilerWarnings'] for s in steps),'success':True}
for name in ['gcc','sanitizer']:
    root=E.parse(p/(name+'-ctest.xml')).getroot()
    receipt[name]={k:int(root.get(k,'0')) for k in ['tests','failures','disabled']}
(p/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps({k:v for k,v in receipt.items() if k!='steps'}))
PY
