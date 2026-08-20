package com.hjy.bookpicks;

import android.app.Activity;
import android.graphics.Color;
import java.io.IOException;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.ConsoleMessage;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

/**
 * 韩俊宇的书库 · 全球书榜(Android 版)
 * WebView 壳:加载内置前端(file:///android_asset),前端直连 Open Library 官方接口
 * (接口均返回 Access-Control-Allow-Origin:*,已在开发环境验证)。
 */
public class MainActivity extends Activity {

    private WebView web;
    private MiniServer server;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // 沉浸式状态栏:透明背景 + 深色图标(适配浅色主题界面)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            Window w = getWindow();
            w.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
            w.setStatusBarColor(Color.TRANSPARENT);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                w.getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR);
            }
        }

        // 本地微型服务器:仅监听 127.0.0.1,提供前端静态页面。
        // 从 http://127.0.0.1 加载后,跨域 fetch https://openlibrary.org
        // 走标准 CORS(接口返回 allow-origin:*),不受 file:// 协议限制。
        try {
            server = new MiniServer(this);
            new Thread(server).start();
        } catch (IOException e) {
            throw new IllegalStateException("无法启动本地服务器", e);
        }

        web = new WebView(this);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);              // localStorage(收藏存储)
        s.setLoadWithOverviewMode(true);
        s.setUseWideViewPort(true);
        s.setCacheMode(WebSettings.LOAD_DEFAULT);
        s.setSupportZoom(false);

        // 页面内不跳外部浏览器,保持单页应用行为
        web.setWebViewClient(new WebViewClient());
        web.setWebChromeClient(new WebChromeClient());
        web.loadUrl("http://127.0.0.1:" + server.getPort() + "/index.html");
        setContentView(web);
    }

    @Override
    public void onBackPressed() {
        // 单页应用无页面栈,返回键直接退出
        finish();
    }

    @Override
    protected void onDestroy() {
        if (server != null) server.close();
        if (web != null) {
            web.stopLoading();
            web.destroy();
        }
        super.onDestroy();
    }
}
