package com.anonymous.AllSync

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
            val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
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
