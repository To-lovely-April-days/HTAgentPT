#!/usr/bin/env python3
"""助手准确率测试：拿一批真实问题去问助手，按「查得准」「答得全」「该问才问」打分，并和上一次比。

用法（在服务器上）：
    export HT_EVAL_URL=http://localhost:8080      # 公司站点地址
    export HT_EVAL_USER=eval_tester               # 专门建一个测试账号，别用自己的（登录会把同账号别处的会话挤掉）
    export HT_EVAL_PASS='……'
    python3 deploy/eval/run_eval.py /srv/htagent/eval/questions.jsonl

题目文件一行一题（JSON），字段见 questions.example.jsonl：
    id            题号，自己起，后面比较靠它对齐
    q             问题原话
    expect_docs   应该查到的资料（文件名或标题里的一段文字），可多个
    expect_projects 应该查到的项目号，可多个
    expect_points 答案要点；一条里用 | 隔开表示「任意一种说法都算」
    expect_ask    这题是不是应该先问用户（默认 false）

结果写在题目文件旁边的 runs/ 目录下（每次一个 JSON），终端里打印汇总和变好 / 变差的题。
只用 Python 标准库，不用装别的包。题目和结果里可能有客户信息，不要提交到代码仓库。
"""
import json
import os
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path


def norm(s):
    s = unicodedata.normalize("NFKC", s or "").lower()
    return re.sub(r"[\s，,。．.、；;：:（）()\[\]「」“”\"'·\-—_/]+", "", s)


def http(url, body=None, token=None, stream=False, timeout=300):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method="POST" if data else "GET")
    req.add_header("Content-Type", "application/json")
    req.add_header("X-Terminal-Id", "eval-runner")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    resp = urllib.request.urlopen(req, timeout=timeout)
    return resp if stream else json.loads(resp.read().decode("utf-8"))


def ask(base, token, question):
    """问一题，收齐流式事件：回答正文、依据、是否反问、出错信息。"""
    resp = http(f"{base}/api/chat/completions", {"question": question}, token, stream=True)
    answer, evidence, asked, error, event = "", {"used": [], "other": []}, None, None, None
    for raw in resp:
        line = raw.decode("utf-8").rstrip("\n")
        if line.startswith("event: "):
            event = line[7:].strip()
        elif line.startswith("data: ") and event:
            try:
                p = json.loads(line[6:])
            except json.JSONDecodeError:
                continue
            if event == "delta":
                answer += p.get("text", "")
            elif event == "replace":
                answer = p.get("text", "") or ""
            elif event == "evidence":
                evidence = p
            elif event == "ask":
                asked = p.get("question", "")
            elif event == "error":
                error = p.get("message", "出错")
    return answer, evidence, asked, error


def score(item, answer, evidence, asked):
    cards = (evidence.get("used") or []) + (evidence.get("other") or [])
    used = evidence.get("used") or []

    def found(needle, pool):
        n = norm(needle)
        return any(n and (n in norm(c.get("title")) or n in norm(c.get("fileName")) or n == norm(c.get("projectNo"))) for c in pool)

    targets = [("doc", d) for d in item.get("expect_docs", [])] + [("project", p) for p in item.get("expect_projects", [])]
    hit = None
    if targets:
        in_used = sum(1 for _, t in targets if found(t, used))
        in_any = sum(1 for _, t in targets if found(t, cards))
        hit = {"total": len(targets), "used": in_used, "found": in_any}

    points = item.get("expect_points", [])
    got = []
    a = norm(answer)
    for pt in points:
        alts = [x for x in pt.split("|") if x.strip()]
        got.append(any(norm(x) in a for x in alts))
    cover = {"total": len(points), "got": sum(got), "missing": [p for p, g in zip(points, got) if not g]} if points else None

    should_ask = bool(item.get("expect_ask", False))
    ask_ok = (asked is not None) == should_ask
    return hit, cover, ask_ok


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    qfile = Path(sys.argv[1])
    base = os.environ.get("HT_EVAL_URL", "http://localhost:8080").rstrip("/")
    user, pwd = os.environ.get("HT_EVAL_USER"), os.environ.get("HT_EVAL_PASS")
    if not user or not pwd:
        print("请先设置 HT_EVAL_USER 和 HT_EVAL_PASS（专门的测试账号）")
        sys.exit(1)
    items = [json.loads(l) for l in qfile.read_text(encoding="utf-8").splitlines() if l.strip() and not l.lstrip().startswith("//")]
    try:
        token = http(f"{base}/api/auth/login", {"username": user, "password": pwd, "terminalId": "eval-runner", "terminalName": "准确率测试"})["token"]
    except urllib.error.HTTPError as e:
        print("登录失败：", e.read().decode("utf-8", "ignore"))
        sys.exit(1)

    results, started = [], time.time()
    for i, item in enumerate(items, 1):
        t0 = time.time()
        try:
            answer, evidence, asked, error = ask(base, token, item["q"])
        except Exception as e:  # 网络、超时：记下来接着跑
            answer, evidence, asked, error = "", {"used": [], "other": []}, None, str(e)
        hit, cover, ask_ok = score(item, answer, evidence, asked)
        r = {"id": item.get("id", str(i)), "q": item["q"], "seconds": round(time.time() - t0, 1), "error": error,
             "asked": asked, "ask_ok": ask_ok, "hit": hit, "cover": cover, "answer": answer[:2000],
             "evidence": [{"n": c.get("n"), "title": c.get("title"), "fileName": c.get("fileName"), "projectNo": c.get("projectNo")}
                          for c in (evidence.get("used") or [])]}
        results.append(r)
        flag = "出错" if error else ("✓" if (not hit or hit["found"] == hit["total"]) and (not cover or cover["got"] == cover["total"]) and ask_ok else "✕")
        print(f"[{i}/{len(items)}] {flag} {r['id']} {item['q'][:40]}（{r['seconds']} 秒）")

    def rate(key, part, whole):
        rows = [r[key] for r in results if r[key]]
        return (sum(x[part] for x in rows) / max(1, sum(x[whole] for x in rows))) if rows else None

    summary = {
        "查得准": rate("hit", "found", "total"),
        "查到且用上": rate("hit", "used", "total"),
        "答得全": rate("cover", "got", "total"),
        "该问才问": sum(1 for r in results if r["ask_ok"]) / max(1, len(results)),
        "出错": sum(1 for r in results if r["error"]),
    }
    runs = qfile.parent / "runs"
    runs.mkdir(exist_ok=True)
    prev_files = sorted(runs.glob("*.json"))
    out = runs / f"{datetime.now():%Y%m%d-%H%M%S}.json"
    out.write_text(json.dumps({"at": datetime.now().isoformat(), "summary": summary, "results": results}, ensure_ascii=False, indent=1), encoding="utf-8")

    print("\n==== 汇总（{} 题，用时 {:.0f} 秒）====".format(len(results), time.time() - started))
    for k, v in summary.items():
        print(f"  {k}：{v if isinstance(v, int) else ('—' if v is None else f'{v * 100:.0f}%')}")

    if prev_files:
        prev = json.loads(prev_files[-1].read_text(encoding="utf-8"))
        old = {r["id"]: r for r in prev["results"]}
        better, worse = [], []
        for r in results:
            o = old.get(r["id"])
            if not o:
                continue
            def pts(x):
                return (x["hit"]["found"] if x["hit"] else 0) + (x["cover"]["got"] if x["cover"] else 0) + (1 if x["ask_ok"] else 0)
            if pts(r) > pts(o):
                better.append(r)
            elif pts(r) < pts(o):
                worse.append(r)
        print(f"\n和上次（{prev_files[-1].stem}）比：变好 {len(better)} 题，变差 {len(worse)} 题")
        for r in worse:
            miss = "、".join((r["cover"] or {}).get("missing", [])[:3])
            print(f"  变差 {r['id']} {r['q'][:40]}" + (f"｜没答到：{miss}" if miss else ""))
    print(f"\n明细：{out}")


if __name__ == "__main__":
    main()
