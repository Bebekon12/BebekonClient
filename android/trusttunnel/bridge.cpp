// SPDX-License-Identifier: Apache-2.0
// Uses the official TrustTunnel client API and its JNI lifetime helpers.
#include <jni.h>
#include <vector>
#include <openssl/x509.h>
#include "jni_utils.h"
#include "net/network_manager.h"
#include "vpn/trusttunnel/client.h"

class Client {
public:
    Client(JNIEnv *env, jobject owner, ag::TrustTunnelConfig &&config) {
        env->GetJavaVM(&m_vm);
        m_owner = {m_vm, owner};
        jclass type = env->GetObjectClass(owner);
        m_protect = env->GetMethodID(type, "protectSocket", "(I)Z");
        m_verify = env->GetMethodID(type, "verifyCertificate", "([[B)Z");
        m_state = env->GetMethodID(type, "onStateChanged", "(I)V");
        ag::VpnCallbacks callbacks;
        callbacks.protect_handler = [this](ag::SocketProtectEvent *event) {
            ScopedJniEnv env(m_vm, 8);
            event->result = env->CallBooleanMethod(m_owner.get(), m_protect, event->fd) ? 0 : -1;
            if (env->ExceptionCheck()) { env->ExceptionClear(); event->result = -1; }
        };
        callbacks.verify_handler = [this](ag::VpnVerifyCertificateEvent *event) {
            // Cap local references and reject malformed or oversized certificate chains.
            int count = event->chain ? sk_X509_num(event->chain) : 0;
            event->result = -1;
            if (!event->cert || count > 16) return;
            ScopedJniEnv env(m_vm, 40);
            jclass bytes = env->FindClass("[B");
            auto chain = env->NewObjectArray(count + 1, bytes, nullptr);
            if (!chain || env->ExceptionCheck()) { env->ExceptionClear(); return; }
            for (int i = 0; i <= count; ++i) {
                X509 *cert = i == 0 ? event->cert : sk_X509_value(event->chain, i - 1);
                int size = i2d_X509(cert, nullptr);
                if (size <= 0 || size > 65536) return;
                std::vector<unsigned char> der(size);
                unsigned char *cursor = der.data();
                if (i2d_X509(cert, &cursor) != size) return;
                auto value = env->NewByteArray(size);
                if (!value || env->ExceptionCheck()) { env->ExceptionClear(); return; }
                env->SetByteArrayRegion(value, 0, size, reinterpret_cast<jbyte *>(der.data()));
                env->SetObjectArrayElement(chain, i, value);
                if (env->ExceptionCheck()) { env->ExceptionClear(); return; }
                env->DeleteLocalRef(value);
            }
            event->result = env->CallBooleanMethod(m_owner.get(), m_verify, chain) ? 0 : -1;
            if (env->ExceptionCheck()) { env->ExceptionClear(); event->result = -1; }
        };
        callbacks.state_changed_handler = [this](ag::VpnStateChangedEvent *event) {
            ScopedJniEnv env(m_vm, 8);
            env->CallVoidMethod(m_owner.get(), m_state, static_cast<jint>(event->state));
            if (env->ExceptionCheck()) env->ExceptionClear();
        };
        m_client = std::make_unique<ag::TrustTunnelClient>(std::move(config), std::move(callbacks));
    }
    ~Client() { m_client->disconnect(); }
    bool start() { return !m_client->connect(ag::TrustTunnelClient::AutoSetup{}); }
    void network(bool available) {
        m_client->notify_network_change(available ? ag::VPN_NS_CONNECTED : ag::VPN_NS_NOT_CONNECTED);
    }
private:
    JavaVM *m_vm = nullptr;
    GlobalRef<jobject> m_owner;
    jmethodID m_protect = nullptr;
    jmethodID m_verify = nullptr;
    jmethodID m_state = nullptr;
    std::unique_ptr<ag::TrustTunnelClient> m_client;
};

extern "C" JNIEXPORT jlong JNICALL Java_com_bebekon_vpn_TrustTunnelNative_createNative(
        JNIEnv *env, jobject owner, jbyteArray text) {
    jsize size = env->GetArrayLength(text);
    if (size <= 0 || size > 131072) return 0;
    std::string utf(size, '\0');
    env->GetByteArrayRegion(text, 0, size, reinterpret_cast<jbyte *>(utf.data()));
    auto parsed = toml::parse(std::string_view(utf));
    if (!parsed) return 0;
    auto config = ag::TrustTunnelConfig::build_config(parsed);
    if (!config || config->location.skip_verification
            || !std::holds_alternative<ag::TrustTunnelConfig::SocksListener>(config->listener)) return 0;
    ag::Logger::set_log_level(ag::LOG_LEVEL_ERROR);
    return reinterpret_cast<jlong>(new Client(env, owner, std::move(*config)));
}
extern "C" JNIEXPORT jboolean JNICALL Java_com_bebekon_vpn_TrustTunnelNative_dnsNative(
        JNIEnv *env, jobject, jobjectArray servers) {
    ag::SystemDnsServers result;
    jsize count = env->GetArrayLength(servers);
    if (count < 1 || count > 16) return false;
    for (jsize i = 0; i < count; ++i) {
        LocalRef<jstring> server(env, reinterpret_cast<jstring>(env->GetObjectArrayElement(servers, i)));
        const char *utf = env->GetStringUTFChars(server.get(), nullptr);
        if (!utf) return false;
        result.main.emplace_back(ag::SystemDnsServer{.address = utf});
        env->ReleaseStringUTFChars(server.get(), utf);
    }
    return ag::vpn_network_manager_update_system_dns(std::move(result));
}
extern "C" JNIEXPORT jboolean JNICALL Java_com_bebekon_vpn_TrustTunnelNative_startNative(
        JNIEnv *, jobject, jlong ptr) {
    return ptr && reinterpret_cast<Client *>(ptr)->start();
}
extern "C" JNIEXPORT void JNICALL Java_com_bebekon_vpn_TrustTunnelNative_networkNative(
        JNIEnv *, jobject, jlong ptr, jboolean available) {
    if (ptr) reinterpret_cast<Client *>(ptr)->network(available);
}
extern "C" JNIEXPORT void JNICALL Java_com_bebekon_vpn_TrustTunnelNative_destroyNative(
        JNIEnv *, jobject, jlong ptr) {
    delete reinterpret_cast<Client *>(ptr);
}
