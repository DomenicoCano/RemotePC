package com.remotepc.app

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import com.remotepc.app.data.PcProfile
import com.remotepc.app.data.PcProfileStore
import com.remotepc.app.service.PcNotificationHelper
import com.remotepc.app.service.PcNotificationService
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : AppCompatActivity() {

    private lateinit var store: PcProfileStore
    private lateinit var listContainer: LinearLayout
    private lateinit var notifHistoryContainer: LinearLayout
    private lateinit var notifHeaderRow: LinearLayout
    private lateinit var tvNetBadge: TextView
    private lateinit var btnBatteryOpt: Button
    private lateinit var tvBatteryStatus: TextView

    private val notifPermLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) {
            PcNotificationService.syncState(this, requestImmediateSync = true)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = PcProfileStore(this)
        buildUi()
        requestNotificationPermissionIfNeeded()
        PcNotificationService.syncState(this)
        handleIncomingIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIncomingIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        updateNetworkBadge()
        updateBatteryUi()
        renderProfiles()
        renderNotificationHistory()
        PcNotificationHelper.onHistoryUpdated = { renderNotificationHistory() }
        PcNotificationService.syncState(this, requestImmediateSync = true)
    }

    override fun onPause() {
        PcNotificationHelper.onHistoryUpdated = null
        super.onPause()
    }

    private fun buildUi() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.parseColor("#0E1118"))
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
        }

        ViewCompat.setOnApplyWindowInsetsListener(root) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.setPadding(dp(18), bars.top + dp(18), dp(18), bars.bottom + dp(16))
            insets
        }

        val title = TextView(this).apply {
            text = "RemotePC"
            textSize = 28f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.parseColor("#38BDF8"))
        }
        val subtitle = TextView(this).apply {
            text = "Удалённый рабочий стол, клавиатура и микрофон ПК (AES-256-GCM)"
            textSize = 13f
            setTextColor(Color.parseColor("#94A3B8"))
            setPadding(0, dp(4), 0, dp(12))
        }
        tvNetBadge = TextView(this).apply {
            textSize = 13f
            setPadding(dp(12), dp(8), dp(12), dp(8))
            setBackgroundResource(R.drawable.bg_card)
            setTextColor(Color.parseColor("#7DD3FC"))
        }

        val actionRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(14), 0, dp(16))
        }

        val btnQr = Button(this).apply {
            text = "Сканировать QR"
            isAllCaps = false
            setBackgroundColor(Color.parseColor("#187AC6"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, dp(48), 1f).apply { marginEnd = dp(8) }
            setOnClickListener { launchQrScanner() }
        }

        val btnManual = Button(this).apply {
            text = "Ввести код"
            isAllCaps = false
            setBackgroundColor(Color.parseColor("#253047"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, dp(48), 1f).apply { marginStart = dp(8) }
            setOnClickListener { showManualPairDialog() }
        }
        actionRow.addView(btnQr)
        actionRow.addView(btnManual)

        // Background PC notifications card (screen-off delivery)
        val notifCard = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.bg_card)
            setPadding(dp(14), dp(12), dp(14), dp(12))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(14) }
        }
        val notifTopRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        val lblNotifTitle = TextView(this).apply {
            text = "Уведомления с ПК в фоне"
            textSize = 15f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val swBgNotifs = Switch(this).apply {
            isChecked = store.bgNotificationsEnabled
            setOnCheckedChangeListener { _, checked ->
                store.bgNotificationsEnabled = checked
                PcNotificationService.syncState(this@MainActivity, requestImmediateSync = checked)
            }
        }
        notifTopRow.addView(lblNotifTitle)
        notifTopRow.addView(swBgNotifs)

        val lblNotifSub = TextView(this).apply {
            text = "Доставка по Wi-Fi и 4G/5G даже при выключенном экране"
            textSize = 12f
            setTextColor(Color.parseColor("#94A3B8"))
            setPadding(0, dp(2), 0, dp(8))
        }
        tvBatteryStatus = TextView(this).apply {
            text = "✓ Без ограничений батареи (работает при выключенном экране)"
            textSize = 12f
            setTextColor(Color.parseColor("#6EE7B7"))
        }
        btnBatteryOpt = Button(this).apply {
            text = "⚡ Отключить экономию батареи для фона"
            isAllCaps = false
            textSize = 13f
            setBackgroundColor(Color.parseColor("#B45309"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(40)
            ).apply { topMargin = dp(4) }
            setOnClickListener { requestIgnoreBatteryOptimizations() }
        }
        notifCard.addView(notifTopRow)
        notifCard.addView(lblNotifSub)
        notifCard.addView(tvBatteryStatus)
        notifCard.addView(btnBatteryOpt)

        val scroll = ScrollView(this).apply {
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f
            )
        }
        val scrollContent = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }

        val lblSaved = TextView(this).apply {
            text = "Ваши компьютеры"
            textSize = 15f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.parseColor("#E2E8F0"))
            setPadding(0, dp(2), 0, dp(10))
        }
        listContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }

        notifHeaderRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(14), 0, dp(8))
        }
        val lblRecentNotifs = TextView(this).apply {
            text = "Последние уведомления с ПК"
            textSize = 15f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.parseColor("#E2E8F0"))
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val btnClearNotifs = TextView(this).apply {
            text = "Очистить"
            textSize = 12.5f
            setTextColor(Color.parseColor("#38BDF8"))
            setPadding(dp(8), dp(4), dp(4), dp(4))
            setOnClickListener {
                store.clearNotificationHistory()
                renderNotificationHistory()
            }
        }
        notifHeaderRow.addView(lblRecentNotifs)
        notifHeaderRow.addView(btnClearNotifs)

        notifHistoryContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }

        scrollContent.addView(lblSaved)
        scrollContent.addView(listContainer)
        scrollContent.addView(notifHeaderRow)
        scrollContent.addView(notifHistoryContainer)
        scroll.addView(scrollContent)

        root.addView(title)
        root.addView(subtitle)
        root.addView(tvNetBadge)
        root.addView(actionRow)
        root.addView(notifCard)
        root.addView(scroll)
        setContentView(root)
    }

    private fun updateBatteryUi() {
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        val ignoring = pm.isIgnoringBatteryOptimizations(packageName)
        tvBatteryStatus.visibility = if (ignoring) View.VISIBLE else View.GONE
        btnBatteryOpt.visibility = if (ignoring) View.GONE else View.VISIBLE
    }

    private fun requestIgnoreBatteryOptimizations() {
        try {
            val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                data = Uri.parse("package:$packageName")
            }
            startActivity(intent)
        } catch (_: Exception) {
            try {
                startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            } catch (_: Exception) {
                Toast.makeText(this, "Откройте Настройки → Батарея → Без ограничений для RemotePC", Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun renderNotificationHistory() {
        val items = store.loadNotificationHistory()
        notifHistoryContainer.removeAllViews()
        notifHeaderRow.visibility = if (items.isEmpty()) View.GONE else View.VISIBLE
        if (items.isEmpty()) return

        val fmt = SimpleDateFormat("HH:mm", Locale.getDefault())
        for (item in items.take(15)) {
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setBackgroundResource(R.drawable.bg_card)
                setPadding(dp(14), dp(10), dp(14), dp(10))
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply { bottomMargin = dp(8) }
            }
            val timeStr = if (item.tsMs > 0L) fmt.format(Date(item.tsMs)) else ""
            val header = TextView(this).apply {
                text = "${item.app} • ${item.pcName}  $timeStr"
                textSize = 11.5f
                setTextColor(Color.parseColor("#38BDF8"))
            }
            val titleTv = TextView(this).apply {
                text = item.title
                textSize = 14f
                typeface = Typeface.DEFAULT_BOLD
                setTextColor(Color.WHITE)
                setPadding(0, dp(2), 0, dp(2))
            }
            row.addView(header)
            row.addView(titleTv)
            if (item.body.isNotBlank()) {
                val bodyTv = TextView(this).apply {
                    text = item.body
                    textSize = 13f
                    setTextColor(Color.parseColor("#CBD5E1"))
                }
                row.addView(bodyTv)
            }
            notifHistoryContainer.addView(row)
        }
    }

    private fun updateNetworkBadge() {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        val hasWifi = caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
        val hasCell = caps?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) == true
        tvNetBadge.text = when {
            hasWifi -> "Сеть: Домашний Wi-Fi (приоритет прямого канала < 5 мс)"
            hasCell -> "Сеть: Мобильный интернет 4G/5G (облачный WSS-канал TLS)"
            else -> "Сеть: Проверка подключения..."
        }
    }

    private fun renderProfiles() {
        listContainer.removeAllViews()
        val profiles = store.loadAll()

        if (profiles.isEmpty()) {
            val empty = TextView(this).apply {
                text = "Нет привязанных ПК.\n\nЗапустите RemotePcHost.exe на компьютере и отсканируйте QR-код или нажмите «Привязать по USB»."
                textSize = 14f
                setTextColor(Color.parseColor("#94A3B8"))
                setPadding(dp(16), dp(22), dp(16), dp(22))
                setBackgroundResource(R.drawable.bg_card)
            }
            listContainer.addView(empty)
            return
        }

        for (p in profiles) {
            val card = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setBackgroundResource(R.drawable.bg_card)
                setPadding(dp(16), dp(14), dp(12), dp(14))
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply { bottomMargin = dp(10) }
                setOnClickListener { openSession(p) }
            }

            val infoCol = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            val tvName = TextView(this).apply {
                text = p.name
                textSize = 17f
                typeface = Typeface.DEFAULT_BOLD
                setTextColor(Color.WHITE)
            }
            val tvDetail = TextView(this).apply {
                text = "LAN: ${p.lanHostPort}  •  ID: ${p.channelId.take(8)}…"
                textSize = 12f
                setTextColor(Color.parseColor("#94A3B8"))
                setPadding(0, dp(3), 0, 0)
            }
            infoCol.addView(tvName)
            infoCol.addView(tvDetail)

            val btnConnect = Button(this).apply {
                text = "Открыть"
                isAllCaps = false
                setBackgroundColor(Color.parseColor("#1D9BF0"))
                setTextColor(Color.WHITE)
                setOnClickListener { openSession(p) }
            }

            val btnDel = Button(this).apply {
                text = "✕"
                setBackgroundColor(Color.TRANSPARENT)
                setTextColor(Color.parseColor("#94A3B8"))
                layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
                setOnClickListener {
                    AlertDialog.Builder(this@MainActivity)
                        .setTitle("Удалить ${p.name}?")
                        .setPositiveButton("Удалить") { _, _ ->
                            store.remove(p.channelId)
                            PcNotificationService.syncState(this@MainActivity)
                            renderProfiles()
                        }
                        .setNegativeButton("Отмена", null)
                        .show()
                }
            }

            card.addView(infoCol)
            card.addView(btnConnect)
            card.addView(btnDel)
            listContainer.addView(card)
        }
    }

    private fun handleIncomingIntent(intent: Intent?) {
        if (intent == null) return
        val extraUri = intent.getStringExtra("pair_uri")
        val dataUri = intent.dataString
        val uriStr = extraUri ?: dataUri
        if (!uriStr.isNullOrBlank()) {
            intent.removeExtra("pair_uri")
            handlePairingUri(uriStr, autoConnect = true)
        }
    }

    private fun handlePairingUri(uriStr: String, autoConnect: Boolean) {
        val profile = PcProfile.parsePairingUri(uriStr)
        if (profile == null) {
            Toast.makeText(this, "Неверный код привязки RemotePC", Toast.LENGTH_LONG).show()
            return
        }
        store.upsert(profile)
        renderProfiles()
        PcNotificationService.syncState(this, requestImmediateSync = true)
        Toast.makeText(this, "ПК «${profile.name}» привязан!", Toast.LENGTH_SHORT).show()
        if (autoConnect) {
            openSession(profile)
        }
    }

    private fun openSession(profile: PcProfile) {
        val i = Intent(this, RemoteSessionActivity::class.java).apply {
            putExtra(RemoteSessionActivity.EXTRA_PROFILE_JSON, profile.toJson().toString())
        }
        startActivity(i)
    }

    private fun launchQrScanner() {
        val options = GmsBarcodeScannerOptions.Builder()
            .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
            .enableAutoZoom()
            .build()
        GmsBarcodeScanning.getClient(this, options)
            .startScan()
            .addOnSuccessListener { barcode ->
                val raw = barcode.rawValue
                if (!raw.isNullOrBlank()) {
                    handlePairingUri(raw, autoConnect = true)
                }
            }
            .addOnFailureListener {
                Toast.makeText(this, "Не удалось открыть QR-сканер, используйте «Ввести код» или USB-привязку", Toast.LENGTH_LONG).show()
            }
    }

    private fun showManualPairDialog() {
        val input = EditText(this).apply {
            hint = "remotepc://pair?v=1&id=..."
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE
            setTextColor(Color.WHITE)
            setHintTextColor(Color.parseColor("#64748B"))
        }
        AlertDialog.Builder(this)
            .setTitle("Вставьте код привязки ПК")
            .setView(input)
            .setPositiveButton("Привязать") { _, _ ->
                handlePairingUri(input.text.toString(), autoConnect = true)
            }
            .setNegativeButton("Отмена", null)
            .show()
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notifPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()
}
