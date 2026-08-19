using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace ExpressPackingMonitoring.UI
{
    public partial class ScanCameraPreviewWindow : System.Windows.Window
    {
        private WriteableBitmap _bitmap;
        private bool _closing;

        public ScanCameraPreviewWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 从扫描摄像头帧更新预览画面。调用方负责 Clone，本方法负责 Dispose。
        /// </summary>
        public void UpdateFrame(Mat frame)
        {
            if (_closing || frame == null || frame.IsDisposed || frame.Empty())
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_closing || !IsVisible) { frame.Dispose(); return; }

                    if (_bitmap == null
                        || _bitmap.PixelWidth != frame.Width
                        || _bitmap.PixelHeight != frame.Height)
                    {
                        _bitmap = new WriteableBitmap(
                            frame.Width, frame.Height, 96, 96,
                            System.Windows.Media.PixelFormats.Bgr24, null);
                        PreviewImage.Source = _bitmap;
                        StatusLabel.Visibility = Visibility.Collapsed;
                    }

                    int stride = checked((int)frame.Step());
                    int bufferSize = checked(stride * frame.Height);
                    _bitmap.WritePixels(
                        new Int32Rect(0, 0, frame.Width, frame.Height),
                        frame.Data, bufferSize, stride);
                }
                catch { }
                finally
                {
                    frame.Dispose();
                }
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _closing = true;
        }
    }
}
