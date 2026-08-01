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

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "スマホ用リモート入力ツール"
            };
            _notifyIcon.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; };
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) Hide();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _notifyIcon.Dispose();
            _webServer.Stop();
            _screenCapture.Stop();
            SaveConfig();
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                AppConfig = new JavaScriptSerializer().Deserialize<Config>(json);
            }
            if (AppConfig.Apps == null || !AppConfig.Apps.Any())
            {
                AppConfig.Apps = Enumerable.Range(1, 10).Select(i => new AppItem { Id = i, Name = $"App{i}", Path = "" }).ToList();
            }
        }

        private void SaveConfig()
        {
            var json = new JavaScriptSerializer().Serialize(AppConfig);
            File.WriteAllText(configPath, json);
        }

        private void InitializeAppList() => AppItemsControl.ItemsSource = AppConfig.Apps;

        private void SaveApp_Click(object sender, RoutedEventArgs e) => SaveConfig();

        private void FullScreen_Click(object sender, RoutedEventArgs e)
        {
            AppConfig.CaptureArea = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            SaveConfig();
            MessageBox.Show("全画面を設定しました。");
        }

        private void DragSelect_Click(object sender, RoutedEventArgs e)
        {
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                Topmost = true, Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
                Cursor = Cursors.Cross
            };
            Point startPoint = new Point();
            overlay.MouseDown += (s, ev) => { startPoint = ev.GetPosition(overlay); };
            overlay.MouseUp += (s, ev) =>
            {
                var endPoint = ev.GetPosition(overlay);
                AppConfig.CaptureArea = new Rect(startPoint, endPoint);
                SaveConfig();
                overlay.Close();
                MessageBox.Show("領域を保存しました。");
            };
            overlay.ShowDialog();
        }

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

    public class Config
    {
        public Rect CaptureArea { get; set; } = new Rect(0,0,1920,1080);
        public int Fps { get; set; } = 30;
        public int Quality { get; set; } = 50;
        public List<AppItem> Apps { get; set; }
    }
    public class AppItem { public int Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
}
