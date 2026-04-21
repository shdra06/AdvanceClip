package com.anonymous.AllSync

import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
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
                arrayOf(MediaStore.Images.Media.DATA, MediaStore.Images.Media.DATE_ADDED, MediaStore.Images.Media.DISPLAY_NAME),
                null, null,
                "${MediaStore.Images.Media.DATE_ADDED} DESC"
            )
            cursor?.use {
                if (it.moveToFirst()) {
                    val path = it.getString(0) ?: return
                    val dateAdded = it.getLong(1)
                    val displayName = it.getString(2) ?: "screenshot.png"
                    if (System.currentTimeMillis() / 1000 - dateAdded < 5) {
                        val lower = path.lowercase()
                        if (lower.contains("screenshot") || lower.contains("screen_shot") || lower.contains("screen shot")) {
                            lastScreenshotTime = now
                            // Store for React Native to pick up
                            latestScreenshotPath = path
                            latestScreenshotName = displayName
                            latestScreenshotTimestamp = now
                            Toast.makeText(context, "📸 Screenshot captured!", Toast.LENGTH_SHORT).show()
                        }
                    }
                }
            }
        } catch (e: Exception) {}
    }

    companion object {
        @JvmStatic var latestScreenshotPath: String = ""
        @JvmStatic var latestScreenshotName: String = ""
        @JvmStatic var latestScreenshotTimestamp: Long = 0L
    }
}
