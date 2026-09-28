namespace SmarterMailMcp.Core;

public static class Logging
{
    public static bool Enabled { get; set; } = false;
    
    public static void Log(string message)
    {
        if (!Enabled)
        {
            // mcp stdio says to log errors to stderr
            Console.Error.WriteLine($"[INFO] {DateTime.UtcNow}: {message}");
            return;
        }
        Console.WriteLine($"[INFO] {DateTime.UtcNow}: {message}");
    }

    public static void LogError(string message)
    {
        Console.Error.WriteLine($"[ERROR] {DateTime.UtcNow}: {message}");
    }
    
    public static void LogException(Exception ex)
    {
        LogError(ex.ToString());
    }
}
