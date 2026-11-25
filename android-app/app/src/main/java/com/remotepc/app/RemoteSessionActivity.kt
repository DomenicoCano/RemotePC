package com.remotepc.app

import android.Manifest
import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import com.google.android.material.bottomsheet.BottomSheetDialog
import com.remotepc.app.data.PcProfile
import com.remotepc.app.data.PcProfileStore
import com.remotepc.app.net.DecodedDesktopFrame
import com.remotepc.app.net.HybridTransportClient
import com.remotepc.app.net.RemoteSessionListener
import com.remotepc.app.net.TransportRoute
import com.remotepc.app.service.ActiveSessionBridge
import com.remotepc.app.service.MicStreamingService
import com.remotepc.app.service.PcNotificationHelper
import com.remotepc.app.ui.DesktopInputCallbacks
import com.remotepc.app.ui.ImeProxyView
import com.remotepc.app.ui.InputMode
import com.remotepc.app.ui.RemoteDesktopView
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.abs

class RemoteSessionActivity : AppCompatActivity(), RemoteSessionListener {

    private lateinit var store: PcProfileStore
    private lateinit var profile: PcProfile
    private lateinit var transport: HybridTransportClient

    private lateinit var desktopView: RemoteDesktopView
    private lateinit var imeProxy: ImeProxyView
    private lateinit var tvStatusPill: TextView
    private lateinit var micAlertBanner: LinearLayout
    private lateinit var tvMicAlertText: TextView
    private lateinit var floatingDock: LinearLayout
    private lateinit var btnDockMic: TextView
    private lateinit var btnDockMode: TextView
    private lateinit var hotkeyBarContainer: HorizontalScrollView

    private val uiHandler = Handler(Looper.getMainLooper())
    private var isKeyboardVisible = false
    private var lastImeBottomPx = 0
    private var lastAutoImePopupMs = 0L
    private var pcHasVbCable = false
    private var isLandscapeLocked = false

    private var videoHeight = 720
    private var jpegQuality = 58
    private var targetFps = 18
    private var liveFps = 0
    private var liveKbps = 0
    private var liveRttMs = 0
    private var pcMonitorCount = 1
    private var activeMonitorIdx = 0
    private var isSessionConnected = false
    private var baseStatusDetail = ""
    private var dockDraggedByUser = false
    private var isStartedOnce = false

    private var lastConfigSendMs = 0L
    private var activeSheetStatsView: TextView? = null
    private var activeSheetHeightButtons: Map<Int, Button>? = null
    private var activeSheetQualitySeek: SeekBar? = null
    private var activeSheetQualityLabel: TextView? = null
    private var activeSheetFpsSeek: SeekBar? = null
    private var activeSheetFpsLabel: TextView? = null
    private var activeSheetFpsChips: Map<Int, TextView>? = null
    private var activeMonitorBtn: Button? = null

    // Sticky PC modifiers for hotkey bar
    private val stickyModifiers = linkedMapOf(
        17 to false, // Ctrl
        18 to false, // Alt
        91 to false, // Win
        16 to false  // Shift
    )
    private val modifierChips = mutableMapOf<Int, TextView>()

    private val micPermLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startMicStreaming()
        else Toast.makeText(this, "Разрешите доступ к микрофону для передачи голоса на ПК", Toast.LENGTH_LONG).show()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        store = PcProfileStore(this)
        videoHeight = store.videoHeight
        jpegQuality = store.jpegQuality
        targetFps = store.targetFps

        val jsonStr = intent.getStringExtra(EXTRA_PROFILE_JSON)
        if (jsonStr.isNullOrBlank()) {
            finish()
            return
        }
        profile = PcProfile.fromJson(JSONObject(jsonStr))
        baseStatusDetail = "Подключение к ${profile.name}..."

        buildUi()

        transport = HybridTransportClient(profile, videoHeight, jpegQuality, targetFps, this)
        ActiveSessionBridge.transport = transport
        ActiveSessionBridge.onMicRunningChanged = { running ->
            runOnUiThread { updateMicButtonUi(running, 0f) }
        }
        ActiveSessionBridge.onMicLevel = { level ->
            runOnUiThread { updateMicButtonUi(ActiveSessionBridge.isMicStreaming, level) }
        }
        transport.start()
    }

    override fun onStart() {
        super.onStart()
        if (isStartedOnce) {
            transport.sendJson(JSONObject().put("t", "stream_pause").put("pause", false))
        }
        isStartedOnce = true
    }

    override fun onStop() {
        super.onStop()
        if (!isChangingConfigurations) {
            transport.sendJson(JSONObject().put("t", "stream_pause").put("pause", true))
        }
    }

    override fun onDestroy() {
        if (ActiveSessionBridge.isMicStreaming) {
            MicStreamingService.stop(this)
        }
        ActiveSessionBridge.onMicLevel = null
        ActiveSessionBridge.onMicRunningChanged = null
        if (ActiveSessionBridge.transport === transport) {
            ActiveSessionBridge.transport = null
        }
        transport.stop()
        super.onDestroy()
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun buildUi() {
        val root = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
        }

        // 1. Desktop canvas
        desktopView = RemoteDesktopView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
            callbacks = object : DesktopInputCallbacks {
                override fun onMouseMoveRelative(dx: Int, dy: Int) {
                    transport.sendJson(JSONObject().put("t", "mouse_move").put("dx", dx).put("dy", dy))
                }

                override fun onMouseButton(btn: String, act: String, normX: Float?, normY: Float?) {
                    val obj = JSONObject().put("t", "mouse_btn").put("btn", btn).put("act", act)
                    if (normX != null && normY != null) {
                        obj.put("x", normX.toDouble()).put("y", normY.toDouble())
                    }
                    transport.sendJson(obj)
                }

                override fun onMouseWheel(dy: Int, dx: Int) {
                    transport.sendJson(JSONObject().put("t", "mouse_wheel").put("dy", dy).put("dx", dx))
                }
            }
        }
        root.addView(desktopView)

        // 2. Invisible IME bridge view
        imeProxy = ImeProxyView(this).apply {
            layoutParams = FrameLayout.LayoutParams(1, 1)
            onTextCommitted = { text -> handleImeText(text) }
            onBackspace = { count ->
                repeat(count.coerceAtMost(24)) { sendKeyTap(8) }
            }
            onEnter = { sendKeyTap(13) }
        }
        root.addView(imeProxy)

        // 3. Top status pill
        tvStatusPill = TextView(this).apply {
            text = "Подключение к ${profile.name}..."
            textSize = 10.5f
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
            setTextColor(Color.parseColor("#E2E8F0"))
            setBackgroundResource(R.drawable.bg_dock_pill)
            setPadding(dp(12), dp(5), dp(12), dp(5))
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.CENTER_HORIZONTAL
            ).apply {
                topMargin = dp(34)
                marginStart = dp(10)
                marginEnd = dp(10)
            }
        }
        root.addView(tvStatusPill)

        // 4. Mic Request Alert Banner (hidden by default)
        micAlertBanner = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            visibility = View.GONE
            setBackgroundColor(Color.parseColor("#E6163428"))
            setPadding(dp(14), dp(10), dp(10), dp(10))
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP
            ).apply {
                topMargin = dp(72)
                marginStart = dp(12)
                marginEnd = dp(12)
            }
        }
        tvMicAlertText = TextView(this).apply {
            text = "🎤 Программа на ПК запрашивает микрофон"
            textSize = 13f
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val btnBannerEnable = Button(this).apply {
            text = "Включить"
            isAllCaps = false
            textSize = 12f
            setBackgroundColor(Color.parseColor("#16A34A"))
            setTextColor(Color.WHITE)
            setOnClickListener {
                micAlertBanner.visibility = View.GONE
                if (!ActiveSessionBridge.isMicStreaming) toggleMic()
            }
        }
        val btnBannerClose = TextView(this).apply {
            text = " ✕ "
            textSize = 16f
            setTextColor(Color.parseColor("#CBD5E1"))
            setPadding(dp(8), dp(4), dp(4), dp(4))
            setOnClickListener { micAlertBanner.visibility = View.GONE }
        }
        micAlertBanner.addView(tvMicAlertText)
        micAlertBanner.addView(btnBannerEnable)
        micAlertBanner.addView(btnBannerClose)
        root.addView(micAlertBanner)

        // 5. Draggable Floating Quick Dock
        floatingDock = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundResource(R.drawable.bg_dock_pill)
            setPadding(dp(8), dp(6), dp(8), dp(6))
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.END
            ).apply {
                marginEnd = dp(14)
                bottomMargin = dp(24)
            }
        }

        val dragHandle = TextView(this).apply {
            text = "⋮⋮"
            textSize = 16f
            setTextColor(Color.parseColor("#64748B"))
            setPadding(dp(6), dp(6), dp(6), dp(6))
        }
        val btnKbd = createDockButton("⌨️") { toggleSoftKeyboard() }
        btnDockMic = createDockButton("🎙️") { toggleMic() }
        btnDockMode = createDockButton("🖱️") { toggleInputMode() }
        val btnMenu = createDockButton("⚡") { showPcMenuSheet() }

        floatingDock.addView(dragHandle)
        floatingDock.addView(btnKbd)
        floatingDock.addView(btnDockMic)
        floatingDock.addView(btnDockMode)
        floatingDock.addView(btnMenu)

        // Allow dragging dock freely by the handle
        var dX = 0f
        var dY = 0f
        dragHandle.setOnTouchListener { _, ev ->
            when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    dX = floatingDock.x - ev.rawX
                    dY = floatingDock.y - ev.rawY
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    dockDraggedByUser = true
                    val nx = (ev.rawX + dX).coerceIn(0f, (root.width - floatingDock.width).toFloat().coerceAtLeast(0f))
                    val ny = (ev.rawY + dY).coerceIn(dp(24).toFloat(), (root.height - floatingDock.height - lastImeBottomPx).toFloat().coerceAtLeast(dp(24).toFloat()))
                    floatingDock.x = nx
                    floatingDock.y = ny
                    true
                }
                else -> false
            }
        }
        root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ ->
            clampFloatingDockPosition(root)
        }
        root.addView(floatingDock)

        // 6. Smart PC Hotkey Bar (sits right above Android soft keyboard)
        hotkeyBarContainer = HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            visibility = View.GONE
            setBackgroundColor(Color.parseColor("#F0131722"))
            setPadding(dp(6), dp(6), dp(6), dp(6))
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM
            )
        }
        val keyRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }

        addKeyChip(keyRow, "Esc") { sendKeyWithModifiers(27) }
        addKeyChip(keyRow, "Tab") { sendKeyWithModifiers(9) }
        addStickyModifierChip(keyRow, "Ctrl", 17)
        addStickyModifierChip(keyRow, "Alt", 18)
        addStickyModifierChip(keyRow, "Win", 91)
        addStickyModifierChip(keyRow, "Shift", 16)
        addKeyChip(keyRow, "←") { sendKeyWithModifiers(37) }
        addKeyChip(keyRow, "↓") { sendKeyWithModifiers(40) }
        addKeyChip(keyRow, "↑") { sendKeyWithModifiers(38) }
        addKeyChip(keyRow, "→") { sendKeyWithModifiers(39) }
        addKeyChip(keyRow, "Del") { sendKeyWithModifiers(46) }
        addKeyChip(keyRow, "Ctrl+C") { sendHotkey(17, 67) }
        addKeyChip(keyRow, "Ctrl+V") { sendHotkey(17, 86) }
        addKeyChip(keyRow, "📋 Вставить с тел.") { sendPhoneClipboardToPc() }
        addKeyChip(keyRow, "Ctrl+Z") { sendHotkey(17, 90) }
        addKeyChip(keyRow, "Alt+Tab") { sendHotkey(18, 9) }
        addKeyChip(keyRow, "Скрыть ⌄") { hideSoftKeyboard() }

        hotkeyBarContainer.addView(keyRow)
        root.addView(hotkeyBarContainer)

        // Track Android 16 IME & system bar insets smoothly
        ViewCompat.setOnApplyWindowInsetsListener(root) { _, insets ->
            val imeInsets = insets.getInsets(WindowInsetsCompat.Type.ime())
            val sysBars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            val imeVisibleNow = insets.isVisible(WindowInsetsCompat.Type.ime())
            isKeyboardVisible = imeVisibleNow
            lastImeBottomPx = if (imeVisibleNow) imeInsets.bottom else sysBars.bottom

            (tvStatusPill.layoutParams as FrameLayout.LayoutParams).topMargin = sysBars.top + dp(8)
            (micAlertBanner.layoutParams as FrameLayout.LayoutParams).topMargin = sysBars.top + dp(46)

            if (imeVisibleNow) {
                hotkeyBarContainer.visibility = View.VISIBLE
                (hotkeyBarContainer.layoutParams as FrameLayout.LayoutParams).bottomMargin = imeInsets.bottom
                hotkeyBarContainer.requestLayout()
                desktopView.setImeOcclusion(imeInsets.bottom + dp(46))
                floatingDock.visibility = View.GONE
            } else {
                hotkeyBarContainer.visibility = View.GONE
                desktopView.setImeOcclusion(0)
                floatingDock.visibility = View.VISIBLE
                (floatingDock.layoutParams as FrameLayout.LayoutParams).bottomMargin = sysBars.bottom + dp(18)
                floatingDock.requestLayout()
                floatingDock.post { clampFloatingDockPosition(root) }
            }
            insets
        }

        setContentView(root)
    }

    private fun createDockButton(label: String, onClick: () -> Unit): TextView = TextView(this).apply {
        text = label
        textSize = 20f
        gravity = Gravity.CENTER
        setPadding(dp(11), dp(7), dp(11), dp(7))
        setTextColor(Color.WHITE)
        setOnClickListener { onClick() }
    }

    private fun addKeyChip(parent: LinearLayout, label: String, onClick: () -> Unit) {
        val tv = TextView(this).apply {
            text = label
            textSize = 13f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.parseColor("#E2E8F0"))
            setBackgroundResource(R.drawable.bg_key_chip)
            setPadding(dp(12), dp(8), dp(12), dp(8))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { marginEnd = dp(6) }
            setOnClickListener { onClick() }
        }
        parent.addView(tv)
    }

    private fun addStickyModifierChip(parent: LinearLayout, label: String, vk: Int) {
        val tv = TextView(this).apply {
            text = label
            textSize = 13f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.parseColor("#93C5FD"))
            setBackgroundResource(R.drawable.bg_key_chip)
            setPadding(dp(12), dp(8), dp(12), dp(8))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { marginEnd = dp(6) }
            setOnClickListener {
                val active = !(stickyModifiers[vk] ?: false)
                stickyModifiers[vk] = active
                refreshModifierChipVisual(vk)
            }
        }
        modifierChips[vk] = tv
        parent.addView(tv)
    }

    private fun refreshModifierChipVisual(vk: Int) {
        val active = stickyModifiers[vk] == true
        modifierChips[vk]?.apply {
            setBackgroundColor(if (active) Color.parseColor("#0284C7") else Color.parseColor("#252D3F"))
            setTextColor(if (active) Color.WHITE else Color.parseColor("#93C5FD"))
        }
    }

    private fun clearStickyModifiers() {
        for (vk in stickyModifiers.keys.toList()) {
            if (stickyModifiers[vk] == true) {
                stickyModifiers[vk] = false
                refreshModifierChipVisual(vk)
            }
        }
    }

    private fun handleImeText(text: String) {
        val activeMods = stickyModifiers.filterValues { it }.keys.toList()
        if (activeMods.isNotEmpty() && text.length == 1) {
            val ch = text[0].uppercaseChar()
            if (ch in 'A'..'Z' || ch in '0'..'9') {
                val keys = activeMods + ch.code
                sendHotkey(*keys.toIntArray())
                clearStickyModifiers()
                return
            }
        }
        transport.sendJson(JSONObject().put("t", "text").put("s", text))
    }

    private fun sendKeyTap(vk: Int) {
        transport.sendJson(JSONObject().put("t", "key").put("vk", vk).put("act", "tap"))
    }

    private fun sendKeyWithModifiers(vk: Int) {
        val activeMods = stickyModifiers.filterValues { it }.keys.toList()
        if (activeMods.isNotEmpty()) {
            sendHotkey(*(activeMods + vk).toIntArray())
            clearStickyModifiers()
        } else {
            sendKeyTap(vk)
        }
    }

    private fun sendHotkey(vararg vks: Int) {
        val arr = JSONArray()
        vks.forEach { arr.put(it) }
        transport.sendJson(JSONObject().put("t", "hotkey").put("keys", arr))
    }

    private fun sendSysAction(act: String) {
        transport.sendJson(JSONObject().put("t", "sys").put("act", act))
    }

    private fun toggleSoftKeyboard() {
        if (isKeyboardVisible) hideSoftKeyboard() else showSoftKeyboard()
    }

    private fun showSoftKeyboard() {
        imeProxy.resetComposing()
        imeProxy.requestFocus()
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        imm.showSoftInput(imeProxy, InputMethodManager.SHOW_IMPLICIT)
    }

    private fun hideSoftKeyboard() {
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        imm.hideSoftInputFromWindow(imeProxy.windowToken, 0)
    }

    private fun toggleInputMode() {
        if (desktopView.inputMode == InputMode.TOUCHPAD) {
            desktopView.inputMode = InputMode.DIRECT_TOUCH
            btnDockMode.text = "👆"
            Toast.makeText(this, "Режим: Прямое касание", Toast.LENGTH_SHORT).show()
        } else {
            desktopView.inputMode = InputMode.TOUCHPAD
            btnDockMode.text = "🖱️"
            Toast.makeText(this, "Режим: Тачпад (1 палец — курсор, 2 — прокрутка/ПКМ)", Toast.LENGTH_SHORT).show()
        }
    }

    private fun toggleMic() {
        if (ActiveSessionBridge.isMicStreaming) {
            MicStreamingService.stop(this)
            return
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            micPermLauncher.launch(Manifest.permission.RECORD_AUDIO)
            return
        }
        startMicStreaming()
    }

    private fun startMicStreaming() {
        if (!pcHasVbCable && !store.outputVoiceToSpeakers) {
            Toast.makeText(
                this,
                "На ПК ещё не установлен VB-Cable. Установите его кнопкой в RemotePC Host или включите вывод в динамики ПК.",
                Toast.LENGTH_LONG
            ).show()
        }
        MicStreamingService.start(this, store.outputVoiceToSpeakers)
    }

    private fun updateMicButtonUi(running: Boolean, level: Float) {
        if (running) {
            btnDockMic.text = if (level > 0.12f) "🔴" else "🎙️"
            btnDockMic.setBackgroundColor(Color.parseColor("#99DC2626"))
        } else {
            btnDockMic.text = "🎙️"
            btnDockMic.setBackgroundColor(Color.TRANSPARENT)
        }
    }

    private fun clampFloatingDockPosition(root: View) {
        if (!dockDraggedByUser || floatingDock.width <= 0 || root.width <= 0) return
        val maxX = (root.width - floatingDock.width).toFloat().coerceAtLeast(0f)
        val minY = dp(24).toFloat()
        val maxY = (root.height - floatingDock.height - lastImeBottomPx).toFloat().coerceAtLeast(minY)
        floatingDock.x = floatingDock.x.coerceIn(0f, maxX)
        floatingDock.y = floatingDock.y.coerceIn(minY, maxY)
    }

    private fun showPcMenuSheet() {
        val sheet = BottomSheetDialog(this)
        val scroll = ScrollView(this).apply {
            setBackgroundColor(Color.parseColor("#131722"))
        }
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(16), dp(18), dp(26))
        }

        fun header(text: String): TextView {
            val tv = TextView(this).apply {
                this.text = text
                textSize = 13f
                typeface = Typeface.DEFAULT_BOLD
                setTextColor(Color.parseColor("#38BDF8"))
                setPadding(0, dp(12), 0, dp(6))
            }
            box.addView(tv)
            return tv
        }

        fun buttonRow(items: List<Pair<String, () -> Unit>>) {
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply { bottomMargin = dp(8) }
            }
            for ((label, action) in items) {
                val b = Button(this).apply {
                    text = label
                    isAllCaps = false
                    textSize = 12.5f
                    setBackgroundColor(Color.parseColor("#232B3E"))
                    setTextColor(Color.WHITE)
                    layoutParams = LinearLayout.LayoutParams(0, dp(42), 1f).apply {
                        marginEnd = dp(6)
                    }
                    setOnClickListener {
                        action()
                        sheet.dismiss()
                    }
                }
                row.addView(b)
            }
            box.addView(row)
        }

        header("Качество картинки и FPS (применяется сразу)")

        val tvLiveStats = TextView(this).apply {
            textSize = 12.5f
            setTextColor(Color.parseColor("#94A3B8"))
            setPadding(0, 0, 0, dp(8))
        }
        activeSheetStatsView = tvLiveStats
        box.addView(tvLiveStats)

        // Presets row (keeps sheet open so user can keep fine-tuning)
        val presetRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(8) }
        }
        val presets = listOf(
            Triple("Эконом", Triple(480, 35, 10), "#1E293B"),
            Triple("Баланс", Triple(720, 55, 15), "#1E293B"),
            Triple("Чёткий", Triple(1080, 75, 24), "#1E293B"),
            Triple("Ультра", Triple(1440, 90, 60), "#1E293B")
        )
        for ((title, vals, bgHex) in presets) {
            val b = Button(this).apply {
                text = title
                isAllCaps = false
                textSize = 12f
                setPadding(dp(4), 0, dp(4), 0)
                setBackgroundColor(Color.parseColor(bgHex))
                setTextColor(Color.WHITE)
                layoutParams = LinearLayout.LayoutParams(0, dp(38), 1f).apply { marginEnd = dp(5) }
                setOnClickListener {
                    applyVideoConfig(vals.first, vals.second, vals.third, immediate = true)
                }
            }
            presetRow.addView(b)
        }
        box.addView(presetRow)

        // Resolution selector row
        val resButtons = mutableMapOf<Int, Button>()
        val resRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(10) }
        }
        for (h in listOf(480, 720, 900, 1080, 1440)) {
            val b = Button(this).apply {
                text = "${h}p"
                isAllCaps = false
                textSize = 11.5f
                setPadding(dp(2), 0, dp(2), 0)
                setTextColor(Color.WHITE)
                layoutParams = LinearLayout.LayoutParams(0, dp(38), 1f).apply { marginEnd = dp(4) }
                setOnClickListener {
                    applyVideoConfig(h, jpegQuality, targetFps, immediate = true)
                }
            }
            resButtons[h] = b
            resRow.addView(b)
        }
        activeSheetHeightButtons = resButtons
        box.addView(resRow)

        // JPEG quality SeekBar (15% .. 95%)
        val tvQualLabel = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.parseColor("#E2E8F0"))
            setPadding(0, dp(2), 0, dp(4))
        }
        activeSheetQualityLabel = tvQualLabel
        box.addView(tvQualLabel)

        val qualRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(10) }
        }
        val btnQualMinus = Button(this).apply {
            text = "−5%"
            isAllCaps = false
            textSize = 12f
            setBackgroundColor(Color.parseColor("#232B3E"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(dp(56), dp(36))
            setOnClickListener {
                applyVideoConfig(videoHeight, (jpegQuality - 5).coerceIn(15, 95), targetFps, immediate = true)
            }
        }
        val seekQuality = SeekBar(this).apply {
            max = 80 // 15..95
            progress = (jpegQuality - 15).coerceIn(0, 80)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginStart = dp(6)
                marginEnd = dp(6)
            }
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                    if (fromUser) {
                        applyVideoConfig(videoHeight, progress + 15, targetFps, immediate = false)
                    }
                }
                override fun onStartTrackingTouch(sb: SeekBar?) {}
                override fun onStopTrackingTouch(sb: SeekBar?) {
                    applyVideoConfig(videoHeight, (sb?.progress ?: 43) + 15, targetFps, immediate = true)
                }
            })
        }
        val btnQualPlus = Button(this).apply {
            text = "+5%"
            isAllCaps = false
            textSize = 12f
            setBackgroundColor(Color.parseColor("#232B3E"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(dp(56), dp(36))
            setOnClickListener {
                applyVideoConfig(videoHeight, (jpegQuality + 5).coerceIn(15, 95), targetFps, immediate = true)
            }
        }
        activeSheetQualitySeek = seekQuality
        qualRow.addView(btnQualMinus)
        qualRow.addView(seekQuality)
        qualRow.addView(btnQualPlus)
        box.addView(qualRow)

        // FPS SeekBar (1 .. 60 FPS) + quick chips
        val tvFpsLabel = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.parseColor("#E2E8F0"))
            setPadding(0, dp(2), 0, dp(4))
        }
        activeSheetFpsLabel = tvFpsLabel
        box.addView(tvFpsLabel)

        val fpsRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(6) }
        }
        val btnFpsMinus = Button(this).apply {
            text = "−"
            textSize = 15f
            setBackgroundColor(Color.parseColor("#232B3E"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(dp(46), dp(36))
            setOnClickListener {
                val step = if (targetFps > 20) 5 else 1
                applyVideoConfig(videoHeight, jpegQuality, (targetFps - step).coerceIn(1, 60), immediate = true)
            }
        }
        val seekFps = SeekBar(this).apply {
            max = 59 // 1..60
            progress = (targetFps - 1).coerceIn(0, 59)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginStart = dp(6)
                marginEnd = dp(6)
            }
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                    if (fromUser) {
                        applyVideoConfig(videoHeight, jpegQuality, progress + 1, immediate = false)
                    }
                }
                override fun onStartTrackingTouch(sb: SeekBar?) {}
                override fun onStopTrackingTouch(sb: SeekBar?) {
                    applyVideoConfig(videoHeight, jpegQuality, (sb?.progress ?: 17) + 1, immediate = true)
                }
            })
        }
        val btnFpsPlus = Button(this).apply {
            text = "+"
            textSize = 15f
            setBackgroundColor(Color.parseColor("#232B3E"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(dp(46), dp(36))
            setOnClickListener {
                val step = if (targetFps >= 20) 5 else 1
                applyVideoConfig(videoHeight, jpegQuality, (targetFps + step).coerceIn(1, 60), immediate = true)
            }
        }
        activeSheetFpsSeek = seekFps
        fpsRow.addView(btnFpsMinus)
        fpsRow.addView(seekFps)
        fpsRow.addView(btnFpsPlus)
        box.addView(fpsRow)

        val fpsChipsMap = mutableMapOf<Int, TextView>()
        val fpsChipsRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(8) }
        }
        for (fpsVal in listOf(5, 10, 15, 24, 30, 60)) {
            val chip = TextView(this).apply {
                text = "$fpsVal"
                textSize = 12f
                gravity = Gravity.CENTER
                typeface = Typeface.DEFAULT_BOLD
                setTextColor(Color.WHITE)
                setPadding(0, dp(7), 0, dp(7))
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                    marginEnd = dp(4)
                }
                setOnClickListener {
                    applyVideoConfig(videoHeight, jpegQuality, fpsVal, immediate = true)
                }
            }
            fpsChipsMap[fpsVal] = chip
            fpsChipsRow.addView(chip)
        }
        activeSheetFpsChips = fpsChipsMap
        box.addView(fpsChipsRow)

        refreshVideoControlsInSheet()

        header("Громкость и медиа ПК")
        buttonRow(listOf(
            "🔉 Тише" to { sendSysAction("vol_down") },
            "🔊 Громче" to { sendSysAction("vol_up") },
            "🔇 Mute" to { sendSysAction("vol_mute") }
        ))
        buttonRow(listOf(
            "⏮ Пред." to { sendSysAction("media_prev") },
            "⏯ Пауза" to { sendSysAction("media_play") },
            "⏭ След." to { sendSysAction("media_next") }
        ))

        header("Команды Windows")
        buttonRow(listOf(
            "Alt+Tab" to { sendSysAction("alt_tab") },
            "Диспетчер" to { sendSysAction("task_manager") },
            "Свернуть всё" to { sendSysAction("minimize_all") }
        ))

        header("Буфер обмена и экран")
        buttonRow(listOf(
            "📲→💻 Вставить на ПК" to { sendPhoneClipboardToPc() },
            "💻→📲 Забрать с ПК" to { transport.sendJson(JSONObject().put("t", "clip_get")) }
        ))

        val monitorRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { bottomMargin = dp(8) }
        }
        val btnResetZoom = Button(this).apply {
            text = "Сбросить зум"
            isAllCaps = false
            textSize = 12.5f
            setBackgroundColor(Color.parseColor("#232B3E"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginEnd = dp(6) }
            setOnClickListener {
                desktopView.resetZoom()
                sheet.dismiss()
            }
        }
        val btnOrient = Button(this).apply {
            text = if (isLandscapeLocked) "Автоповорот" else "Горизонтально"
            isAllCaps = false
            textSize = 12.5f
            setBackgroundColor(Color.parseColor("#232B3E"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginEnd = dp(6) }
            setOnClickListener {
                toggleOrientation()
                sheet.dismiss()
            }
        }
        monitorRow.addView(btnResetZoom)
        monitorRow.addView(btnOrient)
        if (pcMonitorCount > 1) {
            val btnMon = Button(this).apply {
                text = "Монитор ${activeMonitorIdx + 1}/$pcMonitorCount"
                isAllCaps = false
                textSize = 12.5f
                setBackgroundColor(Color.parseColor("#1E3A5F"))
                setTextColor(Color.WHITE)
                layoutParams = LinearLayout.LayoutParams(0, dp(42), 1f)
                setOnClickListener {
                    val nextIdx = (activeMonitorIdx + 1) % pcMonitorCount
                    transport.sendJson(JSONObject().put("t", "monitor").put("idx", nextIdx))
                }
            }
            activeMonitorBtn = btnMon
            monitorRow.addView(btnMon)
        }
        box.addView(monitorRow)

        header("Микрофон со смартфона")
        val swAutoMic = Switch(this).apply {
            text = "Авто-включать микрофон при вызове на ПК"
            setTextColor(Color.WHITE)
            isChecked = store.autoEnableMicOnAlert
            setPadding(0, dp(6), 0, dp(6))
            setOnCheckedChangeListener { _, checked -> store.autoEnableMicOnAlert = checked }
        }
        val swSpeakers = Switch(this).apply {
            text = "Выводить голос в динамики ПК"
            setTextColor(Color.WHITE)
            isChecked = store.outputVoiceToSpeakers
            setPadding(0, dp(6), 0, dp(10))
            setOnCheckedChangeListener { _, checked ->
                store.outputVoiceToSpeakers = checked
                transport.sendJson(
                    JSONObject().put("t", "mic_ctrl").put("on", ActiveSessionBridge.isMicStreaming).put("speakers", checked)
                )
            }
        }
        box.addView(swAutoMic)
        box.addView(swSpeakers)

        header("Питание ПК (с подтверждением)")
        buttonRow(listOf(
            "🔒 Блокировка" to { sendSysAction("lock") },
            "🌙 Сон" to { confirmPower("Перевести ПК в спящий режим?", "sleep") },
            "⏻ Выключить" to { confirmPower("Выключить компьютер через 5 секунд?", "shutdown") }
        ))

        val btnDisconnect = Button(this).apply {
            text = "Отключиться от ПК"
            isAllCaps = false
            setBackgroundColor(Color.parseColor("#7F1D1D"))
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(46)
            ).apply { topMargin = dp(10) }
            setOnClickListener {
                sheet.dismiss()
                finish()
            }
        }
        box.addView(btnDisconnect)

        sheet.setOnDismissListener {
            activeSheetStatsView = null
            activeSheetHeightButtons = null
            activeSheetQualitySeek = null
            activeSheetQualityLabel = null
            activeSheetFpsSeek = null
            activeSheetFpsLabel = null
            activeSheetFpsChips = null
            activeMonitorBtn = null
        }

        scroll.addView(box)
        sheet.setContentView(scroll)
        sheet.show()
    }

    private fun applyVideoConfig(height: Int, quality: Int, fps: Int, immediate: Boolean) {
        videoHeight = height.coerceIn(360, 1440)
        jpegQuality = quality.coerceIn(15, 95)
        targetFps = fps.coerceIn(1, 60)

        store.videoHeight = videoHeight
        store.jpegQuality = jpegQuality
        store.targetFps = targetFps

        refreshVideoControlsInSheet()
        refreshStatusPill()

        val now = System.currentTimeMillis()
        if (immediate || (now - lastConfigSendMs) >= 75L) {
            lastConfigSendMs = now
            transport.sendVideoConfig(videoHeight, jpegQuality, targetFps)
        }
    }

    private fun sendPhoneClipboardToPc() {
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clip = cm.primaryClip
        val text = if (clip != null && clip.itemCount > 0) {
            clip.getItemAt(0).coerceToText(this)?.toString().orEmpty()
        } else ""
        if (text.isEmpty()) {
            Toast.makeText(this, "Буфер обмена телефона пуст", Toast.LENGTH_SHORT).show()
            return
        }
        transport.sendJson(JSONObject().put("t", "clip_set").put("text", text).put("paste", true))
        Toast.makeText(this, "Отправлено на ПК (${text.length} симв.)", Toast.LENGTH_SHORT).show()
    }

    private fun refreshVideoControlsInSheet() {
        val rttPart = if (liveRttMs > 0) " • $liveRttMs мс" else ""
        activeSheetStatsView?.text =
            "Поток: ${videoHeight}p • JPEG ${jpegQuality}% • Лимит $targetFps FPS (факт: $liveFps FPS • $liveKbps КБ/с$rttPart)"
        activeSheetQualityLabel?.text = "Чёткость сжатия JPEG: $jpegQuality% (меньше = быстрее)"
        activeSheetFpsLabel?.text = "Максимум кадров: $targetFps FPS"

        activeSheetQualitySeek?.let { sb ->
            val targetProg = (jpegQuality - 15).coerceIn(0, 80)
            if (sb.progress != targetProg) sb.progress = targetProg
        }
        activeSheetFpsSeek?.let { sb ->
            val targetProg = (targetFps - 1).coerceIn(0, 59)
            if (sb.progress != targetProg) sb.progress = targetProg
        }

        activeSheetHeightButtons?.forEach { (h, btn) ->
            btn.setBackgroundColor(
                Color.parseColor(if (h == videoHeight) "#0284C7" else "#232B3E")
            )
        }
        activeSheetFpsChips?.forEach { (f, tv) ->
            tv.setBackgroundColor(
                Color.parseColor(if (f == targetFps) "#0284C7" else "#232B3E")
            )
        }
    }

    private fun refreshStatusPill() {
        if (isSessionConnected) {
            val rttPart = if (liveRttMs > 0) " • $liveRttMs мс" else ""
            tvStatusPill.text = "$baseStatusDetail • ${videoHeight}p Q${jpegQuality}% • $liveFps FPS • $liveKbps КБ/с$rttPart"
            tvStatusPill.setTextColor(Color.parseColor("#6EE7B7"))
            tvStatusPill.alpha = 0.72f
        } else {
            tvStatusPill.text = baseStatusDetail
            tvStatusPill.setTextColor(Color.parseColor("#FCD34D"))
            tvStatusPill.alpha = 1.0f
        }
    }

    private fun toggleOrientation() {
        isLandscapeLocked = !isLandscapeLocked
        requestedOrientation = if (isLandscapeLocked) {
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
    }

    private fun confirmPower(msg: String, action: String) {
        AlertDialog.Builder(this)
            .setTitle("Подтверждение")
            .setMessage(msg)
            .setPositiveButton("Да, выполнить") { _, _ -> sendSysAction(action) }
            .setNegativeButton("Отмена", null)
            .show()
    }

    // --- RemoteSessionListener callbacks ---

    override fun onStatusChanged(connected: Boolean, route: TransportRoute, detail: String) {
        runOnUiThread {
            isSessionConnected = connected
            baseStatusDetail = detail
            refreshStatusPill()
        }
    }

    override fun onStreamStats(fps: Int, kbps: Int, rttMs: Int) {
        runOnUiThread {
            liveFps = fps
            liveKbps = kbps
            if (rttMs > 0) liveRttMs = rttMs
            refreshStatusPill()
            refreshVideoControlsInSheet()
        }
    }

    override fun onClipboardReceived(text: String) {
        runOnUiThread {
            if (text.isEmpty()) {
                Toast.makeText(this, "Буфер обмена ПК пуст", Toast.LENGTH_SHORT).show()
                return@runOnUiThread
            }
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("RemotePC", text))
            Toast.makeText(this, "Скопировано с ПК (${text.length} симв.)", Toast.LENGTH_SHORT).show()
        }
    }

    override fun onMonitorsUpdated(count: Int, activeIndex: Int) {
        runOnUiThread {
            pcMonitorCount = count.coerceAtLeast(1)
            activeMonitorIdx = activeIndex.coerceIn(0, pcMonitorCount - 1)
            activeMonitorBtn?.text = "Монитор ${activeMonitorIdx + 1}/$pcMonitorCount"
        }
    }

    override fun onLanEndpointUpdated(newLanHostPort: String) {
        runOnUiThread {
            if (newLanHostPort.isNotBlank() && newLanHostPort != profile.lanHostPort) {
                profile = profile.copy(lanHostPort = newLanHostPort)
                store.upsert(profile)
            }
        }
    }

    override fun onPcNotification(obj: JSONObject) {
        PcNotificationHelper.handleIncomingPcNotif(this, profile, obj)
    }

    override fun onFrameDecoded(frame: DecodedDesktopFrame) {
        runOnUiThread {
            desktopView.updateFrame(frame)
        }
    }

    override fun onImeHint(show: Boolean, caretNormY: Float) {
        runOnUiThread {
            if (show) {
                val now = System.currentTimeMillis()
                if (!isKeyboardVisible && (now - lastAutoImePopupMs) > 700L) {
                    lastAutoImePopupMs = now
                    showSoftKeyboard()
                }
                uiHandler.postDelayed({
                    desktopView.ensureNormYVisibleAboveIme(caretNormY)
                }, 220L)
            }
        }
    }

    override fun onMicAlert(active: Boolean, apps: List<String>) {
        runOnUiThread {
            if (!active || apps.isEmpty()) {
                micAlertBanner.visibility = View.GONE
                return@runOnUiThread
            }
            if (ActiveSessionBridge.isMicStreaming) return@runOnUiThread

            if (store.autoEnableMicOnAlert &&
                ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
            ) {
                startMicStreaming()
                Toast.makeText(this, "🎤 Авто-включение микрофона (${apps.joinToString()})", Toast.LENGTH_SHORT).show()
                return@runOnUiThread
            }

            tvMicAlertText.text = "🎤 ${apps.joinToString(", ")} на ПК запрашивает микрофон"
            micAlertBanner.visibility = View.VISIBLE
            vibrateShort()
        }
    }

    override fun onMicState(on: Boolean, hasVbCable: Boolean, switched: Boolean, speakers: Boolean) {
        pcHasVbCable = hasVbCable
        runOnUiThread {
            if (on) {
                val where = if (speakers) "динамики ПК" else if (switched) "виртуальный микрофон ПК" else "ожидание VB-Cable"
                Toast.makeText(this, "Микрофон активен → $where", Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun vibrateShort() {
        try {
            if (Build.VERSION.SDK_INT >= 31) {
                val vm = getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as VibratorManager
                vm.defaultVibrator.vibrate(VibrationEffect.createOneShot(120, VibrationEffect.DEFAULT_AMPLITUDE))
            } else {
                @Suppress("DEPRECATION")
                val v = getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
                v.vibrate(VibrationEffect.createOneShot(120, VibrationEffect.DEFAULT_AMPLITUDE))
            }
        } catch (_: Exception) { }
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()

    companion object {
        const val EXTRA_PROFILE_JSON = "profile_json"
    }
}
