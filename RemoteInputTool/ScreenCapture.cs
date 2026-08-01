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

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Task.Run(() => CaptureLoop());
        }

        public void Stop() => _isRunning = false;

        private void CaptureLoop()
        {
            while (_isRunning)
            {
                var rect = MainWindow.CurrentCaptureArea;
                int width = (int)Math.Max(1, rect.Width), height = (int)Math.Max(1, rect.Height);
                int x = (int)rect.X, y = (int)rect.Y;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                    
                    GetCursorPos(out var pt);
                    double curX = (pt.X - x) / (double)width;
                    double curY = (pt.Y - y) / (double)height;

                    using (var ms = new MemoryStream())
                    {
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, MainWindow.AppConfig.Quality);
                        var jpegCodec = GetEncoderInfo("image/jpeg");
                        bmp.Save(ms, jpegCodec, encoderParams);
                        
                        OnFrameReady?.Invoke(Convert.ToBase64String(ms.ToArray()), curX, curY);
                    }
                }
                Thread.Sleep(1000 / MainWindow.AppConfig.Fps);
            }
        }

        private ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
                if (enc.MimeType == mimeType) return enc;
            return null;
        }
    }
}
