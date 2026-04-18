const { chromium } = require('playwright');
const path = require('path');

// ==========================================
// ADVANCECLIP CLOUD HUB - SESSION ORCHESTRATOR
// ==========================================
// This script physically boots a visible Chrome instance tied exclusively to a persistent 
// cache folder ('cloud-profile'). It acts as your authentication gateway.
// 
// You can navigate to any URL shortener (Pastebin, Bitly, Imgur) manually and log into
// your premium accounts. Playwright perfectly records all cookies locally.
// Once complete, simply exit the terminal and future scripts run autonomously.

(async () => {
    try {
        console.log("\n[ADVANCECLIP] Initiating Universal Cloud Hub Sandbox...");
        console.log("[ADVANCECLIP] Pastebin Cloudflare Captchas are actively blocking Bots.");
        console.log("[ADVANCECLIP] Opening 'catbox.moe' (200MB Files) and 'rentry.co' (Text) natively without Captchas!");
        console.log("[ADVANCECLIP] (When you are finished exploring, simply close the browser window.)\n");

        // Force explicit absolute directory mapping natively isolating the Memory Profile
        const profilePath = path.join(__dirname, 'cloud-profile');

        const context = await chromium.launchPersistentContext(profilePath, {
            headless: false, // Forces a physical 1:1 Graphical Browser window to spawn
            channel: 'chrome', // Use physical Chrome to bypass Google Security Blocks!
            viewport: null,
            ignoreDefaultArgs: ["--enable-automation"], // Strips the 'Chrome is being controlled...' banner
            args: [
                '--start-maximized',
                '--disable-blink-features=AutomationControlled' // Disables the WebDriver Flag explicitly
            ]
        });

        const page = await context.newPage();

        // Dynamically spawn multiple tabs bypassing aggressive CloudFlare Captchas!
        // Tab 1: Image/Video Host (catbox.moe)
        await page.goto("https://catbox.moe/");
        
        // Tab 2: Text/Code Host (rentry.co)
        const textTab = await context.newPage();
        await textTab.goto("https://rentry.co/");
        
        // Block the script permanently allowing infinite exploration time until manual closure
        await new Promise(() => {});

    } catch (err) {
        console.error("GATEWAY ERROR: ", err);
    }
})();
