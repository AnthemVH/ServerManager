package com.anthemvh.servermanager.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.anthemvh.servermanager.data.ApiClient
import com.anthemvh.servermanager.data.ApiResult
import com.anthemvh.servermanager.data.ConsoleLine
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ServerDetailScreen(
    api: ApiClient,
    serverId: String,
    serverName: String,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val listState = rememberLazyListState()

    var lines by remember { mutableStateOf<List<ConsoleLine>>(emptyList()) }
    var command by remember { mutableStateOf("") }
    var message by remember { mutableStateOf<String?>(null) }
    var canSendCommands by remember { mutableStateOf(true) }

    LaunchedEffect(serverId) {
        while (true) {
            when (val result = api.console(serverId)) {
                is ApiResult.Ok -> {
                    lines = result.value
                    message = null
                }
                is ApiResult.Failed -> message = result.message
            }
            delay(2000)
        }
    }

    // Follow the tail as new output arrives.
    LaunchedEffect(lines.size) {
        if (lines.isNotEmpty()) {
            listState.animateScrollToItem(lines.lastIndex)
        }
    }

    fun send() {
        val text = command.trim()
        if (text.isEmpty()) return

        scope.launch {
            when (val result = api.sendCommand(serverId, text)) {
                is ApiResult.Ok -> {
                    command = ""
                    message = result.value
                }
                is ApiResult.Failed -> {
                    message = result.message
                    // A 403 here means the desktop has not granted this device the
                    // command permission, so stop offering the box.
                    if (result.message.contains("not permitted", ignoreCase = true)) {
                        canSendCommands = false
                    }
                }
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(serverName) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {

            message?.let {
                Text(
                    it,
                    Modifier.fillMaxWidth().padding(12.dp),
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            LazyColumn(
                state = listState,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .background(Color(0xFF1B1B1B))
                    .padding(horizontal = 10.dp),
            ) {
                items(lines) { line ->
                    Row(Modifier.padding(vertical = 1.dp)) {
                        Text(
                            line.timestamp,
                            color = Color(0xFF6E7681),
                            fontFamily = FontFamily.Monospace,
                            fontSize = 11.sp,
                        )
                        Spacer(Modifier.width(8.dp))
                        Text(
                            line.text,
                            color = when (line.stream) {
                                "StandardError" -> Color(0xFFF48771)
                                "Launcher" -> Color(0xFF6A9955)
                                else -> Color(0xFFD4D4D4)
                            },
                            fontFamily = FontFamily.Monospace,
                            fontSize = 11.sp,
                        )
                    }
                }
            }

            if (canSendCommands) {
                Row(
                    Modifier.fillMaxWidth().padding(10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    OutlinedTextField(
                        value = command,
                        onValueChange = { command = it },
                        placeholder = { Text("Send a console command") },
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.width(8.dp))
                    IconButton(onClick = ::send) {
                        Icon(Icons.Filled.Send, contentDescription = "Send")
                    }
                }
            } else {
                Text(
                    "This device is not permitted to send console commands. Grant it on the "
                        + "desktop under Settings, Remote access.",
                    Modifier.padding(12.dp),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}
