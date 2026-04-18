import sys
import os
import ctypes

def main():
    if len(sys.argv) < 2:
        print("[ERROR] No file path provided.")
        sys.exit(1)
        
    file_path = sys.argv[1]
    
    if not os.path.exists(file_path):
        print(f"[ERROR] File not found: {file_path}")
        sys.exit(1)
        
    desktop_path = os.path.join(os.path.join(os.environ['USERPROFILE']), 'Desktop')
    base_name = os.path.splitext(os.path.basename(file_path))[0]
    ext = os.path.splitext(file_path)[1].lower()

    try:
        if ext == ".docx" or ext == ".doc":
            # Convert Word to PDF
            from docx2pdf import convert
            out_path = os.path.join(desktop_path, f"{base_name}_converted.pdf")
            convert(file_path, out_path)
            print("SUCCESS")
            
        elif ext == ".pdf":
            # Convert PDF to Word
            from pdf2docx import Converter
            out_path = os.path.join(desktop_path, f"{base_name}_converted.docx")
            cv = Converter(file_path)
            cv.convert(out_path, start=0, end=None)
            cv.close()
            print("SUCCESS")
            
        else:
            print(f"[ERROR] Unsupported extension: {ext}")
            sys.exit(1)
            
    except Exception as e:
        print(f"[CRITICAL ERROR] Failed to convert document: {e}")
        # MessageBox via ctypes for debug
        ctypes.windll.user32.MessageBoxW(0, f"Conversion Error: {e}", "AdvanceClip Python Engine", 0)
        sys.exit(1)

if __name__ == "__main__":
    main()
