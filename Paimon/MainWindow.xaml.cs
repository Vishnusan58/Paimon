using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls; // Added for TextBlock
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using System.Threading; // for CancellationTokenSource
using System.Speech.Synthesis; // TTS

namespace Paimon
{
    public partial class MainWindow : Window
    {
        private const string GeminiApiKeyEnvVar = "GEMINI_API_KEY";
        private const string SystemInstruction = "You are Paimon, the small, floating, and energetic companion from the world of Teyvat in Genshin Impact. Your primary goal is to be the user's loyal and talkative companion, guide, and best friend. The user is your 'Traveler.' You must embody Paimon's personality and speech patterns perfectly in every response.";

        private readonly ObservableCollection<ChatMessage> _messages = new();
        private bool _isBusy;
        
        // Semantic Kernel components
        private Kernel? _kernel;
        private IChatCompletionService? _chatService;
        private ChatHistory? _chatHistory;

        private readonly SpeechSynthesizer _synth = new();
        private bool _voiceEnabled = false;
        private CancellationTokenSource? _speechCts;

        public MainWindow()
        {
            InitializeComponent();
            ChatList.ItemsSource = _messages;
            InitializeSemanticKernel();
            AddMessage("System", "Paimon is ready! Traveler, show Paimon something or ask a question.");
        }

        private void InitializeSemanticKernel()
        {
            try
            {
                var apiKey = GetApiKey();
                if (apiKey == null)
                {
                    AddMessage("Error", $"Gemini API key not found. Set env var '{GeminiApiKeyEnvVar}' or create secrets.json.", true);
                    return;
                }

                // Build Semantic Kernel with Google Gemini
                var builder = Kernel.CreateBuilder();
                builder.AddGoogleAIGeminiChatCompletion(
                    modelId: "gemini-2.0-flash-exp",
                    apiKey: apiKey
                );
                
                _kernel = builder.Build();
                _chatService = _kernel.GetRequiredService<IChatCompletionService>();
                
                // Initialize chat history with system instruction
                _chatHistory = new ChatHistory(SystemInstruction);
                
                AddMessage("System", "Semantic Kernel initialized successfully!");
            }
            catch (Exception ex)
            {
                AddMessage("Error", $"Failed to initialize Semantic Kernel: {ex.Message}", true);
            }
        }

        private static string? GetApiKey()
        {
            var fromEnv = Environment.GetEnvironmentVariable(GeminiApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            return GetApiKeyFromJson();
        }

        private static string? GetApiKeyFromJson()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.json");
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
                return null;
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

        private void AddMessage(string role, string content, bool isError = false)
        {
            var msg = ChatMessage.Create(role, content, isError);
            _messages.Add(msg);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ChatScroll.ScrollToEnd();
            }));
        }

        private void UpdateLastMessage(string content)
        {
            if (_messages.Count > 0)
            {
                var last = _messages[_messages.Count - 1];
                _messages[_messages.Count - 1] = ChatMessage.Create(last.Role, content, last.IsError);
            }
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
        }

        private async void AskWithFullScreen(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var dialog = new AdditionalPromptWindow { Owner = this };
            if (dialog.ShowDialog() != true) return; // cancelled
            var userPrompt = dialog.ExtraText?.Trim();
            if (!string.IsNullOrEmpty(userPrompt))
                AddMessage("Traveler", userPrompt);
            else
                userPrompt = "Explain this screenshot.";
            try
            {
                SetBusy(true);
                var bmp = CaptureScreen();
                await AskGeminiAsync(bmp, userPrompt);
            }
            finally { SetBusy(false); }
        }

        private async void AskWithSelection(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var rect = await SelectRectangleAsync();
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var bmp = CaptureScreen(rect);
            var dialog = new AdditionalPromptWindow { Owner = this };
            if (dialog.ShowDialog() != true) return; // cancelled
            var userPrompt = dialog.ExtraText?.Trim();
            if (!string.IsNullOrEmpty(userPrompt))
                AddMessage("Traveler", userPrompt);
            else
                userPrompt = "Explain this screenshot.";
            try
            {
                SetBusy(true);
                await AskGeminiAsync(bmp, userPrompt, null);
            }
            finally { SetBusy(false); }
        }

        private static Bitmap CaptureScreen()
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary == null) throw new InvalidOperationException("No primary screen available.");
            return CaptureScreen(primary.Bounds);
        }

        private static Bitmap CaptureScreen(System.Drawing.Rectangle rect)
        {
            var bmp = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size);
            return bmp;
        }

        private async Task<System.Drawing.Rectangle> SelectRectangleAsync()
        {
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

        private async Task AskGeminiAsync(Bitmap screenshot, string userPrompt, string? extraInstructions = null)
        {
            if (_chatService == null || _chatHistory == null)
            {
                AddMessage("Error", "Semantic Kernel not initialized. Check your API key.", true);
                return;
            }

            if (string.IsNullOrWhiteSpace(userPrompt))
                userPrompt = "Explain this screenshot.";

            var combinedPrompt = userPrompt;
            if (!string.IsNullOrWhiteSpace(extraInstructions))
            {
                combinedPrompt += "\n\nExtra traveler instructions:\n" + extraInstructions.Trim();
            }

            // OPTIONAL: Downscale very large screenshots to reduce payload (max dimension ~1600)
            screenshot = MaybeDownscaleForGemini(screenshot);

            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                screenshot.Save(ms, ImageFormat.Png);
                imageBytes = ms.ToArray();
            }

            // Guard extremely large images (> 6MB after PNG). Gemini has limits; we warn user.
            if (imageBytes.Length > 6 * 1024 * 1024)
            {
                AddMessage("Error", $"Screenshot is too large after compression ({imageBytes.Length / 1024 / 1024:F2} MB). Try selecting a smaller area.", true);
                return;
            }

            var base64 = Convert.ToBase64String(imageBytes);
            var dataUri = "data:image/png;base64," + base64; // REQUIRED format for ImageContent(string)

            ChatMessageContentItemCollection messageContent = new()
            {
                new TextContent(combinedPrompt),
                new ImageContent(dataUri)
            };

            _chatHistory.AddUserMessage(messageContent);

            try
            {
                AddMessage("Paimon", "");
                var lastMessageIndex = _messages.Count - 1;
                var responseBuilder = new StringBuilder();

                await foreach (var chunk in _chatService.GetStreamingChatMessageContentsAsync(_chatHistory))
                {
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        responseBuilder.Append(chunk.Content);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (lastMessageIndex < _messages.Count)
                            {
                                var current = _messages[lastMessageIndex];
                                _messages[lastMessageIndex] = ChatMessage.Create(current.Role, responseBuilder.ToString(), current.IsError);
                            }
                        }, DispatcherPriority.Background);
                    }
                }

                var finalResponse = responseBuilder.ToString();
                if (!string.IsNullOrEmpty(finalResponse))
                {
                    _chatHistory.AddAssistantMessage(finalResponse);
                    _ = SpeakAsyncIfEnabled(finalResponse); // fire & forget
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_messages.Count > 0 && string.IsNullOrEmpty(_messages[^1].Content))
                            _messages.RemoveAt(_messages.Count - 1);
                    });
                    AddMessage("Error", "No response received from Paimon.", true);
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_messages.Count > 0 && string.IsNullOrEmpty(_messages[^1].Content))
                        _messages.RemoveAt(_messages.Count - 1);
                });
                AddMessage("Error", $"Error calling Semantic Kernel: {ex.Message}", true);
            }
        }

        private static Bitmap MaybeDownscaleForGemini(Bitmap bmp)
        {
            const int maxDim = 1600; // heuristic; keeps clarity but limits payload
            if (bmp.Width <= maxDim && bmp.Height <= maxDim) return bmp;
            var scale = Math.Min((double)maxDim / bmp.Width, (double)maxDim / bmp.Height);
            var newW = (int)(bmp.Width * scale);
            var newH = (int)(bmp.Height * scale);
            var resized = new Bitmap(newW, newH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(bmp, 0, 0, newW, newH);
            }
            bmp.Dispose();
            return resized;
        }

        private void CopySingleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string text)
            {
                try { System.Windows.Clipboard.SetText(text); } catch { }
            }
        }

        private void CopyAllClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var m in _messages)
                {
                    sb.AppendLine($"[{m.TimeStamp:HH:mm:ss}] {m.DisplayRole}: {m.Content}");
                }
                System.Windows.Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private void ClearHistoryClick(object sender, RoutedEventArgs e)
        {
            _messages.Clear();
            _chatHistory?.Clear();
            _chatHistory = new ChatHistory(SystemInstruction);
            AddMessage("System", "Conversation history cleared. Paimon's memory has been reset!");
        }

        private async void SendTravelerMessage(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (_chatService == null || _chatHistory == null)
            {
                AddMessage("Error", "Semantic Kernel not initialized.", true);
                return;
            }
            var text = ChatInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            ChatInput.Text = string.Empty;
            AddMessage("Traveler", text);
            try
            {
                SetBusy(true);
                await StreamTextOnlyAsync(text);
            }
            finally { SetBusy(false); }
        }

        private void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Return && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
            {
                e.Handled = true; // prevent newline
                SendTravelerMessage(sender, new RoutedEventArgs());
            }
        }

        private void ToggleVoice(object sender, RoutedEventArgs e)
        {
            _voiceEnabled = !_voiceEnabled;
            // Update button glyph
            if (VoiceToggle != null)
            {
                VoiceToggle.Content = new TextBlock { Text = _voiceEnabled ? "🔊" : "🔇", FontSize = 16, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            }
            if (!_voiceEnabled)
            {
                _speechCts?.Cancel();
                try { _synth.SpeakAsyncCancelAll(); } catch { }
            }
            else
            {
                // Optionally set a voice (ignore errors if voice missing)
                try
                {
                    // Pick a female voice if available for a more Paimon-like tone
                    foreach (var v in _synth.GetInstalledVoices())
                    {
                        if (v.Enabled && v.VoiceInfo.Gender == VoiceGender.Female)
                        {
                            _synth.SelectVoice(v.VoiceInfo.Name);
                            break;
                        }
                    }
                    _synth.Rate = 1; // slightly faster
                }
                catch { }
            }
        }

        private async Task SpeakAsyncIfEnabled(string text)
        {
            if (!_voiceEnabled || string.IsNullOrWhiteSpace(text)) return;
            _speechCts?.Cancel();
            _speechCts = new CancellationTokenSource();
            var token = _speechCts.Token;
            try
            {
                // Ensure any current speech stopped
                _synth.SpeakAsyncCancelAll();
                await Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return;
                    // Use Speak (blocking inside background thread) for simpler cancellation pattern
                    try { _synth.Speak(text); } catch { }
                }, token);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task StreamTextOnlyAsync(string userText)
        {
            // Add to chat history as plain user text
            _chatHistory!.AddUserMessage(userText);
            AddMessage("Paimon", "");
            var msgIndex = _messages.Count - 1;
            var builder = new StringBuilder();
            try
            {
                await foreach (var chunk in _chatService!.GetStreamingChatMessageContentsAsync(_chatHistory))
                {
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        builder.Append(chunk.Content);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (msgIndex < _messages.Count)
                            {
                                var current = _messages[msgIndex];
                                _messages[msgIndex] = ChatMessage.Create(current.Role, builder.ToString(), current.IsError);
                            }
                        }, DispatcherPriority.Background);
                    }
                }
                var final = builder.ToString();
                if (!string.IsNullOrEmpty(final))
                {
                    _chatHistory.AddAssistantMessage(final);
                    _ = SpeakAsyncIfEnabled(final); // fire & forget
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_messages.Count > 0 && string.IsNullOrEmpty(_messages[^1].Content))
                            _messages.RemoveAt(_messages.Count - 1);
                    });
                    AddMessage("Error", "No response received from Paimon.", true);
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_messages.Count > 0 && string.IsNullOrEmpty(_messages[^1].Content))
                        _messages.RemoveAt(_messages.Count - 1);
                });
                AddMessage("Error", "Chat error: " + ex.Message, true);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _speechCts?.Cancel();
                _synth.SpeakAsyncCancelAll();
                _synth.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }
    }

    internal class ChatMessage
    {
        public string Role { get; init; } = string.Empty;
        public string DisplayRole { get; init; } = string.Empty;
        public string RoleIcon { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public DateTime TimeStamp { get; init; } = DateTime.Now;
        public System.Windows.Media.Brush BubbleColor { get; init; } = System.Windows.Media.Brushes.Transparent;
        public bool IsError { get; init; }
        public bool IsPaimonMessage { get; init; } // NEW for visibility binding

        public static ChatMessage Create(string role, string content, bool isError)
        {
            var normalized = role.ToLowerInvariant();
            string display = normalized switch
            {
                "traveler" => "Traveler",
                "paimon" => "Paimon",
                "system" => "System",
                "error" => "Error",
                _ => role
            };
            string icon = normalized switch
            {
                "traveler" => "👤",
                "paimon" => "✨",
                "system" => "⚙",
                "error" => "⚠",
                _ => "💬"
            };
            var bubble = isError
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0xFF, 0x44, 0x44))
                : normalized switch
                {
                    "traveler" => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x45, 0x40, 0x7B, 0xFF)),
                    "paimon" => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x45, 0xFF, 0xEE, 0xAA)),
                    "system" => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xAA, 0xAA, 0xAA)),
                    _ => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x35, 0x88, 0x88, 0xFF))
                };
            bubble.Freeze();
            return new ChatMessage
            {
                Role = role,
                DisplayRole = display,
                RoleIcon = icon,
                Content = content,
                TimeStamp = DateTime.Now,
                BubbleColor = bubble,
                IsError = isError,
                IsPaimonMessage = normalized == "paimon"
            };
        }
    }
}
