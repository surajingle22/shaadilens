"""face-service: a small internal HTTP API around InsightFace.

Only the .NET API talks to this service (it is not exposed publicly).
Endpoints:
  GET  /health       -> {"status": "ok"} once the model is loaded
  GET  /v1/info      -> model + runtime versions
  POST /v1/analyze   -> multipart "file": detect faces + 512-d embeddings
  POST /v1/selfie    -> multipart "file" (+ optional "max_yaw"): validate a guest selfie
  POST /v1/cluster   -> raw float32 embeddings: group faces into people (DBSCAN)
"""

import logging
import os
import time
from contextlib import asynccontextmanager
from dataclasses import replace

import cv2
import numpy as np
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile

from .cluster import cluster_embeddings
from .engine import FaceEngine
from .selfie import SelfieRules, validate_selfie

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("face-service")

MAX_UPLOAD_BYTES = int(os.environ.get("MAX_UPLOAD_MB", "50")) * 1024 * 1024


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Load the models once at startup (takes a few seconds). uvicorn won't
    # accept requests until this finishes, so /health only answers when ready.
    app.state.engine = FaceEngine(
        det_size=int(os.environ.get("DET_SIZE", "640")),
        max_side=int(os.environ.get("MAX_SIDE", "1600")),
    )
    yield


app = FastAPI(title="ShaadiLens face-service", version="0.1.0", lifespan=lifespan)


def _engine(request: Request) -> FaceEngine:
    return request.app.state.engine


def decode_image(data: bytes) -> np.ndarray:
    if not data:
        raise HTTPException(status_code=400, detail="Empty file")
    if len(data) > MAX_UPLOAD_BYTES:
        raise HTTPException(status_code=413, detail="File too large")
    # IMREAD_COLOR also applies the EXIF orientation flag, so phone photos come out upright.
    img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
    if img is None:
        raise HTTPException(status_code=400, detail="Not a supported image (JPEG/PNG/WebP/BMP)")
    return img


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/v1/info")
def info(request: Request):
    return _engine(request).info()


# Plain `def` (not async): FastAPI runs it in a thread pool, so the CPU-heavy
# inference doesn't block the event loop and /health keeps responding.
@app.post("/v1/analyze")
def analyze(request: Request, file: UploadFile = File(...)):
    img = decode_image(file.file.read())
    t0 = time.perf_counter()
    result = _engine(request).analyze(img)
    elapsed_ms = (time.perf_counter() - t0) * 1000
    result["elapsed_ms"] = round(elapsed_ms, 1)
    # Never log embeddings or image content — only sizes and timings.
    log.info("analyze %dx%d -> %d faces in %.0f ms",
             result["width"], result["height"], len(result["faces"]), elapsed_ms)
    return result


@app.post("/v1/selfie")
def selfie(request: Request, file: UploadFile = File(...), max_yaw: float | None = Form(None)):
    """Analyze + validate a guest selfie. Always 200; `ok=false` comes with a human-friendly `reason`."""
    img = decode_image(file.file.read())
    analysis = _engine(request).analyze(img)
    rules = SelfieRules() if max_yaw is None else replace(SelfieRules(), max_yaw=max_yaw)
    result = validate_selfie(analysis, rules)
    log.info("selfie %dx%d -> %d faces, %s", analysis["width"], analysis["height"], len(analysis["faces"]), result["code"])
    return result


MAX_CLUSTER_FACES = int(os.environ.get("MAX_CLUSTER_FACES", "100000"))


@app.post("/v1/cluster")
async def cluster(request: Request, dim: int = 512, eps: float = 0.5, min_samples: int = 3):
    """Group faces into people (DBSCAN on cosine distance, see app/cluster.py).

    Body: raw little-endian float32 embeddings, N x `dim` (application/octet-stream) — binary instead of
    JSON because 15,000 x 512 numbers would be ~100 MB of JSON text but only 30 MB as floats.
    Returns: {"labels": [N ints, -1 = noise], "clusters": K, "noise": n, "elapsed_ms": ...}
    """
    if not 0.05 <= eps <= 1.0 or not 2 <= min_samples <= 50 or dim < 1:
        raise HTTPException(status_code=400, detail="eps must be 0.05-1.0, min_samples 2-50")
    body = await request.body()
    if len(body) % (4 * dim) != 0:
        raise HTTPException(status_code=400, detail=f"Body must be N x {dim} float32 values")
    n = len(body) // (4 * dim)
    if n > MAX_CLUSTER_FACES:
        raise HTTPException(status_code=413, detail=f"At most {MAX_CLUSTER_FACES} faces per request")
    embeddings = np.frombuffer(body, dtype="<f4").reshape(n, dim)

    t0 = time.perf_counter()
    # DBSCAN is CPU-heavy: run it in a worker thread so /health keeps answering meanwhile.
    from starlette.concurrency import run_in_threadpool
    labels = await run_in_threadpool(cluster_embeddings, embeddings, eps, min_samples)
    elapsed_ms = (time.perf_counter() - t0) * 1000
    k = int(labels.max()) + 1 if len(labels) else 0
    noise = int((labels == -1).sum())
    log.info("cluster %d faces (eps=%.2f, min_samples=%d) -> %d people, %d noise in %.0f ms", n, eps, min_samples, k, noise, elapsed_ms)
    return {"labels": labels.tolist(), "clusters": k, "noise": noise, "elapsed_ms": round(elapsed_ms, 1)}
