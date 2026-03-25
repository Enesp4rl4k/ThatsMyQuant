using System.Security.Cryptography;
using System.Text;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Implements the IHardwareSecurityModule by utilizing the memory-safe ApiVault.
/// This prevents API keys from being held in plain-text strings anywhere in the application.
/// </summary>
public class HardwareSecurityModule : IHardwareSecurityModule
{
    private readonly ApiVault _vault;

    public HardwareSecurityModule(ApiVault vault)
    {
        _vault = vault;
    }

    public Task<string> SignPayloadAsync(string exchangeId, string payload)
    {
        // 1. Fetch the transient key from the vault (AES-GCM decrypted on-demand)
        byte[] keyBytes = _vault.GetTransientDecryptedKey();
        
        try
        {
            // 2. Perform HMAC-SHA256 signing
            using var hmac = new HMACSHA256(keyBytes);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            
            // 3. Return the hex signature
            return Task.FromResult(Convert.ToHexString(hash).ToLower());
        }
        finally
        {
            // 4. CRITICAL: Zero-out the decrypted key immediately after use 
            // to prevent memory scraping from finding the key in the GC heap.
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }
    }
}
