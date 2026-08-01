using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteInputTool
{
    public class ScreenCapture
    {
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);
        public struct POINT { public int X; public int Y; }

        public event Action<string, double, double> OnFrameReady;
        private bool _isRunning = false;
        
        private int _clientCount = 0;
        private ManualResetEventSlim _clientConnectedEvent = new ManualResetEventSlim(false);

        public void AddClient()
        {
            if (Interlocked.Increment(ref _clientCount) > 0)
                _clientConnectedEvent.Set();
        }

        public void RemoveClient()
        {
            if (Interlocked.Decrement(ref _clientCount) == 0)
                _clientConnectedEvent.Reset();
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Task.Run(() => CaptureLoop());
        }

        public void Stop()
        {
            _isRunning = false;
            _clientConnectedEvent.Set(); // ブロック解除してループを抜けさせる
        }

        private void CaptureLoop()
        {
            Bitmap bmp = null;
            Graphics g = null;
            int lastW = 0, lastH = 0;

            ImageCodecInfo jpegCodec = GetEncoderInfo("image/jpeg");

            while (_isRunning)
            {
                // クライアントが0の場合はここでスレッドが待機状態になり、CPU使用率0%になる
                _clientConnectedEvent.Wait();
                if (!_isRunning) break;

                var sw = System.Diagnostics.Stopwatch.StartNew();

                var rect = MainWindow.CurrentCaptureArea;
                int width = (int)Math.Max(1, rect.Width);
                int height = (int)Math.Max(1, rect.Height);
                int x = (int)rect.X;
                int y = (int)rect.Y;

                if (bmp == null || lastW != width || lastH != height)
                {
                    g?.Dispose();
                    bmp?.Dispose();
                    bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    g = Graphics.FromImage(bmp);
                    lastW = width;
                    lastH = height;
                }

                try
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                    
                    GetCursorPos(out var pt);
                    double curX = (pt.X - x) / (double)width;
                    double curY = (pt.Y - y) / (double)height;

                    using (var ms = new MemoryStream())
                    {
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, MainWindow.AppConfig.Quality);
                        bmp.Save(ms, jpegCodec, encoderParams);
                        
                        OnFrameReady?.Invoke(Convert.ToBase64String(ms.ToArray()), curX, curY);
                    }
                }
                catch
                {
                    // 画面ロック時やUAC表示時などは例外が出ることがあるため無視
                }

                sw.Stop();
                // 処理にかかった時間を差し引いてSleepすることでフレームレートを安定させる
                int targetDelay = 1000 / Math.Max(1, MainWindow.AppConfig.Fps);
                int delay = targetDelay - (int)sw.ElapsedMilliseconds;
                
                // CPUが1コアに張り付くのを防ぐため、最低でも1ミリ秒は必ずスレッドを休ませる
                Thread.Sleep(Math.Max(1, delay));
            }

            g?.Dispose();
            bmp?.Dispose();
        }

        private ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
                if (enc.MimeType == mimeType) return enc;
            return null;
        }
    }
}
