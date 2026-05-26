package Plume.Http

import kotlinx.serialization.Serializable

object Responses {
    @Serializable
    data class ErrorResponse(val status: Int, val error: String, val message: String)

}