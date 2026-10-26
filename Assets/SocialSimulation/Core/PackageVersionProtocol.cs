using System;
using System.Globalization;

namespace SocialSimulation.Core
{
    /// <summary>Numeric package versions and a deliberately limited range syntax; not full SemVer.</summary>
    public static class PackageVersionProtocol
    {
        private readonly struct NumericVersion : IComparable<NumericVersion>
        {
            private readonly uint major;
            private readonly uint minor;
            private readonly uint patch;

            public NumericVersion(uint major, uint minor, uint patch)
            { this.major = major; this.minor = minor; this.patch = patch; }

            public int CompareTo(NumericVersion other)
            {
                int result = major.CompareTo(other.major);
                if (result != 0) return result;
                result = minor.CompareTo(other.minor);
                return result != 0 ? result : patch.CompareTo(other.patch);
            }
        }

        private readonly struct VersionRange
        {
            public readonly bool Any;
            public readonly bool Exact;
            public readonly bool HasUpper;
            public readonly NumericVersion Lower;
            public readonly NumericVersion Upper;

            public VersionRange(bool any, bool exact, NumericVersion lower,
                bool hasUpper = false, NumericVersion upper = default)
            { Any = any; Exact = exact; Lower = lower; HasUpper = hasUpper; Upper = upper; }
        }

        public static bool TryValidateVersion(string version, out string error) => TryParseVersion(version, out _, out error);

        public static bool TryValidateRange(string versionRange, out string error) => TryParseRange(versionRange, out _, out error);

        public static bool Matches(string versionRange, string registeredVersion, out string error)
        {
            if (!TryParseRange(versionRange, out var range, out error)) return false;
            if (string.IsNullOrWhiteSpace(registeredVersion))
            { error = "Registered package version is missing."; return false; }
            // Legacy schema 1 packages can use opaque versions. A wildcard intentionally accepts them.
            if (range.Any) { error = null; return true; }
            if (!TryParseVersion(registeredVersion, out var version, out error)) return false;
            bool matches = range.Exact ? version.CompareTo(range.Lower) == 0 :
                version.CompareTo(range.Lower) >= 0 && (!range.HasUpper || version.CompareTo(range.Upper) < 0);
            error = matches ? null : "Registered version " + registeredVersion + " does not satisfy " + versionRange + ".";
            return matches;
        }

        private static bool TryParseVersion(string text, out NumericVersion version, out string error)
        {
            version = default;
            error = "Version must have one to three unsigned decimal integer parts (for example 1, 1.2 or 1.2.3).";
            if (string.IsNullOrEmpty(text) || text != text.Trim()) return false;
            var parts = text.Split('.');
            if (parts.Length < 1 || parts.Length > 3) return false;
            uint major = 0, minor = 0, patch = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) return false;
                foreach (var digit in parts[i]) if (digit < '0' || digit > '9') return false;
                if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
                if (i == 0) major = number; else if (i == 1) minor = number; else patch = number;
            }
            version = new NumericVersion(major, minor, patch);
            error = null;
            return true;
        }

        private static bool TryParseRange(string text, out VersionRange range, out string error)
        {
            range = default;
            error = "Supported ranges are *, an exact numeric version, [min,max), or [min,).";
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text == "*") { range = new VersionRange(true, false, default); error = null; return true; }
            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal))
            {
                var bounds = text.Substring(1, text.Length - 2).Split(',');
                if (bounds.Length != 2 || !TryParseVersion(bounds[0].Trim(), out var lower, out _)) return false;
                bool hasUpper = !string.IsNullOrWhiteSpace(bounds[1]);
                NumericVersion upper = default;
                if (hasUpper && !TryParseVersion(bounds[1].Trim(), out upper, out _)) return false;
                if (hasUpper && lower.CompareTo(upper) >= 0)
                { error = "A version interval must have an upper bound greater than its lower bound."; return false; }
                range = new VersionRange(false, false, lower, hasUpper, upper);
                error = null;
                return true;
            }
            if (!TryParseVersion(text, out var exact, out _)) return false;
            range = new VersionRange(false, true, exact);
            error = null;
            return true;
        }
    }
}
