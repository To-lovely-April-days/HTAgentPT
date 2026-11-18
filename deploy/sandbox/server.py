"""助手的代码解释器：收一段 Python，在独立进程里跑，返回输出和图。

隔离靠三层：
1. 容器：不接外网（compose 里是 internal 网络）、根文件系统只读、去掉全部 capability、限内存/CPU/进程数；
2. 进程：每次一个新的子进程，限 CPU 时间、内存、单文件大小，跑完整个临时目录删掉；
3. 服务：只有 /run 和 /health 两个口，只在容器网络里可见。
"""
import base64
import json
import os
import resource
import shutil
import subprocess
import sys
import tempfile
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PRELUDE = r'''
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
plt.rcParams["font.sans-serif"] = ["WenQuanYi Micro Hei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False
__n = [0]
def __save(*a, **k):
    __n[0] += 1
    plt.savefig(f"out_{__n[0]}.png", dpi=110, bbox_inches="tight")
    plt.close("all")
plt.show = __save
'''
EPILOGUE = r'''
for __i in plt.get_fignums():
    plt.figure(__i)
    __save()
'''

MAX_OUT = 100_000
MAX_IMG = 1_500_000


def limits(cpu_seconds):
    def apply():
        resource.setrlimit(resource.RLIMIT_CPU, (cpu_seconds, cpu_seconds + 1))
        resource.setrlimit(resource.RLIMIT_AS, (1536 * 1024 * 1024,) * 2)
        resource.setrlimit(resource.RLIMIT_FSIZE, (20 * 1024 * 1024,) * 2)
        resource.setrlimit(resource.RLIMIT_NOFILE, (64, 64))
        os.setsid()
    return apply


def run(code, timeout):
    work = tempfile.mkdtemp(prefix="run-", dir="/tmp")
    started = time.time()
    try:
        with open(os.path.join(work, "main.py"), "w", encoding="utf-8") as f:
            f.write(PRELUDE + "\n" + code + "\n" + EPILOGUE)
        env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": work, "MPLCONFIGDIR": work, "PYTHONIOENCODING": "utf-8", "LANG": "C.UTF-8"}
        try:
            p = subprocess.run([sys.executable, "-I", "main.py"], cwd=work, env=env, capture_output=True,
                               timeout=timeout, preexec_fn=limits(timeout))
            out = p.stdout.decode("utf-8", "replace")[:MAX_OUT]
            err = p.stderr.decode("utf-8", "replace")
            error = None
            if p.returncode != 0:
                # 只留最后一段报错，前面的路径对人没用
                error = "\n".join(err.strip().splitlines()[-12:]).replace(work, "") or f"进程退出码 {p.returncode}"
        except subprocess.TimeoutExpired:
            out, error = "", f"运行超过 {timeout} 秒，被停掉了"
        images = []
        for name in sorted(os.listdir(work)):
            if name.startswith("out_") and name.endswith(".png") and len(images) < 3:
                data = open(os.path.join(work, name), "rb").read()
                if len(data) <= MAX_IMG:
                    images.append("data:image/png;base64," + base64.b64encode(data).decode())
        return {"stdout": out, "error": error, "images": images, "seconds": round(time.time() - started, 2)}
    finally:
        shutil.rmtree(work, ignore_errors=True)


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body):
        data = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"ok": True})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/run":
            return self._send(404, {"error": "not found"})
        length = int(self.headers.get("Content-Length") or 0)
        if length > 200_000:
            return self._send(413, {"error": "代码太长"})
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
        except json.JSONDecodeError:
            return self._send(400, {"error": "不是 JSON"})
        code = str(body.get("code") or "")
        timeout = max(5, min(int(body.get("timeout") or 30), 120))
        self._send(200, run(code, timeout))

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8000), Handler).serve_forever()
