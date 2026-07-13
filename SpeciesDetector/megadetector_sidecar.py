import sys
import json
import os
import urllib.request

# ---------------------------------------------------------------------------
# Silence EVERYTHING that normally goes to stdout before we import PyTorch /
# YOLOv5.  Many libraries (Ultralytics, tqdm, etc.) print progress bars and
# banners to *stdout* even when verbose=False.  We redirect stdout → stderr
# for the noisy import phase, then restore it so our single JSON line is the
# only thing that ends up in the temp-file the C# host reads.
# ---------------------------------------------------------------------------
_real_stdout = sys.stdout
sys.stdout = sys.stderr  # all noisy prints go to stderr (discarded by cmd 2>nul)

import torch

sys.stdout = _real_stdout  # restore — from here on, print() → temp file

MODEL_URL = "https://github.com/ecologize/CameraTraps/releases/download/v5.0/md_v5a.0.0.pt"
MODEL_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "md_v5a.0.0.pt")

def download_model():
    if not os.path.exists(MODEL_PATH):
        sys.stderr.write(f"Downloading MegaDetector model to {MODEL_PATH} (this may take a few minutes)...\n")
        urllib.request.urlretrieve(MODEL_URL, MODEL_PATH)
        sys.stderr.write("Download complete.\n")

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "No image path provided."}))
        sys.exit(1)
        
    image_path = sys.argv[1]
    
    if not os.path.exists(image_path):
        print(json.dumps({"error": f"Image file not found: {image_path}"}))
        sys.exit(1)

    try:
        download_model()
        
        sys.stderr.write("Loading MegaDetector model...\n")

        # Redirect stdout → stderr again while loading the model, because
        # torch.hub can still print "Using cache found in …" lines.
        sys.stdout = sys.stderr
        try:
            model = torch.hub.load(
                'ultralytics/yolov5', 'custom',
                path=MODEL_PATH,
                force_reload=False,
                verbose=False,
            )
            # Run inference (tqdm progress bars also go to stderr now)
            results = model(image_path)
        finally:
            sys.stdout = _real_stdout  # restore before any print()
        
        # Parse results. MegaDetector class map (0-indexed):
        # 0 = Animal, 1 = Person, 2 = Vehicle
        df = results.pandas().xyxy[0]
        
        detections = []
        for _, row in df.iterrows():
            detections.append({
                "class_id": int(row['class']),
                "name": row['name'],
                "confidence": float(row['confidence']),
                "bbox": [float(row['xmin']), float(row['ymin']),
                         float(row['xmax']), float(row['ymax'])]
            })
            
        print(json.dumps({"status": "success", "detections": detections}))
        
    except Exception as e:
        sys.stdout = _real_stdout  # make sure we can still print on error
        print(json.dumps({"error": str(e)}))
        sys.exit(1)

if __name__ == "__main__":
    main()
