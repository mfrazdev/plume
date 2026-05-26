package Plume.Http.Routes

import Plume.JSON
import Plume.JsonBuilder
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

fun successJson(builder: JsonBuilder.() -> Unit = {}): JsonObject {
    return JSON {
        "status" to "success"
        builder()
    }
}

fun errorJson(message: String): JsonObject {
    return JSON {
        "status" to "error"
        "message" to message
    }
}

