using System.Text;

namespace Lucy.Core;

/// <summary>
/// Turns a console byte stream (already decoded) into display lines, the way a terminal would:
///   • "\n", "\r\n" and "\r\r\n" all end a line. Python/colcon re-printing captured Windows output
///     produces "\r\r\n"; treating that inner "\r" as a redraw used to wipe every such line.
///   • A "\r" followed by text is a progress redraw (git, pip, yarn): the line restarts. The
///     decision waits for the next character, so a redraw split across two reads works too.
/// </summary>
public sealed class LineSplitter
{
    private readonly StringBuilder _line = new();
    private readonly Action<string> _emit;
    private bool _pendingCr;

    public LineSplitter(Action<string> emit) => _emit = emit;

    public void Feed(ReadOnlySpan<char> chunk)
    {
        foreach (var c in chunk)
        {
            switch (c)
            {
                case '\r':
                    _pendingCr = true;
                    break;
                case '\n':
                    _pendingCr = false;
                    Flush();
                    break;
                default:
                    if (_pendingCr)
                    {
                        _line.Clear(); // redraw: the newest segment replaces the old one
                        _pendingCr = false;
                    }
                    _line.Append(c);
                    break;
            }
        }
    }

    /// <summary>End of stream: emit what is left.</summary>
    public void Complete()
    {
        if (_line.Length > 0) Flush();
        _pendingCr = false;
    }

    private void Flush()
    {
        _emit(_line.ToString());
        _line.Clear();
    }
}
