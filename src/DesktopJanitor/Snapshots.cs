using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopJanitor;

/// <summary>
/// `--snapshots &lt;dir&gt;`: renders the real speech bubble and memory list, filled with sample data,
/// straight to transparent PNGs (2x) for the README. Nothing is shown on screen and no real app is listed.
/// </summary>
public static class Snapshots
{
    private const double Scale = 2;
    private const long MB = Bytes.MB;

    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);

        Save(new SpeechBubble(
                "Spotify hasn't been used for 47 min",
                "It's holding 312 MB. Want me to close it? It'll still get to ask you to save anything.",
                CleanupService.QuestionButtons,
                TimeSpan.FromMinutes(1)),
            Path.Combine(dir, "bubble-question.png"));

        Save(new SpeechBubble("Closed Spotify", "That's about 312 MB swept up.", Array.Empty<BubbleButton>(), TimeSpan.FromSeconds(5)),
            Path.Combine(dir, "bubble-remark.png"));

        // One row of each kind: closable, deny-listed (Close asks twice), background (Trim only), protected.
        Save(MemoryPanel.CreateSample(
                new (string, string?, long, string, bool, bool)[]
                {
                    ("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 2412 * MB,
                        "7 windows, unused 42 min", true, true),
                    ("Visual Studio Code", Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe"),
                        1126 * MB, "2 windows, used just now, deny-listed", true, true),
                    ("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 642 * MB,
                        "Background, 9 processes", true, false),
                    ("Windows Explorer", @"C:\Windows\explorer.exe", 410 * MB, "Protected: Windows system process", false, false),
                    ("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", 388 * MB, "1 window, unused 18 min", true, true),
                    ("Microsoft Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", 296 * MB,
                        "1 window, unused 51 min", true, true),
                },
                "Top 6 of 63 programs by memory, measured like Task Manager's Memory column.",
                ramUsed: 12_900 * MB,
                ramTotal: 16_100 * MB),
            Path.Combine(dir, "memory-panel.png"));

        Save(SetupWindow.CreateSample(), Path.Combine(dir, "setup.png"));
    }

    private static void Save(Window window, string path)
    {
        // Render the window's content on its own (the window is never shown), keeping its text settings.
        var content = (UIElement)window.Content;
        window.Content = null;
        var host = new Border { Padding = new Thickness(6), Child = content };
        TextElement.SetFontFamily(host, window.FontFamily);
        TextElement.SetFontSize(host, window.FontSize);
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);

        double width = double.IsNaN(window.Width) ? double.PositiveInfinity : window.Width + 12;
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.ActualWidth * Scale), (int)Math.Ceiling(host.ActualHeight * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
        window.Close();
    }
}
