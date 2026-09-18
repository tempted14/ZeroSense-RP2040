using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: OcrProbe <loadout-screenshot>");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var file = await StorageFile.GetFileFromPathAsync(path);
using var stream = await file.OpenAsync(FileAccessMode.Read);
var decoder = await BitmapDecoder.CreateAsync(stream);
var engine = OcrEngine.TryCreateFromUserProfileLanguages()
    ?? throw new InvalidOperationException("Windows OCR is unavailable.");

var primary = await RecognizeRegionAsync(decoder, engine, 2, 38, 20, 11);
stream.Seek(0);
decoder = await BitmapDecoder.CreateAsync(stream);
var secondary = await RecognizeRegionAsync(decoder, engine, 2, 49, 20, 11);
Console.WriteLine($"PRIMARY: {primary.ReplaceLineEndings(" | ")}");
Console.WriteLine($"SECONDARY: {secondary.ReplaceLineEndings(" | ")}");
static string Compact(string value) => new(
    value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

return Compact(primary).Contains("R4C", StringComparison.Ordinal) &&
       Compact(secondary).Contains("57USG", StringComparison.Ordinal)
    ? 0
    : 1;

static async Task<string> RecognizeRegionAsync(
    BitmapDecoder decoder,
    OcrEngine engine,
    double xPercent,
    double yPercent,
    double widthPercent,
    double heightPercent)
{
    var transform = new BitmapTransform
    {
        Bounds = new BitmapBounds
        {
            X = (uint)Math.Round(decoder.PixelWidth * xPercent / 100.0),
            Y = (uint)Math.Round(decoder.PixelHeight * yPercent / 100.0),
            Width = (uint)Math.Round(decoder.PixelWidth * widthPercent / 100.0),
            Height = (uint)Math.Round(decoder.PixelHeight * heightPercent / 100.0)
        }
    };
    using var bitmap = await decoder.GetSoftwareBitmapAsync(
        BitmapPixelFormat.Bgra8,
        BitmapAlphaMode.Ignore,
        transform,
        ExifOrientationMode.IgnoreExifOrientation,
        ColorManagementMode.DoNotColorManage);
    return (await engine.RecognizeAsync(bitmap)).Text?.Trim() ?? string.Empty;
}
