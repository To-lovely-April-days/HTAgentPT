#!/usr/bin/env python3
"""把每个 .dc.html 的 <script data-dc-script> 逻辑类抽出来做语法校验。

这些类在打包时不会被解析，语法错误要到画布里打开才暴露，且表现为整个 artboard 空白。
用法：python3 design/tools/check-js.py design/prototype-xxx
"""
import re, io, sys, glob, os, subprocess, tempfile

target = sys.argv[1] if len(sys.argv) > 1 else '.'
files = sorted(glob.glob(os.path.join(target, '*.dc.html')))
if not files:
    print('没有找到 .dc.html'); sys.exit(1)

bad = 0
for p in files:
    s = io.open(p, encoding='utf-8').read()
    m = re.search(r'<script data-dc-script[^>]*>(.*?)</script>', s, re.S)
    if not m:
        print('%-24s 静态 artboard，无逻辑类' % os.path.basename(p)); continue
    code = 'class DCLogic {}\n' + m.group(1)
    with tempfile.NamedTemporaryFile('w', suffix='.js', delete=False, encoding='utf-8') as f:
        f.write(code); tmp = f.name
    r = subprocess.run(['node', '--check', tmp], capture_output=True, text=True)
    os.unlink(tmp)
    if r.returncode:
        bad += 1
        print('%-24s 语法错误：' % os.path.basename(p))
        print('  ' + (r.stderr.strip().splitlines() or ['?'])[0])
    else:
        print('%-24s 语法通过' % os.path.basename(p))

print('\n全部通过' if not bad else '\n%d 个文件有语法错误' % bad)
sys.exit(1 if bad else 0)
