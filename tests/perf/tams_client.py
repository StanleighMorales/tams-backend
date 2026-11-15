"""Tiny stdlib HTTP client shared by the perf scripts (no third-party packages)."""
import json
import ssl
import urllib.error
import urllib.request
import uuid


class Api:
    def __init__(self, base, insecure=False):
        self.base = base.rstrip("/")  # e.g. https://localhost/api or http://localhost:8080/api
        self.token = None
        self.ctx = ssl.create_default_context()
        if insecure:  # local Caddy CA is not trusted by Python
            self.ctx.check_hostname = False
            self.ctx.verify_mode = ssl.CERT_NONE

    def call(self, method, path, body=None, headers=None, raw=None, content_type=None, expect=(200, 201, 204)):
        h = dict(headers or {})
        data = raw
        if body is not None:
            data = json.dumps(body).encode()
            h["Content-Type"] = "application/json"
        if content_type:
            h["Content-Type"] = content_type
        if self.token:
            h["Authorization"] = "Bearer " + self.token
        req = urllib.request.Request(self.base + path, data=data, method=method, headers=h)
        try:
            with urllib.request.urlopen(req, context=self.ctx, timeout=60) as r:
                status, text = r.status, r.read().decode()
        except urllib.error.HTTPError as e:
            status, text = e.code, e.read().decode()
        if status not in expect:
            raise RuntimeError(f"{method} {path} -> {status}: {text[:300]}")
        return json.loads(text) if text and text.lstrip()[:1] in "{[" else text

    def login(self, email, password):
        self.token = self.call("POST", "/auth/login", {"email": email, "password": password})["accessToken"]

    def multipart_file(self, path, filename, data, mime):
        boundary = uuid.uuid4().hex
        body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
                f"Content-Type: {mime}\r\n\r\n").encode() + data + f"\r\n--{boundary}--\r\n".encode()
        return self.call("POST", path, raw=body, content_type=f"multipart/form-data; boundary={boundary}")
