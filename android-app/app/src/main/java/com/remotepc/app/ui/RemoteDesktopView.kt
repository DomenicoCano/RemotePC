package com.remotepc.app.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.os.Handler
import android.os.Looper
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.view.ViewConfiguration
import com.remotepc.app.net.DecodedDesktopFrame
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min

enum class InputMode { TOUCHPAD, DIRECT_TOUCH }

interface DesktopInputCallbacks {
    fun onMouseMoveRelative(dx: Int, dy: Int)
    fun onMouseButton(btn: String, act: String, normX: Float? = null, normY: Float? = null)
    fun onMouseWheel(dy: Int, dx: Int = 0)
}

/**
 * High-performance desktop renderer with Pinch-to-Zoom (up to 5x), smooth panning,
 * Touchpad mode (laptop-style cursor, tap=LMB, 2-finger tap=RMB, 2-finger swipe=scroll,
 * double-tap-hold=drag), Direct Touch mode, and automatic IME caret avoidance shift.
 */
class RemoteDesktopView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var callbacks: DesktopInputCallbacks? = null
    var inputMode: InputMode = InputMode.TOUCHPAD

    private val bitmapPaint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val drawMatrix = Matrix()
    private val uiHandler = Handler(Looper.getMainLooper())

    private var currentBitmap: Bitmap? = null
    private var pcScreenW = 2560
    private var pcScreenH = 1440

    // Viewport transform: zoom >= 1.0 relative to fit-screen scale
    private var userZoom = 1f
    private var panX = 0f
    private var panY = 0f
    private var imeOcclusionBottomPx = 0
    private var imeFocusNormY: Float? = null

    // Touch gesture tracking
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()
    private var downX = 0f
    private var downY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var downTime = 0L
    private var movedBeyondSlop = false
    private var maxPointersInGesture = 0
    private var twoFingerLastY = 0f
    private var twoFingerLastX = 0f
    private var twoFingerScrollAccum = 0f
    private var isPinching = false

    // Double-tap & hold drag for Touchpad mode + sub-pixel cursor accumulator
    private var lastSingleTapTime = 0L
    private var lastDirectTapX = 0f
    private var lastDirectTapY = 0f
    private var isSecondTapCandidate = false
    private var isDraggingLeftButton = false
    private var remDx = 0f
    private var remDy = 0f

    // Direct touch long press -> RMB
    private var longPressTriggered = false
    private val longPressRunnable = Runnable {
        if (inputMode == InputMode.DIRECT_TOUCH && !movedBeyondSlop && maxPointersInGesture == 1) {
            longPressTriggered = true
            val pt = viewToNormalizedPc(downX, downY)
            if (pt != null) {
                callbacks?.onMouseButton("right", "click", pt.first, pt.second)
            }
        }
    }

    private val scaleDetector = ScaleGestureDetector(context, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScaleBegin(detector: ScaleGestureDetector): Boolean {
            isPinching = true
            return true
        }

        override fun onScale(detector: ScaleGestureDetector): Boolean {
            val prevZoom = userZoom
            val nextZoom = (userZoom * detector.scaleFactor).coerceIn(1f, 5f)
            if (abs(nextZoom - prevZoom) > 0.001f) {
                val factor = nextZoom / prevZoom
                val fx = detector.focusX - width / 2f
                val fy = detector.focusY - visibleHeight() / 2f
                panX = (panX - fx) * factor + fx
                panY = (panY - fy) * factor + fy
                userZoom = nextZoom
                clampPan()
                invalidate()
            }
            return true
        }

        override fun onScaleEnd(detector: ScaleGestureDetector) {
            isPinching = false
        }
    })

    fun updateFrame(frame: DecodedDesktopFrame) {
        currentBitmap = frame.bitmap
        pcScreenW = frame.screenWidth
        pcScreenH = frame.screenHeight
        if (inputMode == InputMode.TOUCHPAD && userZoom > 1.08f && !isPinching) {
            followCursorInZoom(frame.cursorNormX, frame.cursorNormY)
        }
        invalidate()
    }

    private fun followCursorInZoom(normX: Float, normY: Float) {
        val bmp = currentBitmap ?: return
        val vw = width.toFloat()
        val vh = visibleHeight()
        if (vw <= 1f || vh <= 1f) return

        val b = computeDrawBounds(bmp)
        val screenCursorX = b[0] + normX * b[2]
        val screenCursorY = b[1] + normY * b[3]
        val marginX = vw * 0.12f
        val marginY = vh * 0.12f

        var shifted = false
        if (screenCursorX < marginX) {
            panX += (marginX - screenCursorX) * 0.65f
            shifted = true
        } else if (screenCursorX > vw - marginX) {
            panX -= (screenCursorX - (vw - marginX)) * 0.65f
            shifted = true
        }
        if (screenCursorY < marginY) {
            panY += (marginY - screenCursorY) * 0.65f
            shifted = true
        } else if (screenCursorY > vh - marginY) {
            panY -= (screenCursorY - (vh - marginY)) * 0.65f
            shifted = true
        }
        if (shifted) clampPan()
    }

    fun resetZoom() {
        userZoom = 1f
        panX = 0f
        panY = 0f
        invalidate()
    }

    fun setImeOcclusion(bottomPx: Int, focusNormY: Float? = null) {
        imeOcclusionBottomPx = max(0, bottomPx)
        if (focusNormY != null) imeFocusNormY = focusNormY
        if (imeOcclusionBottomPx > 0 && imeFocusNormY != null) {
            ensureNormYVisibleAboveIme(imeFocusNormY!!)
        } else if (imeOcclusionBottomPx == 0) {
            imeFocusNormY = null
            clampPan()
        }
        invalidate()
    }

    fun ensureNormYVisibleAboveIme(normY: Float) {
        imeFocusNormY = normY
        val bmp = currentBitmap ?: return
        val visH = visibleHeight()
        if (visH <= 0f || width <= 0) return

        // Zoom slightly if needed when typing on portrait phone so text box is readable
        if (userZoom < 1.35f && height > width) {
            userZoom = 1.6f
        }
        val baseScale = computeBaseFitScale(bmp)
        val totalScale = baseScale * userZoom
        val drawnH = bmp.height * totalScale
        val targetBitmapY = normY * bmp.height
        val desiredScreenY = visH * 0.62f
        val centerOffsetY = (visH - drawnH) / 2f
        panY = desiredScreenY - (centerOffsetY + targetBitmapY * totalScale)
        clampPan()
        invalidate()
    }

    private fun visibleHeight(): Float = max(120f, (height - imeOcclusionBottomPx).toFloat())

    private fun computeBaseFitScale(bmp: Bitmap): Float {
        val vw = width.toFloat().coerceAtLeast(1f)
        val vh = visibleHeight()
        return min(vw / bmp.width.toFloat(), vh / bmp.height.toFloat())
    }

    private fun clampPan() {
        val bmp = currentBitmap ?: return
        val totalScale = computeBaseFitScale(bmp) * userZoom
        val drawnW = bmp.width * totalScale
        val drawnH = bmp.height * totalScale
        val vw = width.toFloat()
        val vh = visibleHeight()

        val maxPanX = max(0f, (drawnW - vw) / 2f)
        val maxPanY = max(0f, (drawnH - vh) / 2f)
        panX = panX.coerceIn(-maxPanX, maxPanX)
        panY = panY.coerceIn(-maxPanY, maxPanY)
    }

    private fun computeDrawBounds(bmp: Bitmap): FloatArray {
        val totalScale = computeBaseFitScale(bmp) * userZoom
        val drawnW = bmp.width * totalScale
        val drawnH = bmp.height * totalScale
        val left = (width - drawnW) / 2f + panX
        val top = (visibleHeight() - drawnH) / 2f + panY
        return floatArrayOf(left, top, drawnW, drawnH, totalScale)
    }

    private fun viewToNormalizedPc(vx: Float, vy: Float): Pair<Float, Float>? {
        val bmp = currentBitmap ?: return null
        val b = computeDrawBounds(bmp)
        val nx = ((vx - b[0]) / b[2]).coerceIn(0f, 1f)
        val ny = ((vy - b[1]) / b[3]).coerceIn(0f, 1f)
        return nx to ny
    }

    override fun onDraw(canvas: Canvas) {
        canvas.drawColor(Color.BLACK)
        val bmp = currentBitmap ?: return
        if (bmp.isRecycled) return
        val b = computeDrawBounds(bmp)
        drawMatrix.reset()
        drawMatrix.postScale(b[4], b[4])
        drawMatrix.postTranslate(b[0], b[1])
        canvas.drawBitmap(bmp, drawMatrix, bitmapPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        scaleDetector.onTouchEvent(event)

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                lastX = event.x
                lastY = event.y
                downTime = System.currentTimeMillis()
                movedBeyondSlop = false
                maxPointersInGesture = 1
                longPressTriggered = false
                twoFingerScrollAccum = 0f
                remDx = 0f
                remDy = 0f
                isSecondTapCandidate = inputMode == InputMode.TOUCHPAD && (downTime - lastSingleTapTime) < 280L

                if (inputMode == InputMode.DIRECT_TOUCH) {
                    uiHandler.postDelayed(longPressRunnable, 480L)
                }
                return true
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                uiHandler.removeCallbacks(longPressRunnable)
                maxPointersInGesture = max(maxPointersInGesture, event.pointerCount)
                isSecondTapCandidate = false
                if (isDraggingLeftButton) {
                    isDraggingLeftButton = false
                    callbacks?.onMouseButton("left", "up")
                }
                if (event.pointerCount == 2) {
                    twoFingerLastX = (event.getX(0) + event.getX(1)) / 2f
                    twoFingerLastY = (event.getY(0) + event.getY(1)) / 2f
                }
                return true
            }

            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount == 2 && !isPinching) {
                    val cx = (event.getX(0) + event.getX(1)) / 2f
                    val cy = (event.getY(0) + event.getY(1)) / 2f
                    val dx = cx - twoFingerLastX
                    val dy = cy - twoFingerLastY
                    twoFingerLastX = cx
                    twoFingerLastY = cy

                    if (hypot(dx, dy) > 2f) movedBeyondSlop = true

                    if (userZoom > 1.05f && inputMode == InputMode.DIRECT_TOUCH) {
                        panX += dx
                        panY += dy
                        clampPan()
                        invalidate()
                    } else if (inputMode == InputMode.TOUCHPAD) {
                        if (userZoom > 1.05f && abs(dx) > abs(dy) * 1.4f) {
                            panX += dx
                            panY += dy
                            clampPan()
                            invalidate()
                        } else {
                            // Two-finger vertical scroll -> PC mouse wheel
                            twoFingerScrollAccum += dy
                            val step = 18f
                            if (abs(twoFingerScrollAccum) >= step) {
                                val ticks = (twoFingerScrollAccum / step).toInt()
                                twoFingerScrollAccum -= ticks * step
                                callbacks?.onMouseWheel(ticks * 60)
                            }
                        }
                    }
                    return true
                }

                if (event.pointerCount == 1 && maxPointersInGesture == 1 && !isPinching) {
                    val dx = event.x - lastX
                    val dy = event.y - lastY
                    lastX = event.x
                    lastY = event.y

                    if (!movedBeyondSlop && hypot(event.x - downX, event.y - downY) > touchSlop) {
                        movedBeyondSlop = true
                        uiHandler.removeCallbacks(longPressRunnable)
                    }

                    if (movedBeyondSlop) {
                        if (inputMode == InputMode.TOUCHPAD) {
                            if (isSecondTapCandidate && !isDraggingLeftButton) {
                                isDraggingLeftButton = true
                                isSecondTapCandidate = false
                                callbacks?.onMouseButton("left", "down")
                            }
                            val speed = hypot(dx, dy)
                            val accel = (1.15f + (speed / 28f).coerceAtMost(1.8f)) * (pcScreenW / 1280f)
                            val rawMx = dx * accel + remDx
                            val rawMy = dy * accel + remDy
                            val mx = rawMx.toInt()
                            val my = rawMy.toInt()
                            remDx = rawMx - mx
                            remDy = rawMy - my
                            if (mx != 0 || my != 0) {
                                callbacks?.onMouseMoveRelative(mx, my)
                            }
                        } else if (userZoom > 1.05f) {
                            panX += dx
                            panY += dy
                            clampPan()
                            invalidate()
                        }
                    }
                }
                return true
            }

            MotionEvent.ACTION_POINTER_UP -> {
                if (event.pointerCount == 2 && !movedBeyondSlop && !isPinching && (System.currentTimeMillis() - downTime) < 280L) {
                    // Two-finger quick tap -> Right click!
                    if (inputMode == InputMode.DIRECT_TOUCH) {
                        val pt = viewToNormalizedPc((event.getX(0) + event.getX(1)) / 2f, (event.getY(0) + event.getY(1)) / 2f)
                        if (pt != null) callbacks?.onMouseButton("right", "click", pt.first, pt.second)
                    } else {
                        callbacks?.onMouseButton("right", "click")
                    }
                    movedBeyondSlop = true
                }
                return true
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                uiHandler.removeCallbacks(longPressRunnable)
                val duration = System.currentTimeMillis() - downTime

                if (isDraggingLeftButton) {
                    isDraggingLeftButton = false
                    isSecondTapCandidate = false
                    callbacks?.onMouseButton("left", "up")
                    lastSingleTapTime = 0L
                    return true
                }

                if (!movedBeyondSlop && maxPointersInGesture == 1 && !longPressTriggered && duration < 300L) {
                    val now = System.currentTimeMillis()
                    if (inputMode == InputMode.TOUCHPAD) {
                        if (isSecondTapCandidate) {
                            isSecondTapCandidate = false
                            lastSingleTapTime = 0L
                            callbacks?.onMouseButton("left", "dblclick")
                        } else {
                            callbacks?.onMouseButton("left", "click")
                            lastSingleTapTime = now
                        }
                    } else {
                        val pt = viewToNormalizedPc(event.x, event.y)
                        if (pt != null) {
                            val isDirectDbl = (now - lastSingleTapTime) < 280L &&
                                hypot(event.x - lastDirectTapX, event.y - lastDirectTapY) < touchSlop * 2.5f
                            if (isDirectDbl) {
                                lastSingleTapTime = 0L
                                callbacks?.onMouseButton("left", "dblclick", pt.first, pt.second)
                            } else {
                                lastSingleTapTime = now
                                lastDirectTapX = event.x
                                lastDirectTapY = event.y
                                callbacks?.onMouseButton("left", "click", pt.first, pt.second)
                            }
                        }
                    }
                }
                isSecondTapCandidate = false
                return true
            }
        }
        return super.onTouchEvent(event)
    }
}
