using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace StartupController
{
    // D7: identifies the exact Run value data a user enabled, so a different program that later reuses the
    // same Run value name (or an edited command) is not launched until the user re-enables it.
    // Only the hash is stored, never the command.
    internal static class RunFingerprint
    {
        internal const int HexLength = 64;

        // Lowercase hex SHA-256 of the UTF-16LE bytes of "{kind}\0{raw}": the value kind (e.g. "String",
        // "ExpandString") and the raw, unexpanded Run string. Including the kind makes a REG_SZ <-> REG_EXPAND_SZ
        // switch (which changes what runs) count as Changed.
        internal static string Compute(RegistryValueKind kind, string raw)
        {
            var input = kind.ToString() + "\0" + raw;
            return Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(input))).ToLowerInvariant();
        }

        // The command to launch and its fingerprint, both derived from ONE read of the Run value, so the
        // approved hash always describes exactly what is launched. REG_EXPAND_SZ is expanded here (the same
        // expansion RegistryKey.GetValue does); REG_SZ is used literally.
        internal static (string Path, string Fingerprint) FromRunValue(RegistryValueKind kind, string raw)
        {
            var path = kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(raw) : raw;
            return (path, Compute(kind, raw));
        }

        internal static bool IsWellFormed(string? hash)
        {
            return hash != null && hash.Length == HexLength && hash.All(Uri.IsHexDigit);
        }

        // A missing or malformed current fingerprint never matches
        internal static bool Matches(string? stored, string? current)
        {
            return IsWellFormed(stored) && IsWellFormed(current)
                && string.Equals(stored, current, StringComparison.OrdinalIgnoreCase);
        }

        // "name|hash", split on the LAST '|' (names may contain '|', hashes can't). False for malformed lines.
        internal static bool TryParseLine(string? line, out string name, out string hash)
        {
            name = "";
            hash = "";
            if (string.IsNullOrEmpty(line)) return false;
            int bar = line.LastIndexOf('|');
            if (bar <= 0) return false;
            var candidateName = line.Substring(0, bar);
            var candidateHash = line.Substring(bar + 1);
            if (string.IsNullOrWhiteSpace(candidateName) || !IsWellFormed(candidateHash)) return false;
            name = candidateName;
            hash = candidateHash.ToLowerInvariant();
            return true;
        }

        internal static string FormatLine(string name, string hash) => name + "|" + hash;
    }
}
