using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.Services;

namespace Pikura.Avalonia.Controls;

/// <summary>Click-through overlay for the active <see cref="SeasonalOccasion"/>: static decorations
/// clustered in the window's corners, and glyphs (snow, leaves…) drifting down the window. Both are
/// scaled by the seasonal intensity; the animation is idle when falling effects are off.</summary>
public sealed class SeasonalParticles : Control
{
    /// <summary>The platform's color emoji font; the default UI font draws emoji as flat gray glyphs.</summary>
    private static readonly Typeface EmojiTypeface = new(new FontFamily(
        OperatingSystem.IsWindows() ? "Segoe UI Emoji"
        : OperatingSystem.IsMacOS() ? "Apple Color Emoji"
        : "Noto Color Emoji"));

    private readonly record struct Decoration(int Corner, double Dx, double Dy, double Size, double Angle, int Glyph);

    private struct Particle
    {
        public double X, Y, Speed, SwayAmp, SwayFreq, Phase, Scale, Angle, Spin;
        public int Glyph;
    }

    private readonly List<Particle> _particles = [];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private FormattedText[] _glyphs = [];
    private FormattedText[] _sceneryGlyphs = [];
    private Decoration[] _scenery = [];
    private SeasonalOccasion? _occasion;
    private string? _intensity;
    private DispatcherTimer? _timer;
    private double _lastSeconds;

    public SeasonalParticles()
    {
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SeasonalTheme.Changed += OnChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
        _occlusionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => UpdateOcclusion());
        _occlusionTimer.Start();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SeasonalTheme.Changed -= OnChanged;
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        _occlusionTimer?.Stop();
        _occlusionTimer = null;
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        _occasion = null;
        Refresh();
    }

    private void Refresh()
    {
        var occasion = SeasonalTheme.CurrentOccasion;
        var falling = SeasonalTheme.EffectsEnabled && occasion is { Particles.Length: > 0 };
        if (!ReferenceEquals(occasion, _occasion) || SeasonalTheme.Intensity != _intensity)
        {
            _occasion = occasion;
            _intensity = SeasonalTheme.Intensity;
            // Plain glyphs need contrast with the background; emoji keep their own colors.
            IBrush brush = ActualThemeVariant == ThemeVariant.Dark
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x7F, 0xA8, 0xD6));
            _glyphs = (occasion?.Particles ?? []).Select(g => Glyph(g, 24, brush)).ToArray();
            _sceneryGlyphs = (occasion?.Scenery ?? []).Select(g => Glyph(g, 64, brush)).ToArray();
            _scenery = BuildScenery(occasion, _intensity);
            _sceneryHidden = [];
            Dispatcher.UIThread.Post(UpdateOcclusion, DispatcherPriority.Background);
            _particles.Clear();
            ParseAmbient(occasion?.Ambient);
            _fx.Clear();
            _nextSwitch = 0;
        }
        if (!SeasonalTheme.SceneryEnabled) _scenery = [];
        else if (_scenery.Length == 0) _scenery = BuildScenery(occasion, _intensity);
        if (falling) Start();
        else
        {
            _particles.Clear();
            _fx.Clear();
            Stop();
        }
        InvalidateVisual();
    }

    private static FormattedText Glyph(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, EmojiTypeface, size, brush);

    private static (double Count, double Opacity, double Size) Level(string? intensity) => intensity switch
    {
        "Subtle" => (0.4, 0.4, 0.75),
        "Festive" => (1.8, 0.8, 1.2),
        _ => (1.0, 0.6, 1.0),
    };

    private static (int PerCorner, double Opacity, double Size) SceneryLevel(string? intensity) => intensity switch
    {
        "Subtle" => (2, 0.16, 0.8),
        "Festive" => (4, 0.3, 1.15),
        _ => (3, 0.22, 1.0),
    };

    /// <summary>Lays out a cluster of decorations in each corner, positioned relative to that corner so
    /// they stay put when the window resizes. Larger pieces sit in the corner, smaller ones fan out,
    /// and pieces partly overflow the edge like a cropped wallpaper. The center stays clear.</summary>
    private static Decoration[] BuildScenery(SeasonalOccasion? occasion, string? intensity)
    {
        if (occasion is not { Scenery.Length: > 0 }) return [];
        var (perCorner, _, size) = SceneryLevel(intensity);
        var rng = new Random(occasion.Key.Length * 7919 + perCorner);
        var list = new List<Decoration>();
        // The bottom-left corner holds the sidebar's account card, so it stays clear.
        foreach (var corner in (int[])[0, 1, 2])
        {
            for (var i = 0; i < perCorner; i++)
            {
                // Spread along an arc around the corner: first pieces biggest and closest.
                var t = perCorner == 1 ? 0.5 : (double)i / (perCorner - 1);
                var angle = t * Math.PI / 2;
                var radius = (i == 0 ? 0 : 26 + i * 20) + rng.NextDouble() * 10;
                var pieceSize = (i == 0 ? 46 : 34 - i * 3 + rng.NextDouble() * 6) * size;
                list.Add(new Decoration(corner,
                    Math.Cos(angle) * radius, Math.Sin(angle) * radius,
                    Math.Max(16, pieceSize), (rng.NextDouble() - 0.5) * 0.7,
                    rng.Next(occasion.Scenery.Length)));
            }
        }
        return list.ToArray();
    }

    private void Start()
    {
        if (_timer != null) return;
        _clock.Restart();
        _lastSeconds = 0;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick());
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
        Y = anywhere ? _rng.NextDouble() * height
            : _rise ? height + 24 + _rng.NextDouble() * 60 : -24 - _rng.NextDouble() * 60,
        Speed = 22 + _rng.NextDouble() * 38,
        SwayAmp = (8 + _rng.NextDouble() * 24) * (_occasion?.Sway ?? 0.5),
        SwayFreq = 0.4 + _rng.NextDouble() * 0.8,
        Phase = _rng.NextDouble() * Math.PI * 2,
        Scale = 0.7 + _rng.NextDouble() * 0.8,
        Angle = _rng.NextDouble() * Math.PI * 2,
        Spin = (_rng.NextDouble() - 0.5) * 2 * (_occasion?.Spin ?? 0.3),
        Glyph = _rng.Next(_glyphs.Length),
    };

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.1, now - _lastSeconds);
        _lastSeconds = now;
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized } || _glyphs.Length == 0)
            return;

        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w < 1 || h < 1) return;
        var target = (int)(Math.Clamp((int)(w * h / 60000), 10, 32) * Level(_intensity).Count);
        while (_particles.Count < target) _particles.Add(Spawn(w, h, anywhere: true));
        if (_particles.Count > target) _particles.RemoveRange(target, _particles.Count - target);

        for (var i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.Y += (_rise ? -p.Speed : p.Speed) * dt;
            p.Phase += p.SwayFreq * dt;
            p.Angle += p.Spin * dt;
            _particles[i] = p.Y > h + 24 || p.Y < -100 ? Spawn(w, h, anywhere: false) : p;
        }
        UpdateAmbient(now, dt, w, h);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (_scenery.Length > 0 && _sceneryGlyphs.Length > 0)
        {
            using (context.PushOpacity(SceneryLevel(_intensity).Opacity))
            {
                for (var i = 0; i < _scenery.Length; i++)
                {
                    if (i < _sceneryHidden.Length && _sceneryHidden[i]) continue;
                    var d = _scenery[i];
                    var glyph = _sceneryGlyphs[d.Glyph % _sceneryGlyphs.Length];
                    var scale = d.Size / 64;
                    var (x, y) = ScenePoint(d, w, h);
                    var m = Matrix.CreateTranslation(-glyph.Width / 2, -glyph.Height / 2)
                            * Matrix.CreateRotation(d.Angle)
                            * Matrix.CreateScale(scale, scale)
                            * Matrix.CreateTranslation(x, y);
                    using (context.PushTransform(m))
                        context.DrawText(glyph, default);
                }
            }
        }

        if (SeasonalTheme.SceneryEnabled && _occasion?.Sprite is { } sprite && _spriteSpot is { } spot)
            DrawSprite(context, sprite, spot);

        DrawAmbient(context, w, h);

        if (_glyphs.Length == 0 || _particles.Count == 0) return;
        var level = Level(_intensity);
        using (context.PushOpacity(level.Opacity))
        {
            foreach (var p in _particles)
            {
                var glyph = _glyphs[p.Glyph % _glyphs.Length];
                var x = p.X + Math.Sin(p.Phase) * p.SwayAmp;
                var scale = p.Scale * level.Size;
                var m = Matrix.CreateTranslation(-glyph.Width / 2, -glyph.Height / 2)
                        * Matrix.CreateRotation(p.Angle)
                        * Matrix.CreateScale(scale, scale)
                        * Matrix.CreateTranslation(x, p.Y);
                using (context.PushTransform(m))
                    context.DrawText(glyph, default);
            }
        }
    }

    private readonly Dictionary<string, IBrush> _spriteBrushes = [];

    private DispatcherTimer? _occlusionTimer;
    private bool[] _sceneryHidden = [];
    private Point? _spriteSpot = null;

    /// <summary>Corner 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left.</summary>
    private static (double X, double Y) ScenePoint(Decoration d, double w, double h) =>
        (d.Corner is 0 or 3 ? d.Dx : w - d.Dx, d.Corner is 0 or 1 ? d.Dy : h - d.Dy);

    private (double Pixel, double Opacity) SpriteLevel() => _intensity switch
    {
        "Subtle" => (2.5, 0.5),
        "Festive" => (4.0, 0.85),
        _ => (3.0, 0.7),
    };

    /// <summary>Hides corner pieces that sit over buttons, inputs, text and images, and moves the
    /// sprite to the nearest clear spot in the bottom-right quarter.</summary>
    private void UpdateOcclusion()
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w < 1 || h < 1 || TopLevel.GetTopLevel(this) is not { } root) return;
        var changed = false;
        _blocked = [];
        CollectBlocked(root, new Rect(0, 0, w, h));

        var hidden = new bool[_scenery.Length];
        for (var i = 0; i < _scenery.Length; i++)
        {
            var (x, y) = ScenePoint(_scenery[i], w, h);
            var half = _scenery[i].Size / 2;
            hidden[i] = Covers(new Rect(x - half, y - half, half * 2, half * 2));
        }
        if (!hidden.AsSpan().SequenceEqual(_sceneryHidden)) { _sceneryHidden = hidden; changed = true; }

        Point? spot = null;
        if (SeasonalTheme.SceneryEnabled && _occasion?.Sprite is { } sprite)
        {
            var pixel = SpriteLevel().Pixel;
            var size = new Size(sprite.Rows.Max(r => r.Length) * pixel, sprite.Rows.Length * pixel);
            var corner = new Point(Math.Round(w - 24 - size.Width), Math.Round(h - 24 - size.Height));
            var candidates = new List<Point>();
            for (var dy = 0.0; dy < h / 2; dy += 20)
            for (var dx = 0.0; dx < w / 2; dx += 20)
                candidates.Add(corner - new Point(dx, dy));
            foreach (var c in candidates.OrderBy(c => Math.Abs(corner.X - c.X) + Math.Abs(corner.Y - c.Y)))
            {
                if (Covers(new Rect(c, size).Inflate(12))) continue;
                spot = c;
                break;
            }
        }
        if (spot != _spriteSpot) { _spriteSpot = spot; changed = true; }
        if (changed) InvalidateVisual();
    }

    private List<Rect> _blocked = [];

    private bool Covers(Rect rect)
    {
        rect = rect.Inflate(6);
        foreach (var r in _blocked)
            if (r.Intersects(rect)) return true;
        return false;
    }

    private static bool IsBlocking(Visual v) =>
        v is Button or TextBox or ComboBox or Slider or TextBlock or Image or ToggleSwitch or ScrollBar
            or AutoCompleteBox or CalendarDatePicker or MenuItem or TabItem or CheckBox or RadioButton
        || v is Border { Classes: var c } && c.Contains("badge");

    /// <summary>Collects the on-screen bounds (clipped by their scroll viewers) of every visible
    /// control, text and image, in this control's coordinates. Disabled controls count too, and
    /// click-through layers such as the artwork overlay are skipped.</summary>
    private void CollectBlocked(Visual visual, Rect clip)
    {
        foreach (var child in visual.GetVisualChildren())
        {
            if (ReferenceEquals(child, this) || !child.IsVisible || child.Opacity <= 0
                || child is InputElement { IsHitTestVisible: false })
                continue;
            if (child.TransformToVisual(this) is not { } m) continue;
            var rect = new Rect(child.Bounds.Size).TransformToAABB(m).Intersect(clip);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                if (child.ClipToBounds) continue;
            }
            else if (IsBlocking(child))
            {
                _blocked.Add(rect);
                continue;
            }
            CollectBlocked(child, child.ClipToBounds ? rect : clip);
        }
    }

    /// <summary>Draws the occasion's pixel sprite at <paramref name="spot"/>, sized by intensity.</summary>
    private void DrawSprite(DrawingContext context, PixelSprite sprite, Point spot)
    {
        var (pixel, opacity) = SpriteLevel();
        var (left, top) = (spot.X, spot.Y);
        if (ReferenceEquals(sprite, SeasonalSprites.Orb))
        {
            // Goku's four-star ball, drawn smoothly to match the floating Dragon Balls.
            var box = sprite.Rows.Length * pixel;
            DrawBall(context, new Point(left + box / 2, top + box / 2), box / 2 / 1.35, 4, opacity);
            return;
        }
        using (context.PushOpacity(opacity))
        {
            for (var y = 0; y < sprite.Rows.Length; y++)
            {
                var row = sprite.Rows[y];
                for (var x = 0; x < row.Length; x++)
                {
                    if (!sprite.Palette.TryGetValue(row[x], out var hex)) continue;
                    if (!_spriteBrushes.TryGetValue(hex, out var brush))
                        _spriteBrushes[hex] = brush = new SolidColorBrush(Color.Parse(hex));
                    context.FillRectangle(brush, new Rect(left + x * pixel, top + y * pixel, pixel, pixel));
                }
            }
        }
    }

    private enum FxKind { Bolt, Ember, Ripple, Mist, Streak, Twinkle, DragonBall }

    private record struct Fx
    {
        public FxKind Kind;
        public double X, Y, Vx, Vy, Age, Life, Size;
        public Color Tint;
        public int Stars;
        public Point[]? Path;
    }

    private readonly List<Fx> _fx = [];
    private string[] _ambient = [];
    private string[][] _ambientPool = [];
    private string[] _activeAmbient = [];
    private bool _rise;
    private double _nextSwitch, _nextBolt, _nextRipple, _nextMist, _nextStreak, _nextTwinkle, _nextBall, _emberDue;

    private static string[] Tokens(string group) =>
        group.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private void ParseAmbient(string? ambient)
    {
        if (ambient?.StartsWith("Random:", StringComparison.Ordinal) == true)
        {
            _ambientPool = ambient["Random:".Length..].Split('|').Select(Tokens).Where(t => t.Length > 0).ToArray();
            _ambient = ["Random"];
        }
        else
        {
            _ambientPool = [];
            _ambient = ambient is null ? [] : Tokens(ambient);
        }
        _rise = _ambient.Contains("Rise");
    }

    private static Color KindColor(string token, Color fallback) =>
        token.IndexOf(':') is var i and >= 0 && Color.TryParse(token[(i + 1)..], out var c) ? c : fallback;

    /// <summary>Spawns and ages the occasion's ambient effects; a "Random:" pool switches group every 20 seconds.</summary>
    private void UpdateAmbient(double now, double dt, double w, double h)
    {
        if (_ambient.Length == 0) { _fx.Clear(); return; }
        if (_ambientPool.Length > 0)
        {
            if (now >= _nextSwitch)
            {
                _activeAmbient = _ambientPool[_rng.Next(_ambientPool.Length)];
                _nextSwitch = now + 20;
            }
        }
        else _activeAmbient = _ambient;

        var rate = Level(_intensity).Count;
        foreach (var token in _activeAmbient)
        {
            var kind = token.Split(':')[0];
            switch (kind)
            {
                case "Lightning" when now >= _nextBolt:
                    _fx.Add(Bolt(w, h) with { Tint = KindColor(token, Color.FromRgb(255, 210, 80)) });
                    _nextBolt = now + 9 - 3 * rate + _rng.NextDouble() * 5;
                    break;
                case "Embers" or "PinkEmbers":
                {
                    var tint = KindColor(token, kind == "Embers" ? Color.FromRgb(255, 150, 40) : Color.FromRgb(255, 140, 190));
                    for (_emberDue += dt * 6 * rate; _emberDue >= 1; _emberDue--)
                        _fx.Add(new Fx
                        {
                            Kind = FxKind.Ember, Tint = tint,
                            X = _rng.NextDouble() * w, Y = h + 6,
                            Vx = (_rng.NextDouble() - 0.5) * 20, Vy = -(30 + _rng.NextDouble() * 50),
                            Life = 3 + _rng.NextDouble() * 3, Size = 2 + _rng.NextDouble() * 2.5,
                        });
                    break;
                }
                case "Ripples" when now >= _nextRipple:
                    _fx.Add(new Fx
                    {
                        Kind = FxKind.Ripple, Tint = KindColor(token, Color.FromRgb(150, 200, 255)),
                        X = _rng.NextDouble() * w, Y = h * (0.55 + _rng.NextDouble() * 0.4),
                        Life = 2.8, Size = 40 + _rng.NextDouble() * 50,
                    });
                    _nextRipple = now + 1.6 / rate;
                    break;
                case "Mist" when now >= _nextMist:
                {
                    var fromLeft = _rng.Next(2) == 0;
                    var speed = 12 + _rng.NextDouble() * 14;
                    _fx.Add(new Fx
                    {
                        Kind = FxKind.Mist, Tint = KindColor(token, Color.FromRgb(230, 240, 245)),
                        X = fromLeft ? -200 : w + 200, Y = _rng.NextDouble() * h,
                        Vx = fromLeft ? speed : -speed, Life = (w + 400) / speed, Size = 120 + _rng.NextDouble() * 120,
                    });
                    _nextMist = now + 5 / rate;
                    break;
                }
                case "Wind" when now >= _nextStreak:
                {
                    var speed = 500 + _rng.NextDouble() * 300;
                    _fx.Add(new Fx
                    {
                        Kind = FxKind.Streak, Tint = KindColor(token, Color.FromRgb(210, 235, 215)),
                        X = -160, Y = _rng.NextDouble() * h,
                        Vx = speed, Life = (w + 320) / speed, Size = 60 + _rng.NextDouble() * 100,
                    });
                    _nextStreak = now + 0.5 / rate;
                    break;
                }
                case "DragonBalls" when now >= _nextBall:
                {
                    // The seven balls (one to seven stars) drift around one at a time, never two alike.
                    var missing = Enumerable.Range(1, 7)
                        .Except(_fx.Where(f => f.Kind == FxKind.DragonBall).Select(f => f.Stars)).ToArray();
                    if (missing.Length > 0)
                        _fx.Add(new Fx
                        {
                            Kind = FxKind.DragonBall, Stars = missing[_rng.Next(missing.Length)],
                            X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h,
                            Vx = (_rng.NextDouble() - 0.5) * 16, Vy = (_rng.NextDouble() - 0.5) * 10,
                            Life = 14 + _rng.NextDouble() * 8, Size = (11 + _rng.NextDouble() * 4) * Level(_intensity).Size,
                        });
                    _nextBall = now + 1.5;
                    break;
                }
                case "Sparkles" when now >= _nextTwinkle:
                    _fx.Add(new Fx
                    {
                        Kind = FxKind.Twinkle, Tint = KindColor(token, Color.FromRgb(255, 230, 140)),
                        X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h,
                        Life = 1.2, Size = 6 + _rng.NextDouble() * 8,
                    });
                    _nextTwinkle = now + 0.35 / rate;
                    break;
            }
        }

        for (var i = _fx.Count - 1; i >= 0; i--)
        {
            var f = _fx[i];
            f.Age += dt;
            f.X += f.Vx * dt;
            f.Y += f.Vy * dt;
            if (f.Age >= f.Life) _fx.RemoveAt(i);
            else _fx[i] = f;
        }
    }

    /// <summary>A zigzag bolt striking down near the left or right edge, away from the content.</summary>
    private Fx Bolt(double w, double h)
    {
        var x = _rng.Next(2) == 0 ? w * (0.03 + _rng.NextDouble() * 0.12) : w * (0.85 + _rng.NextDouble() * 0.12);
        var y = 0.0;
        var path = new List<Point> { new(x, y) };
        for (var i = 0; i < 7; i++)
        {
            y += h * (0.05 + _rng.NextDouble() * 0.04);
            x += (i % 2 == 0 ? 1 : -1) * (10 + _rng.NextDouble() * 18);
            path.Add(new Point(x, y));
        }
        return new Fx { Kind = FxKind.Bolt, Life = 0.5, Path = path.ToArray() };
    }

    private static IBrush Argb(double alpha, Color c) => Argb(alpha, c.R, c.G, c.B);

    private static IBrush Argb(double alpha, byte r, byte g, byte b) =>
        new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), r, g, b));

    private void DrawAmbient(DrawingContext context, double w, double h)
    {
        if (_fx.Count == 0) return;
        var op = Level(_intensity).Opacity;
        foreach (var f in _fx)
        {
            var t = f.Age / f.Life;
            switch (f.Kind)
            {
                case FxKind.Bolt when f.Path is { } path:
                {
                    // A soft, brief brighten of the window, then the bolt fades.
                    var core = Color.FromRgb((byte)((f.Tint.R + 510) / 3), (byte)((f.Tint.G + 510) / 3), (byte)((f.Tint.B + 510) / 3));
                    if (f.Age < 0.25) context.FillRectangle(Argb(0.08 * op * (1 - f.Age / 0.25), core), new Rect(0, 0, w, h));
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(path[0], false);
                        foreach (var pt in path.Skip(1)) g.LineTo(pt);
                        g.EndFigure(false);
                    }
                    var a = (1 - t) * op;
                    context.DrawGeometry(null, new Pen(Argb(0.35 * a, f.Tint), 9, lineJoin: PenLineJoin.Round), geometry);
                    context.DrawGeometry(null, new Pen(Argb(a, core), 2.5, lineJoin: PenLineJoin.Round), geometry);
                    break;
                }
                case FxKind.Ember:
                {
                    var flicker = 0.6 + 0.4 * Math.Sin(f.Age * 14 + f.X);
                    var brush = Argb(0.85 * (1 - t) * flicker * op, f.Tint);
                    context.DrawEllipse(brush, null, new Point(f.X + Math.Sin(f.Age * 3 + f.Y) * 6, f.Y), f.Size, f.Size);
                    break;
                }
                case FxKind.Ripple:
                {
                    var pen = new Pen(Argb(0.6 * (1 - t) * op, f.Tint), 2);
                    var r = f.Size * t;
                    context.DrawEllipse(null, pen, new Point(f.X, f.Y), r, r * 0.35);
                    context.DrawEllipse(null, pen, new Point(f.X, f.Y), r * 0.6, r * 0.21);
                    break;
                }
                case FxKind.Mist:
                {
                    var brush = new RadialGradientBrush
                    {
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb((byte)(50 * op), f.Tint.R, f.Tint.G, f.Tint.B), 0),
                            new GradientStop(Colors.Transparent, 1),
                        },
                    };
                    context.DrawEllipse(brush, null, new Point(f.X, f.Y), f.Size, f.Size * 0.45);
                    break;
                }
                case FxKind.Streak:
                {
                    var pen = new Pen(Argb(0.5 * Math.Sin(Math.PI * t) * op, f.Tint), 1.5, lineCap: PenLineCap.Round);
                    context.DrawLine(pen, new Point(f.X, f.Y), new Point(f.X - f.Size, f.Y + 3));
                    break;
                }
                case FxKind.DragonBall:
                    DrawDragonBall(context, f, op);
                    break;
                case FxKind.Twinkle:
                {
                    var s = f.Size * Math.Sin(Math.PI * t);
                    var pen = new Pen(Argb(0.9 * op, f.Tint), 1.6, lineCap: PenLineCap.Round);
                    context.DrawLine(pen, new Point(f.X - s, f.Y), new Point(f.X + s, f.Y));
                    context.DrawLine(pen, new Point(f.X, f.Y - s), new Point(f.X, f.Y + s));
                    break;
                }
            }
        }
    }

    private static void DrawDragonBall(DrawingContext context, Fx f, double op)
    {
        var a = Math.Clamp(Math.Min(f.Age, f.Life - f.Age) / 1.2, 0, 1) * op;
        DrawBall(context, new Point(f.X, f.Y + Math.Sin(f.Age * 1.3 + f.Stars) * 6), f.Size, f.Stars, a);
    }

    /// <summary>Draws a Dragon Ball: a glowing orange orb with <paramref name="stars"/> red stars.</summary>
    private static void DrawBall(DrawingContext context, Point c, double r, int stars, double a)
    {
        byte A(double k) => (byte)Math.Clamp(a * k * 255, 0, 255);
        var body = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(A(1), 0xFF, 0xE7, 0xA0), 0),
                new GradientStop(Color.FromArgb(A(1), 0xF5, 0xA0, 0x30), 0.55),
                new GradientStop(Color.FromArgb(A(1), 0xC0, 0x60, 0x10), 1),
            },
        };
        context.DrawEllipse(Argb(0.25 * a, 255, 200, 90), null, c, r * 1.35, r * 1.35);
        context.DrawEllipse(body, null, c, r, r);
        context.DrawEllipse(Argb(0.6 * a, 255, 255, 255), null, new Point(c.X - r * 0.35, c.Y - r * 0.42), r * 0.22, r * 0.13);

        var star = Argb(a, 0xD0, 0x20, 0x20);
        var points = new List<Point>();
        if (stars == 1) points.Add(c);
        else
        {
            var ring = stars == 7 ? 6 : stars;
            for (var i = 0; i < ring; i++)
            {
                var angle = -Math.PI / 2 + i * 2 * Math.PI / ring;
                points.Add(new Point(c.X + Math.Cos(angle) * r * 0.42, c.Y + Math.Sin(angle) * r * 0.42));
            }
            if (stars == 7) points.Add(c);
        }
        var size = stars == 1 ? r * 0.38 : r * 0.2;
        foreach (var p in points) context.DrawGeometry(star, null, StarGeometry(p, size));
    }

    private static StreamGeometry StarGeometry(Point center, double radius)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        for (var i = 0; i < 10; i++)
        {
            var rr = i % 2 == 0 ? radius : radius * 0.45;
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var pt = new Point(center.X + Math.Cos(angle) * rr, center.Y + Math.Sin(angle) * rr);
            if (i == 0) g.BeginFigure(pt, true);
            else g.LineTo(pt);
        }
        g.EndFigure(true);
        return geometry;
    }
}
