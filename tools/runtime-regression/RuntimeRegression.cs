using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodexStatusbar;

// Exercise the live adapters without running OverlayContext's startup/IPC/registry constructor.
internal static class RuntimeRegression
{
    private static readonly List<object> Results = [];
    private static int _failures;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        try
        {
            if (args.Length < 2) throw new ArgumentException("Usage: RuntimeRegression <output.json> <case|all>");
            if (args[1] is not ("6" or "7" or "8" or "9" or "10" or "13" or "all")) throw new ArgumentException("Unknown regression case: " + args[1]);
            if (args[1] is "6" or "all") FixedScreen();
            if (args[1] is "7" or "all") PendingGeometry();
            if (args[1] is "8" or "all") EditTransaction();
            if (args[1] is "9" or "all") LegacyMenu();
            if (args[1] is "10" or "all") ThemeMenu();
            if (args[1] is "13" or "all") PixelComposites();
            if (Results.Count == 0) Check("at least one runtime check ran", false);
        }
        catch (Exception exception)
        {
            Check("runtime regression completed without exception", false, exception.ToString());
        }
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { Failures = _failures, Results },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"runtime checks: {Results.Count}, failures: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool passed, object? detail = null)
    {
        Results.Add(new { Name = name, Passed = passed, Detail = detail });
        if (!passed) { _failures++; Console.Error.WriteLine("FAIL: " + name); }
    }

    private static void Set(object instance, string name, object? value) =>
        instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);

    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, args);

    private static CodexWindowTarget Host(IntRect bounds, uint dpi = 96) => new(new CodexWindowInfo(
        new IntPtr(1), bounds, bounds, null, new IntRect(0, 0, 20000, 20000), dpi,
        new WindowChromeMetrics((int)(46 * dpi / 96), (int)(32 * dpi / 96), 1, 1, 0)));

    private sealed class Fixture : IDisposable
    {
        public readonly OverlayContext Context = (OverlayContext)RuntimeHelpers.GetUninitializedObject(typeof(OverlayContext));
        public readonly OverlaySettings Settings = OverlaySettings.CreateDefault();
        public readonly TokenStripForm Form = new();
        public readonly HostWindowSampler Sampler = new();
        public readonly CodexIpcActiveThreadMonitor Route = new();
        public readonly ManualAttachmentCoordinator Attachment = new();
        public readonly ComposerDockTracker Dock = new(() => IntPtr.Zero, () => 0, _ => { });
        private readonly AttachmentTargetHighlightForm _highlight = new();
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "codex-runtime-regression-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
        private readonly System.Windows.Forms.Timer _timer = new();

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Set(Context, "_settings", Settings);
            Set(Context, "_settingsPath", SettingsPath);
            Set(Context, "_manualAttachment", Attachment);
            Set(Context, "_dockTracker", Dock);
            Set(Context, "_targetHighlight", _highlight);
            Set(Context, "_localizedMenuItems", new List<(ToolStripMenuItem, string, string)>());
            Set(Context, "_anchorItems", new Dictionary<AnchorMode, ToolStripMenuItem>());
            Set(Context, "_themeItems", new Dictionary<OverlayThemePreference, ToolStripMenuItem>());
            Set(Context, "_positionModeItems", new Dictionary<OverlayPositionMode, ToolStripMenuItem>());
            Set(Context, "_codexTheme", new CodexThemeSource(Path.Combine(DirectoryPath, "absent-config.toml")));
            Set(Context, "_dockSnapshot", ComposerDockSnapshot.Empty);
            Set(Context, "_lastDock", ComposerDockSnapshot.Empty);
            Set(Context, "_form", Form);
            Set(Context, "_hostSampler", Sampler);
            Set(Context, "_routeMonitor", Route);
            Set(Context, "_routeStatus", new ActiveThreadRouteStatus(null, 0, false, Route.Version, null));
            Set(Context, "_currentTarget", Host(new IntRect(10000, 10000, 1500, 900)));
            Set(Context, "_interaction", new OverlayInteractionState());
            Set(Context, "_anchorTargetState", new OverlayAnchorTargetState());
            Set(Context, "_outsideClickTimer", _timer);
            Set(Context, "_debug", new DebugDiagnostics(false));
            Set(Context, "_presentation", Form.CurrentPresentation);
            Set(Sampler, "_window", new IntPtr(1)); // Invalid handle: native reads cannot touch another app.
        }

        public void Dispose()
        {
            Form.Dispose(); Sampler.Dispose(); Route.Dispose(); _timer.Dispose(); _highlight.Dispose(); Dock.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static void FixedScreen()
    {
        using var fixture = new Fixture();
        fixture.Settings.PositionMode = OverlayPositionMode.FixedScreen;
        fixture.Settings.ManualPlacementEnabled = true;
        fixture.Settings.FixedScreenPosition = new Point(10000, 10000);
        fixture.Form.ApplyLayout(new OverlayLayoutResult(OverlayVisualState.Collapsed,
            CollapsedDisplayMode.TwoFields, ExpansionDirection.Down, 96,
            new IntRect(10000, 10000, 330, 34), new IntRect(0, 0, 330, 34), default, 0));
        fixture.Form.Show(); // Only this test's own no-activation form, outside the desktop.
        var real = (IFastPathTarget)fixture.Context;
        var before = new IntRect(100, 100, 1000, 700);
        var sample = new HostGeometrySample(before, before with { X = 200 }, 96, 96, 1, 1,
            new IntRect(0, 0, 20000, 20000), true);
        var scheduler = new PositionScheduler();
        var now = DateTime.UtcNow;
        scheduler.OnHostEvent(now);
        var action = FastPathDriver.Step(new Adapter(real, sample), scheduler, now, force: true);
        Check("6 fixed screen rejects host translation", action == FastPathAction.None
            && fixture.Form.Left == 10000 && fixture.Form.Top == 10000);
        foreach (var host in new[] { Host(before with { X = 300 }),
            Host(new IntRect(300, 200, 1500, 900)), Host(new IntRect(300, 200, 1500, 900), 144) })
        {
            Call(fixture.Context, "PlaceStrip", host, false);
            var layout = fixture.Form.CurrentLayout!;
            Check("6 accurate layout keeps the saved pixel point through move/resize/DPI",
                layout.WindowBounds.X + layout.CapsuleBounds.X == 10000
                && layout.WindowBounds.Y + layout.CapsuleBounds.Y == 10000 && layout.Dpi == host.HostWindow.Dpi);
        }
        fixture.Settings.PositionMode = OverlayPositionMode.FollowCodex;
        var oldX = fixture.Form.Left;
        action = FastPathDriver.Step(new Adapter(real, sample), scheduler, now.AddMilliseconds(20), force: true);
        Check("6 follow mode still translates through the real adapter", action == FastPathAction.MoveOnly
            && fixture.Form.Left == oldX + 100);
        ResumeFollowing();
    }

    private static void ResumeFollowing()
    {
        foreach (var resume in new[] { "fixed-to-follow", "cancel-edit", "save-edit" })
        {
            using var fixture = new Fixture();
            using var ownHost = new TokenStripForm { Bounds = new Rectangle(10000, 10000, 1500, 900) };
            ownHost.Show();
            if (!fixture.Sampler.Bind(ownHost.Handle, _ => { })) throw new InvalidOperationException("Own host bind failed");
            fixture.Sampler.TryBuildHostInfo(out var originalHost);
            var working = new IntRect(0, 0, 20000, 20000);
            Set(fixture.Sampler, "_workingArea", working);
            originalHost = originalHost with { WorkingArea = working };
            Set(fixture.Context, "_currentTarget", new CodexWindowTarget(originalHost));
            fixture.Settings.PositionMode = resume == "fixed-to-follow"
                ? OverlayPositionMode.FixedScreen : OverlayPositionMode.FollowCodex;
            fixture.Settings.FixedScreenPosition = new Point(10050, 10060);
            Call(fixture.Context, "PlaceStrip", new CodexWindowTarget(originalHost), false);
            if (resume != "fixed-to-follow") Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            ownHost.Left += 100;
            HostWindowSampler.TryReadWindowRect(ownHost.Handle, out var currentBounds);
            var movedHost = originalHost with
            {
                WindowBounds = currentBounds,
                ExtendedFrameBounds = originalHost.ExtendedFrameBounds with { X = originalHost.ExtendedFrameBounds.X + 100 }
            };
            Set(fixture.Context, "_currentTarget", new CodexWindowTarget(movedHost));
            if (resume == "fixed-to-follow")
            {
                using var menu = new ToolStripMenuItem("Position");
                Call(fixture.Context, "AddPositionModeMenu", menu, "跟随", "Follow", OverlayPositionMode.FollowCodex);
                menu.DropDownItems[0].PerformClick();
            }
            else if (resume == "cancel-edit") Call(fixture.Context, "CancelManualEditing", false, true);
            else Call(fixture.Context, "SaveManualEditing");
            Check("6/8 accurate layout synchronizes the consumed host before tracking resumes",
                fixture.Sampler.WindowBounds == currentBounds, resume);
            var laidX = fixture.Form.Left;
            var now = DateTime.UtcNow;
            var scheduler = new PositionScheduler();
            scheduler.OnHostEvent(now);
            ownHost.Left += 25;
            var action = FastPathDriver.Step((IFastPathTarget)fixture.Context, scheduler, now, force: true);
            Check("6/8 the first resumed move excludes motion already applied by accurate layout",
                action == FastPathAction.MoveOnly && fixture.Form.Left == laidX + 25, resume);
            ownHost.Left += 15;
            action = FastPathDriver.Step((IFastPathTarget)fixture.Context, scheduler, now.AddMilliseconds(20), force: true);
            Check("6/8 a second resumed move does not accumulate an old delta",
                action == FastPathAction.MoveOnly && fixture.Form.Left == laidX + 40, resume);
        }
    }

    private static void PixelComposites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-composite-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "composite.png");
        var bitmapOverload = typeof(RenderProbe).GetMethod("SaveComposite", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(Bitmap), typeof(string), typeof(Bitmap)], null)!;
        var colourOverload = typeof(RenderProbe).GetMethod("SaveComposite", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(Bitmap), typeof(string), typeof(Color)], null)!;
        var colour = Color.FromArgb(30, 31, 36);
        try
        {
            foreach (var dpi in new[] { 96, 120, 144, 192, 240 })
            {
                using var surface = new Bitmap(539, 41, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                surface.SetResolution(dpi, dpi);
                surface.SetPixel(529, 38, Color.FromArgb(128, 255, 0, 0));
                foreach (var backgroundDpi in new[] { 96, 192 })
                {
                    using var background = new Bitmap(surface.Width, surface.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    background.SetResolution(backgroundDpi, backgroundDpi);
                    using (var graphics = Graphics.FromImage(background)) graphics.Clear(colour);
                    bitmapOverload.Invoke(null, [surface, path, background]);
                    CheckComposite(path, colour, new { Dpi = dpi, BackgroundDpi = backgroundDpi });
                }
                colourOverload.Invoke(null, [surface, path, colour]);
                CheckComposite(path, colour, new { Dpi = dpi, SolidColour = true });
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckComposite(string path, Color background, object detail)
    {
        using var bitmap = new Bitmap(path);
        var nonOpaque = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).A != 255) nonOpaque++;
        Check("13 every background pixel is covered independently of image DPI", nonOpaque == 0, detail);
        Check("13 bottom-right transparent surface preserves the full background",
            bitmap.GetPixel(538, 40).ToArgb() == background.ToArgb(), detail);
        var marker = bitmap.GetPixel(529, 38);
        Check("13 a source pixel stays at its exact pixel coordinate during alpha composition",
            Math.Abs(marker.R - 143) <= 1 && Math.Abs(marker.G - 15) <= 1 && Math.Abs(marker.B - 18) <= 1, detail);
    }

    private static void ThemeMenu()
    {
        foreach (var language in new[] { OverlayLanguage.Chinese, OverlayLanguage.English })
        {
            using var fixture = new Fixture();
            fixture.Settings.Language = language;
            using var menu = new ToolStripMenuItem("Theme");
            Call(fixture.Context, "PopulateThemeMenu", menu);
            var dark = menu.DropDownItems.Cast<ToolStripMenuItem>().Single(item =>
                item.Text == (language == OverlayLanguage.Chinese ? "深色文字" : "Dark text"));
            dark.PerformClick();
            Check("10 dark text menu produces dark glyphs", fixture.Form.CurrentThemePalette.Value.R < 128, language);
            Check("10 dark text keeps the historical Light palette storage meaning",
                OverlaySettings.Load(fixture.SettingsPath).ThemePreference == OverlayThemePreference.Light);
            var light = menu.DropDownItems.Cast<ToolStripMenuItem>().Single(item =>
                item.Text == (language == OverlayLanguage.Chinese ? "浅色文字" : "Light text"));
            light.PerformClick();
            Check("10 light text menu produces light glyphs", fixture.Form.CurrentThemePalette.Value.R > 128, language);
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.ThemePreference = OverlaySettings.ParseJson("{\"theme\":\"Dark\"}").Settings.ThemePreference;
            Call(fixture.Context, "ApplyEffectiveTheme");
            Check("10 existing Dark settings retain the original light glyph palette",
                fixture.Form.CurrentThemePalette == OverlayThemePalette.For(OverlayThemeKind.Dark));
        }
    }

    private static void LegacyMenu()
    {
        foreach (var initialMode in new[] { OverlayPositionMode.ComposerContextLeft,
            OverlayPositionMode.FixedScreen, OverlayPositionMode.FollowCodex })
        {
            using var fixture = new Fixture();
            fixture.Settings.PositionMode = initialMode;
            fixture.Settings.ManualPlacementEnabled = true;
            fixture.Dock.Enabled = initialMode == OverlayPositionMode.ComposerContextLeft;
            using var menu = new ToolStripMenuItem("Legacy");
            Call(fixture.Context, "AddAnchorMenu", menu, "标题栏右上", "Title bar top right", AnchorMode.TitleBarTopRight);
            menu.DropDownItems[0].PerformClick();
            var saved = OverlaySettings.Load(fixture.SettingsPath);
            Check("9 legacy menu selects a host anchor outside dock/fixed modes",
                fixture.Settings.PositionMode == OverlayPositionMode.FollowCodex && !fixture.Settings.ManualPlacementEnabled
                && fixture.Settings.AnchorMode == AnchorMode.TitleBarTopRight, initialMode);
            Check("9 legacy menu persists the selected placement and disables docking",
                saved.PositionMode == OverlayPositionMode.FollowCodex && !saved.ManualPlacementEnabled && !fixture.Dock.Enabled);
            Check("9 legacy menu produces a visible anchored layout", fixture.Form.Visible
                && fixture.Form.CurrentLayout is { State: OverlayVisualState.Collapsed });
        }
    }

    private static void EditTransaction()
    {
        foreach (var mode in new[] { OverlayPositionMode.FixedScreen, OverlayPositionMode.ComposerContextLeft })
        {
            using var fixture = EditFixture(mode);
            var original = File.ReadAllText(fixture.SettingsPath);
            var oldPoint = fixture.Settings.FixedScreenPosition;
            var oldAttachment = fixture.Settings.MainAttachment;
            Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            Check("8 entering edit never persists a placement draft", File.ReadAllText(fixture.SettingsPath) == original, mode);
            fixture.Form.Location = new Point(12000, 12000);
            Call(fixture.Context, "HandleEditGestureCompleted", null,
                new OverlayEditPreviewEventArgs(OverlayEditGestureKind.Move, fixture.Form.Location, fixture.Form.Location, 100));
            Check("8 fixed-screen preview leaves committed settings unchanged",
                mode != OverlayPositionMode.FixedScreen || fixture.Settings.FixedScreenPosition == oldPoint);
            Call(fixture.Context, "CancelManualEditing", false, false);
            Check("8 cancel restores mode, screen point, attachment and persistence",
                !fixture.Attachment.IsEditing && fixture.Settings.PositionMode == mode
                && fixture.Settings.FixedScreenPosition == oldPoint && fixture.Settings.MainAttachment == oldAttachment
                && File.ReadAllText(fixture.SettingsPath) == original, mode);
            Check("8 cancel restores dock tracker mode", fixture.Dock.Enabled == (mode == OverlayPositionMode.ComposerContextLeft));
        }
        using (var fixture = EditFixture(OverlayPositionMode.FixedScreen))
        {
            Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            fixture.Form.Location = new Point(12000, 12000);
            Call(fixture.Context, "HandleEditGestureCompleted", null,
                new OverlayEditPreviewEventArgs(OverlayEditGestureKind.Move, fixture.Form.Location, fixture.Form.Location, 100));
            Call(fixture.Context, "SaveManualEditing");
            var saved = OverlaySettings.Load(fixture.SettingsPath);
            Check("8 successful fixed-screen save commits the exact pixel point once",
                !fixture.Attachment.IsEditing && saved.PositionMode == OverlayPositionMode.FixedScreen
                && saved.FixedScreenPosition == new Point(12000, 12000)
                && fixture.Settings.FixedScreenPosition == saved.FixedScreenPosition);
        }
        using (var fixture = EditFixture(OverlayPositionMode.FixedScreen))
        {
            var original = File.ReadAllText(fixture.SettingsPath);
            var oldPoint = fixture.Settings.FixedScreenPosition;
            Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            fixture.Form.Location = new Point(12000, 12000);
            Call(fixture.Context, "HandleEditGestureCompleted", null,
                new OverlayEditPreviewEventArgs(OverlayEditGestureKind.Move, fixture.Form.Location, fixture.Form.Location, 100));
            Set(fixture.Context, "_settingsPath", fixture.DirectoryPath); // Saving over a directory must fail.
            Set(fixture.Context, "_saveFailureNotified", true); // Suppress tray notifications in this isolated test.
            Call(fixture.Context, "SaveManualEditing");
            Check("8 save failure retains the edit and original committed settings",
                fixture.Attachment.IsEditing && fixture.Settings.FixedScreenPosition == oldPoint
                && File.ReadAllText(fixture.SettingsPath) == original);
            Call(fixture.Context, "ApplyLayout", Host(new IntRect(10000, 10000, 1500, 900)));
            Check("8 layout after failure still uses the pending fixed-screen point",
                fixture.Form.Left == 12000 && fixture.Form.Top == 12000);
            Set(fixture.Context, "_settingsPath", fixture.SettingsPath);
            Call(fixture.Context, "SaveManualEditing");
            Check("8 retry after save failure retains the draft point",
                !fixture.Attachment.IsEditing && OverlaySettings.Load(fixture.SettingsPath).FixedScreenPosition == new Point(12000, 12000));
        }
        using (var fixture = EditFixture(OverlayPositionMode.ComposerContextLeft))
        {
            var oldAttachment = fixture.Settings.MainAttachment;
            var original = File.ReadAllText(fixture.SettingsPath);
            Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            Set(fixture.Context, "_settingsPath", fixture.DirectoryPath);
            Set(fixture.Context, "_saveFailureNotified", true);
            Call(fixture.Context, "SaveManualEditing");
            Check("8 failed dock-to-manual save keeps original mode and persistence",
                fixture.Attachment.IsEditing && fixture.Settings.PositionMode == OverlayPositionMode.ComposerContextLeft
                && fixture.Settings.MainAttachment == oldAttachment && File.ReadAllText(fixture.SettingsPath) == original);
            Call(fixture.Context, "CancelManualEditing", false, false);
            Check("8 cancel after failure restores docking", fixture.Dock.Enabled && !fixture.Attachment.IsEditing);
        }
        using (var fixture = EditFixture(OverlayPositionMode.ComposerContextLeft))
        {
            Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            fixture.Settings.ThemePreference = OverlayThemePreference.Light;
            fixture.Settings.Save(fixture.SettingsPath);
            Check("8 an unrelated settings save cannot persist the placement draft",
                OverlaySettings.Load(fixture.SettingsPath).PositionMode == OverlayPositionMode.ComposerContextLeft);
            Call(fixture.Context, "SaveManualEditing");
            var saved = OverlaySettings.Load(fixture.SettingsPath);
            Check("8 successful dock-to-manual save commits mode and keeps unrelated preferences",
                !fixture.Attachment.IsEditing && !fixture.Dock.Enabled && saved.PositionMode == OverlayPositionMode.FollowCodex
                && saved.ManualPlacementEnabled && saved.ThemePreference == OverlayThemePreference.Light);
        }
        using (var fixture = EditFixture(OverlayPositionMode.FixedScreen))
        {
            var original = File.ReadAllBytes(fixture.SettingsPath);
            var oldPoint = fixture.Settings.FixedScreenPosition;
            Call(fixture.Context, "BeginManualEditingForCurrentTarget");
            fixture.Form.Location = new Point(12000, 12000);
            Set(fixture.Context, "_saveFailureNotified", true);
            // Deny rename/delete while permitting writes: staging succeeds but atomic replacement fails.
            using (var blocker = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Call(fixture.Context, "SaveManualEditing");
                Check("8 failed atomic replacement preserves original bytes and committed state",
                    fixture.Attachment.IsEditing && fixture.Settings.FixedScreenPosition == oldPoint
                    && File.ReadAllBytes(fixture.SettingsPath).SequenceEqual(original));
                Check("8 failed atomic replacement cleans only its own staging file",
                    Directory.GetFiles(fixture.DirectoryPath).Length == 1);
            }
            if (fixture.Attachment.IsEditing) Call(fixture.Context, "SaveManualEditing");
            Check("8 atomic save retries after the target lock is released",
                !fixture.Attachment.IsEditing && OverlaySettings.Load(fixture.SettingsPath).FixedScreenPosition == new Point(12000, 12000));
        }
    }

    private static Fixture EditFixture(OverlayPositionMode mode)
    {
        var fixture = new Fixture();
        fixture.Settings.PositionMode = mode;
        fixture.Settings.ManualPlacementEnabled = true;
        fixture.Settings.FixedScreenPosition = new Point(10050, 10060);
        fixture.Settings.Save(fixture.SettingsPath);
        fixture.Dock.Enabled = mode == OverlayPositionMode.ComposerContextLeft;
        Set(fixture.Context, "_currentTarget", Host(new IntRect(10000, 10000, 1500, 900)));
        fixture.Form.ApplyLayout(new OverlayLayoutResult(OverlayVisualState.Collapsed,
            CollapsedDisplayMode.TwoFields, ExpansionDirection.Down, 96,
            new IntRect(10050, 10060, 330, 34), new IntRect(0, 0, 330, 34), default, 0));
        fixture.Form.Show();
        return fixture;
    }

    private static void PendingGeometry()
    {
        using var sampler = new HostWindowSampler();
        var before = new IntRect(100, 100, 1000, 700);
        var after = new IntRect(200, 150, 1500, 900);
        var working = new IntRect(0, 0, 2560, 1440);
        Set(sampler, "_window", new IntPtr(1));
        Set(sampler, "_primed", true);
        Set(sampler, "_windowBounds", before);
        Set(sampler, "_frameBounds", before);
        Set(sampler, "_workingArea", working);
        Set(sampler, "_dpi", 96u);
        Set(sampler, "_monitor", 1L);
        SetPending(sampler, after, 144, working);
        var sample = new HostGeometrySample(before, after, 96, 144, 1, 2, working, true);
        var target = new SamplerAdapter(sampler, sample);
        var now = DateTime.UtcNow;
        var scheduler = new PositionScheduler();
        scheduler.OnHostEvent(now);
        var action = FastPathDriver.Step(target, scheduler, now, force: true);
        Check("7 relayout consumes the current sampled host, frame, working area and DPI",
            action == FastPathAction.Relayout && target.Used is { } used && used.WindowBounds == after
            && used.ExtendedFrameBounds == after && used.WorkingArea == working && used.Dpi == 144);
        Check("7 geometry is uncommitted while the relayout consumes it", target.BaselineDuringLayout == before);
        Check("7 successful relayout commits exactly once", sampler.WindowBounds == after && sampler.CommitCount == 1);

        var next = after with { Width = 1600 };
        SetPending(sampler, next, 144, working);
        target.Sample = new HostGeometrySample(after, next, 144, 144, 2, 2, working, true);
        action = FastPathDriver.Step(target, scheduler, now.AddMilliseconds(1), force: false);
        Check("7 a rate-limited resize does not consume pending geometry",
            action == FastPathAction.None && sampler.WindowBounds == after && sampler.CommitCount == 1);
        action = FastPathDriver.Step(target, scheduler, now.AddMilliseconds(16), force: true);
        Check("7 catch-up frame applies pending geometry once",
            action == FastPathAction.Relayout && target.Used?.WindowBounds == next && sampler.WindowBounds == next
            && sampler.CommitCount == 2);
        sampler.TryBuildHostInfo(out var committed);
        Check("7 reads without pending data use the committed geometry", committed.WindowBounds == next && committed.Dpi == 144);

        using var ownHost = new TokenStripForm { Bounds = new Rectangle(10000, 10000, 1000, 700) };
        using var liveSampler = new HostWindowSampler();
        ownHost.Show();
        Check("7 native sampler binds only to this test's own window", liveSampler.Bind(ownHost.Handle, _ => { }));
        liveSampler.TryBuildHostInfo(out var oldInfo);
        ownHost.Left += 40;
        var sampled = liveSampler.TrySample(out var moved);
        liveSampler.TryBuildHostInfo(out var pendingInfo);
        Check("7 native translation moves pending frame together with window bounds",
            sampled && pendingInfo.WindowBounds.X == oldInfo.WindowBounds.X + 40
            && pendingInfo.ExtendedFrameBounds.X == oldInfo.ExtendedFrameBounds.X + 40
            && liveSampler.WindowBounds == oldInfo.WindowBounds);
        liveSampler.Commit();
        ownHost.Left += 30;
        Check("7 subsequent native movement starts at the committed sample",
            liveSampler.TrySample(out var secondMove) && secondMove.PreviousBounds == moved.CurrentBounds
            && secondMove.CurrentBounds.X - secondMove.PreviousBounds.X == 30);
    }

    private static void SetPending(HostWindowSampler sampler, IntRect bounds, uint dpi, IntRect working)
    {
        Set(sampler, "_pendingBounds", bounds);
        Set(sampler, "_pendingFrame", bounds);
        Set(sampler, "_pendingWorkingArea", working);
        Set(sampler, "_pendingDpi", dpi);
        Set(sampler, "_pendingMonitor", 2L);
        Set(sampler, "_hasPending", true);
    }

    private sealed class SamplerAdapter(HostWindowSampler sampler, HostGeometrySample sample) : IFastPathTarget
    {
        public HostGeometrySample Sample = sample;
        public CodexWindowInfo? Used;
        public IntRect BaselineDuringLayout;
        public bool CanTrack => true;
        public IntRect CurrentWindowBounds => new(600, 500, 330, 34);
        public bool TrySampleHost(out HostGeometrySample value) { value = Sample; return true; }
        public void CommitHostGeometry() => sampler.Commit();
        public void MoveWindowTo(IntRect bounds) => throw new InvalidOperationException("Expected relayout");
        public void RelayoutFromAccuratePath()
        {
            BaselineDuringLayout = sampler.WindowBounds;
            sampler.TryBuildHostInfo(out var used);
            Used = used;
        }
        public void HideForUnusableHost() => throw new InvalidOperationException("Unexpected hide");
    }

    private sealed class Adapter(IFastPathTarget real, HostGeometrySample sample) : IFastPathTarget
    {
        public bool CanTrack => real.CanTrack;
        public IntRect CurrentWindowBounds => real.CurrentWindowBounds;
        public bool TrySampleHost(out HostGeometrySample value) { value = sample; return true; }
        public void CommitHostGeometry() { }
        public void MoveWindowTo(IntRect bounds) => real.MoveWindowTo(bounds);
        public void RelayoutFromAccuratePath() => real.RelayoutFromAccuratePath();
        public void HideForUnusableHost() => real.HideForUnusableHost();
    }
}
