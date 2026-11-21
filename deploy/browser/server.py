"""助手的内置浏览器：服务器上跑一个 Chromium，每个对话一个独立的浏览器环境（登录状态互不相通），里面可以开多个标签。

后端（只有后端）调这里：开网页、读页面（正文、表格、编了号的可操作元素）、按编号点 / 填 / 选、截图、
转发员工在画面上的点击和打字。规矩（哪些要先问、哪些不做）在后端判，这里只管执行和拦内网：
- 内网、本机、保留地址一律拦（不然从这台服务器上的浏览器能摸到数据库、模型服务），后端给的白名单除外；
- 不下载文件，弹窗一律取消；
- 闲置太久的浏览器环境关掉；同时开的数量有上限。
所有浏览器操作都在一个线程里做（Playwright 的对象不能跨线程用），HTTP 请求排队交给它。
"""
import ipaddress
import json
import os
import queue
import re
import socket
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

from playwright.sync_api import sync_playwright, TimeoutError as PwTimeout, Error as PwError

MAX_SESSIONS = int(os.environ.get("MAX_SESSIONS", "4"))
MAX_TABS = int(os.environ.get("MAX_TABS", "8"))
IDLE = int(os.environ.get("IDLE_MINUTES", "15")) * 60
CHROMIUM = os.environ.get("CHROMIUM_PATH", "/usr/bin/chromium")
VIEW = {"width": 1280, "height": 800}

SNAPSHOT_JS = r"""
() => {
  document.querySelectorAll('[data-htref]').forEach(e => e.removeAttribute('data-htref'));
  const sel = 'a[href], button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=link], [role=tab], [role=menuitem], [role=checkbox], [role=radio], [role=combobox], [onclick], [contenteditable=true]';
  const vis = (e) => {
    const r = e.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const s = getComputedStyle(e);
    return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
  };
  const clip = (s, n) => (s || '').replace(/\s+/g, ' ').trim().slice(0, n);
  const nameOf = (e) => {
    const lab = e.labels && e.labels[0] ? e.labels[0].innerText.replace(e.innerText || '', '') : '';   // 标签里别带上下拉框自己的选项
    return clip(e.getAttribute('aria-label') || lab || (e.tagName === 'INPUT' || e.tagName === 'SELECT' || e.tagName === 'TEXTAREA' ? '' : e.innerText)
      || e.getAttribute('placeholder') || e.getAttribute('title') || e.getAttribute('alt') || e.getAttribute('name') || e.value, 40);
  };
  const out = [];
  let n = 0;
  for (const e of document.querySelectorAll(sel)) {
    if (!vis(e) || out.length >= 150) continue;
    n += 1;
    e.setAttribute('data-htref', String(n));
    const r = e.getBoundingClientRect();
    const tag = e.tagName.toLowerCase();
    const item = { n, tag, type: (e.getAttribute('type') || '').toLowerCase(), role: e.getAttribute('role') || '', name: nameOf(e),
      autocomplete: e.getAttribute('autocomplete') || '', field: e.getAttribute('name') || e.id || '',
      inView: r.top < innerHeight && r.bottom > 0 };
    if (tag === 'input' || tag === 'textarea') item.value = item.type === 'password' ? '' : clip(e.value, 60);
    if (tag === 'input' && (item.type === 'checkbox' || item.type === 'radio')) item.checked = e.checked;
    if (tag === 'select') { item.value = clip(e.options[e.selectedIndex] ? e.options[e.selectedIndex].text : '', 40); item.options = Array.from(e.options).slice(0, 10).map(o => clip(o.text, 30)); }
    if (tag === 'a') item.href = (e.href || '').slice(0, 200);
    out.push(item);
  }
  const root = document.querySelector('main, article, [role=main]') || document.body;
  const tables = Array.from(document.querySelectorAll('table')).filter(vis).slice(0, 3).map(t =>
    Array.from(t.rows).slice(0, 16).map(r => Array.from(r.cells).slice(0, 10).map(c => clip(c.innerText, 30))));
  return { text: clip(root ? root.innerText : '', 8000), tables, elements: out, scroll: Math.round(scrollY), height: document.documentElement.scrollHeight };
}
"""


def is_private_host(host):
    """内网、本机、保留地址：拦。解析失败也拦。"""
    try:
        infos = socket.getaddrinfo(host, None)
    except OSError:
        return True
    for info in infos:
        ip = ipaddress.ip_address(info[4][0].split("%")[0])
        if ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved or ip.is_multicast or ip.is_unspecified:
            return True
    return False


def host_allowed(host, allow):
    host = (host or "").lower().rstrip(".")
    return any(host == a or host.endswith("." + a) for a in allow)


class Tab:
    def __init__(self, page):
        self.id = uuid.uuid4().hex[:8]
        self.page = page
        self.rev = 0


class Session:
    def __init__(self, sid, context):
        self.sid = sid
        self.context = context
        self.tabs = {}
        self.allow = []
        self.used = time.time()


class Engine:
    """一个线程里干所有浏览器的活。"""

    def __init__(self):
        self.jobs = queue.Queue()
        self.sessions = {}
        self.ready = threading.Event()
        threading.Thread(target=self._loop, daemon=True).start()
        self.ready.wait(60)

    def call(self, fn, *args, timeout=60):
        box = {}
        done = threading.Event()
        self.jobs.put((fn, args, box, done))
        if not done.wait(timeout):
            raise TimeoutError("浏览器太忙了，稍后再试")
        if "error" in box:
            raise box["error"]
        return box["value"]

    def _loop(self):
        with sync_playwright() as pw:
            self.browser = pw.chromium.launch(executable_path=CHROMIUM if os.path.exists(CHROMIUM) else None,
                                              args=["--no-sandbox", "--disable-dev-shm-usage", "--lang=zh-CN"])
            self.ready.set()
            last_sweep = time.time()
            while True:
                try:
                    fn, args, box, done = self.jobs.get(timeout=5)
                    try:
                        box["value"] = fn(*args)
                    except Exception as e:  # noqa: BLE001 - 交回给请求方
                        box["error"] = e
                    finally:
                        done.set()
                except queue.Empty:
                    pass
                if time.time() - last_sweep > 30:
                    last_sweep = time.time()
                    for sid in [s for s, v in self.sessions.items() if time.time() - v.used > IDLE]:
                        self._close(sid)

    # ── 以下都在浏览器线程里跑 ─────────────────────────
    def _session(self, sid, create=False, allow=None):
        s = self.sessions.get(sid)
        if s is None:
            if not create:
                raise KeyError("这个对话的浏览器已经关了（闲置太久），重新打开网页就行")
            if len(self.sessions) >= MAX_SESSIONS:
                raise OverflowError(f"同时开着的浏览器到上限了（{MAX_SESSIONS} 个），等别人用完或关掉一个")
            ctx = self.browser.new_context(viewport=VIEW, locale="zh-CN", accept_downloads=False,
                                           user_agent="Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 HTLAB-Assistant")
            s = Session(sid, ctx)
            ctx.route("**/*", lambda route: self._guard(s, route))
            ctx.on("page", lambda page: self._adopt(s, page))
            self.sessions[sid] = s
        if allow is not None:
            s.allow = [a.strip().lower() for a in allow if a and a.strip()]
        s.used = time.time()
        return s

    def _guard(self, s, route):
        url = route.request.url
        p = urlparse(url)
        if p.scheme in ("data", "blob", "about"):
            return route.continue_()
        if p.scheme not in ("http", "https") or not p.hostname:
            return route.abort("blockedbyclient")
        if host_allowed(p.hostname, s.allow) or not is_private_host(p.hostname):
            return route.continue_()
        return route.abort("blockedbyclient")

    def _adopt(self, s, page):
        if any(t.page is page for t in s.tabs.values()):
            return
        if len(s.tabs) >= MAX_TABS:
            page.close()
            return
        page.on("dialog", lambda d: d.dismiss())
        t = Tab(page)
        s.tabs[t.id] = t

    def _tab(self, s, tid):
        t = s.tabs.get(tid)
        if t is None or t.page.is_closed():
            s.tabs.pop(tid, None)
            raise KeyError("这个网页已经关了")
        return t

    def _wait(self, page):
        try:
            page.wait_for_load_state("domcontentloaded", timeout=8000)
        except PwTimeout:
            pass
        time.sleep(0.4)

    def _info(self, t, extra=None):
        d = {"tab": t.id, "url": t.page.url, "title": t.page.title()[:120], "rev": t.rev}
        d.update(extra or {})
        return d

    def open(self, sid, url, tid=None, allow=None):
        s = self._session(sid, create=True, allow=allow)
        p = urlparse(url)
        if p.scheme not in ("http", "https") or not p.hostname:
            raise ValueError("只能打开 http / https 网址")
        if not host_allowed(p.hostname, s.allow) and is_private_host(p.hostname):
            raise PermissionError("这是公司内网或本机地址，默认不让打开；需要的话请管理员加进白名单")
        if tid:
            t = self._tab(s, tid)
        else:
            if len(s.tabs) >= MAX_TABS:
                raise OverflowError(f"这个对话最多开 {MAX_TABS} 个网页，先关掉一个")
            page = s.context.new_page()
            self._adopt(s, page)
            t = next(x for x in s.tabs.values() if x.page is page)
        try:
            t.page.goto(url, wait_until="domcontentloaded", timeout=20000)
        except PwTimeout:
            pass
        time.sleep(0.6)
        t.rev += 1
        return self._info(t)

    def tabs(self, sid):
        s = self.sessions.get(sid)
        if s is None:
            return {"tabs": [], "alive": False}
        s.used = time.time()
        for tid in [k for k, v in s.tabs.items() if v.page.is_closed()]:
            s.tabs.pop(tid, None)
        return {"alive": True, "tabs": [self._info(t) for t in s.tabs.values()]}

    def read(self, sid, tid):
        s = self._session(sid)
        t = self._tab(s, tid)
        snap = t.page.evaluate(SNAPSHOT_JS)
        return self._info(t, snap)

    def act(self, sid, tid, action, n=None, text=None, value=None, submit=False):
        s = self._session(sid)
        t = self._tab(s, tid)
        page = t.page
        loc = page.locator(f'[data-htref="{int(n)}"]').first if n is not None else None
        if action in ("click", "type", "select", "check") and (loc is None or loc.count() == 0):
            raise LookupError(f"页面上找不到编号 [{n}]（页面可能变了），先重新读一下页面")
        if action == "click":
            loc.click(timeout=6000)
        elif action == "type":
            loc.fill(text or "", timeout=6000)
            if submit:
                loc.press("Enter")
        elif action == "select":
            try:
                loc.select_option(label=value, timeout=6000)
            except PwError:
                loc.select_option(value=value, timeout=6000)
        elif action == "check":
            loc.click(timeout=6000)
        elif action == "scroll":
            page.mouse.wheel(0, -700 if (text or "").startswith("up") else 700)
        elif action == "press":
            page.keyboard.press(text or "Enter")
        elif action == "back":
            page.go_back(timeout=10000)
        elif action == "forward":
            page.go_forward(timeout=10000)
        elif action == "reload":
            page.reload(timeout=15000)
        else:
            raise ValueError(f"不认识的操作：{action}")
        self._wait(page)
        t.rev += 1
        return self._info(t)

    def user_input(self, sid, tid, kind, x=None, y=None, text=None, key=None, dy=None):
        """员工在画面上的操作：坐标是 0–1 的比例。"""
        s = self._session(sid)
        t = self._tab(s, tid)
        page = t.page
        if kind == "click":
            page.mouse.click(float(x) * VIEW["width"], float(y) * VIEW["height"])
            self._wait(page)
        elif kind == "type":
            page.keyboard.type((text or "")[:500])
        elif kind == "key":
            if key not in ("Enter", "Backspace", "Tab", "Escape", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Delete", "Home", "End", "PageUp", "PageDown"):
                raise ValueError("这个键不支持")
            page.keyboard.press(key)
            if key == "Enter":
                self._wait(page)
        elif kind == "scroll":
            page.mouse.wheel(0, max(-2000, min(2000, int(dy or 0))))
            time.sleep(0.2)
        else:
            raise ValueError("不认识的操作")
        t.rev += 1
        return self._info(t)

    def shot(self, sid, tid):
        s = self._session(sid)
        t = self._tab(s, tid)
        return t.page.screenshot(type="jpeg", quality=70), t.rev

    def close_tab(self, sid, tid):
        s = self._session(sid)
        t = s.tabs.pop(tid, None)
        if t and not t.page.is_closed():
            t.page.close()
        return {"ok": True}

    def _close(self, sid):
        s = self.sessions.pop(sid, None)
        if s:
            try:
                s.context.close()
            except PwError:
                pass
        return {"ok": True}

    def close(self, sid):
        return self._close(sid)


ENGINE = None
ROUTE = re.compile(r"^/s/([A-Za-z0-9-]{8,64})(?:/t/([a-f0-9]{8})(?:/(\w+))?|/(\w+))?$")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _json(self, code, body):
        data = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n > 100_000:
            raise ValueError("请求太大")
        return json.loads(self.rfile.read(n) or b"{}") if n else {}

    def _run(self, fn, *args):
        try:
            return 200, ENGINE.call(fn, *args)
        except KeyError as e:
            return 404, {"error": str(e).strip("'\"")}
        except (PermissionError, OverflowError) as e:
            return 409, {"error": str(e)}
        except (ValueError, LookupError) as e:
            return 400, {"error": str(e)}
        except PwTimeout:
            return 504, {"error": "网页没反应（超时）"}
        except (PwError, TimeoutError) as e:
            return 502, {"error": str(e).split("\n")[0][:200]}

    def do_GET(self):
        if self.path == "/health":
            return self._json(200, {"ok": True, "sessions": len(ENGINE.sessions)})
        m = ROUTE.match(self.path)
        if not m:
            return self._json(404, {"error": "not found"})
        sid, tid, sub, op = m.groups()
        if tid and sub == "shot":
            try:
                img, rev = ENGINE.call(ENGINE.shot, sid, tid)
            except KeyError as e:
                return self._json(404, {"error": str(e).strip("'\"")})
            except (PwError, TimeoutError) as e:
                return self._json(502, {"error": str(e).split("\n")[0][:200]})
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("X-Rev", str(rev))
            self.send_header("Content-Length", str(len(img)))
            self.end_headers()
            self.wfile.write(img)
            return
        if not tid and op == "tabs":
            return self._json(*self._run(ENGINE.tabs, sid))
        self._json(404, {"error": "not found"})

    def do_POST(self):
        m = ROUTE.match(self.path)
        if not m:
            return self._json(404, {"error": "not found"})
        sid, tid, sub, op = m.groups()
        try:
            b = self._body()
        except (ValueError, json.JSONDecodeError):
            return self._json(400, {"error": "请求不是 JSON"})
        if not tid and op == "open":
            return self._json(*self._run(ENGINE.open, sid, str(b.get("url") or ""), b.get("tab"), b.get("allow") or []))
        if tid and sub == "read":
            return self._json(*self._run(ENGINE.read, sid, tid))
        if tid and sub == "act":
            return self._json(*self._run(ENGINE.act, sid, tid, str(b.get("action") or ""), b.get("n"), b.get("text"), b.get("value"), bool(b.get("submit"))))
        if tid and sub == "input":
            return self._json(*self._run(ENGINE.user_input, sid, tid, str(b.get("kind") or ""), b.get("x"), b.get("y"), b.get("text"), b.get("key"), b.get("dy")))
        self._json(404, {"error": "not found"})

    def do_DELETE(self):
        m = ROUTE.match(self.path)
        if not m:
            return self._json(404, {"error": "not found"})
        sid, tid, sub, op = m.groups()
        if tid and not sub:
            return self._json(*self._run(ENGINE.close_tab, sid, tid))
        if not tid and not op:
            return self._json(*self._run(ENGINE.close, sid))
        self._json(404, {"error": "not found"})


if __name__ == "__main__":
    ENGINE = Engine()
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8100"))), Handler).serve_forever()
