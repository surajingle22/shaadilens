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
