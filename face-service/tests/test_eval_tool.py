"""tools/eval.py on a tiny generated dataset: one group photo, one empty photo, a selfie cut from the group."""

import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np
from insightface.data import get_image


def test_eval_tool_reports_precision_recall(tmp_path: Path):
    group = get_image("t1")
    (tmp_path / "selfies").mkdir()
    (tmp_path / "photos").mkdir()
    # "Selfie": the first face region of the group photo, enlarged (same trick as the selfie tests).
    h, w = group.shape[:2]
    crop = group[int(h * 0.12):int(h * 0.55), int(w * 0.03):int(w * 0.27)]
    cv2.imwrite(str(tmp_path / "selfies" / "rachel.jpg"), cv2.resize(crop, None, fx=3, fy=3))
    cv2.imwrite(str(tmp_path / "photos" / "group.jpg"), group)
    cv2.imwrite(str(tmp_path / "photos" / "empty.jpg"), np.full((600, 800, 3), 128, np.uint8))
    (tmp_path / "labels.csv").write_text("photo,people\ngroup.jpg,rachel\nempty.jpg,\n", encoding="utf-8")

    tool = Path(__file__).resolve().parents[1] / "tools" / "eval.py"
    out = subprocess.run([sys.executable, str(tool), str(tmp_path)], capture_output=True, text=True, timeout=300)
    assert out.returncode == 0, out.stderr
    assert "precision" in out.stdout and "Best F1" in out.stdout
