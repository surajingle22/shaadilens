"""Selfie validation.

A selfie becomes the guest's "reference" face: every wedding photo is compared against it.
A bad selfie (too far, turned away, blurry, two people) gives a bad reference and therefore
missed or wrong matches — so we reject it early with a friendly, actionable message.
"""

from dataclasses import dataclass
import os


@dataclass(frozen=True)
class SelfieRules:
    min_face_px: float = float(os.environ.get("SELFIE_MIN_FACE_PX", "80"))
    min_det_score: float = float(os.environ.get("SELFIE_MIN_DET_SCORE", "0.7"))
    max_yaw: float = float(os.environ.get("SELFIE_MAX_YAW", "30"))
    # Laplacian variance on a 112x112 face crop; phone selfies are typically 60-400.
    min_blur: float = float(os.environ.get("SELFIE_MIN_BLUR", "20"))
    # A second face at least this fraction of the main face's size = "two people in the selfie".
    second_face_ratio: float = 0.6


def validate_selfie(analysis: dict, rules: SelfieRules) -> dict:
    """Returns {ok, code, reason, face?, embedding?}. Faces in `analysis` are sorted largest first."""
    faces = analysis["faces"]

    def fail(code: str, reason: str, face: dict | None = None) -> dict:
        result = {"ok": False, "code": code, "reason": reason, "embedding": None}
        if face is not None:
            result["face"] = _summary(face)
        return result

    if not faces:
        return fail("no_face", "We couldn't find a face. Make sure your face is in the frame and well lit.")

    main = faces[0]
    if len(faces) > 1 and faces[1]["face_px"] >= rules.second_face_ratio * main["face_px"]:
        return fail("multiple_faces", "Only one face please — take the selfie on your own.", main)
    if main["face_px"] < rules.min_face_px:
        return fail("too_small", "Move closer — your face should fill most of the oval.", main)
    if main["det_score"] < rules.min_det_score:
        return fail("low_confidence", "We can't see your face clearly. Try better light and remove sunglasses.", main)
    if abs(main["pose"]["yaw"]) > rules.max_yaw:
        return fail("turned", "Look straight at the camera.", main)
    if main["blur"] < rules.min_blur:
        return fail("blurry", "Too dark or blurry — hold the phone still and face the light.", main)

    return {"ok": True, "code": "ok", "reason": None, "face": _summary(main), "embedding": main["embedding"]}


def _summary(face: dict) -> dict:
    """Face info without the embedding (for showing what went wrong)."""
    return {k: v for k, v in face.items() if k != "embedding"}
