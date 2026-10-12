package com.emulate.pcbridge;

import android.app.Activity;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.widget.Toast;

import java.io.InputStream;
import java.util.ArrayList;

/** Share target: copies shared files to the PC (Downloads\Emulate). */
public class SaveToPcActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (!Bridge.isConfigured(this)) {
            Toast.makeText(this, "PC Bridge isn't connected yet. Start this device from the Emulate app on your PC.", Toast.LENGTH_LONG).show();
            finish();
            return;
        }

        final ArrayList<Uri> uris = new ArrayList<>();
        Intent in = getIntent();
        if (Intent.ACTION_SEND_MULTIPLE.equals(in.getAction())) {
            ArrayList<Uri> list = in.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (list != null) uris.addAll(list);
        } else {
            Uri u = in.getParcelableExtra(Intent.EXTRA_STREAM);
            if (u != null) uris.add(u);
        }
        final String text = in.getStringExtra(Intent.EXTRA_TEXT);
        if (uris.isEmpty() && text == null) {
            finish();
            return;
        }

        Toast.makeText(this, "Saving to your PC…", Toast.LENGTH_SHORT).show();
        new Thread(() -> {
            int ok = 0;
            String error = null;
            try {
                if (uris.isEmpty()) {
                    byte[] bytes = text.getBytes("UTF-8");
                    Bridge.upload(this, "shared-text.txt", new java.io.ByteArrayInputStream(bytes), bytes.length);
                    ok++;
                }
                for (Uri u : uris) {
                    String name = "file";
                    long size = -1;
                    try (Cursor c = getContentResolver().query(u, null, null, null, null)) {
                        if (c != null && c.moveToFirst()) {
                            int ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                            int si = c.getColumnIndex(OpenableColumns.SIZE);
                            if (ni >= 0 && c.getString(ni) != null) name = c.getString(ni);
                            if (si >= 0 && !c.isNull(si)) size = c.getLong(si);
                        }
                    } catch (Exception ignored) {
                    }
                    if (u.getLastPathSegment() != null && "file".equals(name)) name = u.getLastPathSegment();
                    try (InputStream is = getContentResolver().openInputStream(u)) {
                        if (is == null) continue;
                        Bridge.upload(this, Bridge.safeName(name), is, size);
                        ok++;
                    }
                }
            } catch (Exception e) {
                error = e.getMessage();
            }
            final int done = ok;
            final String err = error;
            runOnUiThread(() -> {
                Toast.makeText(this, err == null
                        ? "Saved " + done + " file(s) to Downloads\\Emulate on your PC"
                        : "Couldn't save to PC: " + err, Toast.LENGTH_LONG).show();
                finish();
            });
        }).start();
    }
}
