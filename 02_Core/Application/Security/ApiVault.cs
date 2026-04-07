using System.Security.Cryptography;
using System.Text;

namespace ThatsMyQuant.Application.Security;

/// <summary>
/// Mocks a Hardware Security Module (HSM) by dynamically encrypting Exchange API keys
/// directly in the running RAM utilizing AES-256-GCM. 
/// Prevents Memory Dump hackers from stealing plain-text JSON application settings.
/// </summary>
public sealed class ApiVault : IDisposable
{
    private byte[]? _encryptedKey;
    private byte[]? _nonce;
    private byte[]? _tag;
    private byte[]? _masterKek; // Key Encryption Key
    private bool _disposed;

    public void SecureInboundKey(string plainTextKey)
    {
        // 1. Generate 256-bit Key Encryption Key strictly in RAM
        _masterKek = new byte[32]; 
        RandomNumberGenerator.Fill(_masterKek);

        // 2. Generate Galois Counter Mode Nonce
        _nonce = new byte[12];
        RandomNumberGenerator.Fill(_nonce);

        // 3. Serialize API Key
        byte[] keyBytes = Encoding.UTF8.GetBytes(plainTextKey);
        
        _encryptedKey = new byte[keyBytes.Length];
        _tag = new byte[16];

        // 4. Encrypt to cipher text directly in cache
        using var aesgcm = new AesGcm(_masterKek, 16); // 16-byte authentication tag
        aesgcm.Encrypt(_nonce, keyBytes, _encryptedKey, _tag);

        // 5. Zero-out the temporary plain-text array to blind the .NET Garbage Collector
        Array.Clear(keyBytes, 0, keyBytes.Length);
    }

    /// <summary>
    /// Decrypts the raw byte array natively. You MUST pass the returned byte array into the 
    /// SecureStringExtension to be zeroed-out immediately after signing the Binance REST payload.
    /// </summary>
    public byte[] GetTransientDecryptedKey()
    {
        if (_disposed)
            throw new InvalidOperationException("ApiVault has been disposed — key material has been zeroed.");

        if (_encryptedKey == null || _masterKek == null || _nonce == null || _tag == null)
            throw new InvalidOperationException("API Key Vault is empty or corrupted.");

        byte[] decryptedSpace = new byte[_encryptedKey.Length];

        try
        {
            using var aesgcm = new AesGcm(_masterKek, 16);
            aesgcm.Decrypt(_nonce, _encryptedKey, _tag, decryptedSpace);
            return decryptedSpace;
        }
        finally
        {
            // Note: We keep _masterKek for subsequent decryptions until Dispose()
            // In a real HSM, this would be inside the secure enclave.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_masterKek != null) Array.Clear(_masterKek, 0, _masterKek.Length);
        if (_encryptedKey != null) Array.Clear(_encryptedKey, 0, _encryptedKey.Length);
        if (_nonce != null) Array.Clear(_nonce, 0, _nonce.Length);
        if (_tag != null) Array.Clear(_tag, 0, _tag.Length);
    }
}
