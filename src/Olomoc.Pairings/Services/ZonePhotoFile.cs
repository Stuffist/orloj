using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Olomoc.Tournaments;
using Olomoc.Zones;

namespace Olomoc.Pairings.Services;

/// <summary>
/// A photo chosen for a game, as sent to the server: reduced by the browser (PhotoMaxSide, JPEG),
/// lighter and without its location and other metadata.
/// </summary>
public static class ZonePhotoFile
{
    public const string ContentType = "image/jpeg";

    /// <summary>The reduced image, or the problem (too large, not an image the browser can read).</summary>
    public static async Task<(byte[]? Image, Problem? Problem)> ReadAsync(IBrowserFile file)
    {
        try
        {
            var image = await file.RequestImageFileAsync(ContentType, ZoneRules.PhotoMaxSide, ZoneRules.PhotoMaxSide);
            await using var stream = image.OpenReadStream(ZoneRules.MaxPhotoBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return (buffer.ToArray(), null);
        }
        catch (IOException) { return (null, Problem.Of("photoTooLarge", (ZoneRules.MaxPhotoBytes / (1024 * 1024)).ToString())); }
        catch (JSException) { return (null, Problem.Of("photoType")); }
    }
}
