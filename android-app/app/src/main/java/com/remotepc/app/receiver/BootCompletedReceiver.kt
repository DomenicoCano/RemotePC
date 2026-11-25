package com.remotepc.app.receiver

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.remotepc.app.service.PcNotificationService

class BootCompletedReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        val action = intent?.action ?: return
        if (action == Intent.ACTION_BOOT_COMPLETED ||
            action == Intent.ACTION_MY_PACKAGE_REPLACED ||
            action == "android.intent.action.QUICKBOOT_POWERON"
        ) {
            PcNotificationService.syncState(context, requestImmediateSync = true)
        }
    }
}
