package com.bebekon.vpn;

/** A separate UID exercises an installed release APK through Android's actual VPN routing. */
public final class TrafficProbeActivity extends android.app.Activity {
    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        android.widget.TextView text = new android.widget.TextView(this);
        text.setText("Bebekon release traffic fixture"); setContentView(text);
        startService(new android.content.Intent(this, TrafficProbeService.class).putExtra("url", getIntent().getStringExtra("url")));
    }
}
