using SnippingToolOcrCore;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ZLogger;
using ConsoleAppFramework;

namespace SimpleOneOcr;

class Program
{
    static void Main(string[] args)
    {
        ConsoleApp.Run(args, (
            [Argument] string imagePath,
            bool saveResultImage = false,
            bool debug = false
        ) => Execute(imagePath, saveResultImage, debug));
    }
    
    static void Execute(string imagePath, bool saveResultImage, bool debug)
    {
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Information);
            logging.AddZLoggerConsole();
        });
        var logger = factory.CreateLogger("SimpleOneOcr");
        
        var ocrEngine = new Ocr(logger);
        
        var lines = ConvertToText(ocrEngine, imagePath);
        if (lines is null) return;
        
        ocrEngine.ResultWriteLines(lines);
        
        if (saveResultImage) SaveResultImage(imagePath, lines);
    }
    
    [SupportedOSPlatform("windows")]
    static Line[]? ConvertToText(Ocr ocrEngine, string imageFileName)
    {
        // Load the image
        var img = new Bitmap(imageFileName);
        if (img is null)
        {
            throw new Exception("Can't read image!");
        }

        // Convert the image format to BGRA
        try
        {
            using Bitmap imgRgba = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(imgRgba);
            g.DrawImage(img, 0, 0);

            int rows = imgRgba.Height;
            int cols = imgRgba.Width;
            int step = Image.GetPixelFormatSize(imgRgba.PixelFormat) / 8 * cols;

            // Get pixel data
            BitmapData bitmapData = imgRgba.LockBits(new Rectangle(0, 0, imgRgba.Width, imgRgba.Height), ImageLockMode.ReadOnly, imgRgba.PixelFormat);
            IntPtr dataPtr = bitmapData.Scan0;

            // Create an instance of the Img structure
            Img formattedImage = new Img
            {
                t = 3,
                col = cols,
                row = rows,
                _unk = 0,
                step = step,
                data_ptr = dataPtr
            };

            // Execute OCR processing
            var result = ocrEngine.RunOcr(formattedImage);

            imgRgba.UnlockBits(bitmapData);
            return result;
        }
        finally
        {
            img.Dispose();
        }
    }
    
    [SupportedOSPlatform("windows")]
    static void SaveResultImage(string imagePath, Line[] lines)
    {
        // Load the image
        using var img = new Bitmap(imagePath);
        // Convert the image format to BGRA (also handles cases where rotation information is included in JPEG images, etc.)
        using Bitmap imgRgba = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        using Graphics graphic = Graphics.FromImage(imgRgba);
        graphic.DrawImage(img, 0, 0);
        using var g = Graphics.FromImage(imgRgba);
        Pen pen = new Pen(Color.Red, 2);
        Font font = new Font("Arial", 30);

        foreach (var line in lines)
        {
            var points = new PointF[]
            {
                new PointF(line.X1, line.Y1),
                new PointF(line.X2, line.Y2),
                new PointF(line.X3, line.Y3),
                new PointF(line.X4, line.Y4)
            };
            g.DrawPolygon(pen, points);
            g.DrawString(line.Text, font, Brushes.Blue, points[0]);
        }

        pen.Dispose();
        font.Dispose();
        imgRgba.Save(imagePath + "_result.jpg", ImageFormat.Jpeg);
    }
}