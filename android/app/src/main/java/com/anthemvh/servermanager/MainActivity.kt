package com.anthemvh.servermanager

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import com.anthemvh.servermanager.data.ApiClient
import com.anthemvh.servermanager.data.TokenStore
import com.anthemvh.servermanager.ui.DashboardScreen
import com.anthemvh.servermanager.ui.PairingScreen
import com.anthemvh.servermanager.ui.ServerDetailScreen

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val tokens = TokenStore(applicationContext)
        val api = ApiClient(tokens)

        setContent {
            ServerManagerTheme {
                AppNavigation(tokens, api)
            }
        }
    }
}

@Composable
private fun ServerManagerTheme(content: @Composable () -> Unit) {
    // Mirrors the desktop app's dark palette so the two feel like one product.
    val dark = darkColorScheme(
        primary = Color(0xFF3E9BE0),
        surface = Color(0xFF252526),
        background = Color(0xFF1E1E1E),
        error = Color(0xFFE06C63),
    )

    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) dark else lightColorScheme()) {
        Surface(color = MaterialTheme.colorScheme.background) { content() }
    }
}

/** Which screen is showing. Deliberately tiny: there are only three. */
private sealed class Screen {
    data object Pairing : Screen()
    data object Dashboard : Screen()
    data class Detail(val serverId: String, val serverName: String) : Screen()
}

@Composable
private fun AppNavigation(tokens: TokenStore, api: ApiClient) {
    var screen by remember {
        mutableStateOf<Screen>(if (tokens.isPaired()) Screen.Dashboard else Screen.Pairing)
    }

    when (val current = screen) {
        is Screen.Pairing -> PairingScreen(
            api = api,
            onPaired = { screen = Screen.Dashboard },
        )

        is Screen.Dashboard -> DashboardScreen(
            api = api,
            onOpenServer = { id, name -> screen = Screen.Detail(id, name) },
            onUnpaired = {
                tokens.clear()
                screen = Screen.Pairing
            },
        )

        is Screen.Detail -> ServerDetailScreen(
            api = api,
            serverId = current.serverId,
            serverName = current.serverName,
            onBack = { screen = Screen.Dashboard },
        )
    }
}
