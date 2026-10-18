package com.phonemic.app;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.IBinder;
import android.os.Process;
import android.os.SystemClock;
import android.util.Log;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Captures the microphone in the background and streams it to the PC.
 *
 * <p>A foreground service is required here: Android suspends plain background
 * work, and a microphone that stops as soon as the screen locks would be
 * useless. The notification is the price of staying alive.
 */
public class StreamService extends Service {

    public static final String ACTION_START = "com.phonemic.app.action.START";
    public static final String ACTION_STOP = "com.phonemic.app.action.STOP";
    public static final String EXTRA_IP = "com.phonemic.app.extra.IP";
    public static final String EXTRA_PORT = "com.phonemic.app.extra.PORT";

    private static final String TAG = "PhoneMic";
    private static final String CHANNEL_ID = "phonemic_stream";
    private static final int NOTIFICATION_ID = 1001;

    // Must match server/Program.cs.
    private static final int SAMPLE_RATE = 48000;
    private static final int PACKET_MS = 10;
    private static final int HEADER_BYTES = 16;
    private static final int MAGIC = 0x43494D50; // 'PMIC'

    private volatile boolean running;
    private Thread worker;
    private DatagramSocket socket;
    private AudioRecord recorder;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) {
            return START_NOT_STICKY;
        }

        String action = intent.getAction();

        if (ACTION_STOP.equals(action)) {
            stopStreaming();
            stopSelf();
            return START_NOT_STICKY;
        }

        if (ACTION_START.equals(action)) {
            String ip = intent.getStringExtra(EXTRA_IP);
            int port = intent.getIntExtra(EXTRA_PORT, 8765);
            startForegroundCompat();
            startStreaming(ip, port);
            return START_NOT_STICKY;
        }

        return START_NOT_STICKY;
    }

    private void startForegroundCompat() {
        NotificationManager manager =
                (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID, "PhoneMic 推流", NotificationManager.IMPORTANCE_LOW);
            channel.setDescription("麦克风正在发送到电脑");
            manager.createNotificationChannel(channel);
        }

        Intent open = new Intent(this, MainActivity.class);
        int pendingFlags = Build.VERSION.SDK_INT >= Build.VERSION_CODES.M
                ? PendingIntent.FLAG_IMMUTABLE : 0;
        PendingIntent pending = PendingIntent.getActivity(this, 0, open, pendingFlags);

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        Notification notification = builder
                .setContentTitle("PhoneMic")
                .setContentText("正在把麦克风发送到电脑")
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentIntent(pending)
                .setOngoing(true)
                .build();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
    }

    private synchronized void startStreaming(String ip, int port) {
        if (running) return;

        try {
            InetAddress target = InetAddress.getByName(ip);
            socket = new DatagramSocket();
            socket.setSendBufferSize(1 << 18);

            int frameSamples = SAMPLE_RATE / 1000 * PACKET_MS; // 480
            int frameBytes = frameSamples * 2;                 // 960

            int minBuffer = AudioRecord.getMinBufferSize(
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            int bufferSize = Math.max(minBuffer, frameBytes * 8);

            recorder = new AudioRecord(
                    MediaRecorder.AudioSource.MIC,
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferSize);

            if (recorder.getState() != AudioRecord.STATE_INITIALIZED) {
                Log.e(TAG, "AudioRecord could not be initialised");
                cleanup();
                stopSelf();
                return;
            }

            running = true;
            worker = new Thread(() -> captureLoop(target, port, frameBytes), "phonemic-capture");
            worker.start();
        } catch (Exception error) {
            Log.e(TAG, "could not start streaming", error);
            cleanup();
            stopSelf();
        }
    }

    private void captureLoop(InetAddress target, int port, int frameBytes) {
        // Ask the scheduler for a real-time-ish slot: this thread's deadline is
        // 10 ms away, every time.
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO);

        byte[] frame = new byte[frameBytes];
        byte[] header = new byte[HEADER_BYTES];
        byte[] datagram = new byte[HEADER_BYTES + frameBytes];
        ByteBuffer headerBuffer = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);

        long sequence = 0;

        try {
            recorder.startRecording();

            while (running) {
                int filled = 0;
                while (filled < frameBytes && running) {
                    int read = recorder.read(frame, filled, frameBytes - filled);
                    if (read <= 0) {
                        if (read == AudioRecord.ERROR_INVALID_OPERATION
                                || read == AudioRecord.ERROR_BAD_VALUE) {
                            throw new IllegalStateException("AudioRecord.read returned " + read);
                        }
                        break;
                    }
                    filled += read;
                }

                if (filled < frameBytes) continue;

                headerBuffer.clear();
                headerBuffer.putInt(MAGIC);
                headerBuffer.putInt((int) sequence);
                headerBuffer.putLong(SystemClock.elapsedRealtime());

                System.arraycopy(header, 0, datagram, 0, HEADER_BYTES);
                System.arraycopy(frame, 0, datagram, HEADER_BYTES, frameBytes);

                socket.send(new DatagramPacket(datagram, datagram.length, target, port));
                sequence++;
            }
        } catch (Exception error) {
            Log.e(TAG, "capture loop stopped", error);
        } finally {
            try {
                if (recorder != null) recorder.stop();
            } catch (Exception ignored) {
                // already stopped or never started
            }
        }
    }

    private synchronized void stopStreaming() {
        running = false;

        if (worker != null) {
            try {
                worker.join(1500);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
            }
            worker = null;
        }

        cleanup();
    }

    private void cleanup() {
        running = false;

        try {
            if (recorder != null) recorder.release();
        } catch (Exception ignored) {
            // release is best effort
        }
        recorder = null;

        try {
            if (socket != null) socket.close();
        } catch (Exception ignored) {
            // closing a dead socket is not an error
        }
        socket = null;
    }

    @Override
    public void onDestroy() {
        stopStreaming();
        super.onDestroy();
    }
}
