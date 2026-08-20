using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BookPicks
{
    /// <summary>主窗口:内嵌 WebView2 展示本地前端界面。</summary>
    public sealed class MainForm : Form
    {
        public MainForm(string baseUrl)
        {
            Text = "韩俊宇的书库 · 全球书榜";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(940, 620);
            BackColor = Color.FromArgb(250, 246, 239);

            var web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(web);

            // 独立用户数据目录,收藏/主题等本地存储可持久化
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BookPicks", "WebView2");

            Load += async (_, _) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, userData);
                    await web.EnsureCoreWebView2Async(env);
                    web.CoreWebView2.Navigate(baseUrl + "index.html");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("内置浏览器(WebView2)初始化失败:\n" + ex.Message,
                        "韩俊宇的书库", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }
    }
}
