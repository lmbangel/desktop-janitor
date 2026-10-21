using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopJanitor;

public sealed class Animation
{
    public Animation(ImageSource[] frames, double fps)
    {
        Frames = frames;
        Fps = fps <= 0 ? 8 : fps;
    }

    public ImageSource[] Frames { get; }
    public double Fps { get; }

    public ImageSource FrameAt(double seconds) => Frames[(int)(seconds * Fps) % Frames.Length];
}

/// <summary>Cuts the sheet into walk / sweep / idle animations (one row each).</summary>
public sealed class SpriteSheet
{
    private static readonly Uri BuiltIn = new("pack://application:,,,/Assets/janitor.png");

    private SpriteSheet(int frameWidth, int frameHeight, Animation walk, Animation sweep, Animation idle)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Walk = walk;
        Sweep = sweep;
        Idle = idle;
    }

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public Animation Walk { get; }
    public Animation Sweep { get; }
    public Animation Idle { get; }

    public static SpriteSheet Load(SpriteConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Path))
        {
            try
            {
                return Cut(LoadBitmap(new Uri(Path.GetFullPath(config.Path))), config);
            }
            catch (Exception ex)
            {
                Log.Info($"Custom sprite sheet '{config.Path}' could not be used ({ex.Message}); using the built-in janitor.");
            }
        }
        return Cut(LoadBitmap(BuiltIn), new SpriteConfig());
    }

    private static BitmapSource LoadBitmap(Uri uri)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static SpriteSheet Cut(BitmapSource sheet, SpriteConfig config)
    {
        int w = config.FrameWidth, h = config.FrameHeight;

        Animation Row(AnimationConfig a)
        {
            if (a.Frames < 1 || (a.Row + 1) * h > sheet.PixelHeight || a.Frames * w > sheet.PixelWidth)
                throw new InvalidDataException($"row {a.Row} with {a.Frames} frames of {w}x{h} doesn't fit a {sheet.PixelWidth}x{sheet.PixelHeight} sheet");

            var frames = new ImageSource[a.Frames];
            for (int i = 0; i < a.Frames; i++)
            {
                var frame = new CroppedBitmap(sheet, new Int32Rect(i * w, a.Row * h, w, h));
                frame.Freeze();
                frames[i] = frame;
            }
            return new Animation(frames, a.Fps);
        }

        return new SpriteSheet(w, h, Row(config.Walk), Row(config.Sweep), Row(config.Idle));
    }
}
