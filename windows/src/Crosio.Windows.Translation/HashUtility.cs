using System.Security.Cryptography;

namespace Crosio.Windows.Translation;

internal static class HashUtility
{
    public static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    public static bool EqualsSha256(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            Convert.FromHexString(actual));

    public static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
