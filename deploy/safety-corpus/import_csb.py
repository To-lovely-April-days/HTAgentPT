#!/usr/bin/env python3
"""把美国化学品安全委员会（CSB）的反应失控、聚合失控事故调查报告导进知识库。

CSB 是美国联邦政府机构，它的报告是公有领域作品，可以放进知识库、翻译、引用（引用时写明出处）。
清单在旁边的 csb_reports.json（每份：公司、年份、中文标题、一句话摘要、csb.gov 上的 PDF 地址）。

用法（在服务器上，项目目录里）：
    export HT_URL=http://localhost:8080
    export HT_USER=管理员账号          # 要有「资料管理」和「知识库管理」权限
    export HT_PASS='……'
    python3 deploy/safety-corpus/import_csb.py              # 从 csb.gov 下载并导入
    python3 deploy/safety-corpus/import_csb.py --dry-run    # 只看要做什么，不下载、不上传
    python3 deploy/safety-corpus/import_csb.py --dir 某目录  # 服务器上不了 csb.gov：在别的电脑下载好拷过来，从这个目录读

做的事：
    1. 找名叫「安全事故案例（公开资料）」的知识库，没有就建一个（--kb 可改名字）
    2. 逐份下载 PDF（存在 --cache 目录，默认 /srv/htagent/corpus/csb，重跑不重复下载）
    3. 上传到这个库：密级「公开」、文档类别「资料」、设备类型「反应釜」、客户名称写「公开资料（美国 CSB）」，
       标题用中文（如「CSB 事故调查：Synthron 丙烯酸聚合失控爆炸（2006）」）；库里已有同名文件的跳过
    4. 上传完的一起提交解析；解析进度在后台「资料管理」里看
登录会把这个账号在别处（浏览器）的登录挤掉，跑完重新登录即可；或者专门建一个导入用的账号。
只用 Python 标准库。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
UA = "Mozilla/5.0 (compatible; corpus-import/1.0)"
CUSTOMER = "公开资料（美国 CSB）"


def api(base, path, token=None, body=None, method=None, raw=None, ctype=None, timeout=120):
    data = raw if raw is not None else (json.dumps(body).encode("utf-8") if body is not None else None)
    req = urllib.request.Request(base + path, data=data, method=method or ("POST" if data is not None else "GET"))
    req.add_header("Content-Type", ctype or "application/json")
    req.add_header("X-Terminal-Id", "csb-import")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            text = r.read().decode("utf-8")
            return json.loads(text) if text else None
    except urllib.error.HTTPError as e:
        msg = e.read().decode("utf-8", "ignore")
        try:
            msg = json.loads(msg).get("message", msg)
        except (ValueError, AttributeError):
            pass
        raise SystemExit(f"{method or 'GET'} {path} 失败（{e.code}）：{msg}")


def multipart(fields, file_field, file_name, file_bytes, file_type="application/pdf"):
    boundary = uuid.uuid4().hex
    out = bytearray()
    for k, v in fields.items():
        out += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode("utf-8")
    out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{file_field}\"; filename=\"{file_name}\"\r\n"
            f"Content-Type: {file_type}\r\n\r\n").encode("utf-8")
    out += file_bytes + f"\r\n--{boundary}--\r\n".encode("utf-8")
    return bytes(out), f"multipart/form-data; boundary={boundary}"


def fetch_pdf(item, cache: Path, local: Path | None):
    """先看 --dir，再看缓存，都没有才去 csb.gov 下载。返回 PDF 内容。"""
    name = item["file"]
    for d in [local, cache]:
        if d and (d / name).is_file():
            return (d / name).read_bytes()
    if local:
        raise RuntimeError(f"{local} 里没有 {name}")
    last = None
    for attempt in range(3):
        try:
            req = urllib.request.Request(item["url"], headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=300) as r:
                data = r.read()
            if not data.startswith(b"%PDF"):
                raise RuntimeError("下载到的不是 PDF（网站可能换了地址）")
            cache.mkdir(parents=True, exist_ok=True)
            (cache / name).write_bytes(data)
            return data
        except (urllib.error.URLError, TimeoutError, RuntimeError) as e:
            last = e
            time.sleep(3 * (attempt + 1))
    raise RuntimeError(f"下载失败：{last}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--kb", default="安全事故案例（公开资料）", help="导进哪个知识库（没有就新建）")
    ap.add_argument("--cache", default="/srv/htagent/corpus/csb", help="下载的 PDF 存哪里")
    ap.add_argument("--dir", help="已经下载好的 PDF 所在目录（文件名照 csb_reports.json 的 file）")
    ap.add_argument("--device-type", default="反应釜", help="设备类型（要在后台词表里）")
    ap.add_argument("--category", default="资料", help="文档类别（要在后台词表里）")
    ap.add_argument("--only", help="只导这几份（逗号隔开的 id）")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    items = json.loads((HERE / "csb_reports.json").read_text(encoding="utf-8"))["reports"]
    if a.only:
        keep = {x.strip() for x in a.only.split(",")}
        items = [x for x in items if x["id"] in keep]
    if a.dry_run:
        print(f"要导入 {len(items)} 份到「{a.kb}」：")
        for x in items:
            print(f"  {x['id']:<14} {x['year']}  {x['title']}\n                 {x['url']}")
        return

    base = os.environ.get("HT_URL", "http://localhost:8080").rstrip("/")
    user, pwd = os.environ.get("HT_USER"), os.environ.get("HT_PASS")
    if not user or not pwd:
        raise SystemExit("请先设置 HT_USER 和 HT_PASS（管理员账号）")
    token = api(base, "/api/auth/login", body={"username": user, "password": pwd, "terminalId": "csb-import", "terminalName": "导入 CSB 报告"})["token"]

    # 1. 知识库
    kbs = api(base, "/api/kbs", token)
    kb = next((k for k in kbs if k["name"] == a.kb), None)
    if kb is None:
        kb_id = api(base, "/api/kbs", token, body={"name": a.kb, "tier": "Private", "defaultChunkStrategy": "ByHeading",
                                                   "description": "美国化学品安全委员会（CSB）反应失控、聚合失控事故调查报告。公有领域，引用时写明出处。"})["id"]
        print(f"新建知识库「{a.kb}」")
    else:
        kb_id = kb["id"]

    # 2. 词表：设备类型、文档类别要在词表里
    for key, value in [("device_type", a.device_type), ("doc_category", a.category)]:
        terms = api(base, f"/api/vocab/{key}", token)
        if not any(t["value"] == value and t.get("isActive", True) for t in terms):
            raise SystemExit(f"词表「{key}」里没有「{value}」：在后台词表里加上，或者用 --device-type / --category 换一个已有的")

    have = {d["fileName"] for d in api(base, f"/api/documents?kbId={kb_id}", token)}
    cache, local = Path(a.cache), Path(a.dir) if a.dir else None
    uploaded, skipped, failed = [], [], []
    for i, x in enumerate(items, 1):
        tag = f"[{i}/{len(items)}] {x['id']}"
        if x["file"] in have:
            skipped.append(x["id"])
            print(f"{tag} 库里已有，跳过")
            continue
        try:
            pdf = fetch_pdf(x, cache, local)
        except RuntimeError as e:
            failed.append((x["id"], str(e)))
            print(f"{tag} {e}")
            continue
        body, ctype = multipart({
            "kbId": kb_id, "classification": "Public", "customerName": CUSTOMER, "year": str(x["year"]),
            "deviceType": a.device_type, "docCategory": a.category, "title": x["title"], "docVersion": "CSB final report",
            "registerProject": "false",
        }, "file", x["file"], pdf)
        r = api(base, "/api/documents", token, raw=body, ctype=ctype, timeout=600)
        uploaded.append(r["docId"])
        print(f"{tag} 已上传（{len(pdf) / 1048576:.1f} MB）{x['title']}")

    if uploaded:
        q = api(base, "/api/documents/parse", token, body={"docIds": uploaded})
        print(f"\n已提交解析 {q.get('queued', len(uploaded))} 份，进度在后台「资料管理」里看。")
    print(f"上传 {len(uploaded)} 份，跳过 {len(skipped)} 份（已有），失败 {len(failed)} 份。")
    for fid, why in failed:
        print(f"  失败 {fid}：{why}")
    if failed:
        print("下载失败的可以在能上 csb.gov 的电脑上下载（地址见 csb_reports.json），拷到服务器后用 --dir 再跑一次。")
        sys.exit(1)


if __name__ == "__main__":
    main()
