"""Image-quality helpers.

Blur score = variance of the Laplacian.
The Laplacian is a second-derivative filter: it responds strongly to edges.
A sharp face has many crisp edges (eyelashes, hair, lips) -> high variance.
A blurry face is smooth -> low variance. Typical values on a 112x112 face crop:
  < 30   very blurry / motion blur
  30-100 soft
  > 100  sharp
"""

import cv2
import numpy as np

# Crops are resized to a fixed size first so the score doesn't depend on how
# big the face is in the photo (a large face would otherwise look "sharper").
_BLUR_CROP = 112


def blur_score(img_bgr: np.ndarray, x1: int, y1: int, x2: int, y2: int) -> float:
    """Laplacian variance of the face crop (higher = sharper)."""
    crop = img_bgr[max(y1, 0):max(y2, 0), max(x1, 0):max(x2, 0)]
    if crop.size == 0:
        return 0.0
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    gray = cv2.resize(gray, (_BLUR_CROP, _BLUR_CROP), interpolation=cv2.INTER_AREA)
    return float(cv2.Laplacian(gray, cv2.CV_64F).var())


# ---------------------------------------------------------------------------------------------
# Culling helpers (Phase 3)
# ---------------------------------------------------------------------------------------------

def image_sharpness(img_bgr: np.ndarray, size: int = 512) -> float:
    """Laplacian variance of the WHOLE photo, resized to `size` px on the long edge.

    Used for photos without faces (decor, venue, food). For photos with people the .NET side
    prefers the sharpest FACE instead: wedding photographers blur the background on purpose
    (shallow depth of field), so a whole-image score would call a perfect portrait "blurry".
    """
    h, w = img_bgr.shape[:2]
    scale = size / max(h, w)
    gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    if scale < 1:
        gray = cv2.resize(gray, (round(w * scale), round(h * scale)), interpolation=cv2.INTER_AREA)
    return float(cv2.Laplacian(gray, cv2.CV_64F).var())


def phash(img_bgr: np.ndarray) -> str:
    """64-bit perceptual hash ("pHash"), as 16 hex characters.

    Unlike SHA-256 (which changes completely if one pixel changes), a perceptual hash changes only a
    little when the picture changes a little. Recipe:
      1. greyscale, shrink to 32x32         -> throws away detail, keeps the overall layout
      2. DCT (the maths inside JPEG)        -> turns the image into "frequencies"
      3. keep the 8x8 lowest frequencies    -> the coarse structure: where the bright/dark masses are
      4. bit = 1 if above the median        -> 64 bits
    Two burst shots of the same moment differ in only a few bits; different scenes differ in ~32.
    The number of differing bits is the Hamming distance (computed on the .NET side).
    """
    gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    small = cv2.resize(gray, (32, 32), interpolation=cv2.INTER_AREA).astype(np.float32)
    low = cv2.dct(small)[:8, :8].flatten()
    median = np.median(low[1:])  # the first value (DC) is just the average brightness: leave it out of the median
    value = 0
    for bit in low > median:
        value = (value << 1) | int(bit)
    return f"{value:016x}"


# 68-point layout (iBUG 300-W): points 36-41 = one eye, 42-47 = the other, each in this order:
#   0 outer corner, 1-2 upper lid, 3 inner corner, 4-5 lower lid
_EYES_68 = (slice(36, 42), slice(42, 48))


def eye_aspect_ratio(landmarks_68: np.ndarray | None) -> float | None:
    """Eye aspect ratio (EAR), averaged over both eyes.

        EAR = (|p1-p5| + |p2-p4|) / (2 * |p0-p3|)    = lid opening / eye width

    Open eyes ~0.25-0.35, closed ~0.05-0.15 (Soukupova & Cech, 2016). It is a ratio, so it doesn't
    depend on how big the face is. Unreliable on side profiles: the .NET side ignores faces turned
    more than ~35 degrees and tiny faces.
    """
    if landmarks_68 is None or len(landmarks_68) < 68:
        return None
    pts = np.asarray(landmarks_68, dtype=np.float64)[:, :2]
    ratios = []
    for eye in _EYES_68:
        p = pts[eye]
        width = np.linalg.norm(p[0] - p[3])
        if width < 1e-6:
            return None
        ratios.append((np.linalg.norm(p[1] - p[5]) + np.linalg.norm(p[2] - p[4])) / (2 * width))
    return float(np.mean(ratios))
