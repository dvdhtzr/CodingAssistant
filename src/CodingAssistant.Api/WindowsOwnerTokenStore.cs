using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// The Windows debug host and the Windows one-click launcher share one
/// DPAPI CurrentUser-protected, owner-private credential. No token enters
/// source control, process arguments, logs, URLs or the SQLite database.
/// Linux remains explicitly configured by its existing launcher.
/// </summary>
public static class WindowsOwnerTokenStore
{
    private const string FileName = "owner-token.dpapi";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("CodingAssistant|owner-token|v1");
    private static readonly Regex TokenPattern = new(
        @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string GetOrCreate(string privateDataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Persistent owner token bootstrap is Windows-only");
        if (string.IsNullOrWhiteSpace(privateDataDirectory))
            throw new InvalidOperationException("Owner-private data directory is required");

        var directory = Path.GetFullPath(privateDataDirectory);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing redirected owner-token directory");

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            // The new file is encrypted for the current Windows user. Publish
            // atomically, so Visual Studio and the launcher can race safely.
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();
            var protectedBytes = ProtectedData.Protect(
                Encoding.ASCII.GetBytes(token), Entropy,
                DataProtectionScope.CurrentUser);
            var staging = Path.Combine(directory,
                "owner-token-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(staging, Convert.ToBase64String(protectedBytes),
                    Encoding.ASCII);
                try
                {
                    File.Move(staging, path); // no overwrite; another process may win
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Read the winner's protected token instead of changing it.
                }
            }
            finally
            {
                if (File.Exists(staging))
                    File.Delete(staging);
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        // An invalid, moved, corrupted or inaccessible credential is a hard
        // failure. Never silently rotate it and lock out an open browser.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing redirected owner-token file");
        try
        {
            var file = new FileInfo(path);
            if (file.Length is < 20 or > 2048)
                throw new InvalidOperationException("Invalid saved Windows owner credential");
            var blob = Convert.FromBase64String(
                File.ReadAllText(path, Encoding.ASCII).Trim());
            var clearBytes = ProtectedData.Unprotect(
                blob, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var resolved = Encoding.ASCII.GetString(clearBytes);
                if (!TokenPattern.IsMatch(resolved))
                    throw new InvalidOperationException(
                        "Invalid saved Windows owner credential");
                return resolved;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        catch (Exception error) when (error is CryptographicException or
            FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Cannot open Windows owner credential for this user. " +
                "Check your Windows account and the private token file; " +
                "credentials are never silently replaced.", error);
        }
    }
}
