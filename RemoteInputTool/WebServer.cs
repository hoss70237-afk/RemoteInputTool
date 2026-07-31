using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Web.Script.Serialization;

namespace RemoteInputTool
{
    public class WebServer
    {
        private HttpListener _listener;
        private ScreenCapture _capture;
        private HashSet<string> _allowedIps;
        private string ipsFilePath = "allowed_ips.json";
        private JavaScriptSerializer _json = new JavaScriptSerializer();

        public WebServer(ScreenCapture capture)
        {
            _capture = capture;
            LoadAllowedIps();
        }

        private void LoadAllowedIps()
        {
            if (File.Exists(ipsFilePath))
                _allowedIps = new HashSet<string>(_json.Deserialize<List<string>>(File.ReadAllText(ipsFilePath)));
            else
                _allowedIps = new HashSet<string>();
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://*:5360/");
            _listener.Start();
            Task.Run(ListenLoop);
        }

        public void Stop() => _listener?.Stop();

        private async Task ListenLoop()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    string ip = context.Request.RemoteEndPoint.Address.ToString();

                    if (!_allowedIps.Contains(ip) && ip != "127.0.0.1")
                    {
                        bool isAllowed = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var res = MessageBox.Show($"IP: {ip} からの接続要求があります。許可しますか？", "セキュリティ確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);
                            isAllowed = (res == MessageBoxResult.Yes);
                        });

                        if (isAllowed)
                        {
                            _allowedIps.Add(ip);
                            File.WriteAllText(ipsFilePath, _json.Serialize(_allowedIps));
                        }
                        else
                        {
                            context.Response.StatusCode = 403;
                            context.Response.Close();
                            continue;
                        }
                    }

                    if (context.Request.IsWebSocketRequest)
                        _ = ProcessWebSocket(context);
                    else
                        ServeHtml(context);
                }
                catch { }
            }
        }

        private void ServeHtml(HttpListenerContext context)
        {
            string html = File.ReadAllText("WebClient.html"); // HTMLは外部ファイル、または埋め込みリソースから読み込む
            byte[] buf = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buf.Length;
            context.Response.OutputStream.Write(buf, 0, buf.Length);
            context.Response.Close();
        }

        private async Task ProcessWebSocket(HttpListenerContext context)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var ws = wsContext.WebSocket;

            Action<string> onImageCaptured = async (base64) =>
            {
                if (ws.State == WebSocketState.Open)
                {
                    // 実際にはマウスカーソル座標も付与して送信
                    var payload = _json.Serialize(new { type = "image", data = base64, cursor = new { x = 0.5, y = 0.5 } });
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            };

            _capture.OnFrameReady += onImageCaptured;
            _capture.Start();

            byte[] buffer = new byte[1024 * 4];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var cmd = _json.Deserialize<Dictionary<string, object>>(msg);
                    HandleCommand(cmd);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            _capture.OnFrameReady -= onImageCaptured;
        }

        private void HandleCommand(Dictionary<string, object> cmd)
        {
            if (!cmd.ContainsKey("action")) return;
            string action = cmd["action"].ToString();
            
            try {
                if (action == "move") InputEmulator.MoveMouse(Convert.ToDouble(cmd["x"]), Convert.ToDouble(cmd["y"]));
                else if (action == "move_rel") InputEmulator.MoveMouseRel(Convert.ToInt32(cmd["dx"]), Convert.ToInt32(cmd["dy"]));
                else if (action == "click") InputEmulator.Click(cmd["button"].ToString());
                else if (action == "drag") InputEmulator.Drag(cmd["state"].ToString());
                else if (action == "scroll") InputEmulator.Scroll(Convert.ToInt32(cmd["value"]));
                else if (action == "launch_app") {
                    int id = Convert.ToInt32(cmd["id"]);
                    var app = MainWindow.AppConfig.Apps.Find(a => a.Id == id);
                    if (app != null && File.Exists(app.Path)) System.Diagnostics.Process.Start(app.Path);
                }
            } catch { }
        }
    }
}
