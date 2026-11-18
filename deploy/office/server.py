"""Word → PDF 转换服务。POST /convert（multipart 字段 file）→ 返回 PDF；GET /health。

每次转换用独立的临时目录和 LibreOffice 用户配置，跑完删掉；同一时间只转一份（LibreOffice 不适合并发）。
只在容器网络里可见，不对外开端口。
"""
import cgi
import os
import shutil
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_IN = 50 * 1024 * 1024
LOCK = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send(self, code, body, ctype="application/json"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, b'{"ok":true}')
        else:
            self._send(404, b'{"error":"not found"}')

    def do_POST(self):
        if self.path != "/convert":
            return self._send(404, b'{"error":"not found"}')
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_IN:
            return self._send(413, b'{"error":"file too large"}')
        form = cgi.FieldStorage(fp=self.rfile, headers=self.headers,
                                environ={"REQUEST_METHOD": "POST", "CONTENT_TYPE": self.headers.get("Content-Type", "")})
        if "file" not in form or not getattr(form["file"], "file", None):
            return self._send(400, b'{"error":"missing file"}')
        name = os.path.basename(form["file"].filename or "input.docx")
        ext = os.path.splitext(name)[1].lower()
        if ext not in (".docx", ".doc", ".xlsx", ".pptx", ".odt", ".rtf"):
            ext = ".docx"
        work = tempfile.mkdtemp(prefix="conv-")
        try:
            src = os.path.join(work, "input" + ext)
            with open(src, "wb") as f:
                shutil.copyfileobj(form["file"].file, f)
            with LOCK:
                r = subprocess.run(
                    ["soffice", f"-env:UserInstallation=file://{work}/profile", "--headless", "--norestore",
                     "--convert-to", "pdf", "--outdir", work, src],
                    capture_output=True, timeout=120)
            out = os.path.join(work, "input.pdf")
            if r.returncode != 0 or not os.path.exists(out):
                return self._send(500, b'{"error":"convert failed"}')
            with open(out, "rb") as f:
                self._send(200, f.read(), "application/pdf")
        except subprocess.TimeoutExpired:
            self._send(504, b'{"error":"timeout"}')
        finally:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 3000), Handler).serve_forever()
