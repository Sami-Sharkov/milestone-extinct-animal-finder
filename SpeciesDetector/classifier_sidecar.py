import sys
import json
import os
import requests
from PIL import Image

def main():
    if len(sys.argv) < 6:
        print(json.dumps({"error": "Usage: python classifier_sidecar.py <image_path> <config_path> <xmin> <ymin> <xmax> <ymax>"}))
        sys.exit(1)
        
    image_path = sys.argv[1]
    config_path = sys.argv[2]
    try:
        xmin = float(sys.argv[3])
        ymin = float(sys.argv[4])
        xmax = float(sys.argv[5])
        ymax = float(sys.argv[6])
    except ValueError:
        print(json.dumps({"error": "Invalid bbox coordinates"}))
        sys.exit(1)

    if not os.path.exists(image_path):
        print(json.dumps({"error": f"Image file not found: {image_path}"}))
        sys.exit(1)

    if not os.path.exists(config_path):
        print(json.dumps({"error": f"Config file not found: {config_path}"}))
        sys.exit(1)

    with open(config_path, 'r') as f:
        config = json.load(f)

    try:
        # We suppress warnings to keep stdout clean for JSON
        import logging
        logging.getLogger().setLevel(logging.ERROR)
        
        # We import here so it doesn't slow down arg parsing/validation
        from speciesnet.scripts.run_model import run_model
        from pybioclip import BioClip
        
        # 1. Crop image
        img = Image.open(image_path)
        img_w, img_h = img.size
        # The bbox from MegaDetector is normalized [0, 1]
        abs_xmin = int(xmin * img_w)
        abs_ymin = int(ymin * img_h)
        abs_xmax = int(xmax * img_w)
        abs_ymax = int(ymax * img_h)
        
        crop_img = img.crop((abs_xmin, abs_ymin, abs_xmax, abs_ymax))
        
        crop_path = os.path.join(os.path.dirname(image_path), f"crop_{os.path.basename(image_path)}")
        if config.get("save_all_crops", True):
            crop_img.save(crop_path)
            
        # 2. SpeciesNet
        sys.stderr.write("Running SpeciesNet...\n")
        country = config.get("country_code", None)
        snet_preds = run_model([crop_path], country=country)
        # snet_preds is usually a list of dicts or similar, depending on the library version.
        # Assuming it returns a list of dictionaries with 'label' and 'score' or similar structure.
        # For this prototype we will fake a mock output if the API changes, but let's assume it returns a dict.
        snet_top = None
        snet_conf = 0.0
        if isinstance(snet_preds, list) and len(snet_preds) > 0:
            if hasattr(snet_preds[0], 'top_prediction'):
                snet_top = snet_preds[0].top_prediction.name
                snet_conf = snet_preds[0].top_prediction.score
            elif isinstance(snet_preds[0], dict) and 'predictions' in snet_preds[0]:
                snet_top = snet_preds[0]['predictions'][0]['name']
                snet_conf = snet_preds[0]['predictions'][0]['score']

        # 3. BioCLIP
        sys.stderr.write("Running BioCLIP...\n")
        bc = BioClip()
        candidates = config.get("candidate_species", [])
        if not candidates:
            candidates = [config.get("target_species", "Animal")]
            
        bc_results = bc.predict(crop_img, candidates)
        
        target_species = config.get("target_species")
        bc_top = None
        bc_top_score = 0.0
        bc_target_score = 0.0
        
        if bc_results:
            bc_top = bc_results[0]['label']
            bc_top_score = bc_results[0]['score']
            for res in bc_results:
                if res['label'] == target_species:
                    bc_target_score = res['score']
                    break

        target_match = bc_target_score >= config.get("confidence_threshold", 0.5)

        # 4. Discord Alert
        discord_sent = False
        webhook_url = config.get("discord_webhook_url", "")
        if target_match and webhook_url:
            sys.stderr.write("Sending Discord alert...\n")
            with open(crop_path, 'rb') as f:
                payload = {
                    "content": f"🚨 **TARGET SPECIES DETECTED!** 🚨\n**{target_species}** (BioCLIP Confidence: {bc_target_score:.1%})\nSpeciesNet guess: {snet_top} ({snet_conf:.1%})"
                }
                files = {"file": (os.path.basename(crop_path), f, "image/jpeg")}
                r = requests.post(webhook_url, data=payload, files=files)
                discord_sent = r.status_code in [200, 204]

        # 5. Output JSON
        result = {
            "status": "success",
            "crop_path": crop_path,
            "speciesnet": {
                "top_species": snet_top,
                "confidence": snet_conf
            },
            "bioclip": {
                "target_match": target_match,
                "target_score": bc_target_score,
                "top_species": bc_top,
                "top_score": bc_top_score
            },
            "discord_sent": discord_sent
        }
        
        print(json.dumps(result))

    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)

if __name__ == "__main__":
    main()
