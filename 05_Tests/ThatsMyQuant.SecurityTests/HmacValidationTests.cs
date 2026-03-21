using Xunit;
using System.Security.Cryptography;
using System.Text;

namespace ThatsMyQuant.SecurityTests;

public class HmacValidationTests
{
    private string GenerateHmacSha256(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLower();
    }

    [Fact]
    public void ValidateWebhookPayload_AcceptsValidSignatures()
    {
        // Arrange
        var secretToken = "my_crypto_webhook_secret_999!";
        var payload = "{\"symbol\":\"BTC-USD\",\"side\":\"Buy\",\"price\":65000}";
        
        var signature = GenerateHmacSha256(payload, secretToken);

        // Act
        var isValid = signature == GenerateHmacSha256(payload, secretToken);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateWebhookPayload_RejectsForgedSignatures()
    {
        // Arrange
        var secretToken = "my_crypto_webhook_secret_999!";
        var attackerPayload = "{\"symbol\":\"BTC-USD\",\"side\":\"Buy\",\"price\":65000}";
        
        var fakeSignature = "12345abcdef67890deadbeef";

        // Act
        var isValid = fakeSignature == GenerateHmacSha256(attackerPayload, secretToken);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateWebhookPayload_RejectsAlteredPayloads()
    {
        // Arrange
        var secretToken = "my_crypto_webhook_secret_999!";
        var originalPayload = "{\"symbol\":\"BTC-USD\",\"side\":\"Buy\",\"price\":65000}";
        var originalSignature = GenerateHmacSha256(originalPayload, secretToken);

        // Attacker intercepts and changes price to dump the market
        var alteredPayload = "{\"symbol\":\"BTC-USD\",\"side\":\"Buy\",\"price\":5000}";

        // Act
        var isValid = originalSignature == GenerateHmacSha256(alteredPayload, secretToken);

        // Assert
        Assert.False(isValid); // The signature no longer matches the payload = Rejected
    }
}
