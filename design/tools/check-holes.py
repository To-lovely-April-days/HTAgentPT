#!/usr/bin/env python3
"""核对 .dc.html 模板里的 {{hole}} 都能在 renderVals() 或 sc-for 循环变量里找到来源。

这是该格式最容易静默出错的地方——hole 找不到来源不报错，只渲染成空白。
两级检查：
  1. 根键：{{foo}} / {{foo.bar}} 的 foo 必须是 renderVals 返回的键或 sc-for 的 as 变量
  2. 循环项字段：{{item.field}} 的 field 必须在脚本体里作为某个对象键出现过
     （启发式，但足以抓住「模板引用了一个从没算过的字段」这类错误）

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
    body = s.split('<script data-dc-script')[1] if '<script data-dc-script' in s else ''

    holes = set(re.findall(r'\{\{\s*([A-Za-z_$][\w.$]*)\s*\}\}', tpl))
    loops = set(re.findall(r'<sc-for[^>]*as="(\w+)"', s))
    keys = set(re.findall(r'(?:^|[\s{,])([A-Za-z_$][\w$]*)\s*:', body))

    roots = set(h.split('.')[0] for h in holes) - loops - {'true', 'false'}
    miss_root = sorted(r for r in roots if r not in keys)

    # 循环项字段：{{x.field}} 里的 field
    miss_field = sorted(set(
        h for h in holes
        if '.' in h and h.split('.')[0] in loops and h.split('.')[1] not in keys
    ))

    if miss_root or miss_field:
        bad += 1
    print('%-24s holes=%-3d 循环=%-14s 缺根键=%-12s 缺字段=%s'
          % (os.path.basename(p), len(holes), ','.join(sorted(loops)) or '-',
             miss_root or '无', miss_field or '无'))

print('\n全部通过' if not bad else '\n%d 个文件有问题' % bad)
sys.exit(1 if bad else 0)
