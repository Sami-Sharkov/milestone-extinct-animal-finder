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
        from speciesnet import SpeciesNet
        from speciesnet.utils import prepare_instances_dict
        
        # 1. Crop image
        img = Image.open(image_path).convert("RGB")
        img_w, img_h = img.size
        # The bbox from MegaDetector is already in absolute pixels
        abs_xmin = int(xmin)
        abs_ymin = int(ymin)
        abs_xmax = int(xmax)
        abs_ymax = int(ymax)
        
        crop_img = img.crop((abs_xmin, abs_ymin, abs_xmax, abs_ymax))
        
        crop_path = os.path.join(os.path.dirname(image_path), f"crop_{os.path.basename(image_path)}")
        crop_img.save(crop_path)
            
        # 2. SpeciesNet
        sys.stderr.write("Running SpeciesNet...\n")
        country = config.get("country_code", None)
        instances_dict = prepare_instances_dict(filepaths=[crop_path], country=country)
        
        from speciesnet import DEFAULT_MODEL
        model = SpeciesNet(DEFAULT_MODEL, components="classifier")
        preds = model.classify(instances_dict=instances_dict)
        
        snet_top = None
        snet_conf = 0.0
        
        if preds and crop_path in preds:
            crop_preds = preds[crop_path]
            if "classifications" in crop_preds:
                classes = crop_preds["classifications"].get("classes", [])
                scores = crop_preds["classifications"].get("scores", [])
                if classes and scores:
                    snet_top = classes[0]
                    snet_conf = scores[0]

        # 3. BioCLIP
        sys.stderr.write("Running BioCLIP...\n")
        from bioclip import CustomLabelsClassifier
        candidates = config.get("candidate_species", [])
        if not candidates:
            candidates = [config.get("target_species", "Animal")]
            
        bc = CustomLabelsClassifier(candidates)
        # bioclip predict expects a file path or list of paths
        bc_results = bc.predict(crop_path)
        
        target_species = config.get("target_species")
        bc_top = None
        bc_top_score = 0.0
        bc_target_score = 0.0
        
        if bc_results:
            # Check if it returns a list of dicts (like [{"classification": "cat", "score": 0.9}])
            # bioclip typically returns a list of dicts with 'classification' or 'label' and 'score'
            # We'll handle both just in case.
            first_res = bc_results[0]
            label_key = 'classification' if 'classification' in first_res else 'label'
            
            bc_top = first_res[label_key]
            bc_top_score = first_res['score']
            for res in bc_results:
                if res[label_key] == target_species:
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
