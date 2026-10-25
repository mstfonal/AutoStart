using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoStart
{
    internal static class UpdatePackage
    {
        internal static UpdateInfo? Select(string json, string current)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var version) || version <= new Version(current)) return null;
            if (!root.TryGetProperty("assets", out var assets)) return null;
            foreach (string expected in new[] { "AutoStart.zip", "AutoStart.exe" })
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(asset.GetProperty("name").GetString(), expected, StringComparison.OrdinalIgnoreCase)) continue;
                    string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                        uri.Host != "github.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                        uri.AbsolutePath != "/mstfonal/AutoStart/releases/download/" + Uri.EscapeDataString(tag) + "/" + expected) continue;
                    string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                    if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
                        !digest.Substring(7).All(Uri.IsHexDigit)) continue;
                    return new UpdateInfo { TagName = tag, DownloadUrl = url, Sha256 = digest.Substring(7), IsZip = expected.EndsWith(".zip") };
                }
            return null;
        }

        internal static byte[] VerifyAndExtract(byte[] bytes, UpdateInfo info)
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update SHA-256 does not match GitHub release metadata.");
            byte[] exe = bytes;
            if (info.IsZip)
            {
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var entries = archive.Entries.Where(e => e.FullName.Equals("AutoStart.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Length != 1 || entries[0].Length > 250 * 1024 * 1024)
                    throw new InvalidDataException("Update ZIP must contain exactly one root AutoStart.exe.");
                using var source = entries[0].Open();
                using var output = new MemoryStream();
                source.CopyTo(output); exe = output.ToArray();
            }
            ValidateExecutable(exe);
            return exe;
        }

        internal static void ValidateExecutable(byte[] exe)
        {
            try
            {
                using var reader = new PEReader(new MemoryStream(exe));
                if (reader.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus ||
                    reader.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                    throw new InvalidDataException("Update must be a Windows x64 executable.");
            }
            catch (BadImageFormatException ex) { throw new InvalidDataException("Invalid Windows executable.", ex); }
        }

    }
}
