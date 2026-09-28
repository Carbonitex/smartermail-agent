using System.Net;
using System.Net.Sockets;
using System.Text;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Core.Auth;

public class LoginServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly AuthenticationService _authService;
    private readonly TimeSpan _timeout;
    private readonly string _csrfToken;
    private bool _disposed;

    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/login/";
    public bool AuthenticationCompleted { get; private set; }

    public LoginServer(AuthenticationService authService, TimeSpan? timeout = null)
    {
        _authService = authService;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _csrfToken = Guid.NewGuid().ToString("N");
    }

    public string Start()
    {
        // Find an available port
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        Port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/login/");
        _listener.Start();

        _cts = new CancellationTokenSource(_timeout);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested && !AuthenticationCompleted)
                {
                    var contextTask = _listener.GetContextAsync();
                    var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, _cts.Token));
                    if (completed != contextTask) break;

                    var context = await contextTask;
                    await HandleRequestAsync(context);
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                Stop();
            }
        });

        return Url;
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "GET")
            {
                await WriteResponseAsync(context, GetLoginFormHtml());
            }
            else if (context.Request.HttpMethod == "POST")
            {
                await HandlePostAsync(context);
            }
            else
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LoginServer request error: {ex.Message}");
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandlePostAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var form = ParseFormData(body);

        // Validate CSRF token
        if (!form.TryGetValue("csrf", out var csrf) || csrf != _csrfToken)
        {
            await WriteResponseAsync(context, GetErrorHtml("Invalid form submission. Please try again."), 403);
            return;
        }

        form.TryGetValue("baseUrl", out var baseUrl);
        form.TryGetValue("username", out var username);
        form.TryGetValue("password", out var password);
        var readOnlyMode = form.ContainsKey("readOnlyMode");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            await WriteResponseAsync(context, GetErrorHtml("All fields are required."), 400);
            return;
        }

        var (success, message, tokenData) = await _authService.AuthenticateAsync(baseUrl, username, password, readOnlyMode);

        if (success && tokenData != null)
        {
            AuthenticationCompleted = true;
            await WriteResponseAsync(context, GetSuccessHtml(tokenData.Username ?? username, tokenData.BaseUrl ?? baseUrl));

            // Schedule shutdown after a brief delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                Stop();
            });
        }
        else
        {
            await WriteResponseAsync(context, GetErrorHtml(message ?? "Authentication failed."), 401);
        }
    }

    private static Dictionary<string, string> ParseFormData(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            else if (parts.Length == 1)
                result[Uri.UnescapeDataString(parts[0])] = "";
        }
        return result;
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, string html, int statusCode = 200)
    {
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Add("X-Frame-Options", "DENY");
        context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'; style-src 'unsafe-inline'");
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
    }

    private string GetLoginFormHtml()
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>SmarterMail Login</title>
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f0f2f5; display: flex; justify-content: center; align-items: center; min-height: 100vh; }}
  .card {{ background: #fff; border-radius: 12px; box-shadow: 0 2px 16px rgba(0,0,0,0.1); padding: 40px; width: 100%; max-width: 420px; }}
  h1 {{ font-size: 24px; color: #1a1a1a; margin-bottom: 8px; }}
  .subtitle {{ color: #666; font-size: 14px; margin-bottom: 28px; }}
  label {{ display: block; font-size: 14px; font-weight: 500; color: #333; margin-bottom: 6px; }}
  input[type=""url""], input[type=""text""], input[type=""password""] {{ width: 100%; padding: 10px 12px; border: 1px solid #d0d0d0; border-radius: 8px; font-size: 15px; margin-bottom: 18px; transition: border-color 0.2s; }}
  input[type=""url""]:focus, input[type=""text""]:focus, input[type=""password""]:focus {{ outline: none; border-color: #4a90d9; box-shadow: 0 0 0 3px rgba(74,144,217,0.15); }}
  .checkbox-row {{ display: flex; align-items: center; gap: 8px; margin-bottom: 24px; }}
  .checkbox-row input {{ width: 18px; height: 18px; }}
  .checkbox-row label {{ margin-bottom: 0; font-weight: 400; }}
  button {{ width: 100%; padding: 12px; background: #4a90d9; color: #fff; border: none; border-radius: 8px; font-size: 16px; font-weight: 600; cursor: pointer; transition: background 0.2s; }}
  button:hover {{ background: #3a7bc8; }}
  button:active {{ background: #2d6ab0; }}
  .note {{ margin-top: 20px; font-size: 12px; color: #888; text-align: center; line-height: 1.5; }}
</style>
</head>
<body>
<div class=""card"">
  <h1>SmarterMail Login</h1>
  <p class=""subtitle"">Enter your credentials to connect.</p>
  <form method=""POST"" action=""/login/"">
    <input type=""hidden"" name=""csrf"" value=""{_csrfToken}"">
    <label for=""baseUrl"">Server URL</label>
    <input type=""url"" id=""baseUrl"" name=""baseUrl"" placeholder=""https://mail.example.com"" required>
    <label for=""username"">Username</label>
    <input type=""text"" id=""username"" name=""username"" placeholder=""user@example.com"" required>
    <label for=""password"">Password</label>
    <input type=""password"" id=""password"" name=""password"" required>
    <div class=""checkbox-row"">
      <input type=""checkbox"" id=""readOnlyMode"" name=""readOnlyMode"">
      <label for=""readOnlyMode"">Read-only mode (prevents modifications)</label>
    </div>
    <button type=""submit"">Log In</button>
  </form>
  <p class=""note"">This form runs locally on your machine. Credentials are sent directly to your SmarterMail server.</p>
</div>
</body>
</html>";
    }

    private string GetSuccessHtml(string username, string baseUrl)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Login Successful</title>
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f0f2f5; display: flex; justify-content: center; align-items: center; min-height: 100vh; }}
  .card {{ background: #fff; border-radius: 12px; box-shadow: 0 2px 16px rgba(0,0,0,0.1); padding: 40px; width: 100%; max-width: 420px; text-align: center; }}
  .icon {{ font-size: 48px; margin-bottom: 16px; }}
  h1 {{ font-size: 24px; color: #1a1a1a; margin-bottom: 12px; }}
  .info {{ color: #666; font-size: 14px; margin-bottom: 8px; }}
  .note {{ margin-top: 20px; font-size: 13px; color: #888; }}
</style>
</head>
<body>
<div class=""card"">
  <div class=""icon"">&#10003;</div>
  <h1>Authentication Successful</h1>
  <p class=""info"">Connected as <strong>{System.Net.WebUtility.HtmlEncode(username)}</strong></p>
  <p class=""info"">Server: {System.Net.WebUtility.HtmlEncode(baseUrl)}</p>
  <p class=""note"">You can close this tab and return to your conversation.</p>
</div>
</body>
</html>";
    }

    private string GetErrorHtml(string errorMessage)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Login Failed</title>
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f0f2f5; display: flex; justify-content: center; align-items: center; min-height: 100vh; }}
  .card {{ background: #fff; border-radius: 12px; box-shadow: 0 2px 16px rgba(0,0,0,0.1); padding: 40px; width: 100%; max-width: 420px; text-align: center; }}
  .icon {{ font-size: 48px; margin-bottom: 16px; }}
  h1 {{ font-size: 24px; color: #1a1a1a; margin-bottom: 12px; }}
  .error {{ color: #c0392b; font-size: 14px; margin-bottom: 20px; background: #fdf0ef; padding: 12px; border-radius: 8px; }}
  a {{ display: inline-block; padding: 10px 24px; background: #4a90d9; color: #fff; text-decoration: none; border-radius: 8px; font-weight: 600; transition: background 0.2s; }}
  a:hover {{ background: #3a7bc8; }}
</style>
</head>
<body>
<div class=""card"">
  <div class=""icon"">&#10007;</div>
  <h1>Login Failed</h1>
  <p class=""error"">{System.Net.WebUtility.HtmlEncode(errorMessage)}</p>
  <a href=""/login/"">Try Again</a>
</div>
</body>
</html>";
    }

    public void Stop()
    {
        if (_disposed) return;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
