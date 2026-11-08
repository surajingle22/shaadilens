"""Measure face matching on YOUR photos, to choose the thresholds.

Folder layout (put it anywhere, e.g. ./eval-data next to docker-compose.yml):

    eval-data/
      selfies/asha.jpg          one selfie per person (file name = person)
      selfies/bunty.jpg
      photos/IMG_001.jpg        any wedding photos
      photos/IMG_002.jpg
      labels.csv                who is in which photo:
                                    photo,people
                                    IMG_001.jpg,asha;bunty
                                    IMG_002.jpg,            <- nobody from the selfies

Run (inside the face-service image, which has the models):

    docker compose run --rm -v ./eval-data:/data face-service python tools/eval.py /data

What it does — exactly what ShaadiLens does, offline:
  1. embedding of each selfie, embeddings of every face in every photo
  2. score(person, photo) = best cosine similarity between the selfie and any face in the photo
  3. for each threshold t: the photo "matches" the person if score >= t, then compare with labels.csv:
       precision = of the photos we would show, how many really contain the person   (strangers = low)
       recall    = of the photos that contain the person, how many we would show     (missed = low)
       F1        = one number balancing both
Pick MatchThreshold near the best F1 (or higher if wrong photos bother you more than missed ones), and
ProbableThreshold a bit lower — "Might be you?" catches the near misses.
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # so "app" imports when run from anywhere
from app.engine import FaceEngine  # noqa: E402

IMAGE_EXT = {".jpg", ".jpeg", ".png", ".webp", ".bmp"}


def embeddings(engine: FaceEngine, path: Path, min_face_px: float, min_det: float) -> np.ndarray:
    img = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if img is None:
        print(f"  ! can't read {path.name}", file=sys.stderr)
        return np.empty((0, 512), np.float32)
    faces = [f for f in engine.analyze(img)["faces"] if f["face_px"] >= min_face_px and f["det_score"] >= min_det]
    return np.asarray([f["embedding"] for f in faces], np.float32).reshape(-1, 512)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("data", type=Path)
    ap.add_argument("--min-face-px", type=float, default=40)
    ap.add_argument("--min-det", type=float, default=0.6)
    ap.add_argument("--from", dest="t_from", type=float, default=0.20)
    ap.add_argument("--to", dest="t_to", type=float, default=0.70)
    ap.add_argument("--step", type=float, default=0.02)
    args = ap.parse_args()

    selfie_dir, photo_dir, labels_csv = args.data / "selfies", args.data / "photos", args.data / "labels.csv"
    for p in (selfie_dir, photo_dir, labels_csv):
        if not p.exists():
            sys.exit(f"missing {p} (see the folder layout at the top of tools/eval.py)")

    truth: dict[str, set[str]] = {}
    with labels_csv.open(newline="", encoding="utf-8-sig") as fh:
        for row in csv.DictReader(fh):
            truth[row["photo"].strip()] = {p.strip().lower() for p in (row.get("people") or "").split(";") if p.strip()}

    engine = FaceEngine()
    print("Selfies:")
    selfies: dict[str, np.ndarray] = {}
    for path in sorted(selfie_dir.iterdir()):
        if path.suffix.lower() not in IMAGE_EXT:
            continue
        e = embeddings(engine, path, 0, 0.5)
        if len(e) == 0:
            print(f"  ! no face in selfie {path.name} — skipped")
            continue
        selfies[path.stem.lower()] = e[0]  # the largest face
        print(f"  {path.stem}")

    print(f"Photos: analysing {len(truth)} labelled photos…")
    # scores[person][photo] = best similarity of the person's selfie with any face in the photo
    scores: dict[str, dict[str, float]] = {p: {} for p in selfies}
    for name in sorted(truth):
        path = photo_dir / name
        if not path.exists():
            print(f"  ! {name} is in labels.csv but not in photos/")
            continue
        faces = embeddings(engine, path, args.min_face_px, args.min_det)
        for person, ref in selfies.items():
            # Embeddings are L2-normalised, so the dot product IS the cosine similarity.
            scores[person][name] = float((faces @ ref).max()) if len(faces) else -1.0

    print()
    print(" threshold | shown | correct | missed | precision | recall |   F1")
    print("-----------+-------+---------+--------+-----------+--------+------")
    best = (0.0, 0.0)
    t = args.t_from
    while t <= args.t_to + 1e-9:
        tp = fp = fn = 0
        for person, per_photo in scores.items():
            for photo, s in per_photo.items():
                present = person in truth[photo]
                if s >= t and present: tp += 1
                elif s >= t: fp += 1
                elif present: fn += 1
        precision = tp / (tp + fp) if tp + fp else 1.0
        recall = tp / (tp + fn) if tp + fn else 1.0
        f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
        best = max(best, (f1, t))
        print(f"   {t:5.2f}   | {tp + fp:5d} | {tp:7d} | {fn:6d} |   {precision:6.1%}  | {recall:6.1%} | {f1:.3f}")
        t += args.step

    print()
    print(f"Best F1 {best[0]:.3f} at threshold {best[1]:.2f}. ShaadiLens defaults: Match 0.42, Probable 0.34.")
    print("Set them in admin → Overview → Face matching, then 'Apply to all guests'.")


if __name__ == "__main__":
    main()
