using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Oxydriver.Services;

public sealed class SettingsBackupService
{
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int Iterations = 120_000;

    public async Task ExportAsync(string outputPath, string password, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Mot de passe de backup manquant.");

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var plain = Encoding.UTF8.GetBytes(json);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        using var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var encKey = kdf.GetBytes(32);
        var macKey = kdf.GetBytes(32);

        var iv = RandomNumberGenerator.GetBytes(IvSize);
        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        }

        var payload = new BackupPayload
        {
            Version = 1,
            Salt = Convert.ToBase64String(salt),
            Iv = Convert.ToBase64String(iv),
            CipherText = Convert.ToBase64String(cipher)
        };
        var body = JsonSerializer.Serialize(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputeHmac(bodyBytes, macKey);

        var envelope = new BackupEnvelope
        {
            Payload = body,
            Hmac = Convert.ToBase64String(hmac)
        };

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    public async Task<AppSettings> ImportAsync(string inputPath, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Mot de passe de backup manquant.");

        var raw = await File.ReadAllTextAsync(inputPath, Encoding.UTF8);
        var envelope = JsonSerializer.Deserialize<BackupEnvelope>(raw) ?? throw new InvalidOperationException("Backup invalide.");
        var payloadBytes = Encoding.UTF8.GetBytes(envelope.Payload ?? string.Empty);
        var payload = JsonSerializer.Deserialize<BackupPayload>(envelope.Payload ?? string.Empty) ?? throw new InvalidOperationException("Payload backup invalide.");

        var salt = Convert.FromBase64String(payload.Salt);
        using var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var encKey = kdf.GetBytes(32);
        var macKey = kdf.GetBytes(32);

        var expected = ComputeHmac(payloadBytes, macKey);
        var incoming = Convert.FromBase64String(envelope.Hmac ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(expected, incoming))
            throw new InvalidOperationException("Mot de passe incorrect ou backup corrompu.");

        var iv = Convert.FromBase64String(payload.Iv);
        var cipher = Convert.FromBase64String(payload.CipherText);
        byte[] plain;
        using (var aes = Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        var json = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidOperationException("Contenu backup invalide.");
    }

    private static byte[] ComputeHmac(byte[] data, byte[] key)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(data);
    }

    private sealed class BackupEnvelope
    {
        public string? Payload { get; set; }
        public string? Hmac { get; set; }
    }

    private sealed class BackupPayload
    {
        public int Version { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Iv { get; set; } = string.Empty;
        public string CipherText { get; set; } = string.Empty;
    }
}

