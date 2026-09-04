using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Remote;

namespace ServerLauncher.App.Views;

/// <summary>
/// Shows a one-off pairing code as a QR image.
/// </summary>
/// <remarks>
/// The code is created when this window opens and withdrawn when it closes, so pairing is
/// only ever possible while someone is deliberately looking at this dialog.
/// </remarks>
public partial class PairingWindow : Window
{
    private readonly PairingService _pairing;
    private readonly RemoteAccessSettings _settings;
    private readonly DispatcherTimer _countdown;

    public PairingWindow(PairingService pairing, AppSettings settings)
    {
        InitializeComponent();

        _pairing = pairing;
        _settings = settings.RemoteAccess;

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => UpdateCountdown();
        _countdown.Start();

        IssueCode();
    }

    private void IssueCode()
    {
        var address = _settings.ResolvePhoneAddress();

        if (string.IsNullOrWhiteSpace(address))
        {
            ShowWarning(
                "No address for phones is set and no Tailscale address was found. Set one in "
                + "Settings, otherwise the app will not know where to connect.");
        }

        var code = _pairing.BeginPairing();

        AddressBox.Text = string.IsNullOrWhiteSpace(address) ? "(not configured)" : address;
        CodeBox.Text = code;

        RenderQr(BuildPayload(address, code));
        UpdateCountdown();
    }

    /// <summary>
    /// The QR carries everything the app needs to connect, so the phone never has to be
    /// told an address separately.
    /// </summary>
    internal static string BuildPayload(string address, string code) =>
        JsonSerializer.Serialize(new
        {
            v = 1,
            app = "ServerManager",
            url = address,
            code
        });

    private void RenderQr(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

        var modules = data.ModuleMatrix;
        var size = modules.Count;

        // Drawn as rectangles rather than decoded from a PNG: no image codec involved, and
        // it stays crisp at whatever size the dialog uses.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (modules[y][x])
                    {
                        context.DrawRectangle(Brushes.Black, null, new Rect(x, y, 1, 1));
                    }
                }
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        QrImage.Source = bitmap;
    }

    private void UpdateCountdown()
    {
        var expiry = _pairing.ActiveCodeExpiry;

        if (expiry is null)
        {
            CountdownText.Text = "This code has been used. Generate a new one to pair another phone.";
            return;
        }

        var remaining = expiry.Value - DateTimeOffset.Now;

        CountdownText.Text = remaining <= TimeSpan.Zero
            ? "This code has expired. Generate a new one."
            : $"Expires in {remaining.Minutes}:{remaining.Seconds:00}.";
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningPanel.Visibility = Visibility.Visible;
    }

    private void OnNewCode(object sender, RoutedEventArgs e)
    {
        WarningPanel.Visibility = Visibility.Collapsed;
        IssueCode();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _countdown.Stop();

        // Closing the dialog withdraws the code, so a QR left on a second monitor or in a
        // screenshot cannot be used later.
        _pairing.CancelPairing();

        base.OnClosed(e);
    }
}
