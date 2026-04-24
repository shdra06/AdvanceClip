const { withDangerousMod, withMainApplication, withAndroidManifest } = require('expo/config-plugins');
const fs = require('fs');
const path = require('path');

const PACKAGE_NAME = 'com.anonymous.AllSync';
const PACKAGE_DIR = 'com/anonymous/AllSync';

// ====== NATIVE KOTLIN SOURCE FILES ======

const OVERLAY_SERVICE_KT = `package ${PACKAGE_NAME}

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.graphics.PixelFormat
import android.os.IBinder
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.os.Build
import android.content.ClipboardManager
import android.content.ClipData
import android.content.Context
import android.widget.Toast
import org.json.JSONArray
import android.widget.ScrollView
import android.provider.MediaStore
import android.graphics.drawable.GradientDrawable
import android.graphics.Typeface
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.graphics.drawable.LayerDrawable
import android.os.Handler
import android.os.Looper
import android.view.HapticFeedbackConstants

class OverlayService : Service() {

    private var windowManager: WindowManager? = null
    private var floatingBallView: View? = null
    private var panelView: View? = null
    private var dimView: View? = null
    private var isPanelVisible = false
    private var screenshotObserver: ScreenshotObserver? = null
    private var panelParams: WindowManager.LayoutParams? = null
    private var ballParams: WindowManager.LayoutParams? = null
    private val autoHideHandler = Handler(Looper.getMainLooper())
    private var autoHideRunnable: Runnable? = null
    private var clipboardListener: ClipboardManager.OnPrimaryClipChangedListener? = null
    private var lastAutoClipTime: Long = 0

    companion object {
        var clipboardItems: String = "[]"
        var ballSizeDp: Int = 48
        var autoHideDelayMs: Long = 3000L
        var lastCopiedText: String = ""
        var instance: OverlayService? = null
        const val CHANNEL_ID = "advanceclip_overlay"
        const val NOTIF_ID = 1001
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(CHANNEL_ID, "FlyShelf Overlay", NotificationManager.IMPORTANCE_LOW)
            channel.setShowBadge(false)
            val nm = getSystemService(NotificationManager::class.java)
            nm?.createNotificationChannel(channel)
            val notification = Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("FlyShelf Active")
                .setContentText("Floating clipboard is running")
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .build()
            startForeground(NOTIF_ID, notification)
        }
        windowManager = getSystemService(WINDOW_SERVICE) as WindowManager
        createFloatingBall()
        try {
            screenshotObserver = ScreenshotObserver(this)
            contentResolver.registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, screenshotObserver!!)
        } catch(e: Exception) {}
        // Listen for system clipboard changes — auto-capture anything copied on the phone
        try {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
                try {
                    val now = System.currentTimeMillis()
                    if (now - lastAutoClipTime < 1500) return@OnPrimaryClipChangedListener
                    lastAutoClipTime = now
                    val clip = cm.primaryClip
                    if (clip != null && clip.itemCount > 0) {
                        val text = clip.getItemAt(0).text?.toString() ?: ""
                        if (text.isNotEmpty() && text != lastCopiedText) {
                            lastCopiedText = text
                            // Also inject into the overlay's clip list
                            try {
                                val arr = org.json.JSONArray(clipboardItems)
                                // Check if already exists at top
                                if (arr.length() == 0 || arr.getJSONObject(0).optString("Raw") != text) {
                                    val obj = org.json.JSONObject()
                                    obj.put("Raw", text)
                                    obj.put("Title", text.take(60))
                                    obj.put("Type", "Text")
                                    obj.put("SourceDeviceName", "Phone")
                                    // Insert at top
                                    val newArr = org.json.JSONArray()
                                    newArr.put(obj)
                                    for (i in 0 until Math.min(arr.length(), 19)) {
                                        newArr.put(arr.getJSONObject(i))
                                    }
                                    clipboardItems = newArr.toString()
                                    pulseBall()
                                }
                            } catch(e: Exception) {}
                        }
                    }
                } catch(e: Exception) {}
            }
            cm.addPrimaryClipChangedListener(clipboardListener)
        } catch(e: Exception) {}
    }

    private fun scheduleAutoHide() {
        autoHideRunnable?.let { autoHideHandler.removeCallbacks(it) }
        autoHideRunnable = Runnable { floatingBallView?.animate()?.alpha(0.15f)?.setDuration(600)?.start() }
        autoHideHandler.postDelayed(autoHideRunnable!!, autoHideDelayMs)
    }

    private fun cancelAutoHide() {
        autoHideRunnable?.let { autoHideHandler.removeCallbacks(it) }
        floatingBallView?.animate()?.alpha(1f)?.setDuration(200)?.start()
    }

    private fun createFloatingBall() {
        val density = resources.displayMetrics.density
        val sizePx = (ballSizeDp * density).toInt()
        val ballContainer = FrameLayout(this)
        val ballDrawable = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(0xFF6C63FF.toInt(), 0xFF3B82F6.toInt(), 0xFF8B5CF6.toInt()))
        ballDrawable.shape = GradientDrawable.OVAL
        ballDrawable.setStroke((1.5f * density).toInt(), 0x40FFFFFF)
        val ball = ImageView(this)
        ball.setImageResource(android.R.drawable.ic_dialog_info)
        ball.setColorFilter(0xFFFFFFFF.toInt())
        ball.background = ballDrawable
        val iconPad = (10 * density).toInt()
        ball.setPadding(iconPad, iconPad, iconPad, iconPad)
        ball.elevation = 12f * density
        ballContainer.addView(ball, FrameLayout.LayoutParams(sizePx, sizePx))

        val params = WindowManager.LayoutParams(
            sizePx + (6 * density).toInt(), sizePx + (6 * density).toInt(),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else WindowManager.LayoutParams.TYPE_PHONE,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, PixelFormat.TRANSLUCENT
        )
        params.gravity = Gravity.TOP or Gravity.START
        params.x = (4 * density).toInt()
        params.y = (200 * density).toInt()
        ballParams = params

        var initialX = 0; var initialY = 0; var initialTouchX = 0f; var initialTouchY = 0f; var isDragging = false
        ballContainer.setOnTouchListener { v, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> { cancelAutoHide(); initialX = params.x; initialY = params.y; initialTouchX = event.rawX; initialTouchY = event.rawY; isDragging = false; ball.animate().scaleX(0.85f).scaleY(0.85f).setDuration(100).start(); true }
                MotionEvent.ACTION_MOVE -> { val dx = (event.rawX - initialTouchX).toInt(); val dy = (event.rawY - initialTouchY).toInt(); if (Math.abs(dx) > 10 || Math.abs(dy) > 10) isDragging = true; params.x = initialX + dx; params.y = initialY + dy; try { windowManager?.updateViewLayout(floatingBallView, params) } catch(e: Exception) {}; true }
                MotionEvent.ACTION_UP -> { ball.animate().scaleX(1f).scaleY(1f).setDuration(200).setInterpolator(OvershootInterpolator()).start(); if (!isDragging) togglePanel(); scheduleAutoHide(); true }
                else -> false
            }
        }
        floatingBallView = ballContainer
        try { windowManager?.addView(floatingBallView, params) } catch(e: Exception) {}
        scheduleAutoHide()
    }

    private fun togglePanel() { if (isPanelVisible) hidePanel() else showPanel() }

    private fun showPanel() {
        if (panelView != null) return
        floatingBallView?.animate()?.alpha(0.05f)?.setDuration(300)?.start()

        val density = resources.displayMetrics.density
        val panelWidth = (300 * density).toInt()
        val panelHeight = (400 * density).toInt()

        val dim = View(this)
        dim.setBackgroundColor(0x44000000)
        dim.setOnClickListener { hidePanel() }
        val dimParams = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT,
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else WindowManager.LayoutParams.TYPE_PHONE,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN, PixelFormat.TRANSLUCENT
        )
        dimView = dim
        try { windowManager?.addView(dimView, dimParams) } catch(e: Exception) {}
        dim.alpha = 0f; dim.animate().alpha(1f).setDuration(250).start()

        val glassBase = GradientDrawable()
        glassBase.shape = GradientDrawable.RECTANGLE
        glassBase.cornerRadius = 28f * density
        glassBase.setColor(0xE61A1D27.toInt())
        glassBase.setStroke((1 * density).toInt(), 0x30FFFFFF)
        val glassFrost = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0x18FFFFFF, 0x08FFFFFF, 0x00000000))
        glassFrost.cornerRadius = 28f * density

        val outerFrame = FrameLayout(this)
        val container = LinearLayout(this)
        container.orientation = LinearLayout.VERTICAL
        container.background = LayerDrawable(arrayOf(glassBase, glassFrost))
        container.setPadding((20 * density).toInt(), (18 * density).toInt(), (20 * density).toInt(), (18 * density).toInt())
        container.elevation = 24f * density
        container.clipToOutline = true
        container.outlineProvider = object : android.view.ViewOutlineProvider() {
            override fun getOutline(view: View, outline: android.graphics.Outline) {
                outline.setRoundRect(0, 0, view.width, view.height, 28f * density)
            }
        }

        val grabBarWrap = LinearLayout(this)
        grabBarWrap.gravity = Gravity.CENTER
        grabBarWrap.setPadding(0, 0, 0, (6 * density).toInt())
        val grabBar = View(this)
        val grabBg = GradientDrawable(); grabBg.cornerRadius = 3f * density; grabBg.setColor(0x40FFFFFF); grabBar.background = grabBg
        grabBarWrap.addView(grabBar, LinearLayout.LayoutParams((40 * density).toInt(), (5 * density).toInt()))
        container.addView(grabBarWrap)

        val headerRow = LinearLayout(this)
        headerRow.orientation = LinearLayout.HORIZONTAL
        headerRow.gravity = Gravity.CENTER_VERTICAL
        val iconBg = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(0xFF6C63FF.toInt(), 0xFF3B82F6.toInt()))
        iconBg.shape = GradientDrawable.OVAL
        val appIcon = ImageView(this)
        appIcon.setImageResource(android.R.drawable.ic_dialog_info)
        appIcon.setColorFilter(0xFFFFFFFF.toInt())
        appIcon.background = iconBg
        val iSize = (28 * density).toInt()
        appIcon.setPadding((5 * density).toInt(), (5 * density).toInt(), (5 * density).toInt(), (5 * density).toInt())
        headerRow.addView(appIcon, LinearLayout.LayoutParams(iSize, iSize))
        val title = TextView(this)
        title.text = "Floating Clipboard"
        title.textSize = 16f
        title.setTextColor(0xFFFFFFFF.toInt())
        title.typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
        title.setPadding((10 * density).toInt(), 0, 0, 0)
        headerRow.addView(title, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val closeX = TextView(this)
        closeX.text = "\\u2715"
        closeX.textSize = 16f
        closeX.setTextColor(0x99FFFFFF.toInt())
        closeX.gravity = Gravity.CENTER
        val closeBg = GradientDrawable(); closeBg.shape = GradientDrawable.OVAL; closeBg.setColor(0x20FFFFFF); closeX.background = closeBg
        closeX.setOnClickListener { hidePanel() }
        headerRow.addView(closeX, LinearLayout.LayoutParams((28 * density).toInt(), (28 * density).toInt()))
        container.addView(headerRow)

        val divider1 = View(this)
        val div1Bg = GradientDrawable(); div1Bg.setColor(0x15FFFFFF); div1Bg.cornerRadius = 1f * density; divider1.background = div1Bg
        val divLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, (1 * density).toInt())
        divLp.topMargin = (10 * density).toInt(); divLp.bottomMargin = (10 * density).toInt()
        container.addView(divider1, divLp)

        val recentLabel = TextView(this)
        recentLabel.text = "RECENT CLIPS"
        recentLabel.textSize = 10f
        recentLabel.setTextColor(0x80FFFFFF.toInt())
        recentLabel.typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        recentLabel.letterSpacing = 0.12f
        val rlLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        rlLp.bottomMargin = (8 * density).toInt()
        container.addView(recentLabel, rlLp)

        val scrollView = ScrollView(this)
        scrollView.isVerticalScrollBarEnabled = false
        scrollView.overScrollMode = View.OVER_SCROLL_NEVER
        val clipList = LinearLayout(this)
        clipList.orientation = LinearLayout.VERTICAL

        try {
            val arr = JSONArray(clipboardItems)
            val count = Math.min(arr.length(), 12)
            for (i in 0 until count) {
                val obj = arr.getJSONObject(i)
                val raw = obj.optString("Raw", obj.optString("Title", "Unknown"))
                val clipTitle = obj.optString("Title", raw.take(55))
                val lowerTitle = clipTitle.lowercase()
                val isWordFile = lowerTitle.endsWith(".doc") || lowerTitle.endsWith(".docx")
                val isPdfFile = lowerTitle.endsWith(".pdf")

                val clipCard = LinearLayout(this)
                clipCard.orientation = LinearLayout.HORIZONTAL
                clipCard.gravity = Gravity.CENTER_VERTICAL
                val cardBg = GradientDrawable()
                cardBg.cornerRadius = 12f * density
                cardBg.setColor(if (isWordFile) 0x203B82F6 else if (isPdfFile) 0x20EF4444 else 0x15FFFFFF)
                clipCard.background = cardBg
                clipCard.setPadding((12 * density).toInt(), (10 * density).toInt(), (12 * density).toInt(), (10 * density).toInt())

                val badge = TextView(this)
                badge.text = "\${i + 1}"
                badge.textSize = 9f
                badge.setTextColor(0xFFFFFFFF.toInt())
                badge.gravity = Gravity.CENTER
                badge.typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
                val badgeBg = GradientDrawable()
                badgeBg.shape = GradientDrawable.OVAL
                badgeBg.setColor(if (isWordFile) 0xFF3B82F6.toInt() else if (isPdfFile) 0xFFEF4444.toInt() else 0xFF6C63FF.toInt())
                badge.background = badgeBg
                val bSize = (20 * density).toInt()
                val badgeLp = LinearLayout.LayoutParams(bSize, bSize)
                badgeLp.rightMargin = (8 * density).toInt()
                clipCard.addView(badge, badgeLp)

                val clipText = TextView(this)
                clipText.text = clipTitle.take(48)
                clipText.textSize = 12f
                clipText.setTextColor(0xDDFFFFFF.toInt())
                clipText.maxLines = 2
                clipText.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
                clipCard.addView(clipText, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))

                if (isWordFile || isPdfFile) {
                    val typeTag = TextView(this)
                    typeTag.text = if (isWordFile) "DOC" else "PDF"
                    typeTag.textSize = 8f
                    typeTag.setTextColor(0xFFFFFFFF.toInt())
                    typeTag.gravity = Gravity.CENTER
                    typeTag.typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
                    val tagBg = GradientDrawable()
                    tagBg.cornerRadius = 6f * density
                    tagBg.setColor(if (isWordFile) 0xFF3B82F6.toInt() else 0xFFEF4444.toInt())
                    typeTag.background = tagBg
                    typeTag.setPadding((6 * density).toInt(), (2 * density).toInt(), (6 * density).toInt(), (2 * density).toInt())
                    val tagLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                    tagLp.leftMargin = (6 * density).toInt()
                    clipCard.addView(typeTag, tagLp)
                }

                clipCard.setOnClickListener {
                    it.animate().scaleX(0.96f).scaleY(0.96f).setDuration(60).withEndAction { it.animate().scaleX(1f).scaleY(1f).setDuration(100).start() }.start()
                    if (isWordFile) {
                        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", raw))
                        lastCopiedText = raw
                        Toast.makeText(this, "Copied! Open main app for Convert to PDF option.", Toast.LENGTH_LONG).show()
                    } else if (isPdfFile) {
                        val downloadUrl = obj.optString("DownloadUrl", raw)
                        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", if (downloadUrl.startsWith("http")) downloadUrl else raw))
                        lastCopiedText = if (downloadUrl.startsWith("http")) downloadUrl else raw
                        Toast.makeText(this, "PDF URL copied! Paste in browser to download.", Toast.LENGTH_LONG).show()
                    } else {
                        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", raw))
                        lastCopiedText = raw
                        Toast.makeText(this, "Copied! Long-press in any field to paste.", Toast.LENGTH_SHORT).show()
                    }
                }

                clipCard.setOnLongClickListener { v ->
                    try { v.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS) } catch(e: Exception) {}
                    val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", raw))
                    val clipData = ClipData.newPlainText("FlyShelf", raw)
                    val shadowBuilder = View.DragShadowBuilder(v)
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                        v.startDragAndDrop(clipData, shadowBuilder, null, View.DRAG_FLAG_GLOBAL or View.DRAG_FLAG_GLOBAL_URI_READ)
                    } else {
                        @Suppress("DEPRECATION")
                        v.startDrag(clipData, shadowBuilder, null, 0)
                    }
                    Toast.makeText(this, "Dragging \\u2014 drop into any field", Toast.LENGTH_SHORT).show()
                    hidePanel()
                    true
                }

                val cardLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                cardLp.bottomMargin = (5 * density).toInt()
                clipList.addView(clipCard, cardLp)
            }
            if (count == 0) {
                val emptyRow = LinearLayout(this)
                emptyRow.orientation = LinearLayout.VERTICAL
                emptyRow.gravity = Gravity.CENTER
                emptyRow.setPadding(0, (32 * density).toInt(), 0, (32 * density).toInt())
                val emptyIcon = TextView(this)
                emptyIcon.text = "\\uD83D\\uDCED"
                emptyIcon.textSize = 28f
                emptyIcon.gravity = Gravity.CENTER
                emptyRow.addView(emptyIcon)
                val emptyText = TextView(this)
                emptyText.text = "No clips synced yet"
                emptyText.textSize = 13f
                emptyText.setTextColor(0x60FFFFFF.toInt())
                emptyText.gravity = Gravity.CENTER
                emptyText.typeface = Typeface.create("sans-serif", Typeface.ITALIC)
                val etLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                etLp.topMargin = (6 * density).toInt()
                emptyRow.addView(emptyText, etLp)
                clipList.addView(emptyRow)
            }
        } catch(e: Exception) {}

        scrollView.addView(clipList)
        container.addView(scrollView, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
        outerFrame.addView(container, FrameLayout.LayoutParams(panelWidth, panelHeight))

        val pParams = WindowManager.LayoutParams(
            panelWidth, panelHeight,
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else WindowManager.LayoutParams.TYPE_PHONE,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, PixelFormat.TRANSLUCENT
        )
        pParams.gravity = Gravity.TOP or Gravity.START
        pParams.x = (resources.displayMetrics.widthPixels - panelWidth) / 2
        pParams.y = (resources.displayMetrics.heightPixels - panelHeight) / 2
        panelParams = pParams
        panelView = outerFrame
        try { windowManager?.addView(panelView, pParams) } catch(e: Exception) {}
        isPanelVisible = true

        outerFrame.scaleX = 0.7f; outerFrame.scaleY = 0.7f; outerFrame.alpha = 0f
        outerFrame.animate().scaleX(1f).scaleY(1f).alpha(1f).setDuration(300).setInterpolator(OvershootInterpolator(0.8f)).start()

        var pInitialX = 0; var pInitialY = 0; var pInitialTouchX = 0f; var pInitialTouchY = 0f; var pIsDragging = false
        val headerDragListener = View.OnTouchListener { v, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> { pInitialX = pParams.x; pInitialY = pParams.y; pInitialTouchX = event.rawX; pInitialTouchY = event.rawY; pIsDragging = false; true }
                MotionEvent.ACTION_MOVE -> { val dx = (event.rawX - pInitialTouchX).toInt(); val dy = (event.rawY - pInitialTouchY).toInt(); if (Math.abs(dx) > 5 || Math.abs(dy) > 5) pIsDragging = true; if (pIsDragging) { pParams.x = pInitialX + dx; pParams.y = pInitialY + dy; try { windowManager?.updateViewLayout(panelView, pParams) } catch(e: Exception) {} }; true }
                MotionEvent.ACTION_UP -> { true }
                else -> false
            }
        }
        grabBarWrap.setOnTouchListener(headerDragListener)
        headerRow.setOnTouchListener(headerDragListener)
    }

    private fun hidePanel() {
        val panel = panelView; val dim = dimView
        if (panel != null) { panel.animate().scaleX(0.85f).scaleY(0.85f).alpha(0f).setDuration(200).setInterpolator(DecelerateInterpolator()).withEndAction { try { windowManager?.removeView(panel) } catch(e: Exception) {} }.start()
        } else { try { if (panelView != null) windowManager?.removeView(panelView) } catch(e: Exception) {} }
        if (dim != null) { dim.animate().alpha(0f).setDuration(200).withEndAction { try { windowManager?.removeView(dim) } catch(e: Exception) {} }.start()
        } else { try { if (dimView != null) windowManager?.removeView(dimView) } catch(e: Exception) {} }
        panelView = null; dimView = null; panelParams = null; isPanelVisible = false
        floatingBallView?.animate()?.alpha(1f)?.setDuration(300)?.start()
        scheduleAutoHide()
    }

    fun pulseBall() {
        Handler(Looper.getMainLooper()).post {
            floatingBallView?.let { ball ->
                cancelAutoHide()
                ball.animate().scaleX(1.3f).scaleY(1.3f).setDuration(150).setInterpolator(OvershootInterpolator())
                    .withEndAction {
                        ball.animate().scaleX(1f).scaleY(1f).setDuration(250).setInterpolator(OvershootInterpolator()).start()
                        scheduleAutoHide()
                    }.start()
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        instance = null
        autoHideRunnable?.let { autoHideHandler.removeCallbacks(it) }
        try { val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager; clipboardListener?.let { cm.removePrimaryClipChangedListener(it) } } catch(e: Exception) {}
        clipboardListener = null
        try { if (panelView != null) windowManager?.removeView(panelView) } catch(e: Exception) {}
        panelView = null
        try { if (dimView != null) windowManager?.removeView(dimView) } catch(e: Exception) {}
        dimView = null; panelParams = null; isPanelVisible = false
        try { if (floatingBallView != null) windowManager?.removeView(floatingBallView) } catch(e: Exception) {}
        floatingBallView = null
        try { if (screenshotObserver != null) contentResolver.unregisterContentObserver(screenshotObserver!!) } catch(e: Exception) {}
        screenshotObserver = null
    }
}
`;

const OVERLAY_MODULE_KT = `package ${PACKAGE_NAME}

import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.net.Uri
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod
import com.facebook.react.bridge.Promise

class AdvanceOverlayModule(reactContext: ReactApplicationContext) : ReactContextBaseJavaModule(reactContext) {

    override fun getName(): String = "AdvanceOverlay"

    @ReactMethod
    fun startOverlay() {
        val context = reactApplicationContext
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && !Settings.canDrawOverlays(context)) {
            return
        }
        val intent = Intent(context, OverlayService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent)
        } else {
            context.startService(intent)
        }
    }

    @ReactMethod
    fun stopOverlay() {
        val context = reactApplicationContext
        context.stopService(Intent(context, OverlayService::class.java))
    }

    @ReactMethod
    fun checkOverlayPermission(promise: Promise) {
        val context = reactApplicationContext
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            promise.resolve(Settings.canDrawOverlays(context))
        } else {
            promise.resolve(true)
        }
    }

    @ReactMethod
    fun requestOverlayPermission() {
        val context = reactApplicationContext
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:\${context.packageName}"))
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            context.startActivity(intent)
        }
    }

    @ReactMethod
    fun syncNativeDB(jsonStr: String) {
        OverlayService.clipboardItems = jsonStr
        OverlayService.instance?.pulseBall()
    }

    @ReactMethod
    fun pushClipToNativeDB(rawText: String, source: String) {
        try {
            val arr = org.json.JSONArray(OverlayService.clipboardItems)
            val obj = org.json.JSONObject()
            obj.put("Raw", rawText)
            obj.put("Title", rawText.take(60))
            obj.put("Source", source)
            arr.put(0, obj)
            OverlayService.clipboardItems = arr.toString()
        } catch(e: Exception) {}
    }

    @ReactMethod
    fun setOverlayConfig(sizeDp: Int, autoHideMs: Int) {
        OverlayService.ballSizeDp = sizeDp
        OverlayService.autoHideDelayMs = autoHideMs.toLong()
    }

    @ReactMethod
    fun getLastCopiedFromOverlay(promise: Promise) {
        val last = OverlayService.lastCopiedText
        if (last.isNotEmpty()) {
            OverlayService.lastCopiedText = ""
            promise.resolve(last)
        } else {
            promise.resolve(null)
        }
    }
}
`;

const OVERLAY_PACKAGE_KT = `package ${PACKAGE_NAME}

import com.facebook.react.ReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.uimanager.ViewManager

class AdvanceOverlayPackage : ReactPackage {
    override fun createNativeModules(reactContext: ReactApplicationContext): List<NativeModule> {
        return listOf(AdvanceOverlayModule(reactContext))
    }

    override fun createViewManagers(reactContext: ReactApplicationContext): List<ViewManager<*, *>> {
        return emptyList()
    }
}
`;

const SCREENSHOT_OBSERVER_KT = `package ${PACKAGE_NAME}

import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.content.ClipboardManager
import android.content.ClipData
import android.widget.Toast

class ScreenshotObserver(private val context: Context) : ContentObserver(Handler(Looper.getMainLooper())) {

    private var lastScreenshotTime = 0L

    override fun onChange(selfChange: Boolean, uri: Uri?) {
        super.onChange(selfChange, uri)
        if (uri == null) return
        
        val now = System.currentTimeMillis()
        if (now - lastScreenshotTime < 3000) return

        try {
            val cursor = context.contentResolver.query(
                MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
                arrayOf(MediaStore.Images.Media.DATA, MediaStore.Images.Media.DATE_ADDED),
                null, null,
                "\${MediaStore.Images.Media.DATE_ADDED} DESC"
            )
            cursor?.use {
                if (it.moveToFirst()) {
                    val path = it.getString(0) ?: return
                    val dateAdded = it.getLong(1)
                    if (System.currentTimeMillis() / 1000 - dateAdded < 5) {
                        val lower = path.lowercase()
                        if (lower.contains("screenshot") || lower.contains("screen_shot") || lower.contains("screen shot")) {
                            lastScreenshotTime = now
                            Toast.makeText(context, "Screenshot detected by FlyShelf", Toast.LENGTH_SHORT).show()
                        }
                    }
                }
            }
        } catch (e: Exception) {}
    }
}
`;

// ====== PLUGIN LOGIC ======

function withOverlayServiceFiles(config) {
  return withDangerousMod(config, ['android', async (config) => {
    const projectRoot = config.modRequest.projectRoot;
    const javaDir = path.join(projectRoot, 'android', 'app', 'src', 'main', 'java', ...PACKAGE_DIR.split('/'));

    // Ensure directory exists
    fs.mkdirSync(javaDir, { recursive: true });

    // Write all Kotlin files
    fs.writeFileSync(path.join(javaDir, 'OverlayService.kt'), OVERLAY_SERVICE_KT);
    fs.writeFileSync(path.join(javaDir, 'AdvanceOverlayModule.kt'), OVERLAY_MODULE_KT);
    fs.writeFileSync(path.join(javaDir, 'AdvanceOverlayPackage.kt'), OVERLAY_PACKAGE_KT);
    fs.writeFileSync(path.join(javaDir, 'ScreenshotObserver.kt'), SCREENSHOT_OBSERVER_KT);

    console.log('[FlyShelf] ✅ Native overlay Kotlin files injected successfully.');
    return config;
  }]);
}

function withOverlayManifest(config) {
  return withAndroidManifest(config, async (config) => {
    const manifest = config.modResults;
    const application = manifest.manifest.application[0];

    // Add FOREGROUND_SERVICE permissions if missing
    const permissions = manifest.manifest['uses-permission'] || [];
    const fgPerm = 'android.permission.FOREGROUND_SERVICE';
    const fgSpecPerm = 'android.permission.FOREGROUND_SERVICE_SPECIAL_USE';
    if (!permissions.some(p => p.$?.['android:name'] === fgPerm)) {
        permissions.push({ $: { 'android:name': fgPerm } });
    }
    if (!permissions.some(p => p.$?.['android:name'] === fgSpecPerm)) {
        permissions.push({ $: { 'android:name': fgSpecPerm } });
    }
    manifest.manifest['uses-permission'] = permissions;

    // Register the OverlayService in manifest
    if (!application.service) application.service = [];
    const serviceExists = application.service.some(s => s.$?.['android:name'] === '.OverlayService');
    if (!serviceExists) {
        application.service.push({
            $: {
                'android:name': '.OverlayService',
                'android:exported': 'false',
                'android:foregroundServiceType': 'specialUse'
            }
        });
    }

    console.log('[FlyShelf] ✅ OverlayService registered in AndroidManifest.xml');
    return config;
  });
}

function withOverlayPackageRegistration(config) {
  return withMainApplication(config, async (config) => {
    let contents = config.modResults.contents;

    // Add the package registration if not already present
    if (!contents.includes('AdvanceOverlayPackage')) {
        // Add import
        contents = contents.replace(
            'import com.facebook.react.ReactPackage',
            'import com.facebook.react.ReactPackage\nimport com.anonymous.AllSync.AdvanceOverlayPackage'
        );

        // Add to getPackages
        contents = contents.replace(
            '// Packages that cannot be autolinked yet can be added manually here, for example:',
            '// Packages that cannot be autolinked yet can be added manually here, for example:\n              add(AdvanceOverlayPackage())'
        );
    }

    config.modResults.contents = contents;
    console.log('[FlyShelf] ✅ AdvanceOverlayPackage registered in MainApplication.kt');
    return config;
  });
}

module.exports = function withOverlayService(config) {
  config = withOverlayServiceFiles(config);
  config = withOverlayManifest(config);
  config = withOverlayPackageRegistration(config);
  return config;
};
