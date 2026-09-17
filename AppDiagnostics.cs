using System.Text;

namespace NeteaseLyricsOverlay;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeteaseLyricsOverlay",
        "diagnostic.log");

    public static void Write(string area, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{area}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public static void Write(string area, Exception exception) =>
        Write(area, $"{exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
}
