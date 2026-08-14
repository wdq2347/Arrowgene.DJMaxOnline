using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Arrowgene.DJMaxOnline.Launcher;

/// <summary>
/// A flat, dark, modern skin: soft cards on a near-black ground, one blue accent, rounded
/// corners and Segoe UI. Everything is owner-drawn with GDI+, so the launcher stays a
/// single self-contained executable with no image assets.
/// </summary>
internal static class ModernTheme
{
    internal static readonly Color Ground = Color.FromArgb(0x14, 0x16, 0x1B);
    internal static readonly Color Card = Color.FromArgb(0x1C, 0x1F, 0x26);
    internal static readonly Color Well = Color.FromArgb(0x15, 0x18, 0x1E);
    internal static readonly Color Line = Color.FromArgb(0x2B, 0x30, 0x3A);
    internal static readonly Color Text = Color.FromArgb(0xE7, 0xEA, 0xEF);
    internal static readonly Color TextDim = Color.FromArgb(0x8A, 0x93, 0xA1);
    internal static readonly Color TextFaint = Color.FromArgb(0x5E, 0x66, 0x73);
    internal static readonly Color Accent = Color.FromArgb(0x3B, 0x9E, 0xF5);
    internal static readonly Color AccentDim = Color.FromArgb(0x2B, 0x74, 0xB8);
    internal static readonly Color Ok = Color.FromArgb(0x3F, 0xC8, 0x82);
    internal static readonly Color Alert = Color.FromArgb(0xF0, 0x6A, 0x6A);

    internal static Font Ui(float size = 9F, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        GraphicsPath path = new();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void FillRounded(Graphics g, RectangleF bounds, float radius, Color fill)
    {
        using GraphicsPath path = Rounded(bounds, radius);
        using SolidBrush brush = new(fill);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// Fills and outlines in one call - the shape every surface here uses. Fully
    /// transparent colours are skipped rather than drawn: asking GDI+ to stroke a path
    /// with alpha 0 still lays down a faint antialiased edge, which is what made the
    /// borderless buttons look like they had a stray outline along the top.
    /// </summary>
    internal static void Surface(
        Graphics g, RectangleF bounds, float radius, Color fill, Color border, float width = 1F)
    {
        if (fill.A == 0 && border.A == 0)
        {
            return;
        }

        using GraphicsPath path = Rounded(bounds, radius);
        if (fill.A > 0)
        {
            using SolidBrush brush = new(fill);
            g.FillPath(brush, path);
        }
        if (border.A > 0)
        {
            using Pen pen = new(border, width);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>A small upper-case section heading, standing in for a group-box caption.</summary>
    internal static void SectionTitle(Graphics g, string text, Point origin)
    {
        using Font font = Ui(7.5F, FontStyle.Bold);
        using SolidBrush brush = new(TextFaint);
        using StringFormat format = new(StringFormat.GenericTypographic);
        g.DrawString(text.ToUpperInvariant(), font, brush, origin, format);
    }

    internal static void Label(Graphics g, string text, Rectangle bounds)
    {
        using Font font = Ui(8.5F);
        TextRenderer.DrawText(g, text, font, bounds, TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Anti-aliased shapes and grid-fit text, applied the same way everywhere.</summary>
    internal static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>
    /// Paints a control's opaque backdrop with anti-aliasing off.
    ///
    /// This has to happen before <see cref="Smooth"/>: with SmoothingMode.AntiAlias an
    /// axis-aligned FillRectangle has its boundary pixels anti-aliased too, so the first
    /// row and column come out around 70% opaque and let the surface behind bleed through.
    /// That soft edge reads as a faint outline around every button and card.
    /// </summary>
    internal static void Clear(Graphics g, Rectangle bounds, Color color)
    {
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using SolidBrush brush = new(color);
        g.FillRectangle(brush, bounds);
        g.SmoothingMode = previous;
    }
}

// ------------------------------------------------------------------------- controls

internal enum ButtonKind
{
    Accent,
    Neutral,
    Ghost
}

/// <summary>A flat rounded button with hover and press states.</summary>
internal sealed class ModernButton : Button
{
    private bool _hot;
    private bool _down;

    internal ModernButton(ButtonKind kind = ButtonKind.Neutral)
    {
        Kind = kind;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        // An opaque backdrop, not Color.Transparent: the rounded shape does not cover the
        // corners of its own rectangle, so without a real fill every repaint composites
        // over the last one and stale text and fills pile up.
        BackColor = ModernTheme.Ground;
        ForeColor = ModernTheme.Text;
        Font = ModernTheme.Ui(9F, FontStyle.Bold);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    internal ButtonKind Kind { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        ModernTheme.Clear(g, ClientRectangle, BackColor);
        ModernTheme.Smooth(g);

        RectangleF bounds = new(0.5F, 0.5F, Width - 1, Height - 1);
        (Color fill, Color border, Color ink) = Resolve();
        ModernTheme.Surface(g, bounds, 6F, fill, border);

        Rectangle text = ClientRectangle;
        if (_down)
        {
            text.Offset(0, 1);
        }
        TextRenderer.DrawText(g, Text, Font, text, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private (Color Fill, Color Border, Color Ink) Resolve()
    {
        if (!Enabled)
        {
            return (Color.FromArgb(0x20, 0x24, 0x2C), ModernTheme.Line, ModernTheme.TextFaint);
        }

        return Kind switch
        {
            ButtonKind.Accent => (
                _down ? ModernTheme.AccentDim
                    : _hot ? Lighten(ModernTheme.Accent, 0.12F) : ModernTheme.Accent,
                Color.FromArgb(0x60, 0xB8, 0xFF), Color.White),
            ButtonKind.Neutral => (
                _down ? Color.FromArgb(0x22, 0x27, 0x30)
                    : _hot ? Color.FromArgb(0x2C, 0x32, 0x3D) : Color.FromArgb(0x25, 0x2A, 0x34),
                ModernTheme.Line, ModernTheme.Text),
            // Ghost: nothing at rest, a soft fill on hover. No border - against a fill
            // this close in tone it reads as an artefact rather than an edge.
            _ => (
                _hot ? Color.FromArgb(0x26, 0x2B, 0x35) : Color.Transparent,
                Color.Transparent,
                _hot ? ModernTheme.Text : ModernTheme.TextDim)
        };
    }

    private static Color Lighten(Color color, float amount) => Color.FromArgb(
        color.A,
        (int)Math.Min(255, color.R + 255 * amount),
        (int)Math.Min(255, color.G + 255 * amount),
        (int)Math.Min(255, color.B + 255 * amount));
}

/// <summary>A rounded dark well hosting a real TextBox, with an accent ring on focus.</summary>
internal sealed class ModernField : Panel
{
    private bool _focused;

    internal ModernField(bool password = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = ModernTheme.Well;

        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            BackColor = ModernTheme.Well,
            ForeColor = ModernTheme.Text,
            Font = ModernTheme.Ui(9F),
            UseSystemPasswordChar = password
        };
        Box.GotFocus += (_, _) =>
        {
            _focused = true;
            Invalidate();
        };
        Box.LostFocus += (_, _) =>
        {
            _focused = false;
            Invalidate();
        };
        Box.EnabledChanged += (_, _) =>
        {
            Box.BackColor = Box.Enabled ? ModernTheme.Well : ModernTheme.Card;
            Invalidate();
        };
        Controls.Add(Box);
        Click += (_, _) => Box.Focus();
    }

    internal TextBox Box { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        ModernTheme.Clear(g, ClientRectangle, ModernTheme.Card);
        ModernTheme.Smooth(g);
        ModernTheme.Surface(g, new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 6F,
            Box.Enabled ? ModernTheme.Well : ModernTheme.Card,
            _focused ? ModernTheme.Accent : ModernTheme.Line);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Centre the single-line box in the well rather than stretching it.
        int inset = Math.Max(2, (Height - Box.PreferredHeight) / 2);
        Padding = new Padding(10, inset, 10, inset);
    }
}

/// <summary>A rounded check box with an accent fill and a drawn tick.</summary>
internal sealed class ModernCheck : CheckBox
{
    internal ModernCheck()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.TextDim;
        Font = ModernTheme.Ui(8.5F);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        ModernTheme.Clear(g, ClientRectangle, BackColor);
        ModernTheme.Smooth(g);

        RectangleF box = new(0.5F, (Height - 15) / 2F, 15, 15);
        ModernTheme.Surface(g, box, 4F,
            Checked ? ModernTheme.Accent : ModernTheme.Well,
            Checked ? ModernTheme.Accent : ModernTheme.Line);

        if (Checked)
        {
            using Pen tick = new(Color.White, 1.8F)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            g.DrawLines(tick, new[]
            {
                new PointF(box.Left + 3.5F, box.Top + 7.5F),
                new PointF(box.Left + 6.2F, box.Top + 10.4F),
                new PointF(box.Left + 11.3F, box.Top + 4.6F)
            });
        }

        Rectangle text = new((int)box.Right + 8, 0, Width - (int)box.Right - 8, Height);
        TextRenderer.DrawText(g, Text, Font, text,
            Enabled ? ForeColor : ModernTheme.TextFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// A thin rounded progress bar. Runs indeterminate while a check is outstanding and the
/// total is not yet known.
/// </summary>
internal sealed class ModernProgress : Control
{
    private int _sweep;

    internal ModernProgress()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = ModernTheme.Ground;
    }

    /// <summary>0..1. Ignored while <see cref="Marquee"/> is set.</summary>
    internal float Value { get; set; }

    internal bool Marquee { get; set; }

    internal Color Fill { get; set; } = ModernTheme.Accent;

    internal void Step()
    {
        if (!Marquee)
        {
            return;
        }

        _sweep = (_sweep + 4) % (Math.Max(1, Width) + 120);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        ModernTheme.Clear(g, ClientRectangle, BackColor);
        ModernTheme.Smooth(g);

        float radius = Height / 2F;
        RectangleF track = new(0, 0, Width, Height);
        ModernTheme.FillRounded(g, track, radius, ModernTheme.Line);

        // Clipping to the track keeps the moving pill's ends correctly rounded.
        using GraphicsPath clip = ModernTheme.Rounded(track, radius);
        Region previous = g.Clip;
        g.SetClip(clip, CombineMode.Intersect);

        if (Marquee)
        {
            ModernTheme.FillRounded(g, new RectangleF(_sweep - 120, 0, 120, Height), radius, Fill);
        }
        else
        {
            float width = Width * Math.Clamp(Value, 0F, 1F);
            if (width > 0.5F)
            {
                ModernTheme.FillRounded(g, new RectangleF(0, 0, width, Height), radius, Fill);
            }
        }

        g.Clip = previous;
    }
}

/// <summary>
/// The news list: an accent rule per item, a bright headline with a dim date and a short
/// body, scrolled with a real scrollbar when the list overflows.
/// </summary>
internal sealed class NewsPanel : Panel
{
    internal sealed record Entry(string Headline, string Date, string Body);

    private const int TrackWidth = 10;

    private readonly List<Entry> _entries = [];
    private int _content;
    private int _offset;
    private bool _dragging;
    private int _grabOffset;

    internal NewsPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = ModernTheme.Card;
    }

    // A drawn scrollbar rather than a VScrollBar: the shell's bar is painted in the
    // system's light colours and reads as a white stripe down a dark panel.
    private bool Scrollable => _content > ClientSize.Height;

    private int MaxOffset => Math.Max(0, _content - ClientSize.Height);

    private int TextWidth =>
        Math.Max(40, ClientSize.Width - (Scrollable ? TrackWidth : 0) - 16);

    internal void SetEntries(IEnumerable<Entry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        UpdateScrollRange();
        Invalidate();
    }

    private int MeasureContent()
    {
        using Font headline = ModernTheme.Ui(9F, FontStyle.Bold);
        using Font body = ModernTheme.Ui(8.5F);
        using Graphics g = CreateGraphics();

        int height = 2;
        foreach (Entry entry in _entries)
        {
            height += headline.Height + 2;
            height += (int)g.MeasureString(entry.Body, body, TextWidth).Height + 16;
        }
        return height;
    }

    private void UpdateScrollRange()
    {
        // The text column narrows once the bar appears, which can make the content taller
        // still - so measure once to decide whether it is needed, then again for real.
        _content = MeasureContent();
        _content = MeasureContent();
        _offset = Math.Clamp(_offset, 0, MaxOffset);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollRange();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Scrollable)
        {
            _offset = Math.Clamp(_offset - e.Delta / 3, 0, MaxOffset);
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    private Rectangle Thumb()
    {
        int height = Math.Max(28, ClientSize.Height * ClientSize.Height / Math.Max(1, _content));
        int travel = ClientSize.Height - height;
        int y = MaxOffset == 0 ? 0 : travel * _offset / MaxOffset;
        return new Rectangle(ClientSize.Width - TrackWidth + 3, y, 4, height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Scrollable && e.Button == MouseButtons.Left &&
            e.X >= ClientSize.Width - TrackWidth)
        {
            Rectangle thumb = Thumb();
            _dragging = true;
            // Grabbing the track itself centres the thumb on the pointer.
            _grabOffset = thumb.Contains(e.Location) ? e.Y - thumb.Y : thumb.Height / 2;
            DragTo(e.Y);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            DragTo(e.Y);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    private void DragTo(int y)
    {
        int travel = ClientSize.Height - Thumb().Height;
        _offset = travel <= 0
            ? 0
            : Math.Clamp((y - _grabOffset) * MaxOffset / travel, 0, MaxOffset);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        ModernTheme.Clear(g, ClientRectangle, ModernTheme.Card);
        ModernTheme.Smooth(g);

        using Font headline = ModernTheme.Ui(9F, FontStyle.Bold);
        using Font date = ModernTheme.Ui(7.5F);
        using Font body = ModernTheme.Ui(8.5F);
        using SolidBrush headInk = new(ModernTheme.Text);
        using SolidBrush dateInk = new(ModernTheme.TextFaint);
        using SolidBrush bodyInk = new(ModernTheme.TextDim);
        using SolidBrush rule = new(ModernTheme.Accent);

        int width = TextWidth;
        float y = 2 - _offset;
        foreach (Entry entry in _entries)
        {
            float bodyHeight = g.MeasureString(entry.Body, body, width).Height;
            // A short accent rule down the left marks each item.
            g.FillRectangle(rule, 0, y + 3, 2, headline.Height + bodyHeight - 2);

            g.DrawString(entry.Headline, headline, headInk, 10, y);
            float indent = 10 + g.MeasureString(entry.Headline, headline).Width;
            g.DrawString(entry.Date, date, dateInk, indent, y + 4);
            y += headline.Height + 2;

            g.DrawString(entry.Body, body, bodyInk, new RectangleF(10, y, width, bodyHeight + 4));
            y += bodyHeight + 16;
        }

        if (!Scrollable)
        {
            return;
        }

        Rectangle thumb = Thumb();
        ModernTheme.FillRounded(g,
            new RectangleF(thumb.X, 0, thumb.Width, ClientSize.Height), thumb.Width / 2F,
            ModernTheme.Well);
        ModernTheme.FillRounded(g, thumb, thumb.Width / 2F, ModernTheme.Line);
    }
}
