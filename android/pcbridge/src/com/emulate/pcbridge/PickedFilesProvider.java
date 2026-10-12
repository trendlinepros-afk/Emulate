package com.emulate.pcbridge;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import android.webkit.MimeTypeMap;

import java.io.File;
import java.io.FileNotFoundException;
import java.util.List;

/** Read-only provider for files picked on the PC: content://com.emulate.pcbridge.files/picked/&lt;sub&gt;/&lt;name&gt; */
public class PickedFilesProvider extends ContentProvider {
    static final String AUTHORITY = "com.emulate.pcbridge.files";

    static Uri uriFor(String sub, String name) {
        return new Uri.Builder().scheme("content").authority(AUTHORITY)
                .appendPath("picked").appendPath(sub).appendPath(name).build();
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    private File fileFor(Uri uri) throws FileNotFoundException {
        List<String> seg = uri.getPathSegments();
        if (seg.size() != 3 || !"picked".equals(seg.get(0))) throw new FileNotFoundException(uri.toString());
        File base = new File(getContext().getCacheDir(), "picked");
        File f = new File(new File(base, seg.get(1)), seg.get(2));
        try {
            if (!f.getCanonicalPath().startsWith(base.getCanonicalPath() + File.separator)) throw new FileNotFoundException();
        } catch (java.io.IOException e) {
            throw new FileNotFoundException(e.getMessage());
        }
        if (!f.exists()) throw new FileNotFoundException(uri.toString());
        return f;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        return ParcelFileDescriptor.open(fileFor(uri), ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        File f;
        try {
            f = fileFor(uri);
        } catch (FileNotFoundException e) {
            return null;
        }
        if (projection == null) projection = new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE};
        MatrixCursor c = new MatrixCursor(projection, 1);
        Object[] row = new Object[projection.length];
        for (int i = 0; i < projection.length; i++) {
            if (OpenableColumns.DISPLAY_NAME.equals(projection[i])) row[i] = f.getName();
            else if (OpenableColumns.SIZE.equals(projection[i])) row[i] = f.length();
        }
        c.addRow(row);
        return c;
    }

    @Override
    public String getType(Uri uri) {
        return mimeOf(uri.getLastPathSegment());
    }

    static String mimeOf(String name) {
        if (name == null) return "application/octet-stream";
        int dot = name.lastIndexOf('.');
        if (dot < 0) return "application/octet-stream";
        String m = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot + 1).toLowerCase());
        return m != null ? m : "application/octet-stream";
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        throw new UnsupportedOperationException();
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }
}
