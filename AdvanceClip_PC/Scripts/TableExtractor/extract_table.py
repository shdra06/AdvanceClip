import sys
import os

try:
    import google.generativeai as genai
    import PIL.Image
except ImportError:
    print("<p><b>Runtime Error:</b> Required Python packages missing.</p>")
    print("<p>Please run: <code>pip install google-generativeai pillow</code> inside the Scripts folder!</p>")
    sys.exit(1)

# ============== CONFIGURATION =================
# Drop your Gemini API key below! 
# Get one free at: https://aistudio.google.com/app/apikey
GOOGLE_API_KEY = "PLACEHOLDER_INSERT_KEY_HERE"
# ==============================================

def main():
    if len(sys.argv) < 2:
        print("<p><b>Error:</b> No image path provided by the C# Application dispatcher.</p>")
        sys.exit(1)

    image_path = sys.argv[1]
    
    if not os.path.exists(image_path):
        print(f"<p><b>Data Error:</b> Image does not exist physically on disk at <i>{image_path}</i></p>")
        sys.exit(1)

    if GOOGLE_API_KEY == "PLACEHOLDER_INSERT_KEY_HERE":
        print("<p><b>Authentication Warning:</b> Google Gemini API Key has not been configured in <code>extract_table.py</code>!</p>")
        print("<p>Please open the script and paste your API key to activate the OCR Engine.</p>")
        sys.exit(0)

    try:
        genai.configure(api_key=GOOGLE_API_KEY)
        model = genai.GenerativeModel('gemini-1.5-pro')
        
        img = PIL.Image.open(image_path)
        
        prompt = (
            "You are a highly advanced OCR Data Extraction assistant designed by Google DeepMind. "
            "Examine this visual element. If there is tabular data, spreadsheets, or ordered lists visible in the image, "
            "extract the data structure perfectly and wrap it EXCLUSIVELY inside a valid raw HTML <table> markup tag. "
            "IMPORTANT: Do not output any markdown formatting (like ```html). Output only the pure <table> element string. "
            "Ensure the output utilizes standard <tr>, <th>, and <td> definitions."
        )

        response = model.generate_content([prompt, img])
        
        # Scrape residual markdown artifacts out if Gemini disobeys format locks
        result_text = response.text.replace("```html", "").replace("```", "").strip()
        
        # Dispatch the HTML directly sequentially back into the C# memory pipes
        print(result_text)
        
    except Exception as e:
        print(f"<p><b>Generative AI Vision Exception:</b> {str(e)}</p>")

if __name__ == "__main__":
    main()
