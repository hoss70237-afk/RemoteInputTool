using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Web.Script.Serialization;
using System.Linq;

namespace RemoteInputTool
{
    public class WebServer
    {
        private TcpListener _listener;
        private ScreenCapture _capture;
        private HashSet<string> _allowedIps;
        private List<TcpClient> _clients = new List<TcpClient>();
        private string ipsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "allowed_ips.json");
        private JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _lock = new object();
        private bool _isRunning = false;
        
        public WebServer(ScreenCapture capture)
        {
            _capture = capture;
            LoadAllowedIps();
        }

        private void LoadAllowedIps()
        {
            if (File.Exists(ipsFilePath)) _allowedIps = new HashSet<string>(_json.Deserialize<List<string>>(File.ReadAllText(ipsFilePath)));
            else _allowedIps = new HashSet<string>();
        }

        public void Start()
        {
            _capture.Start(); // サーバー起動時にキャプチャースレッドも起動（クライアント0ならWaitで待機する）
            _listener = new TcpListener(IPAddress.Any, 5360);
            _listener.Start();
            _isRunning = true;
            Task.Run(ListenLoop);
        }

        public void Stop() { _isRunning = false; _listener?.Stop(); DisconnectAllClients(); }

        public void DisconnectAllClients()
        {
            lock (_lock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
        }

        public void BroadcastInitData()
        {
            var initData = new { 
                type = "init", 
                apps = MainWindow.AppConfig.Apps, 
                areas = MainWindow.AppConfig.CaptureAreas,
                grid2 = MainWindow.AppConfig.Grid2,
                grid4 = MainWindow.AppConfig.Grid4,
                grid9 = MainWindow.AppConfig.Grid9
            };
            var data = Encoding.UTF8.GetBytes(_json.Serialize(initData));
            var frame = CreateWebSocketFrame(data);
            
            lock (_lock) {
                foreach(var c in _clients.ToList()) {
                    try { c.GetStream().Write(frame, 0, frame.Length); } catch { }
                }
            }
        }

        private async Task ListenLoop()
        {
            while (_isRunning)
            {
                try {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => ProcessClientAsync(client));
                } catch { }
            }
        }

        private async Task ProcessClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    bool isAllowed;
                    lock (_lock) { isAllowed = _allowedIps.Contains(ip) || ip == "127.0.0.1"; }

                    if (!isAllowed)
                    {
                        bool userGranted = false;
                        Application.Current.Dispatcher.Invoke(() => {
                            var res = MessageBox.Show($"IP: {ip} からの接続要求があります。許可しますか？", "セキュリティ確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);
                            userGranted = (res == MessageBoxResult.Yes);
                        });
                        if (userGranted) { lock (_lock) { _allowedIps.Add(ip); File.WriteAllText(ipsFilePath, _json.Serialize(_allowedIps.ToList())); } }
                        else {
                            byte[] fbBytes = Encoding.UTF8.GetBytes("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(fbBytes, 0, fbBytes.Length);
                            return;
                        }
                    }

                    byte[] buffer = new byte[8192];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) return;
                    string req = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (req.Contains("Upgrade: websocket")) await ProcessWebSocket(client, stream, req);
                    else if (req.StartsWith("GET / HTTP")) ServeHtml(stream);
                    else {
                        byte[] nfBytes = Encoding.UTF8.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(nfBytes, 0, nfBytes.Length);
                    }
                }
                catch { }
            }
        }

        private void ServeHtml(NetworkStream stream)
        {
            try {
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebClient.html");
                string html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : "Not Found";
                byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
                byte[] headerBytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(headerBytes, 0, headerBytes.Length); stream.Write(htmlBytes, 0, htmlBytes.Length);
            } catch { }
        }

        private async Task ProcessWebSocket(TcpClient client, NetworkStream stream, string req)
        {
            string key = req.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))?.Substring(18).Trim();
            if (string.IsNullOrEmpty(key)) return;

            string acceptKey = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] handshake = Encoding.UTF8.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n");
            await stream.WriteAsync(handshake, 0, handshake.Length);

            lock (_lock) { _clients.Add(client); }
            BroadcastInitData();
            
            // クライアント接続を通知（キャプチャ開始）
            _capture.AddClient();

            bool isConnected = true;
            Action<string, double, double> onImageCaptured = async (base64, curX, curY) => {
                try {
                    if (!isConnected) return;
                    var payload = _json.Serialize(new { type = "image", data = base64, cursor = new { x = curX, y = curY } });
                    byte[] frame = CreateWebSocketFrame(Encoding.UTF8.GetBytes(payload));
                    await stream.WriteAsync(frame, 0, frame.Length);
                } catch { isConnected = false; }
            };

            _capture.OnFrameReady += onImageCaptured;

            try {
                byte[] buffer = new byte[8192];
                while (isConnected) {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    var cmds = DecodeWebSocketFrames(buffer, bytesRead);
                    foreach (var cmd in cmds) {
                        if (cmd.ContainsKey("_close_")) {
                            isConnected = false;
                            break;
                        }
                        if (cmd != null) HandleCommand(cmd);
                    }
                }
            }
            catch { }
            finally {
                isConnected = false; 
                _capture.OnFrameReady -= onImageCaptured;
                // クライアント切断を通知
                _capture.RemoveClient();
                lock (_lock) { _clients.Remove(client); }
            }
        }

        private byte[] CreateWebSocketFrame(byte[] payload)
        {
            int hl = payload.Length <= 125 ? 2 : (payload.Length <= 65535 ? 4 : 10);
            byte[] f = new byte[hl + payload.Length]; f[0] = 0x81;
            if (payload.Length <= 125) f[1] = (byte)payload.Length;
            else if (payload.Length <= 65535) { f[1] = 126; f[2] = (byte)(payload.Length >> 8); f[3] = (byte)(payload.Length & 255); }
            else { f[1] = 127; var len = BitConverter.GetBytes((ulong)payload.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len); Array.Copy(len, 0, f, 2, 8); }
            Array.Copy(payload, 0, f, hl, payload.Length); return f;
        }

        private List<Dictionary<string, object>> DecodeWebSocketFrames(byte[] buffer, int length)
        {
            var res = new List<Dictionary<string, object>>(); int pos = 0;
            while (pos < length - 2) {
                int opcode = buffer[pos] & 15; 
                if (opcode == 8) {
                    res.Add(new Dictionary<string, object> { { "_close_", true } });
                    break;
                }
                
                bool mask = (buffer[pos + 1] & 128) != 0; int len = buffer[pos + 1] & 127;
                int off = pos + 2; if (len == 126) off += 2; else if (len == 127) off += 8;
                if (off > length || !mask) break;
                byte[] key = new byte[4]; Array.Copy(buffer, off, key, 0, 4); off += 4;
                if (off + len > length) break;
                byte[] dec = new byte[len]; for (int i = 0; i < len; i++) dec[i] = (byte)(buffer[off + i] ^ key[i % 4]);
                try { var d = _json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(dec)); if (d != null) res.Add(d); } catch { }
                pos = off + len;
            }
            return res;
        }

        private void HandleCommand(Dictionary<string, object> cmd)
        {
            if (!cmd.ContainsKey("action")) return;
            string action = cmd["action"].ToString();
            try {
                if (action == "change_area") {
                    int i = Convert.ToInt32(cmd["index"]);
                    Application.Current.Dispatcher.Invoke(() => {
                        MainWindow.CurrentCaptureArea = i == -1 
                            ? new Rect(0, 0, SystemParameters.PrimaryScreenWidth * MainWindow.DpiX, SystemParameters.PrimaryScreenHeight * MainWindow.DpiY) 
                            : MainWindow.AppConfig.CaptureAreas[i].Area;
                    });
                }
                else if (action == "move") InputEmulator.MoveMouse(Convert.ToDouble(cmd["x"]), Convert.ToDouble(cmd["y"]), MainWindow.CurrentCaptureArea);
                else if (action == "move_rel") InputEmulator.MoveMouseRel(Convert.ToInt32(cmd["dx"]), Convert.ToInt32(cmd["dy"]));
                else if (action == "click") InputEmulator.Click(cmd["button"].ToString());
                else if (action == "drag") InputEmulator.Drag(cmd["state"].ToString());
                else if (action == "scroll") InputEmulator.Scroll(Convert.ToInt32(cmd["value"]));
                else if (action == "launch_app") {
                    var app = MainWindow.AppConfig.Apps.Find(a => a.Id == Convert.ToInt32(cmd["id"]));
                    if (app != null && File.Exists(app.Path)) System.Diagnostics.Process.Start(app.Path);
                }
                else if (action == "grid_action") {
                    int split = Convert.ToInt32(cmd["split"]);
                    int index = Convert.ToInt32(cmd["index"]);
                    GridSettings grid = null;
                    if (split == 2) grid = MainWindow.AppConfig.Grid2;
                    else if (split == 4) grid = MainWindow.AppConfig.Grid4;
                    else if (split == 9) grid = MainWindow.AppConfig.Grid9;

                    if (grid != null && index >= 0 && index < grid.Cells.Count) {
                        var cell = grid.Cells[index];
                        if (cell.ActionType == "Mouse") InputEmulator.Click(cell.Detail);
                        else if (cell.ActionType == "Key") InputEmulator.SendKey(cell.Detail);
                        else if (cell.ActionType == "App" && File.Exists(cell.Detail)) System.Diagnostics.Process.Start(cell.Detail);
                    }
                }
            } catch { }
        }
    }
}
