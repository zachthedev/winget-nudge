using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;

namespace WingetNudge.Core.Storage;

/// <summary>
/// Encrypts a short string so only the current Windows user can read it back. A settings file
/// holding a credential is readable by anything running as the user, and by every backup and
/// disk image that file lands in.
/// </summary>
public static unsafe class UserSecret
{
    /// <summary>Encrypts a string to the current user.</summary>
    /// <param name="plain">Text to protect.</param>
    /// <returns>Base64 of the encrypted blob.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused to encrypt.</exception>
    public static string Protect(string plain)
    {
        byte[] input = Encoding.UTF8.GetBytes(plain);
        fixed (byte* buffer = input)
        {
            CRYPT_INTEGER_BLOB source = new() { cbData = (uint)input.Length, pbData = buffer };
            CRYPT_INTEGER_BLOB output = default;
            if (!PInvoke.CryptProtectData(&source, null, null, null, null, 0, &output))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Convert.ToBase64String(
                    new ReadOnlySpan<byte>(output.pbData, (int)output.cbData)
                );
            }
            finally
            {
                _ = PInvoke.LocalFree(new HLOCAL(output.pbData));
            }
        }
    }

    /// <summary>Decrypts a string encrypted by <see cref="Protect"/>.</summary>
    /// <param name="protectedValue">Base64 of the encrypted blob.</param>
    /// <returns>The text, or <c>null</c> when the blob belongs to another user or is corrupt.</returns>
    public static string? Unprotect(string protectedValue)
    {
        byte[] input;
        try
        {
            input = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            return null;
        }

        fixed (byte* buffer = input)
        {
            CRYPT_INTEGER_BLOB source = new() { cbData = (uint)input.Length, pbData = buffer };
            CRYPT_INTEGER_BLOB output = default;
            if (!PInvoke.CryptUnprotectData(&source, null, null, null, null, 0, &output))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(
                    new ReadOnlySpan<byte>(output.pbData, (int)output.cbData)
                );
            }
            finally
            {
                _ = PInvoke.LocalFree(new HLOCAL(output.pbData));
            }
        }
    }
}
