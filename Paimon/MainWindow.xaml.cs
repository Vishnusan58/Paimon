using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Paimon
{
    public partial class MainWindow : Window
    {
        private const string GeminiModel = "models/gemini-2.5-flash-lite:generateContent"; // fast & multimodal
        private const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/";
        private const string GeminiApiKeyEnvVar = "GEMINI_API_KEY"; // environment variable name

        public MainWindow()
        {
            InitializeComponent();
        }

        // Try environment variable first, then secrets.json
        private static string? GetApiKey()
        {
            var fromEnv = Environment.GetEnvironmentVariable(GeminiApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            return GetApiKeyFromJson();
        }

        // Load from secrets.json next to the executable (not committed)
        private static string? GetApiKeyFromJson()
        {
            try
            {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.json");
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("GEMINI_API_KEY", out var prop))
                {
                    var key = prop.GetString();
                    return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
                }
                return null;
            }
            catch (Exception)
            {
                return null; // swallow errors; caller will handle missing key
            }
        }

        private void DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void ToggleTopMost(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
        }

        private void CloseApp(object sender, RoutedEventArgs e) => Close();

        private async void AskWithFullScreen(object sender, RoutedEventArgs e)
        {
            var bmp = CaptureScreen();                // full screen
            await AskGeminiAsync(bmp, PromptBox.Text);
        }

        private async void AskWithSelection(object sender, RoutedEventArgs e)
        {
            var rect = await SelectRectangleAsync();  // user draws a rectangle
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var bmp = CaptureScreen(rect);
            await AskGeminiAsync(bmp, PromptBox.Text);
        }

        // ---- SCREENSHOT ------------------------------------------------------

        private static Bitmap CaptureScreen()            // all screens (primary bounds)
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen; // fully qualified
            if (primary == null) throw new InvalidOperationException("No primary screen available.");
            return CaptureScreen(primary.Bounds);
        }

        private static Bitmap CaptureScreen(System.Drawing.Rectangle rect)
        {
            // If you see scaling issues on high-DPI monitors, mark process DPI-aware (Win32 SetProcessDPIAware). :contentReference[oaicite:1]{index=1}
            var bmp = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size);   // CopyFromScreen docs. :contentReference[oaicite:2]{index=2}
            return bmp;
        }

        private async Task<System.Drawing.Rectangle> SelectRectangleAsync()
        {
            // very lightweight full-screen overlay to draw a selection
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 0, 0, 0)),
                Topmost = true,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                ShowInTaskbar = false
            };

            var canvas = new System.Windows.Controls.Canvas();
            overlay.Content = canvas;
            overlay.Show();

            var rectShape = new System.Windows.Shapes.Rectangle { Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 2, Fill = System.Windows.Media.Brushes.Transparent };
            System.Windows.Point? start = null;

            overlay.MouseLeftButtonDown += (_, e) =>
            {
                start = e.GetPosition(canvas);
                if (!canvas.Children.Contains(rectShape)) canvas.Children.Add(rectShape);
            };

            overlay.MouseMove += (_, e) =>
            {
                if (start == null) return;
                var p = e.GetPosition(canvas);
                var x = Math.Min(p.X, start.Value.X);
                var y = Math.Min(p.Y, start.Value.Y);
                var w = Math.Abs(p.X - start.Value.X);
                var h = Math.Abs(p.Y - start.Value.Y);
                System.Windows.Controls.Canvas.SetLeft(rectShape, x);
                System.Windows.Controls.Canvas.SetTop(rectShape, y);
                rectShape.Width = w;
                rectShape.Height = h;
            };

            var tcs = new TaskCompletionSource<System.Drawing.Rectangle>();
            overlay.MouseLeftButtonUp += (_, e) =>
            {
                if (start == null) { tcs.TrySetResult(System.Drawing.Rectangle.Empty); overlay.Close(); return; }
                var end = e.GetPosition(canvas);
                var x = (int)Math.Min(end.X, start.Value.X) + (int)SystemParameters.VirtualScreenLeft;
                var y = (int)Math.Min(end.Y, start.Value.Y) + (int)SystemParameters.VirtualScreenTop;
                var w = (int)Math.Abs(end.X - start.Value.X);
                var h = (int)Math.Abs(end.Y - start.Value.Y);
                overlay.Close();
                tcs.TrySetResult(new System.Drawing.Rectangle(x, y, w, h));
            };

            return await tcs.Task;
        }

        // ---- GEMINI CALL -----------------------------------------------------

        private async Task AskGeminiAsync(Bitmap screenshot, string userPrompt)
        {
            var apiKey = GetApiKey();
            if (apiKey == null)
            {
                System.Windows.MessageBox.Show(
                    $"Gemini API key not found. Set env var '{GeminiApiKeyEnvVar}' or create a secrets.json with {{ \"GEMINI_API_KEY\": \"your-key\" }}.",
                    "Missing API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(userPrompt))
                userPrompt = "Explain this screenshot.";

            // convert bitmap to base64
            string base64;
            using (var ms = new MemoryStream())
            {
                screenshot.Save(ms, ImageFormat.Png);
                base64 = Convert.ToBase64String(ms.ToArray()); // convert image -> bytes -> base64. :contentReference[oaicite:3]{index=3}
            }

            // Build Gemini JSON payload: text + inline image (PNG).
            // Structure follows the Gemini image-understanding spec. :contentReference[oaicite:4]{index=4}
            var payload = new
            {
                contents = new[]
                {
                    new {
                        parts = new object[]
                        {
                            new { text = userPrompt },
                            new { inline_data = new { mime_type = "image/png", data = base64 } }
                        }
                    }
                }
            };

            using var http = new HttpClient { BaseAddress = new Uri(GeminiEndpoint) };
            var req = new HttpRequestMessage(HttpMethod.Post, $"{GeminiModel}?key={apiKey}")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            HttpResponseMessage res;
            try
            {
                res = await http.SendAsync(req);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Network error calling Gemini:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                System.Windows.MessageBox.Show($"Gemini error:\n{res.StatusCode}\n{json}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var answer = doc.RootElement
                               .GetProperty("candidates")[0]
                               .GetProperty("content")
                               .GetProperty("parts")[0]
                               .GetProperty("text")
                               .GetString();

                System.Windows.MessageBox.Show(answer ?? "(No text)", "Gemini");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to parse Gemini response:\n{ex.Message}\nRaw:\n{json}", "Parse Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
