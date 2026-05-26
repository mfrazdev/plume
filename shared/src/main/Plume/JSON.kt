package Plume

import kotlinx.serialization.json.*

fun JSON(builder: JsonBuilder.() -> Unit): JsonObject {
    return JsonBuilder().apply(builder).build()
}

class JsonBuilder {
    private val map = mutableMapOf<String, JsonElement>()

    infix fun String.to(value: String) {
        map[this] = JsonPrimitive(value)
    }

    infix fun String.to(value: Number) {
        map[this] = JsonPrimitive(value)
    }

    infix fun String.to(value: Boolean) {
        map[this] = JsonPrimitive(value)
    }

    // 🔥 SUPORTE A OBJETO NESTED
    infix fun String.to(builder: JsonBuilder.() -> Unit) {
        val child = JsonBuilder().apply(builder).build()
        map[this] = child
    }

    fun build(): JsonObject {
        return JsonObject(map)
    }
}