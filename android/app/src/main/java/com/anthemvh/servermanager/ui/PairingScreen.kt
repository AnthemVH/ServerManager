package com.anthemvh.servermanager.ui

import android.os.Build
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import com.anthemvh.servermanager.data.ApiClient
import com.anthemvh.servermanager.data.ApiResult
import kotlinx.coroutines.launch

/**
 * Pairs this phone with a ServerManager install.
 *
 * Typed rather than scanned. A camera scanner meant ML Kit, which meant Google Play
 * Services and Firebase: about 30 MB and a pile of components, to save typing an
 * eight-character code once. The desktop still shows the code in large type next to
 * its QR image.
 */
@Composable
fun PairingScreen(api: ApiClient, onPaired: () -> Unit) {
    val scope = rememberCoroutineScope()

    var address by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    fun submit() {
        if (address.isBlank() || code.isBlank()) {
            error = "Enter both the address and the pairing code."
            return
        }

        busy = true
        error = null

        scope.launch {
            val name = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
            when (val result = api.pair(normalise(address), code.trim().uppercase(), name)) {
                is ApiResult.Ok -> onPaired()
                is ApiResult.Failed -> {
                    error = result.message
                    busy = false
                }
            }
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(20.dp)
            .verticalScroll(rememberScrollState()),
    ) {
        Text("Pair with ServerManager", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(8.dp))
        Text(
            "On the desktop open Settings, turn on remote access, then press Pair a phone. "
                + "The address and code are shown beneath the QR image.",
            style = MaterialTheme.typography.bodyMedium,
        )

        Spacer(Modifier.height(22.dp))

        OutlinedTextField(
            value = address,
            onValueChange = { address = it },
            label = { Text("Address") },
            placeholder = { Text("100.x.y.z:8787") },
            singleLine = true,
            enabled = !busy,
            modifier = Modifier.fillMaxWidth(),
        )

        Spacer(Modifier.height(12.dp))

        OutlinedTextField(
            value = code,
            onValueChange = { code = it.uppercase() },
            label = { Text("Pairing code") },
            singleLine = true,
            enabled = !busy,
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters),
            modifier = Modifier.fillMaxWidth(),
        )

        error?.let {
            Spacer(Modifier.height(16.dp))
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer)) {
                Text(
                    it,
                    modifier = Modifier.padding(14.dp),
                    color = MaterialTheme.colorScheme.onErrorContainer,
                )
            }
        }

        Spacer(Modifier.height(20.dp))

        Button(onClick = ::submit, enabled = !busy, modifier = Modifier.fillMaxWidth()) {
            if (busy) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
            }
            Text(if (busy) "Pairing…" else "Pair")
        }

        Spacer(Modifier.height(22.dp))
        Text(
            "The code works once and expires in five minutes. This phone will be able to view "
                + "your servers, start and stop them, and read their consoles; sending console "
                + "commands is granted separately on the desktop, and you can revoke this device "
                + "there at any time.",
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

/** Accepts "100.1.2.3:8787" as readily as a full URL. */
private fun normalise(address: String): String {
    val trimmed = address.trim().trimEnd('/')
    return if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) {
        trimmed
    } else {
        "http://$trimmed"
    }
}
