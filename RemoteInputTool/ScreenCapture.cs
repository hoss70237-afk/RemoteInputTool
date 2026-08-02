using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        // Base64文字列ではなく、byte配列（バイナリ）を直接渡すように変更
        public event Action<byte[], double, double> OnFrameReady;
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
            _clientConnectedEvent.Set();
        }

        private void CaptureLoop()
        {
            Bitmap bmp = null;
            Graphics g = null;
            Bitmap scaledBmp = null;
            Graphics scaledG = null;
            int lastW = 0, lastH = 0;

            ImageCodecInfo jpegCodec = GetEncoderInfo("image/jpeg");

            while (_isRunning)
            {
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
                    scaledG?.Dispose();
                    scaledBmp?.Dispose();

                    bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    g = Graphics.FromImage(bmp);

                    // 最大 1280x720 になるように縮小率を計算（アスペクト比維持）
                    float scale = Math.Min(1280f / width, 720f / height);
                    if (scale > 1f) scale = 1f; // 拡大はしない

                    int scaledW = (int)(width * scale);
                    int scaledH = (int)(height * scale);

                    // 縮小用のビットマップとグラフィックス設定（高速性重視）
                    scaledBmp = new Bitmap(scaledW, scaledH, PixelFormat.Format24bppRgb);
                    scaledG = Graphics.FromImage(scaledBmp);
                    scaledG.InterpolationMode = InterpolationMode.Low; // 高速縮小
                    scaledG.CompositingQuality = CompositingQuality.HighSpeed;
                    scaledG.SmoothingMode = SmoothingMode.HighSpeed;

                    lastW = width;
                    lastH = height;
                }

                try
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                    
                    // 縮小描画
                    scaledG.DrawImage(bmp, 0, 0, scaledBmp.Width, scaledBmp.Height);
                    
                    GetCursorPos(out var pt);
                    double curX = (pt.X - x) / (double)width;
                    double curY = (pt.Y - y) / (double)height;

                    using (var ms = new MemoryStream())
                    {
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, MainWindow.AppConfig.Quality);
                        
                        // リサイズ後の画像をJPEG保存
                        scaledBmp.Save(ms, jpegCodec, encoderParams);
                        
                        // 生のバイト配列をそのまま渡す
                        OnFrameReady?.Invoke(ms.ToArray(), curX, curY);
                    }
                }
                catch { }

                sw.Stop();
                int targetDelay = 1000 / Math.Max(1, MainWindow.AppConfig.Fps);
                int delay = targetDelay - (int)sw.ElapsedMilliseconds;
                Thread.Sleep(Math.Max(1, delay));
            }

            g?.Dispose();
            bmp?.Dispose();
            scaledG?.Dispose();
            scaledBmp?.Dispose();
        }

        private ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
                if (enc.MimeType == mimeType) return enc;
            return null;
        }
    }
}
