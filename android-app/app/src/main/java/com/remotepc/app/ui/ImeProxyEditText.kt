package com.remotepc.app.ui

import android.content.Context
import android.text.InputType
import android.util.AttributeSet
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection

/**
 * Invisible IME bridge View supporting BOTH live character-by-character typing
 * AND Russian/English swipe-typing (Gboard / Xiaomi keyboard) without duplication.
 */
class ImeProxyView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var onTextCommitted: ((String) -> Unit)? = null
    var onBackspace: ((Int) -> Unit)? = null
    var onEnter: (() -> Unit)? = null

    private var composing: String = ""

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    fun resetComposing() {
        composing = ""
    }

    override fun onCheckIsTextEditor(): Boolean = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_AUTO_CORRECT
        outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_ACTION_NONE
        composing = ""

        return object : BaseInputConnection(this, false) {
            private fun applyDelta(oldStr: String, newStr: String) {
                var prefix = 0
                val minLen = minOf(oldStr.length, newStr.length)
                while (prefix < minLen && oldStr[prefix] == newStr[prefix]) {
                    prefix++
                }
                val toDelete = oldStr.length - prefix
                if (toDelete > 0) {
                    onBackspace?.invoke(toDelete)
                }
                if (prefix < newStr.length) {
                    onTextCommitted?.invoke(newStr.substring(prefix))
                }
            }

            override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
                val next = text?.toString() ?: ""
                applyDelta(composing, next)
                composing = next
                return true
            }

            override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
                val next = text?.toString() ?: ""
                applyDelta(composing, next)
                composing = ""
                return true
            }

            override fun finishComposingText(): Boolean {
                composing = ""
                return true
            }

            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                if (composing.isNotEmpty()) {
                    val drop = beforeLength.coerceAtMost(composing.length)
                    composing = composing.dropLast(drop)
                    onBackspace?.invoke(drop)
                    val rem = beforeLength - drop
                    if (rem > 0) onBackspace?.invoke(rem)
                } else {
                    onBackspace?.invoke(beforeLength.coerceAtLeast(1))
                }
                return true
            }

            override fun sendKeyEvent(event: KeyEvent): Boolean {
                if (event.action == KeyEvent.ACTION_DOWN) {
                    when (event.keyCode) {
                        KeyEvent.KEYCODE_DEL -> {
                            if (composing.isNotEmpty()) {
                                composing = composing.dropLast(1)
                            }
                            onBackspace?.invoke(1)
                            return true
                        }
                        KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> {
                            composing = ""
                            onEnter?.invoke()
                            return true
                        }
                        else -> {
                            val ch = event.unicodeChar
                            if (ch != 0 && !Character.isISOControl(ch)) {
                                composing = ""
                                onTextCommitted?.invoke(ch.toChar().toString())
                                return true
                            }
                        }
                    }
                }
                return super.sendKeyEvent(event)
            }
        }
    }
}
