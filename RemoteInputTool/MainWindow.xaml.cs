using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Web.Script.Serialization;
using System.Net;
using System.Net.Sockets;

namespace RemoteInputTool
{
    public partial class MainWindow : Window
    {
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        public static Config AppConfig { get; set; } = new Config();
        private WebServer _webServer;
        private ScreenCapture _screenCapture;
        
        // 【修正】カレントディレクトリに依存しない絶対パス化
        private string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server_config.json");

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            SetupNotifyIcon();
            InitializeAppList();

            string localIp = GetLocalIPAddress();
            UrlTextBox.Text = $"http://{localIp}:5360/";

            _screenCapture = new ScreenCapture();
            _webServer = new WebServer(_screenCapture);
            _webServer.Start();
            StatusTextBlock.Text = "サーバー稼働中...";
        }

        /* ... SetupNotifyIcon から DragSelect_Click までは変更なし ... */

        // 【修正】仮想IPを避け、正しいローカルIPを取得するロジックに変更
        private string GetLocalIPAddress()
        {
            string localIP = "127.0.0.1";
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    // 外部へ通信する際に使用される正しいLAN内IPを取得
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                    {
                        localIP = endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                // オフライン時のフォールバック
                var host = Dns.GetHostEntry(Dns.GetHostName());
                localIP = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
            }
            return localIP;
        }
    }

    /* Config, AppItemクラスはそのまま */
}

    public class Config
    {
        public Rect CaptureArea { get; set; } = new Rect(0,0,1920,1080);
        public int Fps { get; set; } = 30;
        public int Quality { get; set; } = 50;
        public List<AppItem> Apps { get; set; }
    }
    public class AppItem { public int Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
}
