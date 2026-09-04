package com.anthemvh.servermanager.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.TimeUnit

/** A server as the API describes it. */
data class ServerSummary(
    val id: String,
    val name: String,
    val state: String,
    val cpuPercent: Double,
    val memoryMegabytes: Double,
    val processCount: Int,
    val uptime: String,
    val canStart: Boolean,
    val canStop: Boolean,
    val isLauncherDetached: Boolean,
) {
    val isRunning get() = state == "Running"
    val needsAttention get() = state == "Crashed" || state == "Failed"
}

data class ConsoleLine(val timestamp: String, val stream: String, val text: String)

data class LauncherHealth(
    val cpuPercent: Double,
    val memoryMegabytes: Double,
    val threadCount: Int,
    val handleCount: Int,
    val uptime: String,
    val version: String,
    val runningServers: Int,
    val totalServers: Int,
)

/** Distinguishes "the server said no" from "we could not reach it". */
sealed class ApiResult<out T> {
    data class Ok<T>(val value: T) : ApiResult<T>()
    data class Failed(val message: String, val unauthorised: Boolean = false) : ApiResult<Nothing>()
}

/**
 * Talks to a paired ServerManager.
 *
 * Polling rather than a websocket: a phone suspends sockets the moment the app leaves the
 * foreground, so a long-lived connection would spend most of its life dead and needing
 * reconnection. Short polls while the screen is on are simpler and behave better.
 */
class ApiClient(private val tokens: TokenStore) {

    private val http = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(15, TimeUnit.SECONDS)
        .build()

    private val json = "application/json; charset=utf-8".toMediaType()

    /** Exchanges a pairing code for a device token and remembers it. */
    suspend fun pair(baseUrl: String, code: String, deviceName: String): ApiResult<Unit> =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("code", code)
                .put("deviceName", deviceName)
                .toString()
                .toRequestBody(json)

            val request = Request.Builder()
                .url("${baseUrl.trimEnd('/')}/api/v1/pair")
                .post(body)
                .build()

            try {
                http.newCall(request).execute().use { response ->
                    val text = response.body?.string().orEmpty()

                    if (!response.isSuccessful) {
                        return@withContext ApiResult.Failed(errorFrom(text, response.code))
                    }

                    val token = JSONObject(text).getString("token")
                    tokens.save(baseUrl.trimEnd('/'), token)
                    ApiResult.Ok(Unit)
                }
            } catch (e: Exception) {
                ApiResult.Failed(e.message ?: "Could not reach ServerManager.")
            }
        }

    suspend fun servers(): ApiResult<List<ServerSummary>> = get("/api/v1/servers") { text ->
        val array = JSONArray(text)
        (0 until array.length()).map { serverFrom(array.getJSONObject(it)) }
    }

    suspend fun health(): ApiResult<LauncherHealth> = get("/api/v1/health") { text ->
        val o = JSONObject(text)
        LauncherHealth(
            cpuPercent = o.optDouble("cpuPercent", 0.0),
            memoryMegabytes = o.optDouble("memoryMegabytes", 0.0),
            threadCount = o.optInt("threadCount"),
            handleCount = o.optInt("handleCount"),
            uptime = o.optString("uptime", "—"),
            version = o.optString("version", "unknown"),
            runningServers = o.optInt("runningServers"),
            totalServers = o.optInt("totalServers"),
        )
    }

    suspend fun console(serverId: String, tail: Int = 300): ApiResult<List<ConsoleLine>> =
        get("/api/v1/servers/$serverId/console?tail=$tail") { text ->
            val lines = JSONObject(text).getJSONArray("lines")
            (0 until lines.length()).map {
                val o = lines.getJSONObject(it)
                ConsoleLine(
                    o.optString("timestamp"),
                    o.optString("stream"),
                    o.optString("text"),
                )
            }
        }

    suspend fun action(serverId: String, action: String): ApiResult<String> =
        post("/api/v1/servers/$serverId/$action", null) { text ->
            JSONObject(text).optString("message", "Done.")
        }

    suspend fun sendCommand(serverId: String, command: String): ApiResult<String> =
        post(
            "/api/v1/servers/$serverId/command",
            JSONObject().put("command", command).toString(),
        ) { text ->
            JSONObject(text).optString("message", "Sent.")
        }

    // --- plumbing ---

    private suspend fun <T> get(path: String, parse: (String) -> T): ApiResult<T> =
        call(Request.Builder().url(url(path) ?: return ApiResult.Failed("Not paired.")).get(), parse)

    private suspend fun <T> post(path: String, body: String?, parse: (String) -> T): ApiResult<T> =
        call(
            Request.Builder()
                .url(url(path) ?: return ApiResult.Failed("Not paired."))
                .post((body ?: "").toRequestBody(json)),
            parse,
        )

    private fun url(path: String): String? = tokens.baseUrl()?.let { "$it$path" }

    private suspend fun <T> call(builder: Request.Builder, parse: (String) -> T): ApiResult<T> =
        withContext(Dispatchers.IO) {
            val token = tokens.token()
                ?: return@withContext ApiResult.Failed("Not paired.", unauthorised = true)

            val request = builder.addHeader("Authorization", "Bearer $token").build()

            try {
                http.newCall(request).execute().use { response ->
                    val text = response.body?.string().orEmpty()

                    if (response.code == 401) {
                        // Most likely revoked from the desktop, so say something useful
                        // rather than "HTTP 401".
                        return@withContext ApiResult.Failed(
                            "This device is no longer paired. Pair it again.",
                            unauthorised = true,
                        )
                    }

                    if (!response.isSuccessful) {
                        return@withContext ApiResult.Failed(errorFrom(text, response.code))
                    }

                    ApiResult.Ok(parse(text))
                }
            } catch (e: Exception) {
                ApiResult.Failed(e.message ?: "Could not reach ServerManager.")
            }
        }

    private fun errorFrom(body: String, code: Int): String = try {
        JSONObject(body).optString("error").ifBlank { "Request failed ($code)." }
    } catch (e: Exception) {
        "Request failed ($code)."
    }

    private fun serverFrom(o: JSONObject) = ServerSummary(
        id = o.getString("id"),
        name = o.optString("name"),
        state = o.optString("state"),
        cpuPercent = o.optDouble("cpuPercent", 0.0),
        memoryMegabytes = o.optDouble("memoryMegabytes", 0.0),
        processCount = o.optInt("processCount"),
        uptime = o.optString("uptime", "—"),
        canStart = o.optBoolean("canStart"),
        canStop = o.optBoolean("canStop"),
        isLauncherDetached = o.optBoolean("isLauncherDetached"),
    )
}
