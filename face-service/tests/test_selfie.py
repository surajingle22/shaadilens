"""Selfie validation + batched-embedding equivalence."""

import cv2
import numpy as np
import pytest
from fastapi.testclient import TestClient
from insightface.data import get_image

from app.main import app
from app.selfie import SelfieRules, validate_selfie


@pytest.fixture(scope="module")
def client():
    with TestClient(app) as c:
        yield c


@pytest.fixture(scope="module")
def group_img():
    return get_image("t1")


def post_selfie(client, img, **form):
    ok, buf = cv2.imencode(".jpg", img)
    r = client.post("/v1/selfie", files={"file": ("s.jpg", buf.tobytes(), "image/jpeg")}, data=form)
    assert r.status_code == 200, r.text
    return r.json()


def face_crop(group_img, client, upscale: float):
    """Largest face of the group photo on a plain canvas, scaled by `upscale`."""
    ok, buf = cv2.imencode(".jpg", group_img)
    faces = client.post("/v1/analyze", files={"file": ("g.jpg", buf.tobytes(), "image/jpeg")}).json()["faces"]
    b = faces[0]["bbox"]
    pad = 0.35
    x1, y1 = int(max(0, b["x"] - b["w"] * pad)), int(max(0, b["y"] - b["h"] * pad))
    x2, y2 = int(b["x"] + b["w"] * (1 + pad)), int(b["y"] + b["h"] * (1 + pad))
    crop = cv2.resize(group_img[y1:y2, x1:x2], None, fx=upscale, fy=upscale, interpolation=cv2.INTER_CUBIC)
    canvas = np.full((max(480, crop.shape[0] + 80), max(480, crop.shape[1] + 80), 3), 200, np.uint8)
    canvas[40:40 + crop.shape[0], 40:40 + crop.shape[1]] = crop
    return canvas


def test_good_selfie_is_accepted_with_embedding(client, group_img):
    res = post_selfie(client, face_crop(group_img, client, upscale=2.0), max_yaw="60")
    assert res["ok"] is True, res
    assert len(res["embedding"]) == 512
    assert "embedding" not in res["face"]


def test_group_photo_is_rejected_as_multiple_faces(client, group_img):
    res = post_selfie(client, group_img)
    assert res["ok"] is False
    assert res["code"] == "multiple_faces"
    assert res["embedding"] is None


def test_no_face(client):
    res = post_selfie(client, np.full((480, 640, 3), 90, np.uint8))
    assert res == {"ok": False, "code": "no_face", "reason": res["reason"], "embedding": None}
    assert "face" in res["reason"]


def test_small_face_asks_to_move_closer(client, group_img):
    res = post_selfie(client, face_crop(group_img, client, upscale=0.5), max_yaw="60")
    assert res["code"] == "too_small"
    assert res["reason"].startswith("Move closer")


@pytest.mark.parametrize("face_overrides, code", [
    ({"pose": {"yaw": 45.0, "pitch": 0, "roll": 0}}, "turned"),
    ({"blur": 5.0}, "blurry"),
    ({"det_score": 0.5}, "low_confidence"),
])
def test_rules_in_isolation(face_overrides, code):
    face = {"bbox": {}, "kps": [], "det_score": 0.9, "embedding": [0.0] * 512,
            "pose": {"yaw": 0.0, "pitch": 0.0, "roll": 0.0}, "face_px": 200.0, "blur": 150.0}
    face.update(face_overrides)
    assert validate_selfie({"faces": [face]}, SelfieRules())["code"] == code


def test_batched_embeddings_match_insightface_per_face(client, group_img):
    """Our batched path must give the same vectors as FaceAnalysis.get() (one model call per face)."""
    engine = app.state.engine
    ours = sorted(engine._detect_and_embed(group_img), key=lambda f: f.bbox[0])
    reference = sorted(engine._app.get(group_img), key=lambda f: f.bbox[0])
    assert len(ours) == len(reference) >= 3
    for a, b in zip(ours, reference):
        assert float(a.normed_embedding @ b.normed_embedding) > 0.999
        assert a.pose is not None
