package com.emulate.pcbridge;

import android.app.Activity;
import android.content.ClipData;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.util.ArrayList;

/**
 * Handles "pick a file" requests from any app (e.g. WhatsApp's attach button): asks the Windows app
 * to open File Explorer, downloads what was picked and returns it to the calling app.
 */
public class PickActivity extends Activity {
    private boolean started;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (savedInstanceState != null && savedInstanceState.getBoolean("started")) {
            started = true; // a pick is already running in a previous instance
        }
    }

    @Override
    protected void onSaveInstanceState(Bundle outState) {
        super.onSaveInstanceState(outState);
        outState.putBoolean("started", started);
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (started) return;
        started = true;

        if (!Bridge.isConfigured(this)) {
            Toast.makeText(this, "PC Bridge isn't connected yet. Start this device from the Emulate app on your PC.", Toast.LENGTH_LONG).show();
            setResult(RESULT_CANCELED);
            finish();
            return;
        }

        Intent in = getIntent();
        String mime = in.getType();
        String[] extraMimes = in.getStringArrayExtra(Intent.EXTRA_MIME_TYPES);
        if (extraMimes != null && extraMimes.length > 0) {
            StringBuilder sb = new StringBuilder();
            for (String m : extraMimes) {
                if (sb.length() > 0) sb.append(',');
                sb.append(m);
            }
            mime = sb.toString();
        }
        if (mime == null) mime = "*/*";
        final String query = "/pick?mime=" + Bridge.enc(mime) + "&multi=" + (in.getBooleanExtra(Intent.EXTRA_ALLOW_MULTIPLE, false) ? "1" : "0");

        Toast.makeText(this, "Choose the file in the window that opened on your PC…", Toast.LENGTH_LONG).show();

        new Thread(() -> {
            try {
                // The user may take a while in File Explorer.
                JSONArray picked = new JSONArray(Bridge.getString(this, query, 15 * 60 * 1000));
                if (picked.length() == 0) {
                    runOnUiThread(() -> { setResult(RESULT_CANCELED); finish(); });
                    return;
                }
                File dir = new File(getCacheDir(), "picked");
                cleanOld(dir);
                ArrayList<Uri> uris = new ArrayList<>();
                for (int i = 0; i < picked.length(); i++) {
                    JSONObject f = picked.getJSONObject(i);
                    String name = Bridge.safeName(f.getString("name"));
                    String sub = Long.toString(System.nanoTime(), 36);
                    File dest = new File(new File(dir, sub), name);
                    Bridge.download(this, "/file?id=" + Bridge.enc(f.getString("id")), dest);
                    uris.add(PickedFilesProvider.uriFor(sub, name));
                }
                runOnUiThread(() -> finishWith(uris));
            } catch (Exception e) {
                runOnUiThread(() -> {
                    Toast.makeText(this, "Couldn't reach the Emulate app on your PC: " + e.getMessage(), Toast.LENGTH_LONG).show();
                    setResult(RESULT_CANCELED);
                    finish();
                });
            }
        }).start();
    }

    private void finishWith(ArrayList<Uri> uris) {
        Intent result = new Intent();
        result.setData(uris.get(0));
        ClipData clip = ClipData.newRawUri("", uris.get(0));
        for (int i = 1; i < uris.size(); i++) clip.addItem(new ClipData.Item(uris.get(i)));
        result.setClipData(clip);
        result.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        setResult(RESULT_OK, result);
        finish();
    }

    /** Remove picks older than a day so the cache doesn't grow forever. */
    private static void cleanOld(File dir) {
        File[] subs = dir.listFiles();
        if (subs == null) return;
        long cutoff = System.currentTimeMillis() - 24L * 3600 * 1000;
        for (File sub : subs) {
            if (sub.lastModified() >= cutoff) continue;
            File[] files = sub.listFiles();
            if (files != null) for (File f : files) f.delete();
            sub.delete();
        }
    }
}
