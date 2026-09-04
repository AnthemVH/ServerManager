plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.anthemvh.servermanager"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.anthemvh.servermanager"
        // 24 rather than 26: nothing here needs Oreo, and a lower floor is one less
        // reason for a phone to refuse the install.
        minSdk = 24
        targetSdk = 34

        // Overridden by CI from the release tag so the app reports the same version as
        // the desktop build it was released alongside.
        versionCode = (project.findProperty("appVersionCode") as String? ?: "1").toInt()
        versionName = project.findProperty("appVersionName") as String? ?: "1.0.0"
    }

    // Signed with a keystore supplied by CI when the repository secrets are present.
    // Without a stable key, every build would be signed differently and Android would
    // refuse to install one over another.
    val keystorePath = System.getenv("SERVERMANAGER_KEYSTORE")

    signingConfigs {
        if (!keystorePath.isNullOrBlank()) {
            create("release") {
                storeFile = file(keystorePath)
                storePassword = System.getenv("SERVERMANAGER_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("SERVERMANAGER_KEY_ALIAS") ?: "servermanager"
                keyPassword = System.getenv("SERVERMANAGER_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (!keystorePath.isNullOrBlank()) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
    }

    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.14"
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2024.06.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.activity:activity-compose:1.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.2")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.2")

    // The device token is a credential, so it is kept in encrypted preferences rather
    // than plain SharedPreferences.
    implementation("androidx.security:security-crypto:1.1.0-alpha06")

    // No ML Kit or CameraX. Scanning a QR code through them dragged in Google Play
    // Services and Firebase: roughly 30 MB, a 4 MB native barcode library, and a set of
    // content providers and services this app has no use for. Pairing by typing the
    // address and an eight-character code needs none of it.

    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    // JSON comes from android.util/org.json, which ships with the platform. The API has a
    // handful of small payloads, so a serialization library and its compiler plugin would
    // be more moving parts than the job needs.
}
