"""face-service tests. Run inside the container:
    docker compose run --rm face-service pytest -q

Sample images (no third-party photos in the repo):
  group    -> insightface's bundled sample "t1.jpg" (a group photo)
  one face -> the largest face cropped out of t1 and pasted on a plain canvas
  no face  -> a plain grey image
"""

import cv2
import numpy as np
import pytest
from fastapi.testclient import TestClient
from insightface.data import get_image

from app.main import app


@pytest.fixture(scope="module")
def client():
    with TestClient(app) as c:  # `with` runs the lifespan -> loads the model once
        yield c


@pytest.fixture(scope="module")
def group_img() -> np.ndarray:
    return get_image("t1")  # BGR numpy array


def post_image(client, img: np.ndarray):
    ok, buf = cv2.imencode(".jpg", img)
    assert ok
    r = client.post("/v1/analyze", files={"file": ("x.jpg", buf.tobytes(), "image/jpeg")})
    assert r.status_code == 200, r.text
    return r.json()


def one_face_image(group_img, group_result) -> np.ndarray:
    f = group_result["faces"][0]  # largest face
    b = f["bbox"]
    pad = 0.35
    x1 = int(max(0, b["x"] - b["w"] * pad)); y1 = int(max(0, b["y"] - b["h"] * pad))
    x2 = int(b["x"] + b["w"] * (1 + pad));   y2 = int(b["y"] + b["h"] * (1 + pad))
    crop = group_img[y1:y2, x1:x2]
    crop = cv2.resize(crop, None, fx=2.0, fy=2.0, interpolation=cv2.INTER_CUBIC)
    canvas = np.full((max(640, crop.shape[0] + 100), max(640, crop.shape[1] + 100), 3), 200, np.uint8)
    canvas[50:50 + crop.shape[0], 50:50 + crop.shape[1]] = crop
    return canvas


def test_health_and_info(client):
    assert client.get("/health").json() == {"status": "ok"}
    info = client.get("/v1/info").json()
    assert info["model"] == "buffalo_l"
    assert info["embedding_dim"] == 512


def test_group_photo_finds_several_normalised_faces(client, group_img):
    res = post_image(client, group_img)
    assert res["width"] == group_img.shape[1] and res["height"] == group_img.shape[0]
    assert len(res["faces"]) >= 3
    for f in res["faces"]:
        emb = np.array(f["embedding"])
        assert emb.shape == (512,)
        assert np.linalg.norm(emb) == pytest.approx(1.0, abs=1e-3)  # L2-normalised
        assert len(f["kps"]) == 5
        assert f["det_score"] > 0.5


def test_no_face(client):
    res = post_image(client, np.full((480, 640, 3), 128, np.uint8))
    assert res["faces"] == []


def test_one_face_and_same_person_matches(client, group_img):
    group = post_image(client, group_img)
    single = post_image(client, one_face_image(group_img, group))
    assert len(single["faces"]) == 1

    # Same person, different crop/scale -> embeddings should be very similar.
    # Cosine similarity of unit vectors is just the dot product.
    a = np.array(group["faces"][0]["embedding"])
    b = np.array(single["faces"][0]["embedding"])
    assert float(a @ b) > 0.6

    # ...and clearly less similar to everyone else in the group photo.
    others = [float(np.array(f["embedding"]) @ b) for f in group["faces"][1:]]
    assert max(others) < 0.3


def test_large_image_bbox_is_in_original_coordinates(client, group_img):
    """Images above MAX_SIDE are downscaled internally; boxes must still match the original."""
    small = post_image(client, group_img)
    big_img = cv2.resize(group_img, None, fx=3.0, fy=3.0, interpolation=cv2.INTER_CUBIC)
    big = post_image(client, big_img)
    assert len(big["faces"]) == len(small["faces"])
    bs, bb = small["faces"][0]["bbox"], big["faces"][0]["bbox"]
    assert bb["x"] == pytest.approx(bs["x"] * 3, rel=0.1, abs=10)
    assert bb["w"] == pytest.approx(bs["w"] * 3, rel=0.1, abs=10)


def test_rejects_non_image(client):
    r = client.post("/v1/analyze", files={"file": ("x.txt", b"hello", "text/plain")})
    assert r.status_code == 400
