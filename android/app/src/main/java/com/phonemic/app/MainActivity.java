package com.phonemic.app;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

/**
 * The whole user interface. Built in code on purpose: with no layout
 * resources there is nothing for the build to fail on, and the APK stays tiny.
 */
public class MainActivity extends Activity {

    private static final int REQUEST_PERMISSIONS = 42;

    private EditText ipField;
    private EditText portField;
    private TextView statusView;
    private Button toggleButton;

    /** Mirrors the service state, so the button label never lies. */
    private boolean running;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        float density = getResources().getDisplayMetrics().density;
        int pad = (int) (density * 20);
        int gap = (int) (density * 12);

        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setPadding(pad, pad, pad, pad);

        TextView title = new TextView(this);
        title.setText("PhoneMic");
        title.setTextSize(26);
        column.addView(title);

        TextView subtitle = new TextView(this);
        subtitle.setText("把手机麦克风实时发送到电脑");
        subtitle.setTextSize(14);
        subtitle.setPadding(0, (int) (density * 4), 0, gap);
        column.addView(subtitle, matchWidth());

        column.addView(makeLabel("电脑 IP", density), matchWidth());

        ipField = new EditText(this);
        ipField.setSingleLine(true);
        ipField.setHint("例如 192.168.1.10");
        column.addView(ipField, matchWidth());

        column.addView(makeLabel("端口", density), matchWidth());

        portField = new EditText(this);
        portField.setSingleLine(true);
        portField.setText("8765");
        column.addView(portField, matchWidth());

        toggleButton = new Button(this);
        toggleButton.setText("开始");
        toggleButton.setOnClickListener(v -> {
            if (running) {
                stopStreaming();
            } else {
                requestPermissionsAndStart();
            }
        });
        LinearLayout.LayoutParams buttonParams = matchWidth();
        buttonParams.topMargin = gap;
        column.addView(toggleButton, buttonParams);

        statusView = new TextView(this);
        statusView.setTextSize(14);
        statusView.setPadding(0, gap, 0, gap);
        statusView.setText("先在电脑上运行 PhoneMicServer.exe，它会打印出本机地址。");
        column.addView(statusView, matchWidth());

        TextView hint = new TextView(this);
        hint.setTextSize(12);
        hint.setText("手机和电脑要在同一个 Wi-Fi 下。电脑端先把输出设备设为 VB-Audio "
                + "\"CABLE Input\"，其他软件里再把麦克风选成 \"CABLE Output\"。");
        column.addView(hint, matchWidth());

        ScrollView scroll = new ScrollView(this);
        scroll.addView(column);
        setContentView(scroll);
    }

    private LinearLayout.LayoutParams matchWidth() {
        return new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private TextView makeLabel(String text, float density) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextSize(14);
        view.setPadding(0, (int) (density * 8), 0, (int) (density * 4));
        return view;
    }

    private void requestPermissionsAndStart() {
        List<String> missing = new ArrayList<>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.RECORD_AUDIO);
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS);
        }

        if (missing.isEmpty()) {
            startStreaming();
            return;
        }
        requestPermissions(missing.toArray(new String[0]), REQUEST_PERMISSIONS);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != REQUEST_PERMISSIONS) return;

        boolean audioGranted = false;
        for (int i = 0; i < permissions.length; i++) {
            if (Manifest.permission.RECORD_AUDIO.equals(permissions[i])
                    && grantResults[i] == PackageManager.PERMISSION_GRANTED) {
                audioGranted = true;
            }
        }

        if (audioGranted) {
            startStreaming();
        } else {
            statusView.setText("没有录音权限，无法采集麦克风。请在系统设置里允许麦克风权限。");
        }
    }

    private void startStreaming() {
        String ip = ipField.getText().toString().trim();
        if (ip.isEmpty()) {
            statusView.setText("请先填写电脑的 IP 地址。");
            return;
        }

        int port;
        try {
            port = Integer.parseInt(portField.getText().toString().trim());
        } catch (NumberFormatException error) {
            statusView.setText("端口必须是数字，例如 8765。");
            return;
        }
        if (port < 1 || port > 65535) {
            statusView.setText("端口必须在 1 到 65535 之间。");
            return;
        }

        Intent intent = new Intent(this, StreamService.class);
        intent.setAction(StreamService.ACTION_START);
        intent.putExtra(StreamService.EXTRA_IP, ip);
        intent.putExtra(StreamService.EXTRA_PORT, port);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent);
        } else {
            startService(intent);
        }

        running = true;
        toggleButton.setText("停止");
        statusView.setText("正在发送到 " + ip + ":" + port);
    }

    private void stopStreaming() {
        Intent intent = new Intent(this, StreamService.class);
        intent.setAction(StreamService.ACTION_STOP);
        startService(intent);

        running = false;
        toggleButton.setText("开始");
        statusView.setText("已停止。");
    }
}
