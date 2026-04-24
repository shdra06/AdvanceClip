package com.anonymous.AllSync

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
                "${MediaStore.Images.Media.DATE_ADDED} DESC"
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
