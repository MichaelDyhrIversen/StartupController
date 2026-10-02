using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace StartupController
{
    // The D7 hash of one Run value: lowercase hex SHA-256 of the UTF-16LE bytes of "{kind}\0{raw}" (see
    // RunFingerprint). Linked into StartupController.ReturnToWindows.exe (net462), so it avoids APIs .NET Framework
    // 4.6.2 lacks (SHA256.HashData, Convert.ToHexString) and both sides compute exactly the same value.
    internal static class FingerprintHash
    {
        internal static string Compute(RegistryValueKind kind, string raw)
        {
            var input = kind.ToString() + "\0" + raw;
            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.Unicode.GetBytes(input));
            }

            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return hex.ToString();
        }
    }
}
