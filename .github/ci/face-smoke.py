"""ARM smoke test for the face-service image: runs the real buffalo_l model on insightface's bundled sample photo.

Runs INSIDE the face-service container (python + cv2 + insightface are there) against the live uvicorn server.
Prints only counts and status codes — never embeddings.
"""
import os
import sys
import time
import urllib.request

import cv2
import httpx
import insightface

BASE = os.environ.get("FACE_URL", "http://localhost:8000")
SAMPLE = os.path.join(os.path.dirname(insightface.__file__), "data", "images", "t1.jpg")  # group photo, 6 faces


def fail(msg: str) -> None:
    print(f"FAIL: {msg}")
    sys.exit(1)


for _ in range(120):  # model load can take a while on first start
    try:
        urllib.request.urlopen(f"{BASE}/health", timeout=3)
        break
    except Exception:
        time.sleep(1)
else:
    fail("/health never answered")

with open(SAMPLE, "rb") as f:
    group = f.read()

with httpx.Client(base_url=BASE, timeout=120) as c:
    r = c.post("/v1/analyze", files={"file": ("t1.jpg", group, "image/jpeg")})
    r.raise_for_status()
    faces = r.json()["faces"]
    dims = {len(x["embedding"]) for x in faces}
    print(f"/v1/analyze: {len(faces)} faces, embedding sizes {sorted(dims)}")
    if len(faces) < 1 or dims != {512}:
        fail("expected >= 1 face with a 512-d embedding")

    # A selfie needs ONE big, frontal face: crop the most frontal face of the group photo with a margin, enlarge it.
    face = min(faces, key=lambda x: abs(x["pose"]["yaw"]))
    b = face["bbox"]
    img = cv2.imread(SAMPLE)
    h, w = img.shape[:2]
    m = 0.6
    x0, y0 = max(0, int(b["x"] - m * b["w"])), max(0, int(b["y"] - m * b["h"]))
    x1, y1 = min(w, int(b["x"] + (1 + m) * b["w"])), min(h, int(b["y"] + (1 + m) * b["h"]))
    crop = cv2.resize(img[y0:y1, x0:x1], None, fx=4, fy=4, interpolation=cv2.INTER_CUBIC)
    ok, jpg = cv2.imencode(".jpg", crop, [cv2.IMWRITE_JPEG_QUALITY, 92])
    r = c.post("/v1/selfie", files={"file": ("selfie.jpg", jpg.tobytes(), "image/jpeg")})
    r.raise_for_status()
    s = r.json()
    emb = len(s.get("embedding") or [])
    print(f"/v1/selfie: ok={s['ok']} code={s['code']} embedding size {emb}")
    if not s["ok"] or emb != 512:
        fail("selfie was not accepted with a 512-d embedding")

print("face-service smoke test passed")
