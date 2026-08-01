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
        private string ipsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "allowed_ips.json");
        private JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _ipsLock = new object();
        private bool _isRunning = false;

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
            // HttpListenerではなくTcpListenerを使用し、OSの制限と管理者権限の壁を突破する
            _listener = new TcpListener(IPAddress.Any, 5360);
            _listener.Start();
            _isRunning = true;
            Task.Run(ListenLoop);
        }

        public void Stop() 
        {
            _isRunning = false;
            _listener?.Stop();
        }

        private async Task ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => ProcessClientAsync(client));
                }
                catch { }
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
                    lock (_ipsLock) { isAllowed = _allowedIps.Contains(ip) || ip == "127.0.0.1"; }

                    if (!isAllowed)
                    {
                        bool userGranted = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var res = MessageBox.Show($"IP: {ip} からの接続要求があります。許可しますか？", "セキュリティ確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);
                            userGranted = (res == MessageBoxResult.Yes);
                        });

                        if (userGranted)
                        {
                            lock (_ipsLock)
                            {
                                _allowedIps.Add(ip);
                                File.WriteAllText(ipsFilePath, _json.Serialize(_allowedIps.ToList()));
                            }
                        }
                        else
                        {
                            string forbidden = "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n";
                            byte[] fbBytes = Encoding.UTF8.GetBytes(forbidden);
                            await stream.WriteAsync(fbBytes, 0, fbBytes.Length);
                            return;
                        }
                    }

                    // リクエストの読み取り
                    byte[] buffer = new byte[8192];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) return;
                    string requestString = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // WebSocketの要求か、通常のHTML表示の要求かを振り分ける
                    if (requestString.Contains("Upgrade: websocket"))
                    {
                        await ProcessWebSocket(stream, requestString);
                    }
                    else if (requestString.StartsWith("GET / HTTP"))
                    {
                        ServeHtml(stream);
                    }
                    else
                    {
                        string notFound = "HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n";
                        byte[] nfBytes = Encoding.UTF8.GetBytes(notFound);
                        await stream.WriteAsync(nfBytes, 0, nfBytes.Length);
                    }
                }
                catch { }
            }
        }

        private void ServeHtml(NetworkStream stream)
        {
            try
            {
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebClient.html");
                string html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : "<html><body>WebClient.html Not Found</body></html>";
                byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
                
                string header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(htmlBytes, 0, htmlBytes.Length);
            }
            catch { }
        }

        // C#でWebSocketのプロトコルを手動で処理する（Windows7対応）
        private async Task ProcessWebSocket(NetworkStream stream, string requestString)
        {
            string key = "";
            foreach (var line in requestString.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Sec-WebSocket-Key: ", StringComparison.OrdinalIgnoreCase))
                {
                    key = line.Substring("Sec-WebSocket-Key: ".Length).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(key)) return;

            // ハンドシェイクの確立
            string acceptKey = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            string handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                               "Upgrade: websocket\r\n" +
                               "Connection: Upgrade\r\n" +
                               "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";
            byte[] handshakeBytes = Encoding.UTF8.GetBytes(handshake);
            await stream.WriteAsync(handshakeBytes, 0, handshakeBytes.Length);

            bool isConnected = true;

            Action<string> onImageCaptured = async (base64) =>
            {
                try
                {
                    if (!isConnected) return;
                    var payload = _json.Serialize(new { type = "image", data = base64, cursor = new { x = 0.5, y = 0.5 } });
                    byte[] data = Encoding.UTF8.GetBytes(payload);
                    byte[] frame = CreateWebSocketFrame(data);
                    await stream.WriteAsync(frame, 0, frame.Length);
                }
                catch { isConnected = false; }
            };

            _capture.OnFrameReady += onImageCaptured;
            _capture.Start();

            try
            {
                byte[] buffer = new byte[8192];
                while (isConnected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var cmds = DecodeWebSocketFrames(buffer, bytesRead);
                    foreach (var cmd in cmds)
                    {
                        if (cmd != null) HandleCommand(cmd);
                    }
                }
            }
            catch { }
            finally
            {
                isConnected = false;
                _capture.OnFrameReady -= onImageCaptured;
            }
        }

        private byte[] CreateWebSocketFrame(byte[] payload)
        {
            int headerLength = payload.Length <= 125 ? 2 : (payload.Length <= 65535 ? 4 : 10);
            byte[] frame = new byte[headerLength + payload.Length];
            frame[0] = 0x81; // FIN + Text

            if (payload.Length <= 125)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (payload.Length <= 65535)
            {
                frame[1] = 126;
                frame[2] = (byte)((payload.Length >> 8) & 255);
                frame[3] = (byte)(payload.Length & 255);
            }
            else
            {
                frame[1] = 127;
                var lenBytes = BitConverter.GetBytes((ulong)payload.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                Array.Copy(lenBytes, 0, frame, 2, 8);
            }

            Array.Copy(payload, 0, frame, headerLength, payload.Length);
            return frame;
        }

        private List<Dictionary<string, object>> DecodeWebSocketFrames(byte[] buffer, int length)
        {
            var results = new List<Dictionary<string, object>>();
            int pos = 0;
            while (pos < length - 2)
            {
                bool fin = (buffer[pos] & 0b10000000) != 0;
                int opcode = buffer[pos] & 0b00001111;
                if (opcode == 8) // 切断要求
                    break;

                bool mask = (buffer[pos + 1] & 0b10000000) != 0;
                int payloadLen = buffer[pos + 1] & 0b01111111;
                int offset = pos + 2;

                if (payloadLen == 126) { offset += 2; }
                else if (payloadLen == 127) { offset += 8; }

                if (offset > length || !mask) break;

                byte[] maskKey = new byte[4];
                Array.Copy(buffer, offset, maskKey, 0, 4);
                offset += 4;

                if (offset + payloadLen > length) break;

                byte[] decoded = new byte[payloadLen];
                for (int i = 0; i < payloadLen; i++)
                {
                    decoded[i] = (byte)(buffer[offset + i] ^ maskKey[i % 4]);
                }

                string json = Encoding.UTF8.GetString(decoded);
                try
                {
                    var dict = _json.Deserialize<Dictionary<string, object>>(json);
                    if (dict != null) results.Add(dict);
                }
                catch { }

                pos = offset + payloadLen;
            }
            return results;
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
