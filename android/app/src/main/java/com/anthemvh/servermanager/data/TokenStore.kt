package com.anthemvh.servermanager.data

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * Holds the device token and the address it belongs to.
 *
 * The token is a credential that can start and stop real servers, so it lives in
 * EncryptedSharedPreferences, backed by a key in the Android keystore, rather than in
 * plain preferences that any backup or rooted-device dump would expose.
 */
class TokenStore(context: Context) {

    private val prefs: SharedPreferences = run {
        val key = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()

        EncryptedSharedPreferences.create(
            context,
            "servermanager.pairing",
            key,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    fun isPaired(): Boolean = token() != null && baseUrl() != null

    fun token(): String? = prefs.getString(KEY_TOKEN, null)

    fun baseUrl(): String? = prefs.getString(KEY_URL, null)

    fun save(baseUrl: String, token: String) {
        prefs.edit()
            .putString(KEY_URL, baseUrl)
            .putString(KEY_TOKEN, token)
            .apply()
    }

    /** Forgets this pairing. The desktop still lists the device until it is revoked there. */
    fun clear() {
        prefs.edit().remove(KEY_TOKEN).remove(KEY_URL).apply()
    }

    private companion object {
        const val KEY_TOKEN = "token"
        const val KEY_URL = "baseUrl"
    }
}
