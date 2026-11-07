# How the face matching works

This is written for a .NET developer who hasn't worked with computer vision before.

## 1. The big idea: faces → vectors → distance

We never compare *images*. We turn each face into a list of **512 numbers** (an *embedding*) and compare lists.

A neural network (ArcFace) was trained on millions of faces with one goal: photos of the **same person**
should produce vectors that point in nearly the **same direction**, and different people should point
in different directions. Lighting, expression or background shouldn't change the direction much.

```
 photo of Suraj (haldi)    ──► [0.03, -0.11, 0.07, … 512 values]  ─┐
                                                                    ├─ angle small → same person
 selfie of Suraj           ──► [0.04, -0.09, 0.06, … ]             ─┘
 photo of Uncle            ──► [-0.08, 0.02, 0.12, … ]   ← angle large → different person
```

### Cosine similarity
We **L2-normalise** each vector (scale it to length 1). Then the cosine of the angle between two vectors
is just their dot product:

```
similarity = a · b = Σ a[i]*b[i]      (range -1 .. 1)
```

Typical values with `buffalo_l`:

| similarity | meaning |
|---|---|
| 0.60 – 0.85 | almost certainly the same person |
| 0.42 – 0.60 | very likely the same person → **Auto** match (default threshold 0.42) |
| 0.34 – 0.42 | maybe (profile, heavy makeup, bad light) → **Probable** ("Might be you?") |
| < 0.30 | different people |

pgvector's `<=>` operator returns **cosine distance** = `1 - similarity`. So
`ORDER BY embedding <=> @selfie` = "most similar first".

## 2. What face-service does per photo

The model pack is InsightFace **`buffalo_l`** (free, runs on CPU via ONNX Runtime). We load 3 of its models:

| Step | Model file | Output |
|---|---|---|
| 1. Detect | `det_10g.onnx` (SCRFD) | Box + 5 keypoints (eyes, nose, mouth corners) + confidence `det_score` for each face |
| 2. Align | (math, no model) | Uses the 5 keypoints to rotate/scale each face into a standard 112×112 crop with eyes at fixed pixels |
| 3. Embed | `w600k_r50.onnx` (ArcFace, ResNet-50) | 512-d vector per face |
| 4. Pose | `1k3d68.onnx` | yaw / pitch / roll in degrees |

Plus our own quality signal:
- **blur** — variance of the Laplacian on the face crop (resized to 112×112). Sharp faces have many edges → high variance.
- **face_px** — `min(width, height)` of the face box. Faces under ~40 px give unreliable embeddings.

### Speed tricks
- Images larger than `MAX_SIDE` (1600 px) are downscaled before detection. The detector runs at 640×640
  anyway; this just saves time. Boxes are scaled back so they're in **original** image coordinates.
- Gender/age and 106-point landmark models in `buffalo_l` are **not loaded**.
- **Batched embeddings**: InsightFace's `FaceAnalysis.get()` runs ArcFace once per face. We align all faces
  first and run ArcFace **once** on the whole batch (`engine._detect_and_embed`). Same vectors (test checks
  cosine > 0.999), ~35% faster on a 6-face group photo (3.4 s → 2.2 s on an i5-8250U).

### API
`POST /v1/analyze` (multipart `file`) →
```json
{
  "width": 4000, "height": 6000, "elapsed_ms": 640.2,
  "faces": [{
    "bbox": {"x": 1210.4, "y": 880.0, "w": 310.2, "h": 402.7},
    "kps": [[x,y] × 5],
    "det_score": 0.86,
    "embedding": [512 floats, length 1],
    "pose": {"yaw": -8.1, "pitch": 4.0, "roll": 1.2},
    "face_px": 310.2,
    "blur": 182.5
  }]
}
```
Faces are sorted largest first.

### Selfie check — `POST /v1/selfie`
A selfie becomes the guest's **reference**: every wedding photo is compared against it, so a bad selfie means
missed or wrong matches. face-service rejects it early with a message the guest can act on:

| Check (on the largest face) | Rule (env var) | Message |
|---|---|---|
| any face at all | — | "We couldn't find a face…" |
| one person | 2nd face ≥ 60% of the 1st's size → reject | "Only one face please…" |
| big enough | `face_px ≥ 80` (`SELFIE_MIN_FACE_PX`) | "Move closer…" |
| detector confident | `det_score ≥ 0.7` | "Try better light and remove sunglasses." |
| frontal | `|yaw| ≤ MaxSelfieYaw` (event setting, 30°) | "Look straight at the camera." |
| sharp | blur ≥ 20 (`SELFIE_MIN_BLUR`) | "Too dark or blurry…" |

The API answers 422 with `{code, detail}` so the guest page can show the message and let them retry.
Before analysis, the .NET side normalises the selfie (upright, sRGB, ≤1280 px, metadata stripped).

## 3. Why matches sometimes fail (and what we do about it)

| Problem | Why | Mitigation |
|---|---|---|
| Side profile | Only one eye visible → alignment is poor | Pose filter on selfies; "Find more" query expansion; second selfie |
| Tiny faces in group shots | Few pixels → noisy embedding | `MinFaceSizePx` filter; admin review |
| Heavy makeup, veil, turban, sehra | Face partly covered | "That's me" adds the confirmed face as a new reference |
| Motion blur / low light | Edges lost | Blur score; Probable bucket instead of Auto |
| Look-alike relatives (siblings!) | Genuinely similar faces | Threshold tuning with `tools/eval.py`; "Not me" never shows it again |

**Query expansion ("Find more")**: a guest ends up with several reference vectors (selfie, second selfie,
confirmed matches). We search with **each** and keep the **max** similarity per face. A haldi-yellow
side-profile might be far from the selfie but close to a confirmed haldi photo.

## 4. Selfie search (`Infrastructure/Matching/PgvectorFaceMatcher.cs`)

```sql
-- for each of the guest's reference embeddings:
SELECT id, photo_id, embedding <=> @ref AS distance
FROM   "Faces"
WHERE  event_id = @event AND embedding <=> @ref <= 1 - ProbableThreshold   -- i.e. similarity ≥ 0.34
```
1. Per face, keep the **best** similarity over all references (query expansion).
2. Per photo, keep the best face → one `GuestMatch` row per guest × photo. A group photo gets a row for
   **every** guest found in it.
3. similarity ≥ `MatchThreshold` (0.42) → **Auto** ("Your photos"); otherwise → **Probable** ("Might be you?").
4. The guest's own decisions win: **Confirmed** / **Rejected** rows are never touched by a re-search.
5. Auto/Probable rows that no longer qualify (e.g. admin raised the threshold) are removed.

Runs right after registration (~0.4 s) and again when the guest taps **Check for new photos**.

**Exact scan, not the HNSW index.** HNSW is *approximate*: combined with a `WHERE event_id = …` filter it can
silently return fewer neighbours than exist. Comparing a selfie with every face of one wedding (≤ ~100k
faces) is a plain sequential scan of a few tens of milliseconds and never misses a match — the better trade-off
here. The HNSW indexes are kept for "nearest N" queries (`ORDER BY … LIMIT`) such as the People view.

**Real result** (sample group photo, 6 people, uploaded twice; selfie = one guest cropped & enlarged):
2 Auto matches (both copies), 0 false positives — the closest *other* person scored 0.196.

## 5. Where it lives

- `Face.Embedding vector(512)` and `GuestReference.Embedding vector(512)` in Postgres.
- **Forward search** (new selfie → all faces, Phase 1) and **reverse search** (new face → all guest
  references, Phase 2) use the same math.

## 6. Try it yourself

```powershell
curl.exe -F "file=@C:\path\to\photo.jpg" http://localhost:8000/v1/analyze
```
or in Swagger (log in as admin first): `POST /api/v1/diagnostics/face-analyze` (summary without the 512 numbers).
Test a selfie: `curl.exe -F "file=@selfie.jpg" http://localhost:8000/v1/selfie`
