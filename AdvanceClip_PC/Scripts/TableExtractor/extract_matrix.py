import sys
import os
import json

try:
    import cv2
    import numpy as np
    import pytesseract
except ImportError:
    print("ERROR: MISSING_DEPENDENCIES")
    sys.exit(1)

# Ensure Tesseract can find the executable explicitly on Windows!
tess_path = r"C:\Program Files\Tesseract-OCR\tesseract.exe"
if os.path.exists(tess_path):
    pytesseract.pytesseract.tesseract_cmd = tess_path
else:
    print("ERROR: TESSERACT_NOT_INSTALLED")
    sys.exit(1)

def extract_table(img_path):
    if not os.path.exists(img_path):
        print("ERROR: FILE_NOT_FOUND")
        sys.exit(1)

    # 1. Image Preprocessing
    img = cv2.imread(img_path)
    if img is None:
        print("ERROR: INVALID_IMAGE")
        sys.exit(1)

    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    thresh = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY_INV, 11, 2)

    # 2. Extract Lines (Morphology)
    scale = 15
    horiz_size = max(1, thresh.shape[1] // scale)
    vert_size = max(1, thresh.shape[0] // scale)

    horiz_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (horiz_size, 1))
    horizontal_lines = cv2.morphologyEx(thresh, cv2.MORPH_OPEN, horiz_kernel)

    vert_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (1, vert_size))
    vertical_lines = cv2.morphologyEx(thresh, cv2.MORPH_OPEN, vert_kernel)

    # Combine grid
    grid = cv2.addWeighted(horizontal_lines, 0.5, vertical_lines, 0.5, 0.0)
    _, grid = cv2.threshold(grid, 50, 255, cv2.THRESH_BINARY)

    # 3. Find Contours (Cells)
    contours, _ = cv2.findContours(grid, cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
    
    bounding_boxes = []
    for c in contours:
        x, y, w, h = cv2.boundingRect(c)
        if w > 20 and h > 10 and w < img.shape[1] * 0.9: # Filter noise and outer box
            bounding_boxes.append((x, y, w, h))

    if len(bounding_boxes) < 4:
        print("ERROR: NO_GRID_DETECTED")
        sys.exit(0)

    # 4. Sort and build Matrix
    # Sort top-to-bottom
    bounding_boxes = sorted(bounding_boxes, key=lambda b: b[1])
    
    # Cluster into rows based on Y similarity
    rows = []
    current_row = []
    current_y = bounding_boxes[0][1]

    for box in bounding_boxes:
        # If Y difference is small, it's the same row
        if abs(box[1] - current_y) < (box[3] / 2):
            current_row.append(box)
        else:
            # Sort current row left-to-right
            current_row = sorted(current_row, key=lambda b: b[0])
            rows.append(current_row)
            current_row = [box]
            current_y = box[1]

    if current_row:
        current_row = sorted(current_row, key=lambda b: b[0])
        rows.append(current_row)

    # 5. Extract Text via Tesseract
    results = {}
    config = "--psm 6"

    for row_idx, row in enumerate(rows):
        for col_idx, box in enumerate(row):
            x, y, w, h = box
            roi = img[y:y+h, x:x+w]
            
            # Upscale ROI to improve Tesseract accuracy on tiny cells
            roi = cv2.resize(roi, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
            
            data = pytesseract.image_to_data(roi, config=config, output_type=pytesseract.Output.DICT)
            
            words = []
            confidences = []
            for i in range(len(data['text'])):
                txt = data['text'][i].strip()
                cnf = int(data['conf'][i])
                if txt and cnf != -1:
                    words.append(txt)
                    confidences.append(cnf)

            final_text = " ".join(words)
            final_conf = (sum(confidences) / len(confidences) / 100.0) if confidences else 1.0

            results[f"({row_idx},{col_idx})"] = {
                "text": final_text,
                "conf": round(final_conf, 2)
            }

    # Output JSON exactly natively formatted for C# interception
    print(json.dumps(results))

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("ERROR: MISSING_ARGS")
        sys.exit(1)
    extract_table(sys.argv[1])
