using System.Text.Json;

namespace SmarterMailMcp.Core.Models;

public class GlobalContext
{
    public string TokenFilePath { get; }
    public bool ReadOnlyModeEnforced { get; set; } = true;

    /// <summary>
    /// Whether tools may read and write this process's filesystem (<c>upload_attachment</c>'s
    /// <c>filePath</c>, <c>download_email_attachment</c>'s <c>savePath</c>). Off unless the host turns it
    /// on: only meaningful when the caller and the server share a filesystem (a local stdio server),
    /// and on a remote server it would hand the server's files to whoever calls the tools.
    /// </summary>
    public bool LocalFileAccess { get; set; }

    /// <summary>
    /// Server-side constructor: provide token file path and read-only mode directly.
    /// </summary>
    public GlobalContext(string tokenFilePath, bool readOnlyMode = false)
    {
        TokenFilePath = tokenFilePath;
        ReadOnlyModeEnforced = readOnlyMode;
        var directoryName = Path.GetDirectoryName(TokenFilePath);
        if (!string.IsNullOrEmpty(directoryName))
            Directory.CreateDirectory(directoryName);
    }

    public GlobalContext(string[] args)
    {
        if (args.Length > 0)
        {
            ReadOnlyModeEnforced = args.Contains("--read-only") || args.Contains("--readonly");
            
            // Handle --token-file=path or --token-file path
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--token-file="))
                {
                    TokenFilePath = args[i].Split('=')[1].Trim();
                    break;
                }
                if (args[i] == "--token-file" && i + 1 < args.Length)
                {
                    TokenFilePath = args[i + 1].Trim();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(TokenFilePath))
            {
                TokenFilePath = TokenFilePath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var directoryName = Path.GetDirectoryName(TokenFilePath);
                if (!string.IsNullOrEmpty(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
            }
            else if (args.Length == 1 && args[0].EndsWith(".json"))
            {
                TokenFilePath = args[0];
            }
        }

        if (string.IsNullOrWhiteSpace(TokenFilePath))
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TokenFilePath = Path.Combine(homeDir, "smartermail_token.json");
        }
    }

    public TokenData? ReadTokenFile()
    {
        try
        {
            if (!File.Exists(TokenFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(TokenFilePath);
            var tokenData = JsonSerializer.Deserialize<TokenData>(json);
            if (tokenData == null)
                return null;
            if (ReadOnlyModeEnforced)
                tokenData.ReadOnlyMode = true;
            return tokenData;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading token file: {ex.Message}");
            return null;
        }
    }

    public bool WriteTokenFile(TokenData tokenData)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            var json = JsonSerializer.Serialize(tokenData, options);
            File.WriteAllText(TokenFilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error writing token file: {ex.Message}");
            return false;
        }
    }

    public bool DeleteTokenFile()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                File.Delete(TokenFilePath);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error deleting token file: {ex.Message}");
            return false;
        }
    }
}
