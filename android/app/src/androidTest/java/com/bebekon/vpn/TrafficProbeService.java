package com.bebekon.vpn;

import android.app.Service;
import android.content.Intent;
import android.os.*;
import java.net.*;
import java.io.*;

/** Only installed by instrumentation, never packaged with the application. */
public final class TrafficProbeService extends Service {
    private final Messenger messenger = new Messenger(new Handler(Looper.getMainLooper(), message -> {
        Messenger reply = message.replyTo;
        boolean streaming = message.arg2 == 1;
        String url = message.getData().getString("url", "https://example.com/");
        new Thread(() -> {
            android.util.Log.i("BebekonTrafficTest", "HTTPS probe started; UID=" + android.os.Process.myUid());
            android.net.ConnectivityManager network = getSystemService(android.net.ConnectivityManager.class);
            android.net.NetworkCapabilities capabilities = network.getNetworkCapabilities(network.getActiveNetwork());
            android.util.Log.i("BebekonTrafficTest", "Traffic uses VPN: " + (capabilities != null && capabilities.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)));
            boolean usesVpn = capabilities != null && capabilities.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN);
            int status = -1;
            HttpURLConnection connection = null;
            try {
                if (streaming) {
                    // This virtual destination can only be reached through the loopback VPN fixture.
                    try (Socket socket = new Socket()) {
                        socket.connect(new InetSocketAddress("198.18.0.10", 18080), 5000); socket.setSoTimeout(5000);
                        socket.getOutputStream().write("GET / HTTP/1.1\r\nHost: stream.test\r\nConnection: close\r\n\r\n".getBytes(java.nio.charset.StandardCharsets.US_ASCII));
                        ByteArrayOutputStream received = new ByteArrayOutputStream(); byte[] chunk = new byte[2048]; int count;
                        while ((count = socket.getInputStream().read(chunk)) >= 0) received.write(chunk, 0, count);
                        String body = received.toString("US-ASCII");
                        if (body.startsWith("HTTP/1.0 200") && body.endsWith("VVVVVVVVV")) status = 200;
                    }
                } else {
                connection = (HttpURLConnection) new URL(url).openConnection();
                connection.setConnectTimeout(5000);
                connection.setReadTimeout(5000);
                connection.setRequestProperty("Connection", "close");
                status = connection.getResponseCode();
                try (InputStream stream = connection.getInputStream()) {
                    byte[] buffer = new byte[4096];
                    int total = 0, count;
                    while ((count = stream.read(buffer)) > 0 && total < 65536) total += count;
                }
                }
            } catch (Exception error) { android.util.Log.e("BebekonTrafficTest", "Fixture HTTPS failed", error); }
            finally { if (connection != null) connection.disconnect(); }
            android.util.Log.i("BebekonTrafficTest", "HTTPS probe finished: " + status);
            try { Message response = Message.obtain(); response.arg1 = status; response.arg2 = usesVpn ? 1 : 0; reply.send(response); } catch (RemoteException ignored) { }
        }, "BebekonTrafficFixture").start();
        return true;
    }));
    @Override public IBinder onBind(Intent intent) { return messenger.getBinder(); }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        // Black-box check of an installed release APK without depending on obfuscated app classes.
        Message request = Message.obtain();
        request.setData(new Bundle());
        if (intent != null && intent.getStringExtra("url") != null) request.getData().putString("url", intent.getStringExtra("url"));
        request.replyTo = new Messenger(new Handler(Looper.getMainLooper(), response -> {
            android.util.Log.i("BebekonTrafficTest", "Release traffic result: " + response.arg1);
            stopSelf(startId); return true;
        }));
        try { messenger.send(request); } catch (RemoteException failure) { throw new RuntimeException(failure); }
        return START_NOT_STICKY;
    }
}
