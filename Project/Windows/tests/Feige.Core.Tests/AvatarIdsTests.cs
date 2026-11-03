using Feige.Core.Profiles;

namespace Feige.Core.Tests;

public sealed class AvatarIdsTests
{
    [Fact]
    public void SharedPaletteHasStableIdentifiersAndUnknownValuesFallBack()
    {
        Assert.Equal(new[] { "bear", "bird", "cat", "fish", "fox", "leaf", "panda", "rocket", "star", "sun" },
            AvatarIds.All.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("bird", AvatarIds.Normalize(null));
        Assert.Equal("bird", AvatarIds.Normalize("unlisted"));
        Assert.Equal("fox", AvatarIds.Normalize("fox"));
    }

    [Theory]
    [InlineData("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899", true)]
    [InlineData("aa bb cc dd ee ff 00 11 22 33 44 55 66 77 88 99 aa bb cc dd ee ff 00 11 22 33 44 55 66 77 88 99", true)]
    [InlineData("AABBCCDDEEFF00112233", false)]
    [InlineData("Feige-AABBCCDDEEFF0011", false)]
    [InlineData("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF0011223344556677889Z", false)]
    public void PersistedAvatarKeysRequireFullFingerprint(string value, bool expectedValid)
    {
        string? normalized = AvatarIds.NormalizeFingerprint(value);
        Assert.Equal(expectedValid, normalized is not null);
        if (normalized is not null) Assert.Equal(64, normalized.Length);
    }
}
