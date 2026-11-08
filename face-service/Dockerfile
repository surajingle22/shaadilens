# ---------- model stage: download buffalo_l (~280 MB) once, with retries ----------
# Done at BUILD time so the container works fully offline at the venue.
# Its own stage = Docker caches it; code changes never re-download the model.
FROM debian:bookworm-slim AS models
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/*
ARG BUFFALO_URL=https://github.com/deepinsight/insightface/releases/download/v0.7/buffalo_l.zip
RUN curl -fL --retry 10 --retry-all-errors --retry-delay 3 -C - -o /tmp/buffalo_l.zip "$BUFFALO_URL" \
 && mkdir -p /models/buffalo_l \
 && unzip -j /tmp/buffalo_l.zip '*.onnx' -d /models/buffalo_l \
 && rm /tmp/buffalo_l.zip \
 && ls -la /models/buffalo_l

# ---------- build stage: compile insightface's small C++ extension ----------
FROM python:3.11-slim AS build
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN apt-get update \
 && apt-get install -y --no-install-recommends g++ \
 && rm -rf /var/lib/apt/lists/*
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
COPY requirements.txt requirements-dev.txt ./
# numpy first so insightface's build step finds it
RUN pip install numpy==1.26.4 cython \
 && pip install -r requirements.txt -r requirements-dev.txt

# ---------- runtime stage ----------
FROM python:3.11-slim
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PATH="/opt/venv/bin:$PATH" \
    INSIGHTFACE_ROOT=/opt/insightface \
    MPLCONFIGDIR=/tmp/matplotlib
COPY --from=build /opt/venv /opt/venv
COPY --from=models /models /opt/insightface/models

WORKDIR /srv
COPY pytest.ini ./
COPY app ./app
COPY tests ./tests
COPY tools ./tools

RUN useradd --system --uid 10001 face && chown -R face /srv
USER face

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
