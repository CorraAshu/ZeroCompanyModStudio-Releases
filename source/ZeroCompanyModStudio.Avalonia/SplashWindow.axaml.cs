using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The startup screen: the game's own splash, with a line of status text in its black band.
///
/// Borderless and not in the taskbar, because it is a splash and not a window anybody manages. It
/// sizes itself to the picture rather than the other way round -- a splash that resizes itself
/// after appearing is the first thing a person sees the tool do, and it looks broken.
/// </summary>
public partial class SplashWindow : Window, INotifyPropertyChanged
{
    private WriteableBitmap? _picture;
    private string _status = "";
    private Thickness _statusMargin = new(18, 0, 0, 0);

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public WriteableBitmap? Picture
    {
        get => _picture;
        private set { _picture = value; Raise(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; Raise(); }
    }

    public Thickness StatusMargin
    {
        get => _statusMargin;
        private set { _statusMargin = value; Raise(); }
    }

    public SplashWindow() : this(null) { }

    public SplashWindow(string? installFolder)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;

        var loaded = installFolder is null ? null : SplashImage.Load(installFolder);
        if (loaded is { } got)
        {
            Picture = got.Bitmap;
            Width = got.Bitmap.PixelSize.Width;
            Height = got.Bitmap.PixelSize.Height;

            // Inside the band, a little in from the left, vertically centred in whatever height
            // the band turned out to be. When there is no band, BarTop equals the image height
            // and this lands just under the picture instead -- which is the documented fallback.
            var bandHeight = got.Bitmap.PixelSize.Height - got.BarTop;
            StatusMargin = new Thickness(18, got.BarTop + Math.Max(0, (bandHeight - 18) / 2), 0, 0);
        }
        else
        {
            // No artwork: a small plain panel rather than a zero-sized window nobody can see.
            Width = 520;
            Height = 150;
            StatusMargin = new Thickness(18, 64, 0, 0);
        }
    }
}
