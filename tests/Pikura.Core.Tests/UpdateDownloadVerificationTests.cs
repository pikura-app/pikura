using System.Security.Cryptography;
using Pikura.Core.Services;
using Xunit;

namespace Pikura.Core.Tests;

public class UpdateDownloadVerificationTests
{
    [Fact]
    public void ParseSha256_Accepts_GitHub_Digest_Only()
    {
        var hex = new string('a', 64);
        Assert.Equal(hex, UpdateCheckService.ParseSha256("sha256:" + hex.ToUpperInvariant()));
        Assert.Null(UpdateCheckService.ParseSha256(null));
        Assert.Null(UpdateCheckService.ParseSha256("sha1:" + hex));
        Assert.Null(UpdateCheckService.ParseSha256("sha256:abc"));
    }

    [Fact]
    public void Download_Without_Checksum_Record_Is_Not_Trusted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pikura-verify-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try { Assert.False(UpdateCheckService.IsVerifiedDownload(path)); }
        finally { UpdateCheckService.DeleteDownload(path); }
    }

    [Fact]
    public void Verified_Download_Detects_Truncation_And_Expected_Hash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pikura-verify-{Guid.NewGuid():N}.exe");
        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        File.WriteAllBytes(path, data);
        File.WriteAllText(path + ".sha256", hash);
        try
        {
            Assert.True(UpdateCheckService.IsVerifiedDownload(path));
            Assert.True(UpdateCheckService.IsVerifiedDownload(path, hash));
            Assert.False(UpdateCheckService.IsVerifiedDownload(path, new string('0', 64)));

            File.WriteAllBytes(path, data[..1000]);
            Assert.False(UpdateCheckService.IsVerifiedDownload(path));

            UpdateCheckService.DeleteDownload(path);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".sha256"));
        }
        finally { UpdateCheckService.DeleteDownload(path); }
    }
}
