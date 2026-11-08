"""People clustering: "which faces belong to the same person?" without knowing anyone's name.

DBSCAN (Density-Based Spatial Clustering of Applications with Noise), in plain words:
  - Two faces are "neighbours" if their cosine distance is <= eps (distance = 1 - similarity).
    With eps = 0.5 that means similarity >= 0.5 — clearly the same person for ArcFace embeddings.
  - A face with at least `min_samples` neighbours (itself included) is a "core" face.
  - Core faces that are neighbours of each other — and their neighbours — form one cluster (one person).
  - Faces that belong to no cluster are "noise" (label -1): people seen once, blurry or tiny faces.

Why DBSCAN and not k-means? We don't know how many people were at the wedding, and DBSCAN doesn't
need that number. It also leaves odd faces out instead of forcing them into a wrong group.

Cost: compares every face with every face, in chunks (scikit-learn's brute-force neighbour search).
15,000 faces ≈ 112 M distances — a few seconds to a minute on a laptop CPU.
"""

from __future__ import annotations

import numpy as np
from sklearn.cluster import DBSCAN


def cluster_embeddings(embeddings: np.ndarray, eps: float = 0.5, min_samples: int = 3) -> np.ndarray:
    """embeddings: (N, 512) float32, L2-normalised. Returns N labels: 0..K-1, or -1 for noise."""
    if embeddings.ndim != 2:
        raise ValueError("embeddings must be a 2-D array (N, dim)")
    if len(embeddings) == 0:
        return np.empty(0, dtype=np.int64)
    if len(embeddings) < min_samples:
        return np.full(len(embeddings), -1, dtype=np.int64)
    model = DBSCAN(eps=eps, min_samples=min_samples, metric="cosine", algorithm="brute", n_jobs=-1)
    return model.fit_predict(embeddings)
