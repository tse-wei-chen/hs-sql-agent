using System.Text;

namespace ToolBox.Middleware;

/// <summary>
/// A TextWriter that intercepts JSON-RPC messages from stdout and flattens them using McpResponseUtils.
/// </summary>
public class StdioMcpResponseFlattener(TextWriter originalOut) : TextWriter
{
    private readonly TextWriter _originalOut = originalOut;
    private readonly StringBuilder _buffer = new();

    public override Encoding Encoding => _originalOut.Encoding;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            ProcessAndFlushBuffer();
        }
        else
        {
            _buffer.Append(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        Write(new string(buffer, index, count));
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        int lastNewline = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\n')
            {
                string part = value.Substring(lastNewline + 1, i - lastNewline - 1);
                _buffer.Append(part);
                ProcessAndFlushBuffer();
                lastNewline = i;
            }
        }

        if (lastNewline < value.Length - 1)
        {
            _buffer.Append(value.Substring(lastNewline + 1));
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    private void ProcessAndFlushBuffer()
    {
        var line = _buffer.ToString();
        _buffer.Clear();

        if (line.TrimStart().StartsWith("{"))
        {
            var processed = McpResponseUtils.ProcessMcpResponse(line);
            _originalOut.WriteLine(processed);
        }
        else
        {
            _originalOut.WriteLine(line);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_buffer.Length > 0)
            {
                _originalOut.Write(_buffer.ToString());
                _buffer.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
