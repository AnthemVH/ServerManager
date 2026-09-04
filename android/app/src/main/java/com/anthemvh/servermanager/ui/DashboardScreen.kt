package com.anthemvh.servermanager.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.anthemvh.servermanager.data.ApiClient
import com.anthemvh.servermanager.data.ApiResult
import com.anthemvh.servermanager.data.LauncherHealth
import com.anthemvh.servermanager.data.ServerSummary
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Colour for a server state, matching the desktop dots. */
fun stateColour(state: String): Color = when (state) {
    "Running" -> Color(0xFF4CAF50)
    "Starting", "Stopping" -> Color(0xFFFFB300)
    "Crashed" -> Color(0xFFE53935)
    "Failed" -> Color(0xFFB71C1C)
    else -> Color(0xFF6E7681)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    api: ApiClient,
    onOpenServer: (String, String) -> Unit,
    onUnpaired: () -> Unit,
) {
    val scope = rememberCoroutineScope()

    var servers by remember { mutableStateOf<List<ServerSummary>>(emptyList()) }
    var health by remember { mutableStateOf<LauncherHealth?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    var busyServer by remember { mutableStateOf<String?>(null) }

    // Polled while this screen is on. Android suspends sockets in the background anyway,
    // so a short poll is both simpler and better behaved than a live connection.
    LaunchedEffect(Unit) {
        while (true) {
            when (val result = api.servers()) {
                is ApiResult.Ok -> {
                    servers = result.value
                    error = null
                }
                is ApiResult.Failed -> {
                    error = result.message
                    if (result.unauthorised) {
                        onUnpaired()
                        return@LaunchedEffect
                    }
                }
            }

            (api.health() as? ApiResult.Ok)?.let { health = it.value }

            loading = false
            delay(3000)
        }
    }

    fun act(server: ServerSummary, action: String) {
        busyServer = server.id
        scope.launch {
            when (val result = api.action(server.id, action)) {
                is ApiResult.Ok -> (api.servers() as? ApiResult.Ok)?.let { servers = it.value }
                is ApiResult.Failed -> error = result.message
            }
            busyServer = null
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("ServerManager") },
                actions = {
                    TextButton(onClick = onUnpaired) { Text("Unpair") }
                },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {

            health?.let {
                Card(Modifier.fillMaxWidth().padding(12.dp)) {
                    Column(Modifier.padding(14.dp)) {
                        Text(
                            "${it.runningServers} of ${it.totalServers} running",
                            style = MaterialTheme.typography.titleMedium,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "ServerManager ${it.version} · ${"%.1f".format(it.cpuPercent)}% CPU · "
                                + "${it.memoryMegabytes.toInt()} MB · up ${it.uptime}",
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }

            error?.let {
                Card(
                    Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer),
                ) {
                    Text(
                        it,
                        Modifier.padding(14.dp),
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }

            if (loading && servers.isEmpty()) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator()
                }
            } else if (servers.isEmpty()) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text("No servers configured on the desktop.")
                }
            } else {
                LazyColumn(
                    contentPadding = PaddingValues(12.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    items(servers, key = { it.id }) { server ->
                        ServerCard(
                            server = server,
                            busy = busyServer == server.id,
                            onOpen = { onOpenServer(server.id, server.name) },
                            onAction = { act(server, it) },
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun ServerCard(
    server: ServerSummary,
    busy: Boolean,
    onOpen: () -> Unit,
    onAction: (String) -> Unit,
) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(10.dp)
                        .clip(CircleShape)
                        .background(stateColour(server.state))
                )
                Spacer(Modifier.width(9.dp))
                Text(
                    server.name,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f),
                )
                Text(server.state, style = MaterialTheme.typography.bodySmall)
            }

            Spacer(Modifier.height(10.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                Metric("CPU", "%.1f%%".format(server.cpuPercent))
                Metric("Memory", "${server.memoryMegabytes.toInt()} MB")
                Metric("Uptime", server.uptime)
                Metric("Procs", server.processCount.toString())
            }

            if (server.isLauncherDetached) {
                Spacer(Modifier.height(8.dp))
                Text(
                    "Started by a launcher script, so console output is unavailable.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            Spacer(Modifier.height(12.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { onAction("start") }, enabled = server.canStart && !busy) {
                    Text("Start")
                }
                OutlinedButton(onClick = { onAction("stop") }, enabled = server.canStop && !busy) {
                    Text("Stop")
                }
                OutlinedButton(onClick = { onAction("restart") }, enabled = server.canStop && !busy) {
                    Text("Restart")
                }
                Spacer(Modifier.weight(1f))
                TextButton(onClick = onOpen) { Text("Console") }
            }
        }
    }
}

@Composable
private fun Metric(label: String, value: String) {
    Column {
        Text(label, style = MaterialTheme.typography.labelSmall)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

private fun Modifier.background(colour: Color) = this.then(
    androidx.compose.foundation.background(colour)
)
