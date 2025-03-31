using SnippingToolOcrCore;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ZLogger;
using ConsoleAppFramework;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading;
using System.Drawing.Drawing2D;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;

namespace SimpleOneOcr;

class Program
{
    private static HttpListener listener;
    private static readonly int Port = 1277;
    private static ILogger globalLogger;
    private static bool serverRunning = false;
    private static int activeRequests = 0;
    private static readonly ConcurrentDictionary<string, int> requestStats = new ConcurrentDictionary<string, int>();

    [STAThread]
    static void Main(string[] args)
    {
        // Initialize logger
        var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddZLoggerConsole();
        });
        globalLogger = factory.CreateLogger("SimpleOneOcr");

        // Launch the GUI
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new OcrForm());
    }

    public static void StartWebServer(Action<string> statusUpdateCallback)
    {
        if (serverRunning) return;

        try
        {
            // 尝试使用更高的端口号，这些端口通常不需要管理员权限
            int[] portsToTry = { 12770, 12771, 12772, 12773, 12774 };
            bool bound = false;
            int selectedPort = Port;
            
            foreach (int port in portsToTry)
            {
                try
                {
                    listener = new HttpListener();
                    // 只绑定到本地回环地址
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    
                    // 不再尝试绑定到机器名/IP地址
                    
                    listener.Start();
                    bound = true;
                    selectedPort = port;
                    break;
                }
                catch (HttpListenerException ex)
                {
                    // 尝试下一个端口
                    globalLogger.ZLogWarning($"端口 {port} 绑定失败: {ex.Message}");
                    listener.Close();
                }
            }
            
            if (!bound)
            {
                throw new Exception("无法绑定到任何配置的端口。请尝试以管理员身份运行或使用不同的端口。");
            }
            
            serverRunning = true;
            
            statusUpdateCallback($"Web服务器已在本地回环地址上启动 端口 {selectedPort}");
            globalLogger.ZLogInformation($"Web服务器已在本地回环地址上启动 端口 {selectedPort}");

            Task.Run(async () => await HandleIncomingConnections(statusUpdateCallback));
        }
        catch (Exception ex)
        {
            statusUpdateCallback($"启动Web服务器失败: {ex.Message}");
            globalLogger.ZLogError($"启动Web服务器失败: {ex.Message}");
        }
    }

    public static void StopWebServer(Action<string> statusUpdateCallback)
    {
        if (!serverRunning) return;

        try
        {
            serverRunning = false;
            listener.Stop();
            listener.Close();
            statusUpdateCallback("服务器已停止");
            globalLogger.ZLogInformation($"Web server stopped");
        }
        catch (Exception ex)
        {
            statusUpdateCallback($"停止Web服务器时出错: {ex.Message}");
            globalLogger.ZLogError($"Error stopping web server: {ex.Message}");
        }
    }

    private static async Task HandleIncomingConnections(Action<string> statusUpdateCallback)
    {
        while (serverRunning)
        {
            try
            {
                HttpListenerContext context = await listener.GetContextAsync();
                
                // Increment active requests counter
                Interlocked.Increment(ref activeRequests);
                statusUpdateCallback($"当前请求数: {activeRequests}");
                
                // Process the request in a separate task
                _ = Task.Run(async () => 
                {
                    string requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    try
                    {
                        await ProcessRequest(context, requestId);
                    }
                    catch (Exception ex)
                    {
                        globalLogger.ZLogError($"Error processing request {requestId}: {ex.Message}");
                        try
                        {
                            // Send error response
                            byte[] errorBuffer = Encoding.UTF8.GetBytes($"{{\"error\": \"{ex.Message}\"}}");
                            context.Response.StatusCode = 500;
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = errorBuffer.Length;
                            await context.Response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                        }
                        catch { /* Ignore errors in error handling */ }
                    }
                    finally
                    {
                        try { context.Response.Close(); } catch { }
                        
                        // Decrement active requests counter
                        Interlocked.Decrement(ref activeRequests);
                        statusUpdateCallback($"当前请求数: {activeRequests}");
                    }
                });
            }
            catch (Exception ex)
            {
                if (serverRunning)
                {
                    globalLogger.ZLogError($"Error accepting connection: {ex.Message}");
                }
            }
        }
    }

    private static async Task ProcessRequest(HttpListenerContext context, string requestId)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        // Only handle POST requests
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405; // Method Not Allowed
            response.Close();
            return;
        }

        // Create a new OCR engine instance for this request
        using var ocrEngine = new Ocr(globalLogger);
        ocrEngine.CreatePipelineAndProcessOptions(1000);

        globalLogger.ZLogInformation($"[{requestId}] Processing new OCR request");
        
        // Track request in stats
        requestStats.AddOrUpdate("total", 1, (key, oldValue) => oldValue + 1);

        // Process the request based on content type
        string contentType = request.ContentType?.ToLower() ?? "";
        
        Bitmap image = null;
        try
        {
            if (contentType.StartsWith("multipart/form-data"))
            {
                // 处理文件上传
                try
                {
                    // 读取请求流
                    using var memoryStream = new MemoryStream();
                    await request.InputStream.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;
                    
                    // 使用HttpClient的MultipartFormDataContent来解析
                    if (request.ContentLength64 > 0)
                    {
                        // 查找边界
                        string boundary = "";
                        if (contentType.IndexOf("boundary=") != -1)
                        {
                            boundary = contentType.Substring(contentType.IndexOf("boundary=") + 9);
                            if (boundary.Contains(";"))
                                boundary = boundary.Substring(0, boundary.IndexOf(";"));
                        }
                        
                        // 读取整个请求体
                        byte[] buffer = memoryStream.ToArray();
                        string requestBody = Encoding.UTF8.GetString(buffer);
                        
                        // 查找图片数据部分
                        string fileHeader = $"Content-Disposition: form-data; name=\"image\"; filename=";
                        int fileHeaderIndex = requestBody.IndexOf(fileHeader);
                        
                        if (fileHeaderIndex != -1)
                        {
                            // 找到文件名结束位置
                            int filenameStart = requestBody.IndexOf("\"", fileHeaderIndex + fileHeader.Length) + 1;
                            int filenameEnd = requestBody.IndexOf("\"", filenameStart);
                            string filename = requestBody.Substring(filenameStart, filenameEnd - filenameStart);
                            
                            globalLogger.ZLogInformation($"[{requestId}] 找到文件: {filename}");
                            
                            // 找到内容类型
                            string contentTypeHeader = "Content-Type: ";
                            int contentTypeIndex = requestBody.IndexOf(contentTypeHeader, filenameEnd);
                            int contentTypeEnd = requestBody.IndexOf("\r\n", contentTypeIndex + contentTypeHeader.Length);
                            string fileContentType = requestBody.Substring(contentTypeIndex + contentTypeHeader.Length, 
                                                                         contentTypeEnd - (contentTypeIndex + contentTypeHeader.Length));
                            
                            // 找到文件数据开始位置
                            int fileDataStart = requestBody.IndexOf("\r\n\r\n", contentTypeEnd) + 4;
                            
                            // 找到文件数据结束位置
                            string boundaryEnd = $"--{boundary}--";
                            string boundaryNext = $"--{boundary}\r\n";
                            int fileDataEnd = requestBody.IndexOf(boundaryEnd, fileDataStart);
                            if (fileDataEnd == -1)
                                fileDataEnd = requestBody.IndexOf(boundaryNext, fileDataStart);
                            
                            if (fileDataEnd != -1)
                            {
                                // 提取文件数据
                                int fileDataLength = fileDataEnd - fileDataStart;
                                if (requestBody.Substring(fileDataEnd - 2, 2) == "\r\n")
                                {
                                    fileDataLength -= 2; // 减去结尾的\r\n
                                }
                                
                                // 直接从原始字节数组中提取文件数据
                                int byteStart = 0;
                                for (int i = 0; i < fileDataStart; i++)
                                {
                                    byteStart += Encoding.UTF8.GetByteCount(requestBody[i].ToString());
                                }
                                
                                // 确保不超出缓冲区范围
                                int byteLength = Math.Min(buffer.Length - byteStart, fileDataLength);
                                
                                byte[] fileData = new byte[byteLength];
                                Array.Copy(buffer, byteStart, fileData, 0, byteLength);
                                
                                globalLogger.ZLogInformation($"[{requestId}] 提取到文件数据: {fileData.Length} 字节, 开始位置: {byteStart}, 长度: {byteLength}");
                                
                                // 检查文件头以确认是否为有效图像
                                if (fileData.Length > 4)
                                {
                                    string format = "未知";
                                    if (fileData[0] == 0xFF && fileData[1] == 0xD8) format = "JPEG";
                                    else if (fileData[0] == 0x89 && fileData[1] == 0x50) format = "PNG";
                                    else if (fileData[0] == 0x47 && fileData[1] == 0x49) format = "GIF";
                                    else if (fileData[0] == 0x42 && fileData[1] == 0x4D) format = "BMP";
                                    
                                    globalLogger.ZLogInformation($"[{requestId}] 检测到的图像格式: {format}");
                                }
                                
                                try
                                {
                                    using (var ms = new MemoryStream(fileData))
                                    {
                                        // 添加更多调试信息
                                        globalLogger.ZLogInformation($"[{requestId}] 尝试加载图片数据: {fileData.Length} 字节");
                                        
                                        // 保存原始数据到临时文件以便调试
                                        string debugFilePath = Path.Combine(Path.GetTempPath(), $"debug_image_{requestId}.bin");
                                        File.WriteAllBytes(debugFilePath, fileData);
                                        globalLogger.ZLogInformation($"[{requestId}] 已保存原始数据到: {debugFilePath}");
                                        
                                        // 尝试使用Image.FromStream而不是直接创建Bitmap
                                        try
                                        {
                                            using (var tempImage = Image.FromStream(ms))
                                            {
                                                // 创建一个新的Bitmap作为副本
                                                image = new Bitmap(tempImage);
                                                globalLogger.ZLogInformation($"[{requestId}] 成功加载图片: {image.Width}x{image.Height}, 格式: {tempImage.RawFormat}");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            globalLogger.ZLogError($"[{requestId}] Image.FromStream失败: {ex.Message}");
                                            throw;
                                        }
                                    }
                                    
                                    requestStats.AddOrUpdate("file_uploads", 1, (key, oldValue) => oldValue + 1);
                                }
                                catch (Exception ex)
                                {
                                    globalLogger.ZLogError($"[{requestId}] 图片加载失败: {ex.Message}, 堆栈: {ex.StackTrace}");
                                    
                                    // 尝试检测图像格式
                                    string format = "未知";
                                    if (fileData.Length > 2)
                                    {
                                        if (fileData[0] == 0xFF && fileData[1] == 0xD8) format = "JPEG";
                                        else if (fileData[0] == 0x89 && fileData[1] == 0x50) format = "PNG";
                                        else if (fileData[0] == 0x47 && fileData[1] == 0x49) format = "GIF";
                                        else if (fileData[0] == 0x42 && fileData[1] == 0x4D) format = "BMP";
                                    }
                                    
                                    globalLogger.ZLogInformation($"[{requestId}] 检测到的图像格式: {format}");
                                    
                                    response.StatusCode = 400;
                                    byte[] errorBuffer = Encoding.UTF8.GetBytes($"{{\"error\": \"无法加载图片: {ex.Message}\", \"format\": \"{format}\"}}");
                                    response.ContentType = "application/json";
                                    response.ContentLength64 = errorBuffer.Length;
                                    await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                                    return;
                                }
                            }
                            else
                            {
                                globalLogger.ZLogWarning($"[{requestId}] 无法找到文件数据结束位置");
                                response.StatusCode = 400;
                                byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"无法解析上传的文件数据\"}");
                                response.ContentType = "application/json";
                                response.ContentLength64 = errorBuffer.Length;
                                await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                                return;
                            }
                        }
                        else
                        {
                            globalLogger.ZLogWarning($"[{requestId}] 没有找到图片文件字段");
                            response.StatusCode = 400;
                            byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"没有找到上传的图片文件\"}");
                            response.ContentType = "application/json";
                            response.ContentLength64 = errorBuffer.Length;
                            await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                            return;
                        }
                    }
                    else
                    {
                        globalLogger.ZLogWarning($"[{requestId}] 请求内容为空");
                        response.StatusCode = 400;
                        byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"请求内容为空\"}");
                        response.ContentType = "application/json";
                        response.ContentLength64 = errorBuffer.Length;
                        await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    globalLogger.ZLogError($"[{requestId}] 处理表单数据失败: {ex.Message}");
                    response.StatusCode = 400;
                    byte[] errorBuffer = Encoding.UTF8.GetBytes($"{{\"error\": \"处理表单数据失败: {ex.Message}\"}}");
                    response.ContentType = "application/json";
                    response.ContentLength64 = errorBuffer.Length;
                    await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                    return;
                }
            }
            else
            {
                // Handle base64 image
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    string requestBody = await reader.ReadToEndAsync();
                    
                    // Try to parse as JSON
                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(requestBody);
                        string base64Image = null;
                        
                        if (jsonDoc.RootElement.TryGetProperty("image", out var imageElement) && 
                            imageElement.ValueKind == JsonValueKind.String)
                        {
                            base64Image = imageElement.GetString();
                        }
                        
                        if (string.IsNullOrEmpty(base64Image))
                        {
                            response.StatusCode = 400;
                            byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"Missing 'image' field with base64 data\"}");
                            response.ContentType = "application/json";
                            response.ContentLength64 = errorBuffer.Length;
                            await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                            return;
                        }
                        
                        // Remove data URL prefix if present
                        if (base64Image.StartsWith("data:image"))
                        {
                            base64Image = base64Image.Substring(base64Image.IndexOf(',') + 1);
                        }
                        
                        byte[] imageBytes = Convert.FromBase64String(base64Image);
                        using (var ms = new MemoryStream(imageBytes))
                        {
                            image = new Bitmap(ms);
                        }
                        
                        requestStats.AddOrUpdate("base64_uploads", 1, (key, oldValue) => oldValue + 1);
                    }
                    catch (JsonException)
                    {
                        response.StatusCode = 400;
                        byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"Invalid JSON format\"}");
                        response.ContentType = "application/json";
                        response.ContentLength64 = errorBuffer.Length;
                        await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                        return;
                    }
                }
            }

            // Process the image with OCR
            if (image != null)
            {
                // Save to temp file for processing
                string tempImagePath = Path.Combine(Path.GetTempPath(), $"ocr_{requestId}.jpg");
                image.Save(tempImagePath, ImageFormat.Jpeg);
                
                try
                {
                    var lines = ConvertToText(ocrEngine, tempImagePath);
                    
                    if (lines != null)
                    {
                        // Prepare the response
                        var responseObj = new
                        {
                            text = string.Join("\n", lines.Select(l => l.Text)),
                            json = lines
                        };
                        
                        var serializerContext = new SourceGenerationContext(new JsonSerializerOptions
                        {
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            WriteIndented = true
                        });
                        
                        string jsonResponse = JsonSerializer.Serialize(responseObj, 
                            new JsonSerializerOptions { 
                                WriteIndented = true,
                                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                            });
                        
                        byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
                        response.ContentType = "application/json";
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        
                        requestStats.AddOrUpdate("successful", 1, (key, oldValue) => oldValue + 1);
                        globalLogger.ZLogInformation($"[{requestId}] OCR completed successfully");
                    }
                    else
                    {
                        response.StatusCode = 500;
                        byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"OCR processing failed\"}");
                        response.ContentType = "application/json";
                        response.ContentLength64 = errorBuffer.Length;
                        await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                        
                        requestStats.AddOrUpdate("failed", 1, (key, oldValue) => oldValue + 1);
                        globalLogger.ZLogError($"[{requestId}] OCR processing failed");
                    }
                }
                finally
                {
                    // Clean up temp file
                    try
                    {
                        if (File.Exists(tempImagePath))
                        {
                            File.Delete(tempImagePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        globalLogger.ZLogWarning($"Failed to delete temp file: {tempImagePath} - {ex.Message}");
                    }
                }
            }
            else
            {
                response.StatusCode = 400;
                byte[] errorBuffer = Encoding.UTF8.GetBytes("{\"error\": \"Could not process image\"}");
                response.ContentType = "application/json";
                response.ContentLength64 = errorBuffer.Length;
                await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
            }
        }
        finally
        {
            image?.Dispose();
        }
    }

    // Get server statistics
    public static Dictionary<string, int> GetServerStats()
    {
        return requestStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    [SupportedOSPlatform("windows")]
    public static Line[]? ConvertToText(Ocr ocrEngine, string imageFileName)
    {
        // Load the image
        var img = ResizeImage(new Bitmap(imageFileName));

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

    private static Bitmap ResizeImage(Bitmap image)
    {
        if (image is { Width: >= 50, Height: >= 50 }) return image;
        var ratio = Math.Max(50.0 / image.Width, 50.0 / image.Height);

        var newWidth = Math.Max((int)Math.Ceiling(image.Width * ratio), 50);
        var newHeight = Math.Max((int)Math.Ceiling(image.Height * ratio), 50);

        var newImage = new Bitmap(newWidth, newHeight);
        using var graphics = Graphics.FromImage(newImage);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

        graphics.DrawImage(image, 0, 0, newWidth, newHeight);
        return newImage;
    }

    [SupportedOSPlatform("windows")]
    public static void SaveResultImage(string imagePath, Line[] lines)
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

    private static string NaturalSortKey(string input)
    {
        return Regex.Replace(input, @"\d+", match => match.Value.PadLeft(10, '0'));
    }

    private static void ResultWriteLines(Line[]? lines, bool debug = false, ILogger? logger = null)
    {
        if (lines == null) return;

        for (var i = 0; i < lines.Length; i++)
        {
            if (debug)
            {
                logger?.ZLogInformation($"{i}: {lines[i]}");
            }
            else
            {
                logger?.ZLogInformation($"{lines[i].Text}");
            }
        }

        // Output in JSON format
        var context = new SourceGenerationContext(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var json = JsonSerializer.Serialize(lines, context.LineArray);
        logger?.ZLogDebug($"{json}");
    }

    public static void SaveJsonResult(string imagePath, Line[] lines, ILogger logger)
    {
        try
        {
            string jsonFilePath = Path.ChangeExtension(imagePath, ".json");
            var context = new SourceGenerationContext(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            });
            var json = JsonSerializer.Serialize(lines, context.LineArray);
            File.WriteAllText(jsonFilePath, json);
            logger.ZLogInformation($"JSON result saved to {jsonFilePath}");
        }
        catch (Exception ex)
        {
            logger.ZLogError($"Failed to save JSON result for {imagePath}: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Line[]))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}

// Add the GUI form class
public class OcrForm : Form
{
    private Panel dropPanel;
    private PictureBox originalImageBox;
    private PictureBox resultImageBox;
    private TabControl resultTabControl;
    private TabPage textTabPage;
    private TabPage jsonTabPage;
    private TabPage imagesTabPage;
    private RichTextBox textResultBox;
    private RichTextBox jsonResultBox;
    private Button browseButton;
    private Button saveImageButton;
    private Button saveJsonButton;
    private Label statusLabel;
    private Label copyrightLabel;
    private Ocr ocrEngine;
    private ILogger logger;
    private string currentImagePath;
    private Line[] currentResults;
    private Bitmap resultImage;
    private Button startServerButton;
    private Label serverStatusLabel;
    private Label serverStatsLabel;
    private System.Windows.Forms.Timer statsUpdateTimer;
    private int currentServerPort = 0;

    public OcrForm()
    {
        InitializeComponents();
        InitializeOcr();
        
        // 自动启动web服务器
        ToggleServerButton_Click(this, EventArgs.Empty);
        
        // 启动统计更新定时器
        statsUpdateTimer = new System.Windows.Forms.Timer();
        statsUpdateTimer.Interval = 2000; // 每2秒更新一次
        statsUpdateTimer.Tick += StatsUpdateTimer_Tick;
        statsUpdateTimer.Start();
    }

    private void InitializeOcr()
    {
        var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddZLoggerConsole();
        });
        logger = factory.CreateLogger("SimpleOneOcr");
        ocrEngine = new Ocr(logger);
        ocrEngine.CreatePipelineAndProcessOptions(1000);
    }

    private void InitializeComponents()
    {
        // Form settings
        this.Text = "微软AI OCR by xxssxx";
        this.Size = new Size(900, 700);
        this.MinimumSize = new Size(800, 600);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Icon = SystemIcons.Application;

        // 创建主布局面板
        Panel mainPanel = new Panel
        {
            Dock = DockStyle.Fill
        };
        this.Controls.Add(mainPanel);

        // 版权标签
        copyrightLabel = new Label
        {
            Text = "© 52pojie xxssxx",
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Bottom,
            Height = 30,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.Gray
        };
        mainPanel.Controls.Add(copyrightLabel);

        // 结果标签页控件
        resultTabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 0, 10, 10)
        };
        mainPanel.Controls.Add(resultTabControl);

        // 拖放面板
        dropPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 200,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(240, 240, 240),
            AllowDrop = true,
            Margin = new Padding(10, 10, 10, 0)
        };
        mainPanel.Controls.Add(dropPanel);

        // 拖放提示标签
        Label dropLabel = new Label
        {
            Text = "拖放图片到这里或点击浏览按钮选择图片",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 14, FontStyle.Regular)
        };
        dropPanel.Controls.Add(dropLabel);

        // 按钮面板
        FlowLayoutPanel buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(5)
        };
        dropPanel.Controls.Add(buttonPanel);

        // 浏览按钮
        browseButton = new Button
        {
            Text = "浏览...",
            Width = 100,
            Height = 30,
            Margin = new Padding(5),
            FlatStyle = FlatStyle.Flat
        };
        buttonPanel.Controls.Add(browseButton);

        // 保存标注图片按钮
        saveImageButton = new Button
        {
            Text = "保存标注图片",
            Width = 120,
            Height = 30,
            Margin = new Padding(5),
            Enabled = false,
            FlatStyle = FlatStyle.Flat
        };
        buttonPanel.Controls.Add(saveImageButton);

        // 保存JSON按钮
        saveJsonButton = new Button
        {
            Text = "保存JSON",
            Width = 100,
            Height = 30,
            Margin = new Padding(5),
            Enabled = false,
            FlatStyle = FlatStyle.Flat
        };
        buttonPanel.Controls.Add(saveJsonButton);

        // 状态标签
        statusLabel = new Label
        {
            Text = "就绪",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            Margin = new Padding(10, 8, 0, 0)
        };
        buttonPanel.Controls.Add(statusLabel);

        // 图片结果标签页
        imagesTabPage = new TabPage("图片结果");

        // 图片显示面板
        Panel imagePanel = new Panel
        {
            Dock = DockStyle.Fill
        };
        imagesTabPage.Controls.Add(imagePanel);

        // 原始图片面板
        Panel originalPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = imagePanel.Width / 2,
            BorderStyle = BorderStyle.FixedSingle
        };
        imagePanel.Controls.Add(originalPanel);

        // 原始图片标签
        Label originalLabel = new Label
        {
            Text = "原始图片",
            Dock = DockStyle.Top,
            Height = 25,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            BackColor = Color.LightGray
        };
        originalPanel.Controls.Add(originalLabel);

        // 原始图片显示框
        originalImageBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White
        };
        originalPanel.Controls.Add(originalImageBox);

        // 结果图片面板
        Panel resultPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle
        };
        imagePanel.Controls.Add(resultPanel);

        // 结果图片标签
        Label resultLabel = new Label
        {
            Text = "识别结果",
            Dock = DockStyle.Top,
            Height = 25,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            BackColor = Color.LightGray
        };
        resultPanel.Controls.Add(resultLabel);

        // 结果图片显示框
        resultImageBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White
        };
        resultPanel.Controls.Add(resultImageBox);

        // 文本结果标签页
        textTabPage = new TabPage("文本结果");

        // 文本结果显示框
        textResultBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 12),
            BackColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        textTabPage.Controls.Add(textResultBox);

        // JSON结果标签页
        jsonTabPage = new TabPage("JSON结果");

        // JSON结果显示框
        jsonResultBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 12),
            BackColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        jsonTabPage.Controls.Add(jsonResultBox);

        // 添加标签页到TabControl
        resultTabControl.TabPages.Add(imagesTabPage);
        resultTabControl.TabPages.Add(textTabPage);
        resultTabControl.TabPages.Add(jsonTabPage);

        // 注册事件处理程序
        dropPanel.DragEnter += DropPanel_DragEnter;
        dropPanel.DragDrop += DropPanel_DragDrop;
        browseButton.Click += BrowseButton_Click;
        saveImageButton.Click += SaveImageButton_Click;
        saveJsonButton.Click += SaveJsonButton_Click;
        this.FormClosing += OcrForm_FormClosing;

        // 调整控件大小
        this.Resize += (sender, e) => {
            originalPanel.Width = imagePanel.Width / 2;
        };

        // 服务器控制面板
        Panel serverControlPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(10),
            Margin = new Padding(10),
            BackColor = Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.FixedSingle
        };
        mainPanel.Controls.Add(serverControlPanel);

        // 服务器状态标签
        serverStatusLabel = new Label
        {
            Text = "服务器状态: 未启动",
            AutoSize = false,
            Width = 400,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            ForeColor = Color.DarkSlateGray,
            Padding = new Padding(5, 0, 0, 0)
        };
        serverControlPanel.Controls.Add(serverStatusLabel);

        // 服务器统计标签
        serverStatsLabel = new Label
        {
            Text = "统计: N/A",
            AutoSize = false,
            Width = 400,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.Gray,
            Padding = new Padding(5, 0, 0, 0),
            Location = new Point(0, 24)
        };
        serverControlPanel.Controls.Add(serverStatsLabel);

        // 服务器按钮面板
        FlowLayoutPanel serverButtonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Location = new Point(serverControlPanel.Width - 130, 10),
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        serverControlPanel.Controls.Add(serverButtonPanel);

        // 合并的服务器控制按钮
        startServerButton = new Button
        {
            Text = "启动服务器",
            Width = 110,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        startServerButton.FlatAppearance.BorderSize = 0;
        serverButtonPanel.Controls.Add(startServerButton);

        // 注册事件处理程序
        startServerButton.Click += ToggleServerButton_Click;
    }

    private void DropPanel_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void DropPanel_DragDrop(object sender, DragEventArgs e)
    {
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length > 0)
        {
            ProcessImage(files[0]);
        }
    }

    private void BrowseButton_Click(object sender, EventArgs e)
    {
        using (OpenFileDialog openFileDialog = new OpenFileDialog())
        {
            openFileDialog.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*";
            openFileDialog.Title = "选择图片";

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                ProcessImage(openFileDialog.FileName);
            }
        }
    }

    private void SaveImageButton_Click(object sender, EventArgs e)
    {
        if (currentResults != null && !string.IsNullOrEmpty(currentImagePath) && resultImage != null)
        {
            try
            {
                string resultPath = currentImagePath + "_result.jpg";
                resultImage.Save(resultPath, ImageFormat.Jpeg);
                statusLabel.Text = $"已保存标注图片: {resultPath}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存标注图片失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void SaveJsonButton_Click(object sender, EventArgs e)
    {
        if (currentResults != null && !string.IsNullOrEmpty(currentImagePath))
        {
            try
            {
                Program.SaveJsonResult(currentImagePath, currentResults, logger);
                statusLabel.Text = $"已保存JSON: {Path.ChangeExtension(currentImagePath, ".json")}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存JSON失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void OcrForm_FormClosing(object sender, FormClosingEventArgs e)
    {
        // Stop the web server when closing
        Program.StopWebServer(_ => { });
        
        // Stop the timer
        statsUpdateTimer.Stop();
        statsUpdateTimer.Dispose();
        
        ocrEngine?.Dispose();

        if (originalImageBox.Image != null)
        {
            originalImageBox.Image.Dispose();
        }

        if (resultImage != null)
        {
            resultImage.Dispose();
        }
    }

    private void ProcessImage(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            MessageBox.Show("文件不存在", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            statusLabel.Text = "正在处理图片...";
            currentImagePath = imagePath;

            try
            {
                if (originalImageBox.Image != null)
                {
                    originalImageBox.Image.Dispose();
                }
                originalImageBox.Image = new Bitmap(imagePath);
            }
            catch (Exception ex)
            {
                logger.ZLogError($"加载原始图片失败: {ex.Message}");
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    var results = Program.ConvertToText(ocrEngine, imagePath);

                    this.Invoke(new Action(() =>
                    {
                        if (results != null)
                        {
                            currentResults = results;

                            textResultBox.Clear();
                            foreach (var line in results)
                            {
                                textResultBox.AppendText(line.Text + Environment.NewLine);
                            }

                            var context = new SourceGenerationContext(new JsonSerializerOptions
                            {
                                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                WriteIndented = true
                            });
                            var json = JsonSerializer.Serialize(results, context.LineArray);
                            jsonResultBox.Text = json;

                            GenerateAndShowResultImage(imagePath, results);

                            saveImageButton.Enabled = true;
                            saveJsonButton.Enabled = true;

                            statusLabel.Text = "处理完成";
                        }
                        else
                        {
                            statusLabel.Text = "处理失败";
                        }
                    }));
                }
                catch (Exception ex)
                {
                    this.Invoke(new Action(() =>
                    {
                        MessageBox.Show($"处理图片失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        statusLabel.Text = "处理失败";
                    }));
                }
            });

            thread.IsBackground = true;
            thread.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"处理图片失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            statusLabel.Text = "处理失败";
        }
    }

    private void GenerateAndShowResultImage(string imagePath, Line[] lines)
    {
        try
        {
            if (resultImage != null)
            {
                resultImage.Dispose();
            }

            if (resultImageBox.Image != null)
            {
                resultImageBox.Image.Dispose();
                resultImageBox.Image = null;
            }

            using var img = new Bitmap(imagePath);
            resultImage = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resultImage))
            {
                g.DrawImage(img, 0, 0);

                using Pen pen = new Pen(Color.Red, 2);
                float fontSize = Math.Min(12, Math.Max(8, img.Width / 50));
                using Font font = new Font("Arial", fontSize, FontStyle.Bold);
                using SolidBrush textBrush = new SolidBrush(Color.Blue);
                using SolidBrush bgBrush = new SolidBrush(Color.FromArgb(180, Color.White));

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

                    SizeF textSize = g.MeasureString(line.Text, font);

                    g.FillRectangle(bgBrush, points[0].X, points[0].Y, textSize.Width, textSize.Height);

                    g.DrawString(line.Text, font, textBrush, points[0]);
                }
            }

            resultImageBox.Image = resultImage;
        }
        catch (Exception ex)
        {

            MessageBox.Show($"生成结果图片失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleServerButton_Click(object sender, EventArgs e)
    {
        if (startServerButton.Text == "启动服务器")
        {
            Program.StartWebServer(UpdateServerStatus);
        }
        else
        {
            Program.StopWebServer(UpdateServerStatus);
        }
    }

    private void UpdateServerStatus(string status)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<string>(UpdateServerStatus), status);
            return;
        }

        // 提取端口号（如果存在）
        int port = 0;
        if (status.Contains("端口"))
        {
            var match = Regex.Match(status, @"端口\s+(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out port))
            {
                currentServerPort = port;
            }
        }

        // 根据状态设置不同的颜色和按钮文本
        if (status.Contains("启动"))
        {
            serverStatusLabel.Text = $"服务器状态: 运行中 (端口: {currentServerPort})";
            serverStatusLabel.ForeColor = Color.Green;
            startServerButton.Text = "停止服务器";
            startServerButton.BackColor = Color.FromArgb(232, 17, 35); // 红色
        }
        else if (status.Contains("停止") || status.Contains("失败"))
        {
            serverStatusLabel.Text = "服务器状态: 已停止";
            serverStatusLabel.ForeColor = Color.Red;
            startServerButton.Text = "启动服务器";
            startServerButton.BackColor = Color.FromArgb(0, 120, 212); // 蓝色
            currentServerPort = 0;
        }
        else
        {
            serverStatusLabel.Text = $"服务器状态: {status}";
        }
    }

    private void UpdateServerStats()
    {
        if (InvokeRequired)
        {
            Invoke(new Action(UpdateServerStats));
            return;
        }

        var stats = Program.GetServerStats();
        if (stats.Count > 0)
        {
            int total = stats.ContainsKey("total") ? stats["total"] : 0;
            int successful = stats.ContainsKey("successful") ? stats["successful"] : 0;
            int failed = stats.ContainsKey("failed") ? stats["failed"] : 0;
            
            serverStatsLabel.Text = $"统计: 总请求 {total} | 成功 {successful} | 失败 {failed}";
        }
        else
        {
            serverStatsLabel.Text = "统计: N/A";
        }
    }

    private void StatsUpdateTimer_Tick(object sender, EventArgs e)
    {
        UpdateServerStats();
    }
}

// Helper class to parse multipart form data
public class MultipartFormDataParser
{
    public List<MultipartFile> Files { get; } = new List<MultipartFile>();

    public MultipartFormDataParser(HttpListenerRequest request)
    {
        if (!request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Not a multipart/form-data request");
        }

        string boundary = GetBoundary(request.ContentType);
        if (string.IsNullOrEmpty(boundary))
        {
            throw new ArgumentException("No boundary found in multipart/form-data");
        }

        byte[] boundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
        byte[] endBoundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");

        using (var stream = request.InputStream)
        {
            byte[] buffer = new byte[16384]; // 16KB buffer
            int bytesRead;
            bool isFile = false;
            string fileName = null;
            MemoryStream fileData = null;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                string content = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                // Look for Content-Disposition header
                int contentDispositionIndex = content.IndexOf("Content-Disposition: form-data;");
                if (contentDispositionIndex >= 0)
                {
                    int fileNameIndex = content.IndexOf("filename=\"", contentDispositionIndex);
                    if (fileNameIndex >= 0)
                    {
                        isFile = true;
                        int fileNameStart = fileNameIndex + 10;
                        int fileNameEnd = content.IndexOf("\"", fileNameStart);
                        fileName = content.Substring(fileNameStart, fileNameEnd - fileNameStart);

                        // Find the start of file data (after the double CRLF)
                        int dataStart = content.IndexOf("\r\n\r\n", fileNameEnd) + 4;
                        
                        // Create a memory stream for the file data
                        fileData = new MemoryStream();
                        
                        // Write the initial data
                        byte[] initialData = Encoding.ASCII.GetBytes(content.Substring(dataStart));
                        fileData.Write(initialData, 0, initialData.Length);
                        
                        // Continue reading the file data
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // Check if we've reached the boundary
                            if (ContainsBoundary(buffer, bytesRead, boundaryBytes) || 
                                ContainsBoundary(buffer, bytesRead, endBoundaryBytes))
                            {
                                // Find the boundary position
                                int boundaryPos = FindBoundaryPosition(buffer, bytesRead, boundaryBytes, endBoundaryBytes);
                                if (boundaryPos > 0)
                                {
                                    // Write the data before the boundary
                                    fileData.Write(buffer, 0, boundaryPos - 2); // -2 to exclude the CRLF before boundary
                                }
                                
                                // Add the file to the list
                                fileData.Position = 0;
                                Files.Add(new MultipartFile { FileName = fileName, Data = fileData });
                                
                                // Reset for the next part
                                isFile = false;
                                fileName = null;
                                fileData = null;
                                break;
                            }
                            else
                            {
                                // Write the entire buffer
                                fileData.Write(buffer, 0, bytesRead);
                            }
                        }
                    }
                }
            }
        }
    }

    private string GetBoundary(string contentType)
    {
        int index = contentType.IndexOf("boundary=");
        if (index < 0) return null;
        
        string boundary = contentType.Substring(index + 9); // 9 = "boundary=".Length
        
        // Remove quotes if present
        if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
        {
            boundary = boundary.Substring(1, boundary.Length - 2);
        }
        
        return boundary;
    }

    private bool ContainsBoundary(byte[] buffer, int length, byte[] boundaryBytes)
    {
        if (length < boundaryBytes.Length) return false;
        
        for (int i = 0; i <= length - boundaryBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < boundaryBytes.Length; j++)
            {
                if (buffer[i + j] != boundaryBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        
        return false;
    }

    private int FindBoundaryPosition(byte[] buffer, int length, byte[] boundaryBytes, byte[] endBoundaryBytes)
    {
        // Check for regular boundary
        for (int i = 0; i <= length - boundaryBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < boundaryBytes.Length; j++)
            {
                if (buffer[i + j] != boundaryBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        
        // Check for end boundary
        for (int i = 0; i <= length - endBoundaryBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < endBoundaryBytes.Length; j++)
            {
                if (buffer[i + j] != endBoundaryBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        
        return -1;
    }

    public class MultipartFile
    {
        public string FileName { get; set; }
        public MemoryStream Data { get; set; }
    }
}
