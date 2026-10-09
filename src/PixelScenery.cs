using System.Windows;
using System.Windows.Media;

namespace WolfSpeak;

/// <summary>Quiet continuation of the night sky behind the window title.</summary>
public sealed class HeaderStars : FrameworkElement
{
    static readonly Brush Star = new SolidColorBrush(Color.FromRgb(0xDD, 0xE8, 0xFF));
    double time;
    long lastFrame;

    public HeaderStars()
    {
        Star.Freeze();
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    internal void Advance(long now)
    {
        if (!IsVisible || now - lastFrame < 80) return;
        if (lastFrame != 0 && SystemParameters.ClientAreaAnimation)
            time += Math.Min((now - lastFrame) / 1000.0, .2);
        lastFrame = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        for (int i = 0; i < 15; i++)
        {
            double x = Math.Round(((i * 53 + 17) % 240) / 240.0 * ActualWidth);
            // Keep stars away from the title and the control icons.
            double y = i % 3 == 0 ? 7 + i % 5 : ActualHeight - 8 - i % 5;
            dc.PushOpacity(.22 + .22 * (1 + Math.Sin(time * .7 + i)) / 2);
            dc.DrawRectangle(Star, null, new Rect(x, Math.Round(y), 1, 1));
            dc.Pop();
        }
    }
}

/// <summary>A small, code-drawn pixel world. The window drives frames only while visible.</summary>
public sealed class PixelScenery : FrameworkElement
{
    static readonly Brush[][] Palettes =
    [
        Palette("#AFB7A1", "#FFE0A0", "#8A997A", "#566F50", "#3B5337", "#F4C991", "#EED9B5", "#789EA0"),
        Palette("#70B6DA", "#FFF2B1", "#719F75", "#39734D", "#285638", "#B9DCE7", "#EDF3E9", "#4A94AF"),
        Palette("#BE8361", "#FFD08A", "#8A7856", "#4F5F42", "#343E2E", "#F0AA68", "#E7B588", "#856D56"),
        Palette("#0B0F16", "#DDE8FF", "#343C55", "#253344", "#14252B", "#242A41", "#657598", "#33758C")
    ];
    static readonly Brush Cloud = Paint("#93A6AF"), Water = Paint("#52747A"), Ripple = Paint("#B5CCCF");
    static readonly Brush Fire = Paint("#D4965C"), Ember = Paint("#F1CB82"), Wolf = Paint("#B7C6C9"), WolfShade = Paint("#7F939C"), Eye = Paint("#20313B");
    static readonly Brush DeerFur = Paint("#BA9275"), DeerLight = Paint("#DEC1A0"), Alert = Paint("#FF6575");
    static readonly Brush[] GroundFog = Palette("#192430", "#18222F", "#151E29", "#121A24", "#10161F", "#0D121B", "#0B0F16");
    static readonly Brush FogEdge = CreateFogEdge();

    static Brush CreateFogEdge()
    {
        var brush = new LinearGradientBrush(Color.FromArgb(0, 11, 15, 22), Color.FromRgb(11, 15, 22), 90);
        brush.Freeze();
        return brush;
    }
    double time;
    readonly int initialVariant = Random.Shared.Next(3);
    static readonly Brush FlowerPink = Paint("#C990BA"), FlowerBlue = Paint("#8D9DE0"), FlowerGold = Paint("#E2C18B"), Stem = Paint("#47665E");
    long lastFrame;
    internal int? SnapshotHour { get; set; }
    internal double? SnapshotTime { get; set; }
    internal int? SelectedHour { get; set; }
    public string Period { get; private set; } = "Night";

    public PixelScenery() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

    internal void Advance(long now)
    {
        if (!IsVisible || ActualWidth <= 0 || now - lastFrame < 80) return;
        if (SnapshotTime is not null) time = SnapshotTime.Value;
        else if (lastFrame != 0 && SystemParameters.ClientAreaAnimation)
            time += Math.Min((now - lastFrame) / 1000.0, .2);
        lastFrame = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        // One persistent animation clock drives a slow loop across all night variants.
        // Crossfade the complete illustration so objects never pop in during a change.
        const double duration = 90, transition = 8;
        int cycle = (int)(time / duration);
        int variant = (initialVariant + cycle) % 3;
        double progress = Math.Clamp((time % duration - (duration - transition)) / transition, 0, 1);
        progress = progress * progress * (3 - 2 * progress);
        RenderScene(dc, variant);
        if (progress > 0)
        {
            dc.PushOpacity(progress);
            RenderScene(dc, (variant + 1) % 3);
            dc.Pop();
        }
    }

    void RenderScene(DrawingContext dc, int Variant)
    {
        int period = 3;
        Period = new[] { "Dawn", "Day", "Dusk", "Night" }[period];
        var p = Palettes[period];
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.DrawRectangle(p[4], null, new Rect(RenderSize));
        // Uniform scaling preserves square pixels, wolf proportions, and round celestial bodies.
        // Reserve a separate ground extension for the fade, after the complete illustrated scene.
        double sceneHeight = Math.Max(1, ActualHeight - 64);
        double scale = Math.Min(ActualWidth / 240, sceneHeight / 100);
        dc.DrawRectangle(p[0], null, new Rect(0, 0, ActualWidth, Math.Max(0, sceneHeight - 100 * scale)));
        dc.PushTransform(new TranslateTransform((ActualWidth - 240 * scale) / 2, sceneHeight - 100 * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));
        void Box(Brush b, double x, double y, double w, double h) => dc.DrawRectangle(b, null, new Rect(Math.Round(x), Math.Round(y), w, h));
        Box(p[0], 0, 0, 240, 100);
        // Layered sky bands keep the pixel look while giving each sky a horizon glow.
        dc.PushOpacity(.35); Box(p[5], 0, 27, 240, 14); dc.Pop();
        dc.PushOpacity(.65); Box(p[5], 0, 41, 240, 30); dc.Pop();
        double localHour = 22;
        double celestialX, celestialY;
        if (period == 3)
        {
            double progress = (localHour >= 20 ? localHour - 20 : localHour + 4) / 10;
            celestialX = 145 + progress * 65;
            celestialY = 31 - Math.Sin(progress * Math.PI) * 22;
        }
        else
        {
            double progress = Math.Clamp((localHour - 6) / 14, 0, 1);
            celestialX = 76 + progress * 145;
            celestialY = 57 - Math.Sin(progress * Math.PI) * 50;
        }
        dc.PushOpacity(.06);
        dc.DrawEllipse(p[1], null, new Point(celestialX + 8, celestialY + 8), 13, 13);
        dc.Pop();
        // A round pixel moon, with gentle surface shading.
        for (int my = 0; my < 16; my++)
            for (int mx = 0; mx < 16; mx++)
            {
                double dx = mx - 7.5, dy = my - 7.5;
                if (dx * dx + dy * dy <= 58)
                    Box(p[1], celestialX + mx, celestialY + my, 1, 1);
            }
        dc.PushOpacity(.12);
        Box(p[2], celestialX + 4, celestialY + 5, 3, 2);
        Box(p[2], celestialX + 9, celestialY + 9, 3, 3);
        dc.Pop();
        if (period == 3)
        {
            for (int i = 0; i < 32; i++)
            {
                dc.PushOpacity(.35 + .3 * (1 + Math.Sin(time + i)) / 2);
                Box(p[1], (i * 37 + 11) % 240, (i * 13) % 35 + 5, 1, 1);
                dc.Pop();
            }
        }
        // A rare shooting star crosses the open sky, then leaves a quiet interval.
        double meteor = time % 23;
        if (meteor > 14 && meteor < 15.8)
        {
            double travel = (meteor - 14) / 1.8;
            dc.PushOpacity(Math.Sin(travel * Math.PI) * .8);
            for (int i = 0; i < 7; i++)
                Box(p[1], 35 + travel * 85 - i * 2, 7 + travel * 22 - i, 1, 1);
            dc.Pop();
        }
        // A small owl glides through the open sky between long quiet intervals.
        double flight = time % 57;
        if (flight > 25 && flight < 43)
        {
            double ox = (flight - 25) * 15 - 15;
            double oy = 30 + Math.Sin(flight * .5) * 3;
            int wing = (int)(time * 2) % 3 - 1;
            dc.PushOpacity(.65);
            Box(p[6], ox, oy, 2, 2);
            Box(p[6], ox - 3, oy + wing, 3, 1);
            Box(p[6], ox + 2, oy + wing, 3, 1);
            dc.Pop();
        }
        dc.PushOpacity(period == 1 ? .8 : .4);
        for (int i = 0; i < 3; i++)
        {
            double x = (i * 96 + time * 2) % 320 - 60;
            Box(p[6], x, 20 + i * 8, period == 1 ? 39 : 32, 3);
            Box(p[6], x + 6, 17 + i * 8, 17, period == 1 ? 6 : 3);
        }
        dc.Pop();
        if (period == 0)
        {
            // Morning mist hugs the distant hills.
            dc.PushOpacity(.25);
            Box(p[6], (time * 1.2) % 40 - 20, 65, 280, 4);
            Box(p[6], 0, 73, 240, 3);
            dc.Pop();
        }
        if (period < 3)
        {
            // Birds drift over the treetops in the warmer hours.
            for (int i = 0; i < 3; i++)
            {
                double bx = (time * 5 + i * 29 + 128) % 270 - 10;
                int by = 24 + i * 5;
                Box(p[3], bx, by, 2, 1);
                Box(p[3], bx + 2, by + ((int)(time * 3) % 2 == 0 ? 1 : -1), 2, 1);
            }
        }
        for (int x = 0; x < 240; x += 6)
        {
            int h = 22 + (int)Math.Round(Math.Sin(x / 24.0) * 12 + Math.Cos(x / 14.0) * 6);
            Box(p[2], x, 73 - h, 6, h + 27);
        }
        for (int x = 0; x < 240; x += Variant == 1 ? 66 : Variant == 2 ? 44 : 22)
        {
            if (Variant == 1 && x > 30 && x < 190) continue;
            int h = 24 + x * 7 % 29;
            Box(p[3], x + 8, 86 - h, 6, h);
            for (int k = 0; k < 4; k++) Box(p[3], x + 8 - k * 3, 86 - h + k * 7, 6 + k * 6, 7);
            dc.PushOpacity(.3);
            Box(p[2], x + 8 + Math.Sin(time * .7 + x) * .7, 86 - h, 3, 5);
            dc.Pop();
        }
        Box(p[4], 0, 86, 240, 14);
        // Low mist moves behind the foreground and keeps the forest feeling alive.
        dc.PushOpacity(.07);
        for (int i = 0; i < 3; i++)
            Box(p[6], (time * 1.5 + i * 97) % 340 - 80, 76 + i * 4, 65, 2);
        dc.Pop();
        // Slow luminous motes drift through the clearing on independent paths.
        for (int i = 0; i < 12; i++)
        {
            double mx = (i * 47 + time * (i % 2 == 0 ? .6 : -.4) + 480) % 240;
            double my = 71 + i * 7 % 24 + Math.Sin(time * .4 + i * 2) * 3;
            dc.PushOpacity(.08 + .18 * (1 + Math.Sin(time * .8 + i)) / 2);
            Box(i % 3 == 0 ? FlowerBlue : FlowerGold, mx, my, 1, 1);
            dc.Pop();
        }
        // A quiet clearing replaces water; all objects finish above the ground fade.
        for (int i = 0; i < (Variant == 2 ? 8 : 3); i++)
        {
            int rx = 110 + i * 19 % 65;
            int ry = 88 + i * 5 % 10;
            Box(p[2], rx, ry - 2, 5 + i % 3, 3);
            Box(p[3], rx + 1, ry - 3, 3, 1);
            Box(Stem, rx, ry + 1, 6, 1);
        }
        for (int i = 0; i < (Variant == 0 ? 7 : 3); i++)
        {
            int mx = 25 + i * 23;
            int my = 90 + i * 3 % 8;
            Box(FlowerGold, mx + 1, my - 2, 1, 3);
            Box(FlowerPink, mx, my - 3, 3, 1);
            Box(FlowerPink, mx - 1, my - 2, 5, 1);
        }
        // Scattered flowers, grasses, and stones stay within the complete scene.
        int flowerCount = Variant == 1 ? 38 : 14;
        for (int i = 0; i < flowerCount; i++)
        {
            int fx = (i * 43 + 13) % 235;
            int fy = 88 + i * 7 % 11;
            if (fx > 180) continue;
            int sway = Math.Sin(time * .9 + i) > .7 ? 1 : 0;
            Box(Stem, fx, fy - 3, 1, 4);
            Brush petals = i % 3 == 0 ? FlowerPink : i % 3 == 1 ? FlowerBlue : FlowerGold;
            Box(petals, fx - 1 + sway, fy - 4, 3, 1);
            Box(petals, fx + sway, fy - 5, 1, 3);
            Box(Stem, fx - 2, fy - 1, 2, 1);
        }
        for (int i = 0; i < 7; i++)
        {
            int gx = 9 + i * 31;
            if (gx > 180) continue;
            Box(Stem, gx, 96 - i % 4, 1, 3);
            Box(Stem, gx + 2, 95 - i % 4, 1, 4);
            if (i % 2 == 0) Box(p[2], gx + 5, 98 - i % 3, 3, 1);
        }
        if (period >= 2)
        {
            for (int glow = 3; glow > 0; glow--)
            {
                dc.PushOpacity((.025 + .008 * Math.Sin(time * 4)) * (4 - glow));
                dc.DrawEllipse(Ember, null, new Point(211, 89), 5 + glow * 4, 3 + glow * 2);
                dc.Pop();
            }
            Box(WolfShade, 205, 91, 12, 2);
            Box(Fire, 207, 86, 8, 5);
            Box(Ember, 209, 82 + Math.Round(Math.Sin(time * 5)), 4, 7);
            Box(Fire, 206, 85 + Math.Round(Math.Sin(time * 3 + 1)), 2, 5);
            Box(Fire, 214, 86 + Math.Round(Math.Sin(time * 4 + 2)), 2, 4);
            for (int i = 0; i < 4; i++)
            {
                double rise = (time * 5 + i * 4) % 17;
                dc.PushOpacity((1 - rise / 17) * .65);
                Box(Ember, 210 + Math.Sin(time * 2 + i) * 3, 83 - rise, 1, 1);
                dc.Pop();
            }
        }
        else Box(WolfShade, 207, 89, 8, 2); // unlit camp during daylight

        if (period < 2)
        {
            DrawWolf(dc, (time * 15 + 35) % 280 - 20, 89, time, false);
            DrawWolf(dc, (time * 12 + 130) % 280 - 20, 95, time + 1.2, true);
        }
        else
        {
            // Resting wolves by the fire: a subtle breath, no running gait.
            Box(WolfShade, 184, 90, 13, 4);
            Box(Wolf, 183, 88, 6, 4);
            Box(WolfShade, 185, 86, 2, 3);
            Box(Eye, 184, 90, 2, 1);
            Box(WolfShade, 223, 93, 12, 3);
            Box(Wolf, 230, 90 + (int)(Math.Sin(time) > .8 ? 1 : 0), 6, 4);
            Box(WolfShade, 231, 88, 2, 3);
            // A complete encounter loops without resetting when pages or scenery change.
            double patrol = time % 60;
            int encounter = (int)(time / 60) % 4;
            if (encounter == 0)
            {
            double wolfX;
            bool returning = patrol >= 30;
            bool hunting = patrol >= 22 && patrol < 30;
            bool walking = patrol < 20 || patrol >= 22 && patrol < 55;
            if (patrol < 20) wolfX = 28 + patrol * 4.4;
            else if (patrol < 22) wolfX = 116;
            else if (patrol < 30) wolfX = 116 + (patrol - 22) * 19;
            else if (patrol < 40) wolfX = 268 - (patrol - 30) * 15.2;
            else if (patrol < 55) wolfX = 116 - (patrol - 40) * (88.0 / 15);
            else wolfX = 28;
            DrawWolf(dc, returning ? 240 - wolfX : wolfX, 96, walking ? time * (hunting ? 1.15 : .55) : 0, returning);
            if (patrol >= 8 && patrol < 30)
            {
                double deerX = patrol < 20 ? 260 - (patrol - 8) * 8.5 : patrol < 22 ? 158 : 158 + (patrol - 22) * 20;
                DrawDeer(dc, deerX, 95, time, hunting, patrol < 22);
            }
            if (patrol >= 20 && patrol < 22)
            {
                double alertY = 70 - Math.Sin((patrol - 20) * Math.PI) * 2;
                Box(Alert, wolfX + 9, alertY, 2, 6);
                Box(Alert, wolfX + 9, alertY + 8, 2, 2);
            }
            }
            else
            {
                // Every story returns the wolf to the same starting position.
                double approach = Math.Clamp(patrol / 15, 0, 1);
                double retreat = Math.Clamp((patrol - 40) / 15, 0, 1);
                double wx = 28 + 72 * (approach - retreat);
                bool back = patrol >= 40;
                bool moving = patrol < 15 || patrol >= 40 && patrol < 55;
                double jump = 0;
                if (encounter == 1 && patrol >= 27 && patrol < 28)
                    jump = Math.Sin((patrol - 27) * Math.PI) * 3;
                if (encounter == 2 && patrol >= 18 && patrol < 36)
                {
                    wx += Math.Sin((patrol - 18) * Math.PI / 9) * 14;
                    jump = Math.Max(0, Math.Sin((patrol - 18) * Math.PI)) * 3;
                    moving = true;
                }
                if (encounter == 3 && patrol >= 24 && patrol < 26)
                    jump = Math.Sin((patrol - 24) * Math.PI / 2) * 7;
                DrawWolf(dc, back ? 240 - wx : wx, 96 - jump, moving ? time * .55 : 0, back);
                if (encounter == 1 && patrol >= 16 && patrol < 34)
                {
                    // Butterfly lands on the nose; a sneeze sends it fluttering away.
                    double bt = patrol - 16;
                    double bx = bt < 7 ? 150 - bt * 5 : bt < 11 ? wx + 15 : wx + 15 + (bt - 11) * 7;
                    double by = bt < 7 ? 70 + bt * 2 : bt < 11 ? 85 : 85 - (bt - 11) * 3;
                    int wing = (int)(time * 7) % 2;
                    Box(FlowerPink, bx - 2, by - wing, 2, 2 + wing);
                    Box(FlowerBlue, bx + 1, by - wing, 2, 2 + wing);
                    Box(Eye, bx, by, 1, 3);
                    if (patrol >= 27 && patrol < 28)
                        for (int i = 0; i < 3; i++)
                        {
                            dc.PushOpacity(28 - patrol);
                            Box(p[1], wx + 18 + (patrol - 27) * 10, 82 + i * 3, 1, 1);
                            dc.Pop();
                        }
                }
                if (encounter == 2 && patrol >= 16 && patrol < 38)
                {
                    // The glowing insect stays just out of reach of little hops.
                    double fx = wx + 16 + Math.Sin(time * 1.5) * 4;
                    double fy = 75 + Math.Sin(time * 2) * 4;
                    dc.PushOpacity(.12);
                    dc.DrawEllipse(FlowerGold, null, new Point(fx, fy), 4, 4);
                    dc.Pop();
                    Box(FlowerGold, fx, fy, 1, 1);
                }
                if (encounter == 3 && patrol >= 18 && patrol < 34)
                {
                    // A fox peeks over a rock, surprising the wolf, then ducks away.
                    double peek = Math.Clamp(Math.Min(patrol - 18, 34 - patrol) / 3, 0, 1);
                    double fy = 95 - peek * 12;
                    Box(Fire, 135, fy, 8, 6);
                    Box(Fire, 135, fy - 3, 2, 4);
                    Box(Fire, 141, fy - 3, 2, 4);
                    Box(DeerLight, 137, fy + 3, 4, 3);
                    Box(Eye, 136, fy + 2, 1, 1);
                    Box(Eye, 141, fy + 2, 1, 1);
                    Box(p[2], 132, 93, 14, 5);
                    Box(p[3], 134, 91, 10, 2);
                    if (patrol >= 24 && patrol < 26)
                    {
                        Box(Alert, wx + 8, 68 - jump, 2, 6);
                        Box(Alert, wx + 8, 76 - jump, 2, 2);
                    }
                }
            }
        }
        if (period >= 2)
            for (int i = 0; i < 8; i++)
            {
                dc.PushOpacity(.35 + .4 * (1 + Math.Sin(time * 1.4 + i)) / 2);
                Box(Ember, (i * 29 + 20 + time) % 240, 60 + i * 7 % 25 + Math.Sin(time * .7 + i) * 4, 1, 1);
                dc.Pop();
            }
        dc.Pop();
        dc.Pop();
        // Cool, stepped fog replaces the green smooth fade. Its edges drift in
        // whole pixel blocks below the animals and flowers, preserving the scene.
        double fogUnit = Math.Max(1, scale);
        double columnWidth = fogUnit * 8;
        for (int layer = 0; layer < GroundFog.Length; layer++)
            for (double x = 0; x < ActualWidth; x += columnWidth)
            {
                double drift = Math.Round(Math.Sin(x / (fogUnit * 27) + time * .18 + layer * .9) * 2) * fogUnit;
                double top = sceneHeight + layer * (64.0 / (GroundFog.Length - 1)) + drift;
                top = Math.Max(sceneHeight, top);
                if (layer == GroundFog.Length - 1) top = Math.Min(top, ActualHeight - 8);
                dc.DrawRectangle(GroundFog[layer], null,
                    new Rect(x, Math.Round(top), Math.Min(columnWidth, ActualWidth - x), Math.Max(0, ActualHeight - Math.Round(top))));
            }
        // Only the last few pixels soften; the layered fog above stays crisp.
        dc.DrawRectangle(FogEdge, null, new Rect(0, ActualHeight - 22, ActualWidth, 22));
        dc.Pop();
    }

    static void DrawDeer(DrawingContext dc, double x, double y, double t, bool running, bool reverse)
    {
        dc.PushTransform(new TranslateTransform(Math.Round(x), y));
        if (reverse) dc.PushTransform(new ScaleTransform(-1, 1));
        int stride = (int)(t * (running ? 12 : 4)) % 4;
        int bob = running ? stride % 2 : 0;
        void R(Brush b, int px, int py, int w, int h) => dc.DrawRectangle(b, null, new Rect(px, py + bob, w, h));
        R(DeerFur, 0, -10, 12, 5);
        R(DeerLight, 1, -6, 9, 2);
        R(DeerFur, 9, -16, 3, 9);
        R(DeerFur, 10, -17, 6, 4);
        R(DeerLight, 10, -20, 2, 3);
        R(Eye, 14, -16, 1, 1);
        R(DeerLight, -2, -11, 3, 2);
        R(DeerFur, stride < 2 ? 0 : 2, -5, 2, 5);
        R(DeerFur, stride < 2 ? 10 : 8, -5, 2, 5);
        R(DeerLight, 11, -24, 1, 6);
        R(DeerLight, 8, -23, 4, 1);
        R(DeerLight, 8, -25, 1, 3);
        R(DeerLight, 12, -22, 3, 1);
        R(DeerLight, 14, -24, 1, 3);
        if (reverse) dc.Pop();
        dc.Pop();
    }

    static void DrawWolf(DrawingContext dc, double x, double y, double t, bool reverse)
    {
        dc.PushTransform(new TranslateTransform(Math.Round(reverse ? 240 - x : x), Math.Round(y)));
        if (reverse) dc.PushTransform(new ScaleTransform(-1, 1));
        int stride = (int)(t * 9) % 4;
        int bob = stride % 2;
        void R(Brush b, int px, int py, int w, int h) => dc.DrawRectangle(b, null, new Rect(px, py + bob, w, h));
        R(WolfShade, -5, -8 - bob, 5, 3); R(WolfShade, -7, -10 - bob, 3, 3);
        R(Wolf, 0, -8, 11, 5); R(Wolf, 8, -12, 5, 7); R(Wolf, 11, -10, 5, 3);
        R(WolfShade, 8, -15, 2, 4); R(Wolf, 11, -14, 2, 3); R(Eye, 12, -11, 1, 1);
        R(Eye, 15, -9, 2, 1);
        R(WolfShade, stride < 2 ? -2 : 2, -3, 3, 4 - bob);
        R(Wolf, stride < 2 ? 10 : 7, -3, 2, 4 - bob);
        if (reverse) dc.Pop();
        dc.Pop();
    }

    static Brush[] Palette(params string[] colors) => colors.Select(Paint).ToArray();
    static Brush Paint(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
}

/// <summary>Native pixel wolf mark: slate fur, sage eyes, and a muted lavender headset.</summary>
public sealed class PixelWolfLogo : FrameworkElement
{
    public PixelWolfLogo() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    static readonly Brush[] Colors = [Brushes.Transparent, Paint("#AABCC3"), Paint("#E3EBE8"), Paint("#6F7E91"), Paint("#9FC6AF"), Paint("#B4A4CC")];
    static readonly string[] Pixels =
    [
        "0000000000000000", "0033000000330000", "0311300003113000", "0312130031213000",
        "0312213312213000", "0031221122130000", "0553122221355000", "0531141141135000",
        "0531441144135000", "0531222222135000", "0531123321135000", "0553113311355000",
        "0000312213000000", "0000032230055000", "0000003305550000", "0000000000000000"
    ];
    protected override void OnRender(DrawingContext dc)
    {
        double unit = Math.Min(ActualWidth, ActualHeight) / 16;
        for (int y = 0; y < Pixels.Length; y++)
            for (int x = 0; x < Pixels[y].Length; x++)
                if (Pixels[y][x] != '0') dc.DrawRectangle(Colors[Pixels[y][x] - '0'], null, new Rect(x * unit, y * unit, unit, unit));
    }
    static Brush Paint(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
}
