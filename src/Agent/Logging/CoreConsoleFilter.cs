using System.Text;

namespace SmarterMailAgent.Logging;

/// <summary>
/// smartermail-mcp-core logs straight to Console.Out/Console.Error from inside UserContext
/// (folder ids, owner email addresses, and whole SmarterMail error bodies). On a public multi-user
/// service those lines are other people's mail metadata, so they are dropped before they reach the
/// container log. Set <c>CORE_CONSOLE_LOG=true</c> to let them through while debugging.
///
/// Only Core's own prefixes are filtered; ASP.NET's logger output passes through untouched.
/// </summary>
public static class CoreConsoleFilter
{
    private static readonly string[] DroppedPrefixes = ["[INFO] ", "[ERROR] ", "[DB] "];

    public static void Install()
    {
        if (Environment.GetEnvironmentVariable("CORE_CONSOLE_LOG")?
                .Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            return;

        Console.SetOut(new LineFilteringWriter(Console.Out));
        Console.SetError(new LineFilteringWriter(Console.Error));
    }

    private sealed class LineFilteringWriter(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    Flush(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        private void Flush(string line)
        {
            foreach (var prefix in DroppedPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return;
            }

            inner.WriteLine(line);
        }
    }
}
