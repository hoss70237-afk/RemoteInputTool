using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteInputTool
{
    public class ScreenCapture
    {
        public event Action<string> OnFrameReady;
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
            // Windows 7向けGDIフォールバック (Win8以降はSharpDXによるDXGI Desktop Duplication実装を追加推奨)
            var rect = MainWindow.AppConfig.CaptureArea;
            int width = (int)rect.Width, height = (int)rect.Height;
            int x = (int)rect.X, y = (int)rect.Y;

            while (_isRunning)
            {
                using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                    }
                    
                    using (var ms = new MemoryStream())
                    {
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, MainWindow.AppConfig.Quality);
                        var jpegCodec = GetEncoderInfo("image/jpeg");
                        bmp.Save(ms, jpegCodec, encoderParams);
                        
                        string base64 = Convert.ToBase64String(ms.ToArray());
                        OnFrameReady?.Invoke(base64);
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
