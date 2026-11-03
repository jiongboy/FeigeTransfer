using Feige.Core.Profiles;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Feige.App.Services;

public sealed record AvatarChoice(string Id, string DisplayName, ImageSource Image);

/// <summary>Loads the bundled avatar palette used by onboarding, settings, and peer rows.</summary>
public static class AvatarCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bird"] = "飞鸽", ["cat"] = "猫咪", ["fox"] = "狐狸", ["panda"] = "熊猫", ["bear"] = "小熊",
        ["fish"] = "小鱼", ["rocket"] = "火箭", ["sun"] = "太阳", ["star"] = "星星", ["leaf"] = "绿叶",
    };
    private static readonly IReadOnlyDictionary<string, ImageSource> Images = Names.Keys.ToDictionary(
        id => id,
        id => (ImageSource)new BitmapImage(new Uri($"ms-appx:///Assets/Avatars/{id}.png")),
        StringComparer.Ordinal);

    public static IReadOnlyList<AvatarChoice> Choices { get; } = Names.Keys
        .Select(id => new AvatarChoice(id, Names[id], Images[id])).ToArray();

    public static ImageSource GetImageSource(string? avatarId) => Images[AvatarIds.Normalize(avatarId)];

    public static string DisplayName(string? avatarId) => Names[AvatarIds.Normalize(avatarId)];
}

public sealed class AvatarImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => AvatarCatalog.GetImageSource(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
