package com.emulate.pcbridge;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Small status screen explaining how to use the bridge. */
public class MainActivity extends Activity {
    private TextView status;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        int pad = (int) (20 * getResources().getDisplayMetrics().density);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(pad, pad * 2, pad, pad);
        root.setBackgroundColor(Color.parseColor("#0F1115"));

        TextView title = new TextView(this);
        title.setText("PC Bridge");
        title.setTextSize(26);
        title.setTextColor(Color.WHITE);
        root.addView(title);

        status = new TextView(this);
        status.setTextSize(15);
        status.setTextColor(Color.parseColor("#9AA3B2"));
        status.setPadding(0, pad / 2, 0, pad);
        root.addView(status);

        TextView help = new TextView(this);
        help.setTextSize(15);
        help.setTextColor(Color.parseColor("#E8EAF0"));
        help.setText(
                "• Attaching a photo or file in any app (WhatsApp, Gmail, …): choose \"Pick from PC\" "
                        + "or open the file browser and pick \"Windows PC\". File Explorer opens on your computer.\n\n"
                        + "• Photos in your PC's Pictures\\Emulate Shared folder appear in the gallery automatically.\n\n"
                        + "• Share any file to \"Save to PC\" to copy it to Downloads\\Emulate on your computer.");
        root.addView(help);

        Button test = new Button(this);
        test.setText("Try it: pick a file from the PC");
        test.setGravity(Gravity.CENTER);
        test.setOnClickListener(v -> {
            Intent i = new Intent(this, PickActivity.class);
            i.setAction(Intent.ACTION_GET_CONTENT);
            i.setType("*/*");
            startActivityForResult(i, 1);
        });
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = pad;
        root.addView(test, lp);

        setContentView(root);
    }

    @Override
    protected void onResume() {
        super.onResume();
        status.setText(Bridge.isConfigured(this) ? "Connected to the Emulate app on your PC." : "Not connected. Start this device from the Emulate app on your PC.");
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (resultCode == RESULT_OK && data != null && data.getData() != null) {
            status.setText("It works! Received: " + data.getData().getLastPathSegment());
        }
    }
}
