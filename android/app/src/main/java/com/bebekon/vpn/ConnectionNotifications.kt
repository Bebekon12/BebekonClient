package com.bebekon.vpn

import android.app.*
import android.content.Context
import android.content.Intent

object ConnectionNotifications {
    fun event(context: Context, previous: Session, next: Session) {
        if (!context.repo.state.value.preferences.connectionNotifications || previous.phase == next.phase) return
        val text = when {
            next.phase == Phase.ON -> "VPN подключён"
            next.phase == Phase.ERROR -> next.message
            next.phase == Phase.OFF && previous.active || next.phase == Phase.OFF && previous.phase == Phase.STOPPING -> "VPN отключён"
            else -> return
        }
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel("events", "События подключения", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(context, 4, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        if (manager.areNotificationsEnabled()) runCatching { manager.notify(2, Notification.Builder(context, "events").setSmallIcon(R.drawable.ic_vpn).setContentTitle("Bebekon VPN").setContentText(text).setContentIntent(open).setAutoCancel(true).build()) }
    }
}
