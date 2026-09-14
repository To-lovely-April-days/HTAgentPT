#!/usr/bin/env python3
"""核对每个 .dc.html 模板里的 {{hole}} 都能在 renderVals() 的返回值或 sc-for 循环变量里找到来源。

这是该格式最容易静默出错的地方——hole 找不到来源不会报错，只会渲染成空白。
用法：python3 design/tools/check-holes.py design/prototype-xxx
"""
import re, io, sys, glob, os

target = sys.argv[1] if len(sys.argv) > 1 else '.'
files = sorted(glob.glob(os.path.join(target, '*.dc.html')))
if not files:
    print('没有找到 .dc.html'); sys.exit(1)

bad = 0
for p in files:
    s = io.open(p, encoding='utf-8').read()
    tpl = s.split('<script data-dc-script')[0]
    holes = set(re.findall(r'\{\{\s*([A-Za-z_$][\w.$]*)\s*\}\}', tpl))
    loops = set(re.findall(r'<sc-for[^>]*as="(\w+)"', s))
    roots = set(h.split('.')[0] for h in holes) - loops - {'true', 'false'}
    body = s.split('<script data-dc-script')[1] if '<script data-dc-script' in s else ''
    missing = [r for r in sorted(roots) if not re.search(r'(^|[\s{,])' + re.escape(r) + r'\s*:', body)]
    if missing:
        bad += 1
    print('%-26s holes=%-3d 循环=%-18s 缺来源=%s'
          % (os.path.basename(p), len(holes), ','.join(sorted(loops)) or '-', missing or '无'))

print('\n全部通过' if not bad else '\n%d 个文件有缺失的 hole' % bad)
sys.exit(1 if bad else 0)
