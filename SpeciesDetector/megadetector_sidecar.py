import sys
import json
import os
import urllib.request
import torch

# Suppress YOLOv5 console output to keep stdout clean for JSON parsing
import logging
logging.getLogger("yolov5").setLevel(logging.WARNING)

MODEL_URL = "https://github.com/ecologize/CameraTraps/releases/download/v5.0/md_v5a.0.0.pt"
MODEL_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "md_v5a.0.0.pt")

def download_model():
    if not os.path.exists(MODEL_PATH):
        # We write to stderr to not corrupt the JSON stdout stream
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
        
        # Load YOLOv5 model using torch hub, silencing verbose output
        # Setting _verbose=False prevents printing "Using cache found in..."
        sys.stderr.write("Loading MegaDetector model...\n")
        model = torch.hub.load('ultralytics/yolov5', 'custom', path=MODEL_PATH, force_reload=False, _verbose=False)
        
        # Run inference
        results = model(image_path)
        
        # Parse results. MegaDetector outputs:
        # Class 0: Animal
        # Class 1: Person
        # Class 2: Vehicle
        df = results.pandas().xyxy[0]
        
        detections = []
        for index, row in df.iterrows():
            # YOLO classes are 0-indexed. MD typical terminology uses 1=Animal, 2=Person, 3=Vehicle.
            detections.append({
                "class_id": int(row['class']),
                "name": row['name'],
                "confidence": float(row['confidence']),
                "bbox": [float(row['xmin']), float(row['ymin']), float(row['xmax']), float(row['ymax'])]
            })
            
        print(json.dumps({"status": "success", "detections": detections}))
        
    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)

if __name__ == "__main__":
    main()
