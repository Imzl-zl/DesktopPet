#!/usr/bin/env python3
"""本地转发代理：记录 DesktopPet 发出的请求头/体，原样转发到真实端点。
用法：python proxy.py，然后 providers.json baseUrl 指向 http://127.0.0.1:18888/v1"""
import http.server
import urllib.request
import json
import sys

TARGET = "https://newapi.myovo.cc.cd"
LOG = r"C:\Users\zhanglu\AppData\Local\Temp\desktoppet-proxy.log"

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _forward(self):
        length = int(self.headers.get("Content-Length", 0) or 0)
        body = self.rfile.read(length) if length else b""
        url = TARGET + self.path
        req = urllib.request.Request(url, data=body or None, method=self.command)
        for k, v in self.headers.items():
            if k.lower() not in ("host", "connection", "content-length"):
                req.add_header(k, v)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(f"\n===== {self.command} {self.path} =====\n")
            for k, v in self.headers.items():
                f.write(f"{k}: {v}\n")
            f.write(f"BODY({len(body)}B): {body.decode('utf-8', 'replace')[:3000]}\n")
        try:
            resp = urllib.request.urlopen(req, timeout=120)
            data = resp.read()
            with open(LOG, "a", encoding="utf-8") as f:
                f.write(f"-> {resp.status} ({len(data)}B)\n")
            self.send_response(resp.status)
            for k, v in resp.headers.items():
                if k.lower() not in ("transfer-encoding", "connection"):
                    self.send_header(k, v)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except urllib.error.HTTPError as e:
            data = e.read()
            with open(LOG, "a", encoding="utf-8") as f:
                f.write(f"-> HTTPError {e.code}: {data.decode('utf-8','replace')[:500]}\n")
            self.send_response(e.code)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            if data:
                self.wfile.write(data)
        except Exception as e:
            with open(LOG, "a", encoding="utf-8") as f:
                f.write(f"-> EX {type(e).__name__}: {e}\n")
            self.send_response(502)
            self.end_headers()

    do_GET = _forward
    do_POST = _forward
    do_PUT = _forward
    do_DELETE = _forward

    def log_message(self, *a):
        pass

if __name__ == "__main__":
    print(f"proxy on 18888 -> {TARGET}, log={LOG}", flush=True)
    http.server.HTTPServer(("127.0.0.1", 18888), Handler).serve_forever()
