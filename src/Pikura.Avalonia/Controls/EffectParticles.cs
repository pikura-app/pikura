using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pikura.Avalonia.Controls;

/// <summary>Click-through overlay that drifts <see cref="Glyphs"/> down its bounds, used for pixiv
/// effect tags on an artwork. Idle while hidden or without glyphs.</summary>
public sealed class EffectParticles : Control
{
    public static readonly StyledProperty<string[]?> GlyphsProperty =
        AvaloniaProperty.Register<EffectParticles, string[]?>(nameof(Glyphs));

    public string[]? Glyphs
    {
        get => GetValue(GlyphsProperty);
        set => SetValue(GlyphsProperty, value);
    }

    private static readonly Typeface EmojiTypeface = new(new FontFamily(
        OperatingSystem.IsWindows() ? "Segoe UI Emoji"
        : OperatingSystem.IsMacOS() ? "Apple Color Emoji"
        : "Noto Color Emoji"));

    private struct Particle
    {
        public double X, Y, Speed, SwayAmp, SwayFreq, Phase, Scale, Angle, Spin;
        public int Glyph;
    }

    private readonly List<Particle> _particles = [];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private FormattedText[] _glyphs = [];
    private DispatcherTimer? _timer;
    private double _lastSeconds;
    private bool _attached;
    private Window? _window;

    public EffectParticles()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Ancestors watched for visibility, so hiding a host panel (e.g. a viewer that is
    /// collapsed) stops the timer and showing it again resumes it.</summary>
    private readonly List<Visual> _ancestors = [];

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) Dispatcher.UIThread.Post(UpdateTimer);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null) _window.PropertyChanged += OnWindowPropertyChanged;
        for (var v = this.GetVisualParent(); v != null && v != _window; v = v.GetVisualParent())
        {
            v.PropertyChanged += OnAncestorPropertyChanged;
            _ancestors.Add(v);
        }
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_window != null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        foreach (var v in _ancestors) v.PropertyChanged -= OnAncestorPropertyChanged;
        _ancestors.Clear();
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) UpdateTimer();
    }

    /// <summary>Animates only while on screen: attached, effectively visible, with glyphs, in a
    /// window that is neither minimized nor hidden (e.g. closed to the tray).</summary>
    private void UpdateTimer()
    {
        var onScreen = _attached && IsEffectivelyVisible && _glyphs.Length > 0
                       && _window is not { IsVisible: false } and not { WindowState: WindowState.Minimized };
        if (onScreen) Start();
        else Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphsProperty || change.Property == IsVisibleProperty) Refresh();
    }

    private void Refresh()
    {
        _particles.Clear();
        _glyphs = (Glyphs ?? [])
            .Select(g => new FormattedText(g, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                EmojiTypeface, 26, Brushes.White))
            .ToArray();
        UpdateTimer();
        InvalidateVisual();
    }

    private void Start()
    {
        if (_timer != null) return;
        _clock.Restart();
        _lastSeconds = 0;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private Particle Spawn(double width, double height, bool anywhere) => new()
    {
        X = _rng.NextDouble() * width,
        Y = anywhere ? _rng.NextDouble() * height : -30 - _rng.NextDouble() * 60,
        Speed = 35 + _rng.NextDouble() * 55,
        SwayAmp = 6 + _rng.NextDouble() * 22,
        SwayFreq = 0.4 + _rng.NextDouble() * 0.8,
        Phase = _rng.NextDouble() * Math.PI * 2,
        Scale = 0.6 + _rng.NextDouble() * 0.8,
        Angle = _rng.NextDouble() * Math.PI * 2,
        Spin = (_rng.NextDouble() - 0.5) * 1.6,
        Glyph = _rng.Next(_glyphs.Length),
    };

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.1, now - _lastSeconds);
        _lastSeconds = now;
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w < 1 || h < 1 || _glyphs.Length == 0) return;
        var target = Math.Clamp((int)(w * h / 30000), 8, 36);
        while (_particles.Count < target) _particles.Add(Spawn(w, h, anywhere: true));
        if (_particles.Count > target) _particles.RemoveRange(target, _particles.Count - target);
        for (var i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.Y += p.Speed * dt;
            p.Phase += p.SwayFreq * dt;
            p.Angle += p.Spin * dt;
            _particles[i] = p.Y > h + 30 ? Spawn(w, h, anywhere: false) : p;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_glyphs.Length == 0) return;
        using (context.PushOpacity(0.9))
        {
            foreach (var p in _particles)
            {
                var glyph = _glyphs[p.Glyph % _glyphs.Length];
                var x = p.X + Math.Sin(p.Phase) * p.SwayAmp;
                var m = Matrix.CreateTranslation(-glyph.Width / 2, -glyph.Height / 2)
                        * Matrix.CreateRotation(p.Angle)
                        * Matrix.CreateScale(p.Scale, p.Scale)
                        * Matrix.CreateTranslation(x, p.Y);
                using (context.PushTransform(m))
                    context.DrawText(glyph, default);
            }
        }
    }
}
