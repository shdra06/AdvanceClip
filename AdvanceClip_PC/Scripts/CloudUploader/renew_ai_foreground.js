const { chromium } = require('playwright');
const path = require('path');

// ==========================================
// ADVANCECLIP: AUTO-SUBSCRIPTION ENGINE
// ==========================================
// This script utilizes the explicitly logged-in 'cloud-profile' to manage
// Google Workspace AI Ultra Subscriptions autonomously. 

(async () => {
    try {
        console.log("\n[ADVANCECLIP] Initiating AI Ultra Subscription Manager...");

        // Leverage the explicitly shared native profile
        const profilePath = path.join(__dirname, 'cloud-profile');

        const context = await chromium.launchPersistentContext(profilePath, {
            headless: false, // Maintain visibility for User monitoring
            channel: 'chrome',
            viewport: null,
            ignoreDefaultArgs: ["--enable-automation"],
            args: ['--start-maximized', '--disable-blink-features=AutomationControlled']
        });

        // ==========================================
        // PHASE 1: CANCELLATION PROTOCOL
        // ==========================================
        const page = await context.newPage();
        
        console.log("[ADVANCECLIP] Navigating Directly to Home Portal...");
        let navSuccess = false;
        for (let i = 0; i < 5; i++) {
            try {
                await page.goto("https://admin.google.com/ac/home?hl=en", { waitUntil: 'domcontentloaded', timeout: 20000 });
                await page.waitForTimeout(3500); 
                
                const currentUrl = page.url();
                if (currentUrl.includes("/u/") && (!currentUrl.includes("home"))) {
                    console.log(`[ADVANCECLIP] Execution Hijacked: Google natively redirected to (${currentUrl}). Re-asserting target URL dynamically...`);
                    continue; // Force the loop to fire again natively
                }

                navSuccess = true;
                break;
            } catch (e) {
                console.log("[ADVANCECLIP] Safety Lock: Network disconnected or timed out! Rebooting node...");
            }
        }
        
        // Target: "Get set up"
        console.log("[ADVANCECLIP] Looking for 'Get set up'...");
        await page.getByText(/Get set up/i).first().waitFor({ state: 'visible', timeout: 20000 });
        await page.getByText(/Get set up/i).first().click();

        // Target: "Manage subscription"
        console.log("[ADVANCECLIP] Looking for 'Manage subscription'...");
        await page.getByText(/Manage subscription/i).first().waitFor({ state: 'visible', timeout: 15000 });
        await page.getByText(/Manage subscription/i).first().click();

        // Target: "AI Ultra" inside the subscriptions page
        console.log("[ADVANCECLIP] Looking for 'AI Ultra'...");
        await page.getByText(/AI Ultra/i).first().waitFor({ state: 'visible', timeout: 15000 });
        await page.getByText(/AI Ultra/i).first().click();

        // Target: "Cancel subscription" Option
        await page.getByRole('button', { name: /Cancel subscription/i }).first().waitFor({ state: 'visible', timeout: 30000 });
        await page.getByRole('button', { name: /Cancel subscription/i }).first().click();

        // Form: Select any required reason Radio Button and hit Continue
        console.log("[ADVANCECLIP] Processing Option Selection Step...");
        try {
            const radioBtn = page.getByRole('radio').first();
            await radioBtn.waitFor({ state: 'visible', timeout: 10000 });
            await radioBtn.click({ force: true });
        } catch (e) {
            console.log("[ADVANCECLIP] Safety Lock: Primary Radio undetected. Attempting Fallback Native Input...");
            try {
                await page.locator('input[type="radio"], div[role="radio"]').first().click({ force: true, timeout: 5000 });
            } catch (err) {
                console.log("[ADVANCECLIP] Safety Lock: Proceeding anyways in case the selection is optional...");
            }
        }
        
        try {
            await page.getByRole('button', { name: /Continue/i }).click({ timeout: 5000 });
        } catch { console.log("[ADVANCECLIP] No Continue button required."); }

        // CONDITIONAL SENSING: Does it offer "View downgrade options" vs "Cancel Subscription"?
        await page.waitForTimeout(2000); // Micro-pause to let the DOM fork render
        const cancelSecondaryBtn = await page.getByRole('button', { name: /Cancel Subscription/i });
        if (await cancelSecondaryBtn.count() > 0) {
            console.log("[ADVANCECLIP] Conditional Fork Detected: Executing Override 'Cancel Subscription'.");
            await cancelSecondaryBtn.first().click();
        }

        // Final Authorization Protocol
        console.log("[ADVANCECLIP] Initiating Final Form Input...");
        const authCheckbox = page.getByRole('checkbox', { name: /I have read the information above and want to proceed with canceling my subscription/i });
        await authCheckbox.waitFor({ state: 'visible' });
        await authCheckbox.click(); // Tick the literal checkbox box natively

        // Target: Exactly Paste Required Email into the Textbox directly underneath!
        const emailTextbox = page.getByRole('textbox').first();
        await emailTextbox.fill("shdra062@gmail.com");

        // Execute: Cancel my subscription
        await page.getByRole('button', { name: /Cancel my subscription/i }).click();

        console.log("[ADVANCECLIP] Cancellation Fired. Executing 20-Second Cooldown Protocol...");
        await page.waitForTimeout(20000); // Wait explicitly exactly 20 seconds.

        // ==========================================
        // PHASE 2: REPURCHASING PROTOCOL
        // ==========================================
        console.log("[ADVANCECLIP] Navigating to Product Storefront...");
        try {
            await page.goto("https://workspace.google.com/intl/en_in/products/ai-ultra/", { waitUntil: 'domcontentloaded', timeout: 25000 });
        } catch (e) {
            console.log("[ADVANCECLIP] Safety Lock: Storefront took too long! Forcing Reload...");
            await page.reload({ waitUntil: 'domcontentloaded', timeout: 30000 });
        }

        // Target: Upper Right 'Buy Now'
        console.log("[ADVANCECLIP] Waiting for 'Buy Now' Context...");
        const buyNowFallback = page.locator('a, button, span a').filter({ hasText: /Buy now/i });
        await buyNowFallback.first().waitFor({ state: 'attached', timeout: 30000 });

        console.log("[ADVANCECLIP] Scanning for functionally visible buttons...");
        let clicked = false;
        for (let i = 0; i < await buyNowFallback.count(); i++) {
            const btn = buyNowFallback.nth(i);
            if (await btn.isVisible()) {
                await btn.scrollIntoViewIfNeeded();
                await btn.click({ force: true, timeout: 5000 });
                console.log(`[ADVANCECLIP] Successfully clicked functional 'Buy Now' button at index ${i}`);
                clicked = true;
                break;
            }
        }

        if (!clicked) {
            console.log("[ADVANCECLIP] Fallback: Forcing native layer click on the DOM tree...");
            await buyNowFallback.first().click({ force: true, timeout: 5000 });
        }

        // Checkout Wait Protocol
        console.log("[ADVANCECLIP] Executing 20-Second Checkout Delay Protocols...");
        await page.waitForTimeout(20000);
        
        // Target: Continue
        await page.getByRole('button', { name: /Continue/i }).click();

        // Target: Scroll natively and 'Agree and Continue'
        await page.evaluate(() => window.scrollBy(0, 800)); // Natively slide down viewport
        await page.getByRole('button', { name: /Agree and continue/i }).click();

        // ==========================================
        // ENHANCED POPUP CHECKOUT PIPELINE
        // ==========================================
        context.on('page', async newPage => { 
            console.log("\n[ADVANCECLIP] Sensed an external Checkout Popup Window! Hooking execution into the new context layer...");
            
            try {
                const popupOkBtn = newPage.locator('button').filter({ hasText: /^(OK|Ok|Okay)$/i }).first();
                await popupOkBtn.waitFor({ state: 'visible', timeout: 15000 });
                await popupOkBtn.click({ force: true });
                console.log("[ADVANCECLIP-POPUP] Successfully acknowledged external Add Funds modal.");
            } catch { console.log("[ADVANCECLIP-POPUP] No manual OK Modal detected inside external context."); }

            console.log("[ADVANCECLIP-POPUP] Scanning for 'Payment account page' anchor...");
            const paymentAnchor = newPage.getByRole('link', { name: /Payment account page/i }).first();
            
            try {
                await paymentAnchor.waitFor({ state: 'visible', timeout: 20000 });
                console.log("\n[ADVANCECLIP] SUCCESS - Purchase physically confirmed inside external popup context!");
                await paymentAnchor.click();
                await newPage.waitForTimeout(5000); 
                console.log("[ADVANCECLIP] Automation complete. Killing execution process cleanly.");
                await context.close();
                process.exit(0);
            } catch (e) {
                console.log("\n[ADVANCECLIP] FAILURE - Did not find Payment Account link inside the popup window either.");
                // Let the primary execution block continue scanning locally just in case! 
            }
        });

        console.log("[ADVANCECLIP] Handling 'Add funds to account' OK Confirmation...");
        const okModalBtn = page.locator('button').filter({ hasText: /^(OK|Ok|Okay)$/i }).first();
        try {
            await okModalBtn.waitFor({ state: 'visible', timeout: 15000 });
            await okModalBtn.click({ force: true });
            console.log("[ADVANCECLIP] Successfully acknowledged Add Funds modal.");
        } catch { 
            console.log("[ADVANCECLIP] No manual OK Modal required or detected."); 
        }

        console.log("[ADVANCECLIP] Scanning for 'Payment account page' anchor...");
        const paymentAccountPage = page.getByRole('link', { name: /Payment account page/i }).first();
        
        try {
            await paymentAccountPage.waitFor({ state: 'visible', timeout: 20000 });
            console.log("\n[ADVANCECLIP] SUCCESS - Purchase confirmed. Link actively appeared!");
            await paymentAccountPage.click();
            await page.waitForTimeout(5000); // Visual buffer for user
            console.log("[ADVANCECLIP] Automation complete. Killing execution process cleanly.");
            await context.close();
            process.exit(0);
        } catch (e) {
            console.log("\n[ADVANCECLIP] FAILURE - Did not find Payment Account link.");
            process.exit(1);
        }
    } catch (err) {
        console.error("FATAL SCRIPT ERROR: ", err.message);
        process.exit(1);
    }
})();
