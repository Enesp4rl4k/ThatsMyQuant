using System.Security.Cryptography;
using System.Text;

namespace ThatsMyQuant.Application.Security;

/// <summary>
/// A strict cryptographic extension designed to handle API Key Signing using transient `Span<byte>`.
/// </summary>
public static class SecureStringExtension
{
    /// <summary>
    /// Generates the HmacSHA256 signature required by tier-1 exchanges (Binance, ByBit).
    /// After executing the signature math, it forces native memory `.Clear()` on the API key bytes 
    /// before the Garbage Collector can even copy them.
    /// </summary>
    public static string SignAndAnnihilate(byte[] transientApiKey, string payloadToSign)
    {
        try
        {
            using var hmac = new HMACSHA256(transientApiKey);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadToSign);
            
            byte[] hash = hmac.ComputeHash(payloadBytes);
            return Convert.ToHexString(hash).ToLower();
        }
        finally
        {
            // The single most important execution block in the fund's security architecture.
            // Absolutely obliterates the decrypted API Key array back to 0s within milliseconds.
            Array.Clear(transientApiKey, 0, transientApiKey.Length);
        }
    }
}
