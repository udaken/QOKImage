using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace qoiconv;

internal class Program
{
    [STAThread]
    unsafe static void Main(string[] args)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var input = args[0];

        if (Path.GetExtension(input) != ".qoi")
        {
            var img = (Bitmap)Image.FromFile(input);
            var bitmapData = img.LockBits(new Rectangle(0, 0, img.Width, img.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, img.PixelFormat);

            var data = QOKImage.Encode(new ReadOnlySpan<byte>((void*)bitmapData.Scan0, bitmapData.Stride * bitmapData.Height),
                bitmapData.Width, bitmapData.Height, GetChannel(bitmapData.PixelFormat), out var len);
            img.UnlockBits(bitmapData);

            using var stream = File.OpenWrite(Path.ChangeExtension(input, ".qoi"));
            stream.Write(data, 0, len);
        }
        else
        {
            var bytes = File.ReadAllBytes(input);
            var pixels = QOKImage.Decode(bytes, out var width, out var height, 4);

            fixed (byte* dst = pixels)
            {
                var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, (IntPtr)dst);
                bitmap.Save(Path.ChangeExtension(input, ".bmp"), ImageFormat.Bmp);
            }
        }
        Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");

        static int GetChannel(PixelFormat pixelFormat) => pixelFormat switch
        {
            PixelFormat.Format32bppArgb => 4,
            PixelFormat.Format32bppRgb => 4,
            PixelFormat.Format32bppPArgb => 4,
            PixelFormat.Format24bppRgb => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };
    }
}
