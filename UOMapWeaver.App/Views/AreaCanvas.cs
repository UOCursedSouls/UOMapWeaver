// SPDX-License-Identifier: GPL-3.0-only
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UOMapWeaver.Core.WorldFormats;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.App.Views;

/// <summary>Draws the areas of a world and lets the user drag new, moved or resized rectangles.</summary>
public sealed class AreaCanvas : Control
{
    private enum Drag { None, Create, Move, Resize }

    private const int Handle = 8;
    private WorldAreaEditor? _editor;
    private Drag _drag;
    private Point _start;
    private WorldAreaRect _orig;
    private WorldAreaRect? _preview;
    private int _gx0, _gy0, _gw = 1024, _gh = 1024;

    public int SelectedIndex { get; set; } = -1;

    /// <summary>Raised when the editor changed or the selection moved; the second value is a new rectangle to create, if any.</summary>
    public event Action<WorldAreaRect?>? Changed;

    public void SetEditor(WorldAreaEditor? editor)
    {
        _editor = editor;
        SelectedIndex = -1;
        InvalidateVisual();
    }

    private double Scale => Math.Max(0.0001, Math.Min(Bounds.Width / _gw, Bounds.Height / _gh));

    private Point ToWorld(Point p) => new(_gx0 + p.X / Scale, _gy0 + p.Y / Scale);

    private Rect ToView(WorldAreaRect r) => new((r.X - _gx0) * Scale, (r.Y - _gy0) * Scale, r.Width * Scale, r.Height * Scale);

    private void FitView()
    {
        int x0 = 0, y0 = 0, x1 = _editor?.WorldWidth ?? 0, y1 = _editor?.WorldHeight ?? 0;
        if (_editor is not null)
        {
            foreach (var r in _editor.Areas.SelectMany(a => a.Rects))
            {
                (x0, y0, x1, y1) = (Math.Min(x0, r.X), Math.Min(y0, r.Y), Math.Max(x1, r.X + r.Width), Math.Max(y1, r.Y + r.Height));
            }
        }

        // Unbounded world: leave room around the areas to draw new ones.
        if (_editor is { WorldWidth: 0 } || x1 - x0 < 256)
        {
            x1 = Math.Max(x1 + 256, 1024);
            y1 = Math.Max(y1 + 256, 1024);
        }

        (_gx0, _gy0, _gw, _gh) = (x0, y0, x1 - x0, y1 - y0);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#1E1E1E")), new Pen(Brushes.Gray), new Rect(Bounds.Size));
        if (_editor is null)
        {
            return;
        }

        FitView();
        for (var i = 0; i < _editor.Areas.Count; i++)
        {
            var sel = i == SelectedIndex;
            var color = sel ? Colors.Orange : Colors.DeepSkyBlue;
            foreach (var r in _editor.Areas[i].Rects)
            {
                ctx.DrawRectangle(new SolidColorBrush(color, 0.25), new Pen(new SolidColorBrush(color), sel ? 2 : 1), ToView(r));
            }
        }

        if (_preview is { } p)
        {
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1, DashStyle.Dash), ToView(p));
        }
    }

    private (int area, int rect) HitTest(Point world)
    {
        if (_editor is null)
        {
            return (-1, -1);
        }

        for (var i = _editor.Areas.Count - 1; i >= 0; i--)
        {
            var rects = _editor.Areas[i].Rects;
            for (var j = 0; j < rects.Count; j++)
            {
                var r = rects[j];
                if (world.X >= r.X && world.X < r.X + r.Width && world.Y >= r.Y && world.Y < r.Y + r.Height)
                {
                    return (i, j);
                }
            }
        }

        return (-1, -1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        _start = e.GetPosition(this);
        var w = ToWorld(_start);
        var (a, j) = HitTest(w);
        SelectedIndex = a;
        if (a >= 0)
        {
            _orig = _editor.Areas[a].Rects[j];
            var corner = ToView(_orig).BottomRight;
            _drag = Math.Abs(_start.X - corner.X) <= Handle && Math.Abs(_start.Y - corner.Y) <= Handle ? Drag.Resize : Drag.Move;
            _rectIndex = j;
        }
        else
        {
            _drag = Drag.Create;
        }

        e.Pointer.Capture(this);
        Changed?.Invoke(null);
        InvalidateVisual();
    }

    private int _rectIndex;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag == Drag.None)
        {
            return;
        }

        var cur = e.GetPosition(this);
        int dx = (int)Math.Round((cur.X - _start.X) / Scale), dy = (int)Math.Round((cur.Y - _start.Y) / Scale);
        _preview = _drag switch
        {
            Drag.Move => _orig with { X = _orig.X + dx, Y = _orig.Y + dy },
            Drag.Resize => _orig with { Width = Math.Max(1, _orig.Width + dx), Height = Math.Max(1, _orig.Height + dy) },
            _ => Normalize(ToWorld(_start), ToWorld(cur)),
        };
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        if (_preview is { } p && _editor is not null)
        {
            _preview = null;
            if (drag == Drag.Create)
            {
                if (p.Width >= 1 && p.Height >= 1)
                {
                    Changed?.Invoke(p);
                }
            }
            else
            {
                _editor.SetRect(SelectedIndex, _rectIndex, p);
                Changed?.Invoke(null);
            }
        }

        InvalidateVisual();
    }

    private static WorldAreaRect Normalize(Point a, Point b)
    {
        int x = (int)Math.Min(a.X, b.X), y = (int)Math.Min(a.Y, b.Y);
        return new WorldAreaRect(x, y, (int)Math.Abs(a.X - b.X), (int)Math.Abs(a.Y - b.Y), sbyte.MinValue, sbyte.MaxValue);
    }
}
