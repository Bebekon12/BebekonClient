package com.bebekon.vpn

import org.snakeyaml.engine.v2.api.LoadSettings
import org.snakeyaml.engine.v2.api.lowlevel.Parse
import org.snakeyaml.engine.v2.events.Event
import java.util.Collections
import java.util.IdentityHashMap

/** Bound untrusted structures before recursive JSON/YAML constructors run. */
internal object ImportSafety {
    const val MAX_BYTES = 4 * 1024 * 1024
    const val MAX_DEPTH = 32
    const val MAX_VISITS = 100_000
    val yamlSettings = LoadSettings.builder().setMaxAliasesForCollections(25).setCodePointLimit(MAX_BYTES)
        .setAllowDuplicateKeys(false).setAllowRecursiveKeys(false).build()

    fun jsonText(text: String): String {
        require(text.length <= MAX_BYTES) { "Подписка слишком большая" }
        var depth = 0; var visits = 0; var quote = '\u0000'; var escape = false
        // Android's JSONObject also accepts single-quoted strings. Comments are
        // deliberately rejected so they cannot conceal structural delimiters.
        for (c in text) {
            if (quote != '\u0000') {
                if (escape) escape = false else if (c == '\\') escape = true else if (c == quote) quote = '\u0000'
            } else when (c) {
                '"', '\'' -> quote = c
                '/', '#' -> error("Комментарии в JSON подписке не поддерживаются")
                '{', '[' -> require(++depth <= MAX_DEPTH && ++visits <= MAX_VISITS) { "Подписка содержит слишком сложную структуру" }
                '}', ']' -> require(--depth >= 0) { "Повреждённая JSON подписка" }
            }
        }
        require(depth == 0 && quote == '\u0000') { "Повреждённая JSON подписка" }
        return text
    }
    fun yamlText(text: String) {
        var depth = 0; var count = 0; var documents = 0
        for (event in Parse(yamlSettings).parseString(text)) {
            require(++count <= MAX_VISITS) { "Подписка содержит слишком сложную структуру" }
            when (event.eventId) {
                Event.ID.MappingStart, Event.ID.SequenceStart -> require(++depth <= MAX_DEPTH) { "Подписка содержит слишком сложную структуру" }
                Event.ID.MappingEnd, Event.ID.SequenceEnd -> depth--
                Event.ID.DocumentStart -> require(++documents <= 1) { "Ожидается один документ подписки" }
                else -> Unit
            }
        }
    }
    fun yamlTree(root: Any?) {
        val path = Collections.newSetFromMap(IdentityHashMap<Any, Boolean>())
        var visits = 0
        fun walk(value: Any?, depth: Int) {
            require(depth <= MAX_DEPTH && ++visits <= MAX_VISITS) { "Подписка содержит слишком сложную структуру" }
            if (value !is Map<*, *> && value !is List<*>) return
            require(path.add(value)) { "Циклические YAML-ссылки запрещены" }
            when (value) {
                is Map<*, *> -> value.forEach { (key, child) -> require(key is String) { "Ожидается текстовый ключ подписки" }; walk(child, depth + 1) }
                is List<*> -> value.forEach { walk(it, depth + 1) }
            }
            path.remove(value)
        }
        walk(root, 0)
    }
}
