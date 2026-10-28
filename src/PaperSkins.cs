namespace PaperTodo;

/// <summary>Saved surface preference, independent of the opaque semantic color palette.</summary>
public static class PaperSkins
{
    public const string Paper = "paper";
    public const string Mica = "mica";
    public const string Acrylic = "acrylic";
    public const string ClearAcrylic = "clearAcrylic";
    public const string TracingPaper = "tracingPaper";
    public const string Aero = "aero";
    public const string Pixel = "pixel";
    public const string Crystal = "crystal";
    public static readonly string[] All =
        { Paper, Mica, Acrylic, ClearAcrylic, TracingPaper, Aero, Pixel, Crystal };
    public static bool IsValid(string? id) => id is Paper or Mica or Acrylic or ClearAcrylic or
        TracingPaper or Aero or Pixel or Crystal;
    public static string Normalize(string? id) => IsValid(id) ? id! : Paper;
    // A missing field means legacy data; an explicit unknown ID means safe fallback.
    public static string Resolve(string? skin, string? colorScheme, string? oldBackdrop) =>
        skin != null ? Normalize(skin) : colorScheme == ColorSchemes.Mica
            ? MicaBackdropTypes.Normalize(oldBackdrop) : Paper;
    public static string Resolve(AppState? state) =>
        Resolve(state?.PaperSkin, state?.ColorScheme, state?.MicaBackdropType);
    public static bool UsesNativeBackdrop(string? id) => id is Mica or Acrylic or ClearAcrylic or
        TracingPaper or Aero or Crystal;
    internal static bool UsesSampledAuxiliary(string? id) => id is Mica or Acrylic or ClearAcrylic or TracingPaper;
    public static bool IsSystemMaterial(string? id) => id is Mica or Acrylic or ClearAcrylic;
    public static bool IsDecorated(string? id) => id is TracingPaper or Aero or Pixel or Crystal;
    public static bool Decorate(string? id, bool highContrast) => !highContrast && IsDecorated(id);
    // Tracing retains system Acrylic. Aero and Crystal use clear alpha without Acrylic blur.
    public static string NativeBackdrop(string? id) => id switch
    {
        Acrylic or TracingPaper => MicaBackdropTypes.Acrylic,
        Aero or Crystal => NativeMicaBackdrop.AeroGlassMaterial,
        ClearAcrylic => MicaBackdropTypes.ClearAcrylic,
        _ => MicaBackdropTypes.Mica
    };
    public static string LabelKey(string id) => id switch
    {
        Mica => "MicaBackdropMica", Acrylic => "MicaBackdropAcrylic", ClearAcrylic => "MicaBackdropClearAcrylic",
        TracingPaper => "SkinTracingPaper",
        Aero => "SkinAero", Pixel => "SkinPixel", Crystal => "SkinCrystal", _ => "SkinPaper"
    };
}
