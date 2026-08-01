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
using System.Threading.Tasks;

namespace RemoteInputTool
{
    public partial class MainWindow : Window
    {
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        public static Config AppConfig { get; set; } = new Config();
        public static Rect CurrentCaptureArea = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        
        private WebServer _webServer;
        private ScreenCapture _screenCapture;
        private string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server_config.json");

        public MainWindow()
        {
            InitializeComponent();
            
            // 最小化・タスクトレイ起動
            WindowState = WindowState.Minimized;
            Hide();
            ShowInTaskbar = false;

            LoadConfig();
            SetupNotifyIcon();
            InitializeAppList();
            RefreshAreaList();

            string localIp = GetLocalIPAddress();
            UrlTextBox.Text = $"http://{localIp}:5360/";

            _screenCapture = new ScreenCapture();
            _webServer = new WebServer(_screenCapture);
            _webServer.Start();
            StatusTextBlock.Text = "サーバー稼働中...";

            // 1.5秒で消える起動メッセージ
            Task.Run(async () => {
                await Task.Delay(500);
                _notifyIcon.ShowBalloonTip(1500, "リモート入力ツール", "サーバーが最小化状態で起動しました", System.Windows.Forms.ToolTipIcon.Info);
            });
        }

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "スマホ用リモート入力ツール"
            };
            _notifyIcon.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; ShowInTaskbar = true; };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("全切断", null, (s, e) => _webServer.DisconnectAllClients());
            menu.Items.Add("終了", null, (s, e) => Application.Current.Shutdown());
            _notifyIcon.ContextMenuStrip = menu;
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
            }
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
                AppConfig.Apps = Enumerable.Range(1, 10).Select(i => new AppItem { Id = i, Name = $"App{i}", Path = "" }).ToList();
            if (AppConfig.CaptureAreas == null)
                AppConfig.CaptureAreas = new List<CaptureAreaItem>();
        }

        private void SaveConfig()
        {
            File.WriteAllText(configPath, new JavaScriptSerializer().Serialize(AppConfig));
        }

        private void InitializeAppList() => AppItemsControl.ItemsSource = AppConfig.Apps;
        private void SaveApp_Click(object sender, RoutedEventArgs e) { SaveConfig(); _webServer.BroadcastInitData(); }

        private void RefreshAreaList()
        {
            AreaListBox.ItemsSource = null;
            AreaListBox.ItemsSource = AppConfig.CaptureAreas;
        }

        private void PerformDragSelect(Action<Rect> onSelected)
        {
            var overlay = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                Topmost = true, Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight, Cursor = Cursors.Cross
            };
            var canvas = new Canvas();
            var rectUI = new System.Windows.Shapes.Rectangle { Stroke = Brushes.Red, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(50, 255, 0, 0)) };
            canvas.Children.Add(rectUI);
            overlay.Content = canvas;

            Point startPoint = new Point();
            bool isDragging = false;
            overlay.MouseDown += (s, ev) => { 
                startPoint = ev.GetPosition(overlay); 
                isDragging = true;
                Canvas.SetLeft(rectUI, startPoint.X); Canvas.SetTop(rectUI, startPoint.Y);
                rectUI.Width = 0; rectUI.Height = 0;
            };
            overlay.MouseMove += (s, ev) => {
                if(!isDragging) return;
                var current = ev.GetPosition(overlay);
                var x = Math.Min(startPoint.X, current.X); var y = Math.Min(startPoint.Y, current.Y);
                var w = Math.Abs(current.X - startPoint.X); var h = Math.Abs(current.Y - startPoint.Y);
                Canvas.SetLeft(rectUI, x); Canvas.SetTop(rectUI, y);
                rectUI.Width = w; rectUI.Height = h;
            };
            overlay.MouseUp += (s, ev) => {
                isDragging = false;
                var w = rectUI.Width; var h = rectUI.Height;
                var x = Canvas.GetLeft(rectUI); var y = Canvas.GetTop(rectUI);
                overlay.Close();
                if(w > 10 && h > 10) onSelected(new Rect(x, y, w, h));
            };
            overlay.ShowDialog();
        }

        private void AddArea_Click(object sender, RoutedEventArgs e)
        {
            string name = AreaNameTextBox.Text.Trim();
            if(string.IsNullOrEmpty(name)) { MessageBox.Show("領域名を入力してください。"); return; }
            PerformDragSelect(rect => {
                AppConfig.CaptureAreas.Add(new CaptureAreaItem { Name = name, Area = rect });
                SaveConfig(); RefreshAreaList();
                AreaListBox.SelectedIndex = AppConfig.CaptureAreas.Count - 1;
                _webServer.BroadcastInitData();
            });
        }

        private void OverwriteArea_Click(object sender, RoutedEventArgs e)
        {
            if (AreaListBox.SelectedItem is CaptureAreaItem item) {
                string name = AreaNameTextBox.Text.Trim();
                if(!string.IsNullOrEmpty(name)) item.Name = name;
                PerformDragSelect(rect => {
                    item.Area = rect; SaveConfig(); RefreshAreaList(); _webServer.BroadcastInitData();
                });
            } else MessageBox.Show("上書きする領域を選択してください。");
        }

        private void DeleteArea_Click(object sender, RoutedEventArgs e)
        {
            if (AreaListBox.SelectedItem is CaptureAreaItem item) {
                AppConfig.CaptureAreas.Remove(item);
                SaveConfig(); RefreshAreaList(); _webServer.BroadcastInitData();
            }
        }

        private void AreaListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if(AreaListBox.SelectedItem is CaptureAreaItem item)
            {
                AreaNameTextBox.Text = item.Name;
                CurrentCaptureArea = item.Area;
            }
        }

        private string GetLocalIPAddress()
        {
            string localIP = "127.0.0.1";
            try {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0)) {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint) localIP = endPoint.Address.ToString();
                }
            } catch {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                localIP = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
            }
            return localIP;
        }
    }

    public class Config
    {
        public List<CaptureAreaItem> CaptureAreas { get; set; } = new List<CaptureAreaItem>();
        public int Fps { get; set; } = 30;
        public int Quality { get; set; } = 50;
        public List<AppItem> Apps { get; set; }
    }
    public class CaptureAreaItem { public string Name { get; set; } public Rect Area { get; set; } }
    public class AppItem { public int Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
}
