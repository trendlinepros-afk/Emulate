package com.emulate.pcbridge;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Stores the token/port sent by the Windows app via "adb shell am broadcast". */
public class ConfigReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String token = intent.getStringExtra("token");
        int port = intent.getIntExtra("port", 47823);
        if (token == null || token.isEmpty()) return;
        String dev = intent.getStringExtra("dev");
        Bridge.prefs(context).edit().putString("token", token).putInt("port", port).putString("dev", dev == null ? "" : dev).apply();
        setResultCode(1);
        setResultData("configured");
    }
}
