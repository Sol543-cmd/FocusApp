using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FocusApp;

/// <summary>
/// A circular timer: a colored ring that shrinks clockwise as the session runs down,
/// over a faint full circle. Set <see cref="FractionLeft"/> (1 = just started, 0 = done).
/// </summary>
public class RingTimer : UserControl
{
    private const double Size = 196, Thickness = 10;
    private readonly Ellipse _track = new() { Width = Size, Height = Size, StrokeThickness = Thickness };
    private readonly Ellipse _full = new() { Width = Size, Height = Size, StrokeThickness = Thickness };
    private readonly Path _arc = new()
    {
        StrokeThickness = Thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };
    private double _fraction = 1;
    private bool _running;

    public RingTimer()
    {
        Width = Size;
        Height = Size;
        var canvas = new Canvas { Width = Size, Height = Size };
        _track.SetResourceReference(Shape.StrokeProperty, "Panel3");
        canvas.Children.Add(_track);
        canvas.Children.Add(_full);
        canvas.Children.Add(_arc);
        Content = canvas;
        AccentKey = "FocusBrush";
        Update();
    }

    /// <summary>How much time is left: 1 when the session starts, 0 when it ends.</summary>
    public double FractionLeft
    {
        get => _fraction;
        set
        {
            double v = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1);
            if (Math.Abs(v - _fraction) < 0.0005) return; // skip redraws too small to see
            _fraction = v;
            Update();
        }
    }

    /// <summary>When not running (paused or idle), the ring is drawn softer.</summary>
    public bool Running
    {
        get => _running;
        set
        {
            _running = value;
            _arc.Opacity = _full.Opacity = value ? 1 : 0.55;
        }
    }

    /// <summary>The ring's color, as a theme color name like "FocusBrush".</summary>
    public string AccentKey
    {
        set
        {
            _arc.SetResourceReference(Shape.StrokeProperty, value);
            _full.SetResourceReference(Shape.StrokeProperty, value);
        }
    }

    private void Update()
    {
        // A full ring is a plain circle (an arc can't quite close on itself).
        bool full = _fraction >= 0.999;
        _full.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        _arc.Visibility = !full && _fraction > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        if (full || _fraction <= 0.001) return;

        // Starts at 12 o'clock and runs clockwise for the time that's left.
        double r = (Size - Thickness) / 2, c = Size / 2;
        double angle = _fraction * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, _fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _arc.Data = geometry;
    }
}
