using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.Controls
{
    public partial class MarketHeatmapControl : UserControl
    {
        private WriteableBitmap _bitmap;
        private byte[] _pixelBuffer;
        private int _currentColumn = 0;
        private const int BitmapWidth = 1000;
        private const int BitmapHeight = 500;

        public MarketHeatmapControl()
        {
            InitializeComponent();
            _bitmap = new WriteableBitmap(BitmapWidth, BitmapHeight, 96, 96, PixelFormats.Bgra32, null);
            _pixelBuffer = new byte[BitmapWidth * BitmapHeight * 4];
            HeatmapImage.Source = _bitmap;
        }

        public void ProcessSnapshot(OrderBookSnapshot snapshot)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RenderColumn(snapshot);
                _currentColumn = (_currentColumn + 1) % BitmapWidth;
            }));
        }

        private void RenderColumn(OrderBookSnapshot snapshot)
        {
            // Simple Linear Heatmap Rendering
            // Green for Bids, Red for Asks
            // Intensity based on Quantity

            _bitmap.Lock();

            // Clear column
            for (int y = 0; y < BitmapHeight; y++)
            {
                SetPixel(_currentColumn, y, 0, 0, 0, 255);
            }

            // Draw Bids (Bottom half)
            foreach (var bid in snapshot.Bids)
            {
                int y = MapPriceToY(bid.Price, true);
                if (y >= 0 && y < BitmapHeight)
                {
                    byte intensity = (byte)Math.Min(255, bid.Quantity * 10);
                    SetPixel(_currentColumn, y, 0, intensity, 0, 255);
                }
            }

            // Draw Asks (Top half)
            foreach (var ask in snapshot.Asks)
            {
                int y = MapPriceToY(ask.Price, false);
                if (y >= 0 && y < BitmapHeight)
                {
                    byte intensity = (byte)Math.Min(255, ask.Quantity * 10);
                    SetPixel(_currentColumn, y, intensity, 0, 0, 255);
                }
            }

            _bitmap.AddDirtyRect(new Int32Rect(_currentColumn, 0, 1, BitmapHeight));
            _bitmap.Unlock();
        }

        private void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
        {
            unsafe
            {
                byte* pBackBuffer = (byte*)_bitmap.BackBuffer;
                int offset = (y * _bitmap.BackBufferStride) + (x * 4);
                pBackBuffer[offset] = b;
                pBackBuffer[offset + 1] = g;
                pBackBuffer[offset + 2] = r;
                pBackBuffer[offset + 3] = a;
            }
        }

        private int MapPriceToY(decimal price, bool isBid)
        {
            // Simple mapping for demonstration
            // Center is mid-point
            return isBid ? BitmapHeight / 2 + 10 : BitmapHeight / 2 - 10;
        }
    }
}
