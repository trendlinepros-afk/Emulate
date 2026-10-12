package com.emulate.pcbridge;

import android.content.Context;
import android.content.SharedPreferences;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;

/** Talks to the Emulate Windows app. Inside the emulator the PC's localhost is 10.0.2.2. */
final class Bridge {
    static final String HOST = "10.0.2.2";
    private static final String PREFS = "bridge";

    private Bridge() {}

    static SharedPreferences prefs(Context c) {
        return c.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    static boolean isConfigured(Context c) {
        return prefs(c).getString("token", null) != null;
    }

    static String base(Context c) {
        return "http://" + HOST + ":" + prefs(c).getInt("port", 47823);
    }

    static String enc(String s) {
        try {
            return URLEncoder.encode(s, "UTF-8");
        } catch (Exception e) {
            return s;
        }
    }

    static HttpURLConnection open(Context c, String pathAndQuery, int readTimeoutMs) throws IOException {
        HttpURLConnection conn = (HttpURLConnection) new URL(base(c) + pathAndQuery).openConnection();
        conn.setConnectTimeout(5000);
        conn.setReadTimeout(readTimeoutMs);
        conn.setRequestProperty("X-Emulate-Token", prefs(c).getString("token", ""));
        return conn;
    }

    static String getString(Context c, String pathAndQuery, int readTimeoutMs) throws IOException {
        HttpURLConnection conn = open(c, pathAndQuery, readTimeoutMs);
        try {
            int code = conn.getResponseCode();
            if (code != 200) throw new IOException("PC answered " + code);
            InputStream in = conn.getInputStream();
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            copy(in, out);
            return out.toString("UTF-8");
        } finally {
            conn.disconnect();
        }
    }

    static void download(Context c, String pathAndQuery, File dest) throws IOException {
        HttpURLConnection conn = open(c, pathAndQuery, 120000);
        try {
            int code = conn.getResponseCode();
            if (code != 200) throw new IOException("PC answered " + code);
            File parent = dest.getParentFile();
            if (parent != null) parent.mkdirs();
            File tmp = new File(dest.getPath() + ".part");
            try (InputStream in = conn.getInputStream(); OutputStream out = new FileOutputStream(tmp)) {
                copy(in, out);
            }
            if (!tmp.renameTo(dest)) throw new IOException("Could not save " + dest.getName());
        } finally {
            conn.disconnect();
        }
    }

    static void upload(Context c, String name, InputStream in, long length) throws IOException {
        HttpURLConnection conn = open(c, "/upload?name=" + enc(name), 120000);
        try {
            conn.setDoOutput(true);
            conn.setRequestMethod("POST");
            conn.setRequestProperty("Content-Type", "application/octet-stream");
            if (length >= 0) {
                conn.setFixedLengthStreamingMode(length);
                try (OutputStream out = conn.getOutputStream()) {
                    copy(in, out);
                }
            } else {
                // Unknown size: buffer it so we can send a Content-Length (the PC side needs one).
                ByteArrayOutputStream buf = new ByteArrayOutputStream();
                copy(in, buf);
                byte[] bytes = buf.toByteArray();
                conn.setFixedLengthStreamingMode(bytes.length);
                try (OutputStream out = conn.getOutputStream()) {
                    out.write(bytes);
                }
            }
            if (conn.getResponseCode() != 200) throw new IOException("PC answered " + conn.getResponseCode());
        } finally {
            conn.disconnect();
        }
    }

    static void copy(InputStream in, OutputStream out) throws IOException {
        byte[] buf = new byte[64 * 1024];
        int n;
        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
    }

    static String safeName(String name) {
        String s = name == null ? "file" : name.replaceAll("[\\\\/:*?\"<>|\\x00-\\x1f]", "_");
        return s.isEmpty() ? "file" : s;
    }
}
