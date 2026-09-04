using System.Security.Cryptography;

namespace ServerLauncher.Core.Remote;

/// <summary>Result of redeeming a pairing code.</summary>
/// <param name="Success">Whether a device was paired.</param>
/// <param name="Token">The device token, returned exactly once.</param>
/// <param name="Device">The stored device record.</param>
/// <param name="Error">Why pairing failed, for the client to display.</param>
public readonly record struct PairingResult(
    bool Success,
    string? Token,
    PairedDevice? Device,
    string? Error);

/// <summary>
/// Issues and redeems short-lived pairing codes.
/// </summary>
/// <remarks>
/// A pairing code is the one moment this install will hand out a credential, so it is
/// deliberately hard to use by accident or by force: it exists only while the user has the
/// dialog open, expires in minutes, works once, and repeated guesses are rate limited.
/// </remarks>
public sealed class PairingService
{
    /// <summary>How long an unused code stays valid.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Failed redemptions allowed inside the window before pairing is blocked.</summary>
    public const int MaxFailedAttempts = 5;

    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    // Ambiguous characters are excluded: a code is read off a screen or typed by hand.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly DeviceStore _devices;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _failures = new();

    private string? _activeCode;
    private DateTimeOffset _activeCodeExpiry;

    public PairingService(DeviceStore devices, Func<DateTimeOffset>? now = null)
    {
        _devices = devices;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>True while a code is outstanding and unexpired.</summary>
    public bool HasActiveCode
    {
        get
        {
            lock (_gate)
            {
                return _activeCode is not null && _now() < _activeCodeExpiry;
            }
        }
    }

    public DateTimeOffset? ActiveCodeExpiry
    {
        get
        {
            lock (_gate)
            {
                return _activeCode is null ? null : _activeCodeExpiry;
            }
        }
    }

    /// <summary>
    /// Starts pairing, replacing any outstanding code so only one is ever valid.
    /// </summary>
    public string BeginPairing()
    {
        var code = GenerateCode();

        lock (_gate)
        {
            _activeCode = code;
            _activeCodeExpiry = _now().Add(CodeLifetime);
        }

        return code;
    }

    /// <summary>Withdraws the outstanding code, e.g. when the dialog is closed.</summary>
    public void CancelPairing()
    {
        lock (_gate)
        {
            _activeCode = null;
        }
    }

    /// <summary>
    /// Exchanges a pairing code for a device token. The code is consumed whether or not
    /// the caller got it right, so a wrong guess cannot be retried against the same code.
    /// </summary>
    public PairingResult Redeem(string? code, string deviceName, DeviceCapabilities capabilities)
    {
        lock (_gate)
        {
            PruneFailures();

            if (_failures.Count >= MaxFailedAttempts)
            {
                return new PairingResult(false, null, null,
                    "Too many pairing attempts. Wait a minute and start pairing again.");
            }

            if (_activeCode is null)
            {
                RecordFailure();
                return new PairingResult(false, null, null, "No pairing is in progress.");
            }

            if (_now() >= _activeCodeExpiry)
            {
                _activeCode = null;
                return new PairingResult(false, null, null, "The pairing code has expired.");
            }

            var expected = System.Text.Encoding.UTF8.GetBytes(_activeCode);
            var supplied = System.Text.Encoding.UTF8.GetBytes(code ?? string.Empty);

            if (expected.Length != supplied.Length
                || !CryptographicOperations.FixedTimeEquals(expected, supplied))
            {
                RecordFailure();
                return new PairingResult(false, null, null, "That pairing code is not correct.");
            }

            // Single use: consumed on success so the same QR cannot pair twice.
            _activeCode = null;
            _failures.Clear();
        }

        var token = DeviceStore.GenerateToken();
        var device = _devices.Add(deviceName, token, capabilities);

        return new PairingResult(true, token, device, null);
    }

    private void RecordFailure() => _failures.Add(_now());

    private void PruneFailures()
    {
        var cutoff = _now() - FailureWindow;
        _failures.RemoveAll(at => at < cutoff);
    }

    private static string GenerateCode()
    {
        Span<char> code = stackalloc char[8];
        for (var i = 0; i < code.Length; i++)
        {
            code[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(code);
    }
}
