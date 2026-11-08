"""Clustering tests with synthetic embeddings (no images needed)."""

import numpy as np
from fastapi.testclient import TestClient

from app.cluster import cluster_embeddings
from app.main import app


def people(n_people=3, faces_each=6, noise=4, dim=512, seed=7):
    """Each "person" = a random direction + small jitter (like photos of one face); noise = random faces."""
    rng = np.random.default_rng(seed)
    rows, truth = [], []
    for p in range(n_people):
        centre = rng.normal(size=dim)
        for _ in range(faces_each):
            rows.append(centre + rng.normal(scale=0.35, size=dim))
            truth.append(p)
    for _ in range(noise):
        rows.append(rng.normal(size=dim))
        truth.append(-1)
    x = np.asarray(rows, dtype=np.float32)
    return x / np.linalg.norm(x, axis=1, keepdims=True), np.asarray(truth)


def test_dbscan_finds_each_person_and_leaves_strangers_out():
    x, truth = people()
    labels = cluster_embeddings(x, eps=0.5, min_samples=3)
    assert labels.max() + 1 == 3
    for p in range(3):
        assert len(set(labels[truth == p])) == 1, "one person = one cluster"
    assert (labels[truth == -1] == -1).all(), "random faces are noise"


def test_cluster_endpoint_takes_raw_float32():
    x, _ = people()
    with TestClient(app) as client:
        r = client.post("/v1/cluster?eps=0.5&min_samples=3", content=x.astype("<f4").tobytes(),
                        headers={"content-type": "application/octet-stream"})
        assert r.status_code == 200, r.text
        body = r.json()
        assert body["clusters"] == 3 and body["noise"] == 4 and len(body["labels"]) == len(x)

        bad = client.post("/v1/cluster", content=b"\x00" * 10, headers={"content-type": "application/octet-stream"})
        assert bad.status_code == 400
