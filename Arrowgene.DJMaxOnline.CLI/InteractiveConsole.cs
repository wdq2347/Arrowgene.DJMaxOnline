using System.Text;

namespace Arrowgene.DJMaxOnline;

/// <summary>
/// Keeps asynchronous server log output from destroying the line currently being edited.
/// Console.ReadLine owns the terminal's editing buffer, so it cannot be redrawn after a
/// background write. This small editor keeps that buffer in managed code instead.
/// </summary>
internal static class InteractiveConsole
{
    private const int MaximumCommandLength = 4096;
    private static readonly object Sync = new();
    private static readonly StringBuilder Input = new();

    private static bool _reading;
    private static bool _secret;
    private static string _prompt = string.Empty;
    private static int _cursor;
    private static int _originLeft;
    private static int _originTop;
    private static int _renderedRows;

    public static string? ReadCommand(string prompt)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        return ReadInteractive(prompt, secret: false, MaximumCommandLength);
    }

    public static string ReadSecret(string prompt, int maximumLength)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Secret input requires an interactive console.");
        }
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        // A redirected output stream has no cursor to redraw, but ReadKey still keeps
        // the password out of that stream.
        if (Console.IsOutputRedirected)
        {
            Console.Write(prompt);
            return ReadSecretWithoutRedraw(maximumLength);
        }

        return ReadInteractive(prompt, secret: true, maximumLength) ?? string.Empty;
    }

    /// <summary>Writes an asynchronous log line without losing active console input.</summary>
    public static void WriteLogLine(string value)
    {
        lock (Sync)
        {
            if (!_reading || Console.IsOutputRedirected)
            {
                Console.WriteLine(value);
                return;
            }

            try
            {
                ClearRenderedInput();
                SetCursor(_originLeft, _originTop);
                Console.WriteLine(value);
                _originLeft = Console.CursorLeft;
                _originTop = Console.CursorTop;
                RenderInput();
            }
            catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
            {
                // This runs on the logger's write thread, so an escaping console error
                // takes the whole server down. Nothing here is worth that: give up on
                // redrawing the prompt, print the line plainly and let the next keypress
                // re-anchor the editor.
                _renderedRows = 0;
                _originLeft = 0;
                _originTop = 0;
                Console.WriteLine(value);
            }
        }
    }

    /// <summary>
    /// Moves the cursor, clamped to the buffer. The buffer scrolls under us whenever a log
    /// line lands on the last row, and it shrinks when the window is resized, so a row
    /// captured a moment ago can already be out of range.
    /// </summary>
    private static void SetCursor(int left, int top)
    {
        int width = Math.Max(1, Console.BufferWidth);
        int height = Math.Max(1, Console.BufferHeight);
        Console.SetCursorPosition(
            Math.Clamp(left, 0, width - 1),
            Math.Clamp(top, 0, height - 1));
    }

    private static string? ReadInteractive(
        string prompt, bool secret, int maximumLength)
    {
        lock (Sync)
        {
            if (_reading)
            {
                throw new InvalidOperationException("Console input is already active.");
            }

            _reading = true;
            _secret = secret;
            _prompt = prompt;
            _cursor = 0;
            Input.Clear();
            _originLeft = Console.CursorLeft;
            _originTop = Console.CursorTop;
            RenderInput();
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                lock (Sync)
                {
                    FinishInputLine();
                }
                return null;
            }

            lock (Sync)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    string result = Input.ToString();
                    FinishInputLine();
                    return result;
                }
                if (key.Key == ConsoleKey.Escape)
                {
                    if (_secret)
                    {
                        FinishInputLine();
                        throw new OperationCanceledException("Password entry cancelled.");
                    }

                    Input.Clear();
                    _cursor = 0;
                    RenderInput();
                    continue;
                }

                EditInput(key, maximumLength);
                if (!_secret)
                {
                    RenderInput();
                }
            }
        }
    }

    private static void EditInput(ConsoleKeyInfo key, int maximumLength)
    {
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                _cursor = Math.Max(0, _cursor - 1);
                return;
            case ConsoleKey.RightArrow:
                _cursor = Math.Min(Input.Length, _cursor + 1);
                return;
            case ConsoleKey.Home:
                _cursor = 0;
                return;
            case ConsoleKey.End:
                _cursor = Input.Length;
                return;
            case ConsoleKey.Backspace when _cursor > 0:
                Input.Remove(--_cursor, 1);
                return;
            case ConsoleKey.Delete when _cursor < Input.Length:
                Input.Remove(_cursor, 1);
                return;
        }

        if (!char.IsControl(key.KeyChar) && Input.Length < maximumLength)
        {
            Input.Insert(_cursor++, key.KeyChar);
        }
    }

    private static void RenderInput()
    {
        ClearRenderedInput();
        SetCursor(_originLeft, _originTop);
        Console.Write(_prompt);
        if (!_secret)
        {
            Console.Write(Input.ToString());
        }

        int width = Math.Max(1, Console.BufferWidth);
        int visibleLength = _prompt.Length + (_secret ? 0 : Input.Length);
        int rowsBelowOrigin = (_originLeft + visibleLength) / width;

        // Writing at the bottom of the buffer scrolls it, which moves the prompt up a row
        // and leaves the row we captured pointing past the end. Re-derive the origin from
        // where the cursor actually ended up rather than trusting the stored row.
        _originTop = Math.Max(0, Console.CursorTop - rowsBelowOrigin);
        _renderedRows = rowsBelowOrigin + 1;

        int visibleCursor = _prompt.Length + (_secret ? 0 : _cursor);
        int absoluteCursor = _originLeft + visibleCursor;
        SetCursor(absoluteCursor % width, _originTop + absoluteCursor / width);
    }

    private static void ClearRenderedInput()
    {
        if (_renderedRows <= 0)
        {
            return;
        }

        int width = Math.Max(1, Console.BufferWidth);
        int height = Math.Max(1, Console.BufferHeight);
        for (int row = 0; row < _renderedRows; row++)
        {
            int top = _originTop + row;
            if (top >= height)
            {
                // The buffer scrolled or was resized out from under the stored origin.
                break;
            }

            SetCursor(0, top);
            if (width > 1)
            {
                Console.Write(new string(' ', width - 1));
            }
            SetCursor(width - 1, top);
            Console.Write(' ');
        }
    }

    private static void FinishInputLine()
    {
        int width = Math.Max(1, Console.BufferWidth);
        int visibleLength = _prompt.Length + (_secret ? 0 : Input.Length);
        int absoluteEnd = _originLeft + visibleLength;
        SetCursor(absoluteEnd % width, _originTop + absoluteEnd / width);
        Console.WriteLine();

        _reading = false;
        _secret = false;
        _prompt = string.Empty;
        _cursor = 0;
        _renderedRows = 0;
        Input.Clear();
    }

    private static string ReadSecretWithoutRedraw(int maximumLength)
    {
        StringBuilder value = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                throw new OperationCanceledException("Password entry cancelled.");
            }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            {
                value.Length--;
            }
            else if (!char.IsControl(key.KeyChar) && value.Length < maximumLength)
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
