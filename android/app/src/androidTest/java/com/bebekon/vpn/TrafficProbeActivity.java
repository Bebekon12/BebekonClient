package com.bebekon.vpn;

/** A separate UID exercises an installed release APK through Android's actual VPN routing. */
public final class TrafficProbeActivity extends android.app.Activity {
    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        android.widget.TextView text = new android.widget.TextView(this);
        text.setText("Bebekon release traffic fixture"); setContentView(text);
        if (getIntent().getBooleanExtra("attack_tile", false)) {
            boolean blocked = false;
            try {
                startActivity(new android.content.Intent().setComponent(new android.content.ComponentName("com.bebekon.vpn", "com.bebekon.vpn.TileConnectActivity")));
            } catch (SecurityException expected) { blocked = true; }
            android.util.Log.i("BebekonSecurityTest", "Private tile activity blocked=" + blocked);
            startActivity(new android.content.Intent().setComponent(new android.content.ComponentName("com.bebekon.vpn", "com.bebekon.vpn.MainActivity"))
                    .putExtra("tile_connect", true).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK | android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP | android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP));
            finish(); return;
        }
        startService(new android.content.Intent(this, TrafficProbeService.class).putExtra("url", getIntent().getStringExtra("url")));
    }
}
