"""InsightFace wrapper.

What happens for one photo (see docs/AI-PIPELINE.md for the long version):

1. DETECTION  (model: det_10g.onnx, "SCRFD")
   Finds every face -> bounding box + 5 keypoints (eyes, nose, mouth corners)
   + a confidence score (det_score). The detector looks at a 640x640 version
   of the image, so very large photos are downscaled first for speed.

2. ALIGNMENT
   Using the 5 keypoints, each face is rotated/scaled into a standard
   112x112 "passport" crop, so eyes always land on the same pixels.

3. EMBEDDING  (model: w600k_r50.onnx, "ArcFace")
   The aligned crop goes through a ResNet-50 that outputs 512 numbers.
   The network was trained so that photos of the SAME person produce vectors
   pointing in nearly the same direction. We L2-normalise the vector
   (length 1), so cosine similarity is just a dot product:
       same person   ~ 0.45 .. 0.80
       different     ~ -0.1 .. 0.30
   The .NET side stores these in pgvector and compares them there.

4. POSE (model: 1k3d68.onnx) gives pitch / yaw / roll in degrees.
   Large |yaw| = side profile, which makes embeddings less reliable.
"""

import logging
import os
import time

import cv2
import insightface
import numpy as np
import onnxruntime
from insightface.app import FaceAnalysis

from .quality import blur_score

log = logging.getLogger("face-service.engine")

MODEL_NAME = "buffalo_l"


class FaceEngine:
    def __init__(self, det_size: int = 640, max_side: int = 1600, root: str | None = None):
        self.det_size = det_size
        self.max_side = max_side
        root = root or os.environ.get("INSIGHTFACE_ROOT", "~/.insightface")
        t0 = time.perf_counter()
        # CPU only. On a GPU machine you could add "CUDAExecutionProvider" first.
        # buffalo_l also ships gender/age and 106-point landmark models; we skip them to save CPU.
        self._app = FaceAnalysis(
            name=MODEL_NAME,
            root=root,
            providers=["CPUExecutionProvider"],
            allowed_modules=["detection", "recognition", "landmark_3d_68"],
        )
        self._app.prepare(ctx_id=-1, det_size=(det_size, det_size))
        log.info("Loaded %s in %.1fs (det_size=%d, max_side=%d)",
                 MODEL_NAME, time.perf_counter() - t0, det_size, max_side)

    def info(self) -> dict:
        return {
            "model": MODEL_NAME,
            "insightface": insightface.__version__,
            "onnxruntime": onnxruntime.__version__,
            "providers": ["CPUExecutionProvider"],
            "det_size": self.det_size,
            "max_side": self.max_side,
            "embedding_dim": 512,
        }

    def analyze(self, img_bgr: np.ndarray) -> dict:
        """Detect all faces. Coordinates are returned in ORIGINAL image pixels."""
        h, w = img_bgr.shape[:2]

        # Downscale big camera images (e.g. 6000x4000) before detection.
        # The detector works at 640x640 anyway, so this mostly saves decode/resize time.
        scale = 1.0
        work = img_bgr
        if max(h, w) > self.max_side:
            scale = self.max_side / max(h, w)
            work = cv2.resize(img_bgr, (round(w * scale), round(h * scale)), interpolation=cv2.INTER_AREA)

        faces = self._app.get(work)

        out = []
        for f in faces:
            # Map bbox / keypoints back to original coordinates and clamp to the image.
            x1, y1, x2, y2 = (f.bbox / scale).tolist()
            x1, y1 = max(0.0, x1), max(0.0, y1)
            x2, y2 = min(float(w), x2), min(float(h), y2)
            bw, bh = x2 - x1, y2 - y1
            kps = (f.kps / scale).tolist()
            pitch, yaw, roll = (float(v) for v in f.pose) if getattr(f, "pose", None) is not None else (0.0, 0.0, 0.0)

            out.append({
                "bbox": {"x": x1, "y": y1, "w": bw, "h": bh},
                "kps": [[float(px), float(py)] for px, py in kps],
                "det_score": float(f.det_score),
                # normed_embedding = embedding / ||embedding||  (length 1)
                "embedding": f.normed_embedding.astype(np.float32).tolist(),
                "pose": {"yaw": yaw, "pitch": pitch, "roll": roll},
                "face_px": float(min(bw, bh)),
                "blur": blur_score(img_bgr, int(x1), int(y1), int(x2), int(y2)),
            })

        # Biggest faces first — handy for selfies and for "main subject" logic.
        out.sort(key=lambda f: f["face_px"], reverse=True)
        return {"width": w, "height": h, "faces": out}
