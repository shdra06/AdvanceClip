# AdvanceClip - Advanced OS Clipboard Engine

AdvanceClip is a high-performance Windows Clipboard Manager featuring ultra-fast native Windows C# APIs, native AI Image extraction, and headless Playwright DOM automation.

## 🚀 Features
- **Hyper-Fast Native UI:** Built natively in C# WPF `.net10.0-windows` with fluent OS components.
- **Instagram Automation Integration:** Share text, links, or image configurations directly to an Instagram DM. Utilizes headless Node.js Playwright architecture.
- **AI Table Extraction (OCR):** Uses Google DeepMind's Gemini 1.5 Pro to mathematically extract nested lists and visual tables into pure `DataFormats.Html` arrays that paste perfectly into Word / Excel interfaces!
- **Zero-Crash Resilience:** Complete UnhandledException encapsulation means the clipboard background worker never tears down. 

## ⚙️ System Requirements
1. **Windows 10 / 11** (WPF Support)
2. **.NET 10.0 Desktop Runtime**
3. **Node.js** (Required for Playwright Instagram DMs)
4. **Python 3.10+** (Required for Google Gemini Data scraping)

---

## 🛠️ Auto-Installer Setup
You can instantly install the required NPM and PIP packages by running the included batch script:

1. Double-click `install_dependencies.bat`
2. Accept the prompts to install `playwright`, `chromium`, `google-generativeai`, and `pillow`.

---

## 🔑 Post-Installation Integrations

### 1. Instagram Sender (NodeJS)
Before attempting to click "Share to Instagram" in the Hub, you MUST create your persistent authentication cache.
1. Open PowerShell and navigate to `AdvanceClip\Scripts\InstagramSender`.
2. Run `node first_login.js` -> A Chrome window will open.
3. Log in to Instagram manually. Close the browser when done.
4. Finally, open `index.js` inside that folder and change `TARGET_USERNAME = "YOUR_FRIEND_USERNAME_HERE"` to the `@username` of the person you plan to DM!

### 2. Gemini Table Extractor (Python)
Before clicking "Extract Table Data (Gemini AI)", insert your API Key!
1. Get a completely free API Key from: [Google AI Studio](https://aistudio.google.com/app/apikey).
2. Open `AdvanceClip\Scripts\TableExtractor\extract_table.py`.
3. Locate `GOOGLE_API_KEY = "PLACEHOLDER_INSERT_KEY_HERE"` and paste your token inside the quotes.

---

## 🏃 Run Application
If running from source, execute `run.bat` or `dotnet run`. Or run compiled `.exe` files natively. 
Hit `Win + Shift + V` locally across any Windows background context to spawn the AdvanceClip Hub.
