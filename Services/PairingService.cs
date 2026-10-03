using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDeck;

public sealed record PairedDevice(string Name, DateTimeOffset CreatedUtc);

public sealed class PairingService
{
    private readonly object _gate = new();
    private readonly string _storagePath;
    private readonly Queue<DateTimeOffset> _failedAttempts = new();
    private List<TrustedDevice> _devices;
    private string _pairingCode;
    public PairingService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDeck");
        Directory.CreateDirectory(directory); _storagePath = Path.Combine(directory, "trusted-devices.json");
        _devices = LoadDevices(); _pairingCode = CreatePairingCode();
    }
    public string CurrentPairingCode { get { lock (_gate) return _pairingCode; } }
    public bool TryPair(string suppliedCode, string deviceName, out string token)
    {
        token = ""; lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_failedAttempts.Count > 0 && now - _failedAttempts.Peek() > TimeSpan.FromMinutes(1)) _failedAttempts.Dequeue();
            if (_failedAttempts.Count >= 5) return false;
            if (!ConstantTimeEquals(suppliedCode, _pairingCode)) { _failedAttempts.Enqueue(now); return false; }
            token = CreateToken(); var name = string.IsNullOrWhiteSpace(deviceName) ? "Browser device" : deviceName.Trim();
            _devices.Add(new TrustedDevice(name[..Math.Min(name.Length, 50)], HashToken(token), now));
            if (_devices.Count > 20) _devices = _devices.TakeLast(20).ToList();
            SaveDevices(); _failedAttempts.Clear(); _pairingCode = CreatePairingCode(); return true;
        }
    }
    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false; var hash = HashToken(token);
        lock (_gate) return _devices.Any(device => ConstantTimeEquals(device.TokenHash, hash));
    }
    public IReadOnlyList<PairedDevice> GetDevices() { lock (_gate) return _devices.Select(d => new PairedDevice(d.Name, d.CreatedUtc)).ToArray(); }
    public void RegenerateCode() { lock (_gate) _pairingCode = CreatePairingCode(); }
    public void Revoke(string name) { lock (_gate) { _devices.RemoveAll(d => d.Name.Equals(name, StringComparison.Ordinal)); SaveDevices(); } }
    public void ForgetAll() { lock (_gate) { _devices.Clear(); SaveDevices(); _pairingCode = CreatePairingCode(); } }
    private List<TrustedDevice> LoadDevices() { try { if (!File.Exists(_storagePath)) return []; return JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(_storagePath)) ?? []; } catch { return []; } }
    private void SaveDevices() { var tmp = _storagePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, _storagePath, true); }
    private static string CreatePairingCode() => RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool ConstantTimeEquals(string left, string right) { var a = Encoding.UTF8.GetBytes(left ?? ""); var b = Encoding.UTF8.GetBytes(right ?? ""); return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b); }
    private sealed record TrustedDevice(string Name, string TokenHash, DateTimeOffset CreatedUtc);
}
