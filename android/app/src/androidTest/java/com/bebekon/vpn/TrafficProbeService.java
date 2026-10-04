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
        new Thread(() -> {
            android.util.Log.i("BebekonTrafficTest", "HTTPS probe started; UID=" + android.os.Process.myUid());
            android.net.ConnectivityManager network = getSystemService(android.net.ConnectivityManager.class);
            android.net.NetworkCapabilities capabilities = network.getNetworkCapabilities(network.getActiveNetwork());
            android.util.Log.i("BebekonTrafficTest", "Traffic uses VPN: " + (capabilities != null && capabilities.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)));
            int status = -1;
            HttpURLConnection connection = null;
            try {
                connection = (HttpURLConnection) new URL("https://example.com/").openConnection();
                connection.setConnectTimeout(5000);
                connection.setReadTimeout(5000);
                connection.setRequestProperty("Connection", "close");
                status = connection.getResponseCode();
                try (InputStream stream = connection.getInputStream()) {
                    byte[] buffer = new byte[4096];
                    int total = 0, count;
                    while ((count = stream.read(buffer)) > 0 && total < 65536) total += count;
                }
            } catch (Exception error) { android.util.Log.e("BebekonTrafficTest", "Fixture HTTPS failed", error); }
            finally { if (connection != null) connection.disconnect(); }
            android.util.Log.i("BebekonTrafficTest", "HTTPS probe finished: " + status);
            try { Message response = Message.obtain(); response.arg1 = status; reply.send(response); } catch (RemoteException ignored) { }
        }, "BebekonTrafficFixture").start();
        return true;
    }));
    @Override public IBinder onBind(Intent intent) { return messenger.getBinder(); }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        // Black-box check of an installed release APK without depending on obfuscated app classes.
        Message request = Message.obtain();
        request.replyTo = new Messenger(new Handler(Looper.getMainLooper(), response -> {
            android.util.Log.i("BebekonTrafficTest", "Release traffic result: " + response.arg1);
            stopSelf(startId); return true;
        }));
        try { messenger.send(request); } catch (RemoteException failure) { throw new RuntimeException(failure); }
        return START_NOT_STICKY;
    }
}
