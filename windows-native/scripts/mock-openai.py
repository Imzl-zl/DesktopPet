# Phase 6 端到端验收用的本地 mock OpenAI 兼容端点（127.0.0.1:18080）
# POST /v1/chat/completions → 固定回复 + usage；POST /v1/images/generations → 1x1 PNG
import base64, datetime, json, struct, zlib, sys
from http.server import BaseHTTPRequestHandler, HTTPServer

LOG = r"C:\Users\zhanglu\AppData\Local\Temp\desktoppet-mock-requests.log"

def log(msg):
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(f"[{datetime.datetime.now():%H:%M:%S}] {msg}\n")

def tiny_png_b64():
    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c))
    ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)
    raw = b"\x00\xff\x00\x00\xff"
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")
    return base64.b64encode(png).decode()

class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _read(self):
        n = int(self.headers.get("Content-Length", 0))
        return self.rfile.read(n) if n else b""

    def do_POST(self):
        body = self._read().decode("utf-8", "replace")
        log(f"{self.path} <- {body[:500]}")
        if self.path.endswith("/chat/completions"):
            resp = {
                "id": "cmpl-mock", "object": "chat.completion", "created": 0, "model": "mock",
                "choices": [{"index": 0, "message": {"role": "assistant", "content": "（mock）好的小美，加班辛苦了，抱抱~"}, "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 30, "completion_tokens": 10, "total_tokens": 40},
            }
        elif self.path.endswith("/images/generations"):
            resp = {"created": 0, "data": [{"b64_json": tiny_png_b64()}]}
        else:
            self.send_response(404)
            self.end_headers()
            return
        out = json.dumps(resp).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

if __name__ == "__main__":
    open(LOG, "w").close()  # 清空请求日志
    HTTPServer(("127.0.0.1", 18080), H).serve_forever()
