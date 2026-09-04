package com.anthemvh.servermanager.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.anthemvh.servermanager.data.ApiClient
import com.anthemvh.servermanager.data.ApiResult
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.util.concurrent.Executors

/**
 * Pairs this phone with a ServerManager install.
 *
 * The QR is the quick path, but the same details can always be typed: a camera permission
 * refusal, or a phone that cannot see the screen, should not make pairing impossible.
 */
@Composable
fun PairingScreen(api: ApiClient, onPaired: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var address by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var scanning by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    var hasCamera by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED
        )
    }

    val askForCamera = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        hasCamera = granted
        scanning = granted
        if (!granted) {
            error = "Camera access was declined. Enter the address and code by hand instead."
        }
    }

    fun submit() {
        if (address.isBlank() || code.isBlank()) {
            error = "Enter both the address and the pairing code."
            return
        }

        busy = true
        error = null

        scope.launch {
            val name = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
            when (val result = api.pair(address.trim(), code.trim().uppercase(), name)) {
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
        Spacer(Modifier.height(6.dp))
        Text(
            "On the desktop, open Settings, turn on remote access, then press Pair a phone.",
            style = MaterialTheme.typography.bodyMedium,
        )

        Spacer(Modifier.height(18.dp))

        if (scanning && hasCamera) {
            QrScanner(
                onScanned = { payload ->
                    scanning = false
                    parsePayload(payload)?.let { (url, scannedCode) ->
                        address = url
                        code = scannedCode
                        submit()
                    } ?: run { error = "That QR code is not a ServerManager pairing code." }
                },
                onFailed = {
                    scanning = false
                    error = "The camera could not be started. Enter the details by hand."
                },
            )
            Spacer(Modifier.height(12.dp))
            OutlinedButton(onClick = { scanning = false }, modifier = Modifier.fillMaxWidth()) {
                Text("Stop scanning")
            }
        } else {
            Button(
                onClick = {
                    error = null
                    if (hasCamera) scanning = true else askForCamera.launch(Manifest.permission.CAMERA)
                },
                modifier = Modifier.fillMaxWidth(),
                enabled = !busy,
            ) {
                Text("Scan QR code")
            }
        }

        Spacer(Modifier.height(20.dp))
        Text("Or enter it by hand", style = MaterialTheme.typography.titleSmall)
        Spacer(Modifier.height(8.dp))

        OutlinedTextField(
            value = address,
            onValueChange = { address = it },
            label = { Text("Address") },
            placeholder = { Text("http://100.x.y.z:8787") },
            singleLine = true,
            enabled = !busy,
            modifier = Modifier.fillMaxWidth(),
        )

        Spacer(Modifier.height(10.dp))

        OutlinedTextField(
            value = code,
            onValueChange = { code = it.uppercase() },
            label = { Text("Pairing code") },
            singleLine = true,
            enabled = !busy,
            keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(
                capitalization = KeyboardCapitalization.Characters
            ),
            modifier = Modifier.fillMaxWidth(),
        )

        error?.let {
            Spacer(Modifier.height(14.dp))
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer)) {
                Text(
                    it,
                    modifier = Modifier.padding(14.dp),
                    color = MaterialTheme.colorScheme.onErrorContainer,
                )
            }
        }

        Spacer(Modifier.height(18.dp))

        Button(onClick = ::submit, enabled = !busy, modifier = Modifier.fillMaxWidth()) {
            if (busy) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
            }
            Text(if (busy) "Pairing…" else "Pair")
        }

        Spacer(Modifier.height(20.dp))
        Text(
            "This phone will be able to view your servers, start and stop them, and read "
                + "their consoles. Sending console commands is granted separately on the desktop. "
                + "You can revoke this device there at any time.",
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

/** Reads the JSON the desktop encodes into its QR code. */
private fun parsePayload(payload: String): Pair<String, String>? = try {
    val o = JSONObject(payload)
    val url = o.optString("url")
    val code = o.optString("code")
    if (url.isNotBlank() && code.isNotBlank()) url to code else null
} catch (e: Exception) {
    null
}

@Composable
private fun QrScanner(onScanned: (String) -> Unit, onFailed: () -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val executor = remember { Executors.newSingleThreadExecutor() }
    var handled by remember { mutableStateOf(false) }

    DisposableEffect(Unit) {
        onDispose { executor.shutdown() }
    }

    androidx.compose.ui.viewinterop.AndroidView(
        modifier = Modifier
            .fillMaxWidth()
            .height(300.dp),
        factory = { ctx ->
            val previewView = PreviewView(ctx)
            val providerFuture = ProcessCameraProvider.getInstance(ctx)

            providerFuture.addListener({
                try {
                    val provider = providerFuture.get()
                    val scanner = BarcodeScanning.getClient()

                    val preview = Preview.Builder().build().also {
                        it.setSurfaceProvider(previewView.surfaceProvider)
                    }

                    val analysis = ImageAnalysis.Builder()
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .build()

                    analysis.setAnalyzer(executor) { proxy ->
                        val media = proxy.image
                        if (media == null || handled) {
                            proxy.close()
                            return@setAnalyzer
                        }

                        val image = InputImage.fromMediaImage(
                            media, proxy.imageInfo.rotationDegrees
                        )

                        scanner.process(image)
                            .addOnSuccessListener { codes ->
                                codes.firstOrNull { it.valueType == Barcode.TYPE_TEXT || it.rawValue != null }
                                    ?.rawValue
                                    ?.let {
                                        if (!handled) {
                                            handled = true
                                            onScanned(it)
                                        }
                                    }
                            }
                            .addOnCompleteListener { proxy.close() }
                    }

                    provider.unbindAll()
                    provider.bindToLifecycle(
                        lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis
                    )
                } catch (e: Exception) {
                    onFailed()
                }
            }, ContextCompat.getMainExecutor(ctx))

            previewView
        },
    )
}
