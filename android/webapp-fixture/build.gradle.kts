plugins { id("com.android.application") }
android {
    namespace = "com.bebekon.webapkfixture"
    compileSdk = 36
    defaultConfig { applicationId = "com.bebekon.webapkfixture"; minSdk = 29; targetSdk = 36; versionCode = 1; versionName = "1" }
    flavorDimensions += "host"
    productFlavors {
        create("browser") { dimension = "host"; manifestPlaceholders["runtimeHost"] = "com.android.chrome" }
        create("probe") { dimension = "host"; applicationIdSuffix = ".probe"; manifestPlaceholders["runtimeHost"] = "com.bebekon.vpn.test" }
    }
}
