package com.hjy.bookpicks;

import android.content.Context;

import java.io.ByteArrayOutputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * 极简本地 HTTP 服务器:只服务内置前端静态文件(assets/www/*)。
 * 仅监听 127.0.0.1,不与外部通信。
 *
 * 为什么不用 file:// 加载:Android 14+ 的 WebView 禁止 file:// 页面发起
 * 网络请求(ERR_FILE_NOT_FOUND)。改从 http://127.0.0.1 加载后,前端跨域
 * fetch https://openlibrary.org 走标准 CORS(该 API 返回 allow-origin:*),
 * 桌面版本地代理的所有 /api 路径由前端自动映射为直连。
 */
public class MiniServer implements Runnable {

    private final Context ctx;
    private final ServerSocket server;
    private final ExecutorService pool;

    public MiniServer(Context ctx) throws IOException {
        this.ctx = ctx.getApplicationContext();
        this.server = new ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"));
        this.pool = Executors.newCachedThreadPool();
    }

    public int getPort() {
        return server.getLocalPort();
    }

    @Override
    public void run() {
        while (true) {
            try {
                Socket s = server.accept();
                pool.execute(() -> handle(s));
            } catch (IOException e) {
                break; // 服务器关闭
            }
        }
    }

    public void close() {
        try { server.close(); } catch (IOException ignored) {}
        pool.shutdownNow();
    }

    private void handle(Socket s) {
        try (Socket sock = s) {
            sock.setSoTimeout(8000);
            InputStream rawIn = sock.getInputStream();
            // 读请求行(只支持 GET)
            StringBuilder head = new StringBuilder();
            int c;
            while (head.length() < 4096 && (c = rawIn.read()) != -1) {
                if (c == '\n') break;
                head.append((char) c);
            }
            String line = head.toString().trim();
            if (line.isEmpty() || !line.startsWith("GET")) return;
            String[] parts = line.split(" ");
            if (parts.length < 2) return;
            String path = parts[1];
            if (path.contains("?")) path = path.substring(0, path.indexOf('?'));
            if (path.equals("/") || path.equals("/index.html")) path = "/index.html";
            serve(sock, rawIn, path);
        } catch (IOException ignored) {
        }
    }

    private void serve(Socket sock, InputStream rawIn, String path) {
        // 路径安全:只允许 www/ 下的文件,拒绝 ../
        String name = "www" + path;
        if (name.contains("..")) { sendError(sock, 403, "Forbidden"); return; }
        byte[] data;
        try {
            InputStream is = ctx.getAssets().open(name);
            data = readAll(is);
            is.close();
        } catch (FileNotFoundException e) {
            sendError(sock, 404, "Not Found");
            return;
        } catch (IOException e) {
            sendError(sock, 500, "Internal Error");
            return;
        }
        try {
            OutputStream os = sock.getOutputStream();
            StringBuilder resp = new StringBuilder();
            resp.append("HTTP/1.1 200 OK\r\n");
            resp.append("Content-Type: ").append(mimeOf(name)).append("\r\n");
            resp.append("Content-Length: ").append(data.length).append("\r\n");
            resp.append("Connection: close\r\n");
            resp.append("Cache-Control: no-cache\r\n\r\n");
            os.write(resp.toString().getBytes("ISO-8859-1"));
            os.write(data);
            os.flush();
        } catch (IOException ignored) {
        }
    }

    private void sendError(Socket sock, int code, String text) {
        try {
            OutputStream os = sock.getOutputStream();
            String body = "<h1>" + code + " " + text + "</h1>";
            StringBuilder resp = new StringBuilder();
            resp.append("HTTP/1.1 ").append(code).append(" ").append(text).append("\r\n");
            resp.append("Content-Type: text/html; charset=utf-8\r\n");
            resp.append("Content-Length: ").append(body.getBytes("UTF-8").length).append("\r\n");
            resp.append("Connection: close\r\n\r\n");
            os.write(resp.toString().getBytes("ISO-8859-1"));
            os.write(body.getBytes("UTF-8"));
            os.flush();
        } catch (IOException ignored) {
        }
    }

    private static byte[] readAll(InputStream is) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) != -1) out.write(buf, 0, n);
        return out.toByteArray();
    }

    private static String mimeOf(String name) {
        if (name.endsWith(".html")) return "text/html; charset=utf-8";
        if (name.endsWith(".js")) return "application/javascript; charset=utf-8";
        if (name.endsWith(".css")) return "text/css; charset=utf-8";
        if (name.endsWith(".json")) return "application/json; charset=utf-8";
        if (name.endsWith(".png")) return "image/png";
        if (name.endsWith(".jpg") || name.endsWith(".jpeg")) return "image/jpeg";
        if (name.endsWith(".svg")) return "image/svg+xml";
        if (name.endsWith(".ico")) return "image/x-icon";
        return "application/octet-stream";
    }
}
