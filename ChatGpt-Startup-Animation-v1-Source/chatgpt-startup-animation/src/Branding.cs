using System.Reflection;

[assembly: AssemblyTitle(Branding.ProductName)]
[assembly: AssemblyProduct(Branding.ProductName)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Branding
{
    public const string ProductName = "ChatGpt Startup Animation v1";
    public const string LauncherFile = "ChatGpt Startup Animation.exe";
    public const string DefaultWelcomeTitle = "启程";
    public const string UndoTitle = "ChatGpt Startup Animation · 撤销片头";
    // Keep the existing storage locations so earlier shortcuts remain restorable.
    public const string ProfileFolder = "NianAnIntro";
}
