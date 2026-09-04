plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.anthemvh.servermanager"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.anthemvh.servermanager"
        minSdk = 26
        targetSdk = 34

        // Overridden by CI from the release tag so the app reports the same version as
        // the desktop build it was released alongside.
        versionCode = (project.findProperty("appVersionCode") as String? ?: "1").toInt()
        versionName = project.findProperty("appVersionName") as String? ?: "1.0.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
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

    // QR scanning for pairing.
    implementation("androidx.camera:camera-camera2:1.3.4")
    implementation("androidx.camera:camera-lifecycle:1.3.4")
    implementation("androidx.camera:camera-view:1.3.4")
    implementation("com.google.mlkit:barcode-scanning:17.2.0")

    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    // JSON comes from android.util/org.json, which ships with the platform. The API has a
    // handful of small payloads, so a serialization library and its compiler plugin would
    // be more moving parts than the job needs.
}
