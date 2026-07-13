#!/usr/bin/env python
"""
Not Actually Extinct — Persistent Detection Server
====================================================
Loads MegaDetector, SpeciesNet, and BioCLIP once at startup,
then serves HTTP requests from the C# host app.

Start with:
    uvicorn detection_server:app --host 127.0.0.1 --port 5050 --log-level info
  OR just run this file directly:
    python detection_server.py

Endpoints
---------
  GET  /health     — liveness check (returns {"status": "ready"} once models loaded)
  POST /detect     — run MegaDetector on uploaded image
  POST /classify   — crop + run SpeciesNet & BioCLIP, return results + crop bytes
"""

import sys
import json
import os
import io
import logging
import tempfile
import urllib.request
import base64
from contextlib import asynccontextmanager
from typing import List, Optional

import uvicorn
from fastapi import FastAPI, File, UploadFile, Form
from fastapi.responses import JSONResponse
from PIL import Image

# ── logging to stderr only (stdout reserved for uvicorn access log) ─────────
logging.basicConfig(
    stream=sys.stderr, level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s — %(message)s",
)
log = logging.getLogger("detector")

# ── MegaDetector model path ──────────────────────────────────────────────────
MODEL_URL  = "https://github.com/ecologize/CameraTraps/releases/download/v5.0/md_v5a.0.0.pt"
MODEL_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "md_v5a.0.0.pt")

# ── Shared model holders ─────────────────────────────────────────────────────
_models: dict = {}

# ── BioCLIP instance cache (keyed by frozenset of candidates) ───────────────
_bioclip_cache: dict = {}


def _download_megadetector():
    if not os.path.exists(MODEL_PATH):
        log.info("Downloading MegaDetector model to %s — this may take a few minutes…", MODEL_PATH)
        urllib.request.urlretrieve(MODEL_URL, MODEL_PATH)
        log.info("MegaDetector download complete.")


def _get_bioclip(candidates: tuple):
    """Return a cached BioCLIP CustomLabelsClassifier for the given candidate list."""
    key = tuple(sorted(candidates))
    if key not in _bioclip_cache:
        from bioclip import CustomLabelsClassifier
        log.info("Initialising BioCLIP classifier for %d candidates…", len(key))
        _bioclip_cache[key] = CustomLabelsClassifier(list(candidates))
    return _bioclip_cache[key]


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Download and load all ML models exactly once at server startup."""
    import contextlib
    import io as _io

    # ── MegaDetector ────────────────────────────────────────────────────────
    _download_megadetector()
    log.info("Loading MegaDetector (torch.hub/yolov5)…")
    import torch
    with contextlib.redirect_stdout(_io.StringIO()):          # suppress hub banners
        md_model = torch.hub.load(
            "ultralytics/yolov5", "custom",
            path=MODEL_PATH, force_reload=False, verbose=False,
        )
    md_model.conf = 0.05   # permissive threshold; caller applies final cutoff
    _models["megadetector"] = md_model
    log.info("✓ MegaDetector ready.")

    # ── SpeciesNet ──────────────────────────────────────────────────────────
    log.info("Loading SpeciesNet classifier…")
    logging.getLogger("speciesnet").setLevel(logging.ERROR)
    logging.getLogger("absl").setLevel(logging.ERROR)
    from speciesnet import SpeciesNet, DEFAULT_MODEL
    from speciesnet.utils import prepare_instances_dict
    _models["speciesnet"] = SpeciesNet(DEFAULT_MODEL, components="classifier")
    _models["prepare_instances_dict"] = prepare_instances_dict
    log.info("✓ SpeciesNet ready.")

    # ── BioCLIP (lazy-initialised per candidate set in _get_bioclip) ────────
    log.info("BioCLIP will be initialised on first classify request.")
    log.info("=" * 50)
    log.info("All models loaded — server ready to accept requests.")
    log.info("=" * 50)

    yield   # ← server runs here

    _models.clear()
    _bioclip_cache.clear()
    log.info("Models released. Server shutting down.")


app = FastAPI(
    title="Not-Actually-Extinct Detection Server",
    description="Persistent MegaDetector + SpeciesNet + BioCLIP server for the MIP SDK camera-trap pipeline.",
    version="1.0.0",
    lifespan=lifespan,
)


# ── /health ──────────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    ready = "megadetector" in _models and "speciesnet" in _models
    return {"status": "ready" if ready else "loading"}


# ── /detect ──────────────────────────────────────────────────────────────────

@app.post("/detect")
async def detect(image: UploadFile = File(...)):
    """
    Run MegaDetector on the uploaded JPEG/PNG image.

    Returns:
        {
          "status": "success",
          "detections": [
            {"class_id": 0, "name": "animal", "confidence": 0.93,
             "bbox": [xmin, ymin, xmax, ymax]}  ← absolute pixels
          ]
        }
    """
    model = _models.get("megadetector")
    if model is None:
        return JSONResponse(status_code=503, content={"error": "MegaDetector not loaded yet."})

    data = await image.read()
    tmp_path = None
    try:
        with tempfile.NamedTemporaryFile(suffix=".jpg", delete=False) as tmp:
            tmp.write(data)
            tmp_path = tmp.name

        results = model(tmp_path)
        df = results.pandas().xyxy[0]
        detections = []
        for _, row in df.iterrows():
            detections.append({
                "class_id":   int(row["class"]),
                "name":       str(row["name"]),
                "confidence": float(row["confidence"]),
                "bbox":       [float(row["xmin"]), float(row["ymin"]),
                               float(row["xmax"]), float(row["ymax"])],
            })
        return {"status": "success", "detections": detections}

    except Exception as exc:
        log.exception("MegaDetector inference error")
        return JSONResponse(status_code=500, content={"error": str(exc)})
    finally:
        if tmp_path:
            try: os.unlink(tmp_path)
            except: pass


# ── /classify ─────────────────────────────────────────────────────────────────

@app.post("/classify")
async def classify(
    image:                UploadFile = File(...),
    xmin:                 float      = Form(...),
    ymin:                 float      = Form(...),
    xmax:                 float      = Form(...),
    ymax:                 float      = Form(...),
    target_species:       str        = Form(...),
    candidate_species:    str        = Form(...),   # JSON array
    country_code:         Optional[str] = Form(None),
    confidence_threshold: float      = Form(0.5),
):
    """
    Crop the animal region from the uploaded image, then run both classifiers.

    Returns:
        {
          "status": "success",
          "crop_b64": "<base64-encoded JPEG crop>",
          "speciesnet": {"top_species": "...", "confidence": 0.72},
          "bioclip": {
            "target_match": true,
            "target_score": 0.81,
            "top_species": "...",
            "top_score": 0.81
          }
        }
    """
    speciesnet  = _models.get("speciesnet")
    prepare_dict = _models.get("prepare_instances_dict")
    if speciesnet is None or prepare_dict is None:
        return JSONResponse(status_code=503, content={"error": "Classifier models not loaded yet."})

    data     = await image.read()
    full_tmp = None
    crop_tmp = None
    try:
        # ── Save uploaded image ───────────────────────────────────────────
        with tempfile.NamedTemporaryFile(suffix=".jpg", delete=False) as tmp:
            tmp.write(data)
            full_tmp = tmp.name
        crop_tmp = full_tmp.replace(".jpg", "_crop.jpg")

        # ── Crop ─────────────────────────────────────────────────────────
        img = Image.open(full_tmp).convert("RGB")
        w, h = img.size
        # Clamp bbox to image bounds
        box = (
            max(0, int(xmin)), max(0, int(ymin)),
            min(w, int(xmax)), min(h, int(ymax)),
        )
        crop = img.crop(box)
        crop.save(crop_tmp, "JPEG", quality=92)

        # ── SpeciesNet ────────────────────────────────────────────────────
        instances = prepare_dict(filepaths=[crop_tmp], country=country_code or None)
        preds = speciesnet.classify(instances_dict=instances)

        snet_top  = None
        snet_conf = 0.0
        if preds and crop_tmp in preds:
            cp = preds[crop_tmp]
            classes = cp.get("classifications", {}).get("classes", [])
            scores  = cp.get("classifications", {}).get("scores",  [])
            if classes and scores:
                snet_top  = classes[0]
                snet_conf = float(scores[0])

        # ── BioCLIP (cached per candidate set) ────────────────────────────
        candidates = json.loads(candidate_species)
        if not candidates:
            candidates = [target_species]

        bc = _get_bioclip(tuple(candidates))
        bc_results = bc.predict(crop_tmp)

        bc_top          = None
        bc_top_score    = 0.0
        bc_target_score = 0.0

        if bc_results:
            first = bc_results[0]
            label_key   = "classification" if "classification" in first else "label"
            bc_top       = first[label_key]
            bc_top_score = float(first["score"])
            for r in bc_results:
                if r[label_key] == target_species:
                    bc_target_score = float(r["score"])
                    break

        target_match = bc_target_score >= confidence_threshold

        # ── Return crop as base64 so C# can post it to Discord ───────────
        with open(crop_tmp, "rb") as f:
            crop_b64 = base64.b64encode(f.read()).decode("ascii")

        return {
            "status":    "success",
            "crop_b64":  crop_b64,
            "speciesnet": {"top_species": snet_top,  "confidence": snet_conf},
            "bioclip": {
                "target_match":  target_match,
                "target_score":  bc_target_score,
                "top_species":   bc_top,
                "top_score":     bc_top_score,
            },
        }

    except Exception as exc:
        log.exception("Classifier error")
        return JSONResponse(status_code=500, content={"error": str(exc)})
    finally:
        for p in (full_tmp, crop_tmp):
            if p:
                try: os.unlink(p)
                except: pass


# ── Entry point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    uvicorn.run(
        "detection_server:app",
        host="127.0.0.1",
        port=5050,
        log_level="info",
    )
