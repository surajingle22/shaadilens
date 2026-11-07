"""face-service: a small internal HTTP API around InsightFace.

Only the .NET API talks to this service (it is not exposed publicly).
Endpoints:
  GET  /health       -> {"status": "ok"} once the model is loaded
  GET  /v1/info      -> model + runtime versions
  POST /v1/analyze   -> multipart "file": detect faces + 512-d embeddings
"""

import logging
import os
import time
from contextlib import asynccontextmanager

import cv2
import numpy as np
from fastapi import FastAPI, File, HTTPException, Request, UploadFile

from .engine import FaceEngine

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
