namespace Conference.Server;

/// <summary>Writes events to the console and to logs/server-yyyyMMdd.log.</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"server-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch { /* logging must never crash the server */ }
        }
    }
}
