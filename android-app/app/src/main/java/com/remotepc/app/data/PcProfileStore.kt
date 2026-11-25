package com.remotepc.app.data

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject

data class PcProfile(
    val channelId: String,
    val keyBase64Url: String,
    val lanHostPort: String,
    val name: String
) {
    fun lanHost(): String = lanHostPort.substringBefore(":")
    fun lanPort(): Int = lanHostPort.substringAfter(":", "8787").toIntOrNull() ?: 8787

    fun toJson(): JSONObject = JSONObject().apply {
        put("id", channelId)
        put("k", keyBase64Url)
        put("lan", lanHostPort)
        put("n", name)
    }

    companion object {
        fun fromJson(obj: JSONObject): PcProfile = PcProfile(
            channelId = obj.getString("id"),
            keyBase64Url = obj.getString("k"),
            lanHostPort = obj.optString("lan", ""),
            name = obj.optString("n", "Windows PC")
        )

        fun parsePairingUri(raw: String): PcProfile? {
            val trimmed = raw.trim()
            if (!trimmed.startsWith("remotepc://", ignoreCase = true)) return null
            val uri = Uri.parse(trimmed)
            val id = uri.getQueryParameter("id")?.trim().orEmpty()
            val key = uri.getQueryParameter("k")?.trim().orEmpty()
            val lan = uri.getQueryParameter("lan")?.trim().orEmpty()
            val name = uri.getQueryParameter("n")?.trim().takeIf { !it.isNullOrEmpty() } ?: "Домашний ПК"
            if (id.length < 8 || key.length < 32) return null
            return PcProfile(id, key, lan, name)
        }
    }
}

data class PcNotificationRecord(
    val channelId: String,
    val pcName: String,
    val id: Long,
    val app: String,
    val title: String,
    val body: String,
    val tsMs: Long
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("cid", channelId)
        put("pc", pcName)
        put("id", id)
        put("app", app)
        put("title", title)
        put("body", body)
        put("ts", tsMs)
    }

    companion object {
        fun fromJson(obj: JSONObject): PcNotificationRecord = PcNotificationRecord(
            channelId = obj.optString("cid", ""),
            pcName = obj.optString("pc", "ПК"),
            id = obj.optLong("id", 0L),
            app = obj.optString("app", "Windows"),
            title = obj.optString("title", ""),
            body = obj.optString("body", ""),
            tsMs = obj.optLong("ts", 0L)
        )
    }
}

class PcProfileStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("remotepc_store", Context.MODE_PRIVATE)

    fun loadAll(): List<PcProfile> {
        val raw = prefs.getString("profiles_json", "[]") ?: "[]"
        val arr = try { JSONArray(raw) } catch (_: Exception) { JSONArray() }
        val list = ArrayList<PcProfile>(arr.length())
        for (i in 0 until arr.length()) {
            try {
                list.add(PcProfile.fromJson(arr.getJSONObject(i)))
            } catch (_: Exception) { }
        }
        return list
    }

    fun upsert(profile: PcProfile) {
        val list = loadAll().filterNot { it.channelId == profile.channelId }.toMutableList()
        list.add(0, profile)
        saveAll(list)
    }

    fun remove(channelId: String) {
        val list = loadAll().filterNot { it.channelId == channelId }
        saveAll(list)
    }

    private fun saveAll(list: List<PcProfile>) {
        val arr = JSONArray()
        list.forEach { arr.put(it.toJson()) }
        prefs.edit().putString("profiles_json", arr.toString()).apply()
    }

    var autoEnableMicOnAlert: Boolean
        get() = prefs.getBoolean("auto_mic_alert", false)
        set(v) { prefs.edit().putBoolean("auto_mic_alert", v).apply() }

    var outputVoiceToSpeakers: Boolean
        get() = prefs.getBoolean("output_speakers", false)
        set(v) { prefs.edit().putBoolean("output_speakers", v).apply() }

    var videoHeight: Int
        get() = prefs.getInt("video_height", 720).coerceIn(360, 1440)
        set(v) { prefs.edit().putInt("video_height", v.coerceIn(360, 1440)).apply() }

    var jpegQuality: Int
        get() = prefs.getInt("jpeg_quality", 58).coerceIn(15, 95)
        set(v) { prefs.edit().putInt("jpeg_quality", v.coerceIn(15, 95)).apply() }

    var targetFps: Int
        get() = prefs.getInt("target_fps", 18).coerceIn(1, 60)
        set(v) { prefs.edit().putInt("target_fps", v.coerceIn(1, 60)).apply() }

    var bgNotificationsEnabled: Boolean
        get() = prefs.getBoolean("bg_notifs_enabled", true)
        set(v) { prefs.edit().putBoolean("bg_notifs_enabled", v).apply() }

    fun getLastNotifId(channelId: String): Long =
        prefs.getLong("last_notif_id_$channelId", 0L)

    fun setLastNotifId(channelId: String, id: Long) {
        if (id > getLastNotifId(channelId)) {
            prefs.edit().putLong("last_notif_id_$channelId", id).apply()
        }
    }

    fun appendNotificationHistory(rec: PcNotificationRecord) {
        val current = loadNotificationHistory().toMutableList()
        current.removeAll { it.channelId == rec.channelId && it.id == rec.id }
        current.add(0, rec)
        val trimmed = if (current.size > 40) current.subList(0, 40) else current
        val arr = JSONArray()
        trimmed.forEach { arr.put(it.toJson()) }
        prefs.edit().putString("notif_history_json", arr.toString()).apply()
    }

    fun loadNotificationHistory(): List<PcNotificationRecord> {
        val raw = prefs.getString("notif_history_json", "[]") ?: "[]"
        val arr = try { JSONArray(raw) } catch (_: Exception) { JSONArray() }
        val list = ArrayList<PcNotificationRecord>(arr.length())
        for (i in 0 until arr.length()) {
            try {
                list.add(PcNotificationRecord.fromJson(arr.getJSONObject(i)))
            } catch (_: Exception) { }
        }
        return list
    }

    fun clearNotificationHistory() {
        prefs.edit().putString("notif_history_json", "[]").apply()
    }
}
