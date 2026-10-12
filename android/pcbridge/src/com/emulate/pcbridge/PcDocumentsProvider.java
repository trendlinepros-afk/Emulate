package com.emulate.pcbridge;

import android.database.Cursor;
import android.database.MatrixCursor;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract.Document;
import android.provider.DocumentsContract.Root;
import android.provider.DocumentsContract;
import android.provider.DocumentsProvider;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileNotFoundException;

/**
 * Adds a "Windows PC" location to Android's file picker so any app can browse and open files
 * from the PC's folders and drives. Document ids are "root" or "p:" + Windows path.
 */
public class PcDocumentsProvider extends DocumentsProvider {
    private static final String ROOT_ID = "pc";
    private static final String ROOT_DOC = "root";

    private static final String[] ROOT_COLUMNS = {
            Root.COLUMN_ROOT_ID, Root.COLUMN_FLAGS, Root.COLUMN_ICON, Root.COLUMN_TITLE,
            Root.COLUMN_SUMMARY, Root.COLUMN_DOCUMENT_ID, Root.COLUMN_MIME_TYPES,
    };
    private static final String[] DOC_COLUMNS = {
            Document.COLUMN_DOCUMENT_ID, Document.COLUMN_MIME_TYPE, Document.COLUMN_DISPLAY_NAME,
            Document.COLUMN_LAST_MODIFIED, Document.COLUMN_FLAGS, Document.COLUMN_SIZE,
    };

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public Cursor queryRoots(String[] projection) {
        MatrixCursor c = new MatrixCursor(projection != null ? projection : ROOT_COLUMNS);
        MatrixCursor.RowBuilder row = c.newRow();
        row.add(Root.COLUMN_ROOT_ID, ROOT_ID);
        row.add(Root.COLUMN_FLAGS, Root.FLAG_SUPPORTS_IS_CHILD);
        row.add(Root.COLUMN_ICON, R.drawable.ic_pc);
        row.add(Root.COLUMN_TITLE, getContext().getString(R.string.root_title));
        row.add(Root.COLUMN_SUMMARY, Bridge.isConfigured(getContext()) ? "Files on your computer" : "Start this device from Emulate");
        row.add(Root.COLUMN_DOCUMENT_ID, ROOT_DOC);
        row.add(Root.COLUMN_MIME_TYPES, "*/*");
        return c;
    }

    @Override
    public Cursor queryDocument(String documentId, String[] projection) throws FileNotFoundException {
        MatrixCursor c = new MatrixCursor(projection != null ? projection : DOC_COLUMNS);
        if (ROOT_DOC.equals(documentId)) {
            MatrixCursor.RowBuilder row = c.newRow();
            row.add(Document.COLUMN_DOCUMENT_ID, ROOT_DOC);
            row.add(Document.COLUMN_MIME_TYPE, Document.MIME_TYPE_DIR);
            row.add(Document.COLUMN_DISPLAY_NAME, getContext().getString(R.string.root_title));
            row.add(Document.COLUMN_FLAGS, 0);
            return c;
        }
        try {
            JSONObject e = new JSONObject(Bridge.getString(getContext(), "/stat?path=" + Bridge.enc(pathOf(documentId)), 30000));
            addEntry(c, e);
        } catch (Exception ex) {
            throw new FileNotFoundException(ex.getMessage());
        }
        return c;
    }

    @Override
    public Cursor queryChildDocuments(String parentDocumentId, String[] projection, String sortOrder) throws FileNotFoundException {
        MatrixCursor c = new MatrixCursor(projection != null ? projection : DOC_COLUMNS);
        try {
            String json = ROOT_DOC.equals(parentDocumentId)
                    ? Bridge.getString(getContext(), "/roots", 30000)
                    : Bridge.getString(getContext(), "/list?path=" + Bridge.enc(pathOf(parentDocumentId)), 30000);
            JSONArray arr = new JSONArray(json);
            for (int i = 0; i < arr.length(); i++) addEntry(c, arr.getJSONObject(i));
        } catch (Exception ex) {
            Bundle extras = new Bundle();
            extras.putString(DocumentsContract.EXTRA_ERROR,
                    "Can't reach your PC. Make sure the Emulate app is open. (" + ex.getMessage() + ")");
            c.setExtras(extras);
        }
        return c;
    }

    @Override
    public boolean isChildDocument(String parentDocumentId, String documentId) {
        if (ROOT_DOC.equals(parentDocumentId)) return true;
        if (ROOT_DOC.equals(documentId)) return false;
        String parent = pathOf(parentDocumentId);
        String child = pathOf(documentId);
        if (!parent.endsWith("\\")) parent += "\\";
        return child.toLowerCase().startsWith(parent.toLowerCase());
    }

    @Override
    public ParcelFileDescriptor openDocument(String documentId, String mode, CancellationSignal signal) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException("The PC is read-only from the device. Use 'Save to PC' to copy files back.");
        String path = pathOf(documentId);
        String name = Bridge.safeName(path.substring(path.lastIndexOf('\\') + 1));
        File dest = new File(new File(getContext().getCacheDir(), "pcdocs"), Long.toString(System.nanoTime(), 36) + "_" + name);
        try {
            Bridge.download(getContext(), "/get?path=" + Bridge.enc(path), dest);
        } catch (Exception ex) {
            throw new FileNotFoundException(ex.getMessage());
        }
        dest.deleteOnExit();
        return ParcelFileDescriptor.open(dest, ParcelFileDescriptor.MODE_READ_ONLY);
    }

    private static String pathOf(String documentId) {
        return documentId.startsWith("p:") ? documentId.substring(2) : documentId;
    }

    private static void addEntry(MatrixCursor c, JSONObject e) throws org.json.JSONException {
        boolean dir = e.getBoolean("dir");
        String name = e.getString("name");
        String mime = e.optString("mime", "");
        if (!dir && mime.isEmpty()) mime = PickedFilesProvider.mimeOf(name);
        MatrixCursor.RowBuilder row = c.newRow();
        row.add(Document.COLUMN_DOCUMENT_ID, "p:" + e.getString("path"));
        row.add(Document.COLUMN_MIME_TYPE, dir ? Document.MIME_TYPE_DIR : mime);
        row.add(Document.COLUMN_DISPLAY_NAME, name);
        long modified = e.optLong("modified", 0);
        row.add(Document.COLUMN_LAST_MODIFIED, modified > 0 ? modified : null);
        row.add(Document.COLUMN_FLAGS, 0);
        row.add(Document.COLUMN_SIZE, dir ? null : e.optLong("size", 0));
    }
}
