"""Culling helpers: eye aspect ratio, whole-photo sharpness, perceptual hash."""

import cv2
import numpy as np
from insightface.data import get_image

from app.quality import eye_aspect_ratio, image_sharpness, phash


def ham(a: str, b: str) -> int:
    return bin(int(a, 16) ^ int(b, 16)).count("1")


def landmarks(lid_open: float) -> np.ndarray:
    """68 points where only the eyes matter: width 30 px, lids `lid_open` px apart."""
    pts = np.zeros((68, 3))
    for start, x0 in ((36, 100), (42, 160)):
        h = lid_open / 2
        pts[start:start + 6, :2] = [(x0, 100), (x0 + 10, 100 - h), (x0 + 20, 100 - h),
                                    (x0 + 30, 100), (x0 + 20, 100 + h), (x0 + 10, 100 + h)]
    return pts


def test_ear_open_vs_closed():
    assert abs(eye_aspect_ratio(landmarks(9)) - 0.3) < 1e-6   # 9 / 30
    assert eye_aspect_ratio(landmarks(2)) < 0.1
    assert eye_aspect_ratio(None) is None
    assert eye_aspect_ratio(np.zeros((5, 2))) is None


def test_phash_is_stable_for_small_changes_and_differs_between_scenes():
    img = get_image("t1")
    h = phash(img)
    assert len(h) == 16
    smaller_brighter = cv2.convertScaleAbs(cv2.resize(img, None, fx=0.5, fy=0.5), alpha=1.2, beta=10)
    assert ham(h, phash(smaller_brighter)) <= 6
    assert ham(h, phash(cv2.GaussianBlur(img, (0, 0), 2))) <= 6
    assert ham(h, phash(cv2.flip(img, 1))) > 12  # mirrored = a different picture


def test_sharpness_drops_when_blurred():
    img = get_image("t1")
    assert image_sharpness(cv2.GaussianBlur(img, (0, 0), 3)) < image_sharpness(img) / 3
