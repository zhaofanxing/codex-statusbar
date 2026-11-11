using System.Windows.Automation;

namespace CodexStatusbar;

/// <summary>Which element the strip is currently docked against.</summary>
internal enum ComposerReferenceSource
{
    /// <summary>No usable reference; the strip cannot be docked.</summary>
    None = 0,

    /// <summary>The official Context-usage indicator itself. The preferred source.</summary>
    UiaContext = 1,

    /// <summary>The model selector: its left edge stands in for the Context indicator's.</summary>
    UiaModel = 2,

    /// <summary>The composer card: the toolbar row is derived from its bottom-right corner.</summary>
    UiaComposer = 3,

    /// <summary>Nothing but the Codex window rectangle: fixed DIP offsets from its bottom-right.</summary>
    Window = 4
}

/// <summary>
/// One measurement of the Codex composer toolbar, in physical screen pixels.
///
/// <para>Every rectangle here was read from the real UI Automation tree of the running Codex
/// Desktop window, not inferred from a window-size percentage. <see cref="ReferenceRect"/> is the
/// element the strip's right edge is placed against; <see cref="RowRect"/> is the toolbar row's own
/// box, used for vertical alignment, because the Context icon is not centred in its row (its CSS
/// <c>vertical-align: middle</c> sits it ~2.5 px low at 150%) and aligning to it would put the text
/// baseline below the model selector's.</para>
/// </summary>
internal sealed record ComposerDockSnapshot(
    ComposerReferenceSource Source,
    IntRect ReferenceRect,
    IntRect RowRect,
    IntRect ComposerRect,
    IntRect LeftClusterRect,
    string ElementName,
    string AutomationId,
    string ControlType,
    string ClassName,
    long WindowHandle,
    int ElementIndex,
    string? Failure,
    bool IsDraft = false,
    long RouteVersion = -1)
{
    public static readonly ComposerDockSnapshot Empty = new(
        ComposerReferenceSource.None,
        default,
        default,
        default,
        default,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        -1,
        null);

    /// <summary>True when there is a real element rectangle to dock against.</summary>
    public bool HasReference =>
        Source is ComposerReferenceSource.UiaContext or ComposerReferenceSource.UiaModel
        && !ReferenceRect.IsEmpty;

    public bool CanDock(long windowHandle, long routeVersion) =>
        WindowHandle == windowHandle && RouteVersion == routeVersion
        && HasReference && !OverlayLayoutCalculator.IsUnusableRect(ReferenceRect);

    /// <summary>True when the toolbar row's own box is known, which is what vertical alignment uses.</summary>
    public bool HasRow => !RowRect.IsEmpty;

    /// <summary>The row's vertical centre: the row box when known, otherwise the reference box.</summary>
    public int RowCenterY => Source != ComposerReferenceSource.UiaModel && HasRow
        ? RowRect.Y + (RowRect.Height / 2)
        : ReferenceRect.Y + (ReferenceRect.Height / 2);

    public string DescribeSource() => Source switch
    {
        ComposerReferenceSource.UiaContext => "uia-context",
        ComposerReferenceSource.UiaModel => "uia-model",
        ComposerReferenceSource.UiaComposer => "uia-composer",
        ComposerReferenceSource.Window => "window-fallback",
        _ => "none"
    };
}

/// <summary>
/// Finds and tracks the official Codex composer toolbar through UI Automation.
///
/// <para><b>The ladder.</b> The Context-usage indicator is the primary reference because it is a
/// direct child of the composer card and survived every geometry change tested (resize, maximise,
/// restore, move). The model selector is the fallback: it is found the same way but React replaces
/// its DOM node on relayout, so a held reference to it goes stale and it must be re-discovered.</para>
///
/// <para><b>Why a thread.</b> UI Automation reads cross the process boundary and can block for as
/// long as the target application takes to answer. All UIA work therefore happens on one dedicated
/// background thread which publishes an immutable snapshot; the overlay's UI thread only ever reads
/// that snapshot, so a slow or wedged Codex cannot stall the strip.</para>
///
/// <para><b>Read-only.</b> Nothing here invokes a pattern, sets a value, or sends input. The only
/// operations are tree walks and property reads.</para>
/// </summary>
internal sealed class ComposerDockTracker : IDisposable
{
    /// <summary>ClassName Chromium gives the composer's contenteditable host.</summary>
    private const string ComposerEditorClass = "ProseMirror";
    private const string DraftComposerClass = "group/home-composer-layout";

    /// <summary>English and Chinese labels of the Context-usage indicator, used only as a hint.</summary>
    private static readonly string[] ContextNameHints =
    {
        "上下文用量", "Context usage", "Context window", "context window"
    };

    /// <summary>Design-system class fragment on the Context indicator, a text-independent signal.</summary>
    private const string ContextClassHint = "codex-description";

    /// <summary>
    /// How often the fast path (re-reading the held element) runs when nothing is moving. This is the
    /// accurate path's own cadence and is unchanged from the original design.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The fastest the fast path may be asked to run, in milliseconds.</summary>
    private const int MinimumPollIntervalMs = 16;

    /// <summary>The slowest it may be asked to run. A request can never make this thread lazier than idle.</summary>
    private const int MaximumPollIntervalMs = (int)250;

    /// <summary>A full tree walk at least this often, as a safety net against silent layout drift.</summary>
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MissingReferenceRetryInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<IntPtr> _windowProvider;
    private readonly Func<long> _routeVersionProvider;
    private readonly Action<IntPtr> _observeWindow;
    private Thread? _thread;
    private CancellationTokenSource? _cancellation;
    private volatile ComposerDockSnapshot _latest = ComposerDockSnapshot.Empty;
    private volatile bool _enabled = true;
    private long _walkCount;
    private long _fastReadCount;
    private long _lastWalkMilliseconds;
    private long _revision;
    private long _resyncRequestCount;
    private long _lastReadTicks;

    /// <summary>
    /// Requested interval for the cheap re-read, in milliseconds. The position fast path raises this
    /// during a drag or a resize so the calibration keeps up with the motion; it is lowered again when
    /// the burst ends, so an idle machine polls exactly as often as it always did.
    ///
    /// <para>Note what this does <b>not</b> change: a full tree walk still happens only when the
    /// reference rectangle moves, when the reference is lost, or on the 20 s safety resync. The burst
    /// raises the rate of one cross-process rectangle read, never the rate of a traversal.</para>
    /// </summary>
    private volatile int _pollIntervalMs = (int)PollInterval.TotalMilliseconds;

    /// <summary>
    /// Whether the cheap re-read path is currently usable, i.e. the loop is holding a toolbar element it
    /// can re-read with one cross-process call.
    ///
    /// <para>This is deliberately a <b>live</b> flag rather than a reading of the last published snapshot:
    /// the snapshot can still say <c>uia-context</c> for a frame or two after the element has gone, and a
    /// caller that used it to decide "the accurate path is cheap right now" would speed up a loop that is
    /// about to fall back to walking the whole tree on every poll. That is not hypothetical — it turned
    /// one drag into 8 traversals per burst before this flag existed.</para>
    /// </summary>
    private volatile bool _hasCheapPath;

    /// <summary>
    /// Set by <see cref="RequestResync"/>, consumed by the loop. A plain int driven through
    /// <c>Interlocked</c>: the interlocked exchange is the barrier, so marking it volatile as well would
    /// only make the <c>ref</c> argument stop being volatile (and warn about it).
    /// </summary>
    private int _resyncRequested;

    /// <summary>
    /// Whether a moving reference rectangle should trigger a full tree walk.
    ///
    /// <para>True is the original behaviour and stays true whenever the host is at rest: a reference that
    /// moved means the composer re-laid-out, and only a traversal can re-read its siblings. The position
    /// fast path turns it off while a burst is in flight, because during motion the reference moves on
    /// every read and each one would become a traversal — 25 per second during a resize. The motion is
    /// answered geometrically instead, and one forced <see cref="RequestResync"/> when it ends confirms
    /// the settled layout.</para>
    /// </summary>
    public bool WalkOnReferenceMove { get; set; } = true;

    public ComposerDockTracker(
        Func<IntPtr> windowProvider, Func<long> routeVersionProvider, Action<IntPtr> observeWindow)
    {
        _windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
        _routeVersionProvider = routeVersionProvider;
        _observeWindow = observeWindow;
    }

    public ComposerDockSnapshot Latest => _latest;

    /// <summary>通知会话或停靠可用性变化，普通坐标变化仍由定位循环处理。</summary>
    public event Action? DockStateChanged;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public long WalkCount => Interlocked.Read(ref _walkCount);

    public long FastReadCount => Interlocked.Read(ref _fastReadCount);

    public long LastWalkMilliseconds => Interlocked.Read(ref _lastWalkMilliseconds);

    /// <summary>Increments on every published snapshot change, so the owner can tell stale from fresh.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    public long ResyncRequestCount => Interlocked.Read(ref _resyncRequestCount);

    /// <summary>True while the accurate path can re-read its held element instead of walking the tree.</summary>
    public bool HasCheapPath => _hasCheapPath;

    /// <summary>
    /// Milliseconds since the accurate path last took a measurement — a cheap rectangle re-read or a
    /// full walk. This is the age of the numbers the placement is using, which is what the position
    /// diagnostics need; the age of the last *change* would be misleading, because a stable window
    /// stops changing while it is still being measured.
    /// </summary>
    public double MillisecondsSinceLastRead
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastReadTicks);
            return ticks == 0 ? -1 : Environment.TickCount64 - ticks;
        }
    }

    /// <summary>
    /// Accurate-path cadence, in milliseconds. Clamped: a caller may ask for a faster re-read, never
    /// for a slower one than the idle default, and never for a busy loop.
    /// </summary>
    public int AccurateRefreshIntervalMs
    {
        get => _pollIntervalMs;
        set => _pollIntervalMs = Math.Clamp(value, MinimumPollIntervalMs, MaximumPollIntervalMs);
    }

    /// <summary>
    /// Asks for one full discovery pass on the next iteration. Used at the end of a move/resize burst:
    /// the cheap re-read keeps the reference rectangle honest during the motion, but only a traversal
    /// can confirm that the composer's *siblings* (the left cluster, the row) settled back where the
    /// layout thinks they are.
    /// </summary>
    public void RequestResync()
    {
        Interlocked.Increment(ref _resyncRequestCount);
        Interlocked.Exchange(ref _resyncRequested, 1);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _thread = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "CodexStatusbar.ComposerDock"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            thread = _thread;
            cancellation = _cancellation;
            _thread = null;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        // Do not join: a UIA call inside the target process can outlive the cancel request, and the
        // thread is a background thread so it cannot keep the process alive.
        _ = thread;
        cancellation?.Dispose();
    }

    private void Run(CancellationToken token)
    {
        AutomationElement? composer = null;
        AutomationElement? reference = null;
        var lastReferenceRect = default(IntRect);
        var walksRemaining = 2;
        var lastWalkAt = DateTime.UtcNow;
        var rebinding = true;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var hwnd = _windowProvider();
                // 固定位置模式也需要跟随会话。读取标题复用此后台线程，避免阻塞界面。
                _observeWindow(hwnd);
                if (!_enabled || hwnd == IntPtr.Zero)
                {
                    Publish(ComposerDockSnapshot.Empty);
                    reference = null;
                    composer = null;
                    walksRemaining = 2;
                    rebinding = true;
                    _hasCheapPath = false;
                    Sleep(token);
                    continue;
                }

                var routeVersion = _routeVersionProvider();
                if (_latest.WindowHandle != hwnd.ToInt64() || _latest.RouteVersion != routeVersion)
                {
                    // 同一个 HWND 也会在切换会话时更换输入框，必须重新发现控件。
                    reference = null;
                    composer = null;
                    walksRemaining = 2;
                    rebinding = true;
                }

                // Fast path: re-read the held element only. One cross-process call instead of a walk.
                var fastRect = default(IntRect);
                if (reference is not null)
                {
                    if (TryReadRect(reference, out fastRect))
                    {
                        Interlocked.Increment(ref _fastReadCount);
                        Interlocked.Exchange(ref _lastReadTicks, Environment.TickCount64);
                    }
                    else
                    {
                        reference = null;
                        composer = null;
                        walksRemaining = 2;
                        rebinding = true;
                        Publish(_latest with { Source = ComposerReferenceSource.None });
                    }
                }

                if (!fastRect.IsEmpty && fastRect != lastReferenceRect && WalkOnReferenceMove)
                {
                    // The layout moved. Two walks, not one: the Context indicator's own rectangle is
                    // updated before its siblings', so a single walk taken as soon as the reference
                    // moves can read the *previous* composer layout for everything else. Measured at
                    // the 1100 px sidebar breakpoint, where one walk left the left-cluster boundary at
                    // its pre-breakpoint value (607 px instead of 880) for as long as the geometry
                    // stayed put — the resync interval was the only thing that eventually fixed it.
                    walksRemaining = 2;
                }

                if (Interlocked.Exchange(ref _resyncRequested, 0) != 0)
                {
                    walksRemaining = 2;
                }

                var resyncInterval = reference is null ? MissingReferenceRetryInterval : ResyncInterval;
                if (DateTime.UtcNow - lastWalkAt > resyncInterval)
                {
                    walksRemaining = 2;
                }

                if (walksRemaining > 0 || composer is null)
                {
                    var walkStarted = Environment.TickCount64;
                    var snapshot = Walk(hwnd, ref composer, out reference) with { RouteVersion = routeVersion };
                    Interlocked.Increment(ref _walkCount);
                    Interlocked.Exchange(ref _lastWalkMilliseconds, Environment.TickCount64 - walkStarted);
                    Interlocked.Exchange(ref _lastReadTicks, Environment.TickCount64);
                    lastWalkAt = DateTime.UtcNow;
                    walksRemaining--;
                    lastReferenceRect = snapshot.ReferenceRect;
                    if (rebinding && walksRemaining > 0)
                    {
                        // 渲染器可能仍暴露切换前的控件，完成第二次重新发现前保持隐藏。
                        composer = null;
                        snapshot = snapshot with { Source = ComposerReferenceSource.None, Failure = "calibrating" };
                    }
                    else
                    {
                        rebinding = false;
                    }

                    Publish(snapshot);
                }
                else if (!fastRect.IsEmpty)
                {
                    lastReferenceRect = fastRect;
                    Publish(_latest with
                    {
                        ReferenceRect = fastRect
                    });
                }

                // The live answer to "can the next iteration be a cheap re-read". Recomputed every pass
                // from the element the loop is actually holding.
                _hasCheapPath = reference is not null;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
            {
                Publish(_latest with { Source = ComposerReferenceSource.None, Failure = exception.GetType().Name });
                _hasCheapPath = false;
                reference = null;
                composer = null;
                walksRemaining = 2;
                rebinding = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Publish(ComposerDockSnapshot.Empty with { Failure = exception.GetType().Name });
                _hasCheapPath = false;
                reference = null;
                composer = null;
                walksRemaining = 2;
                rebinding = true;
                Sleep(token);
            }

            Sleep(token);
        }
    }

    private void Sleep(CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(_pollIntervalMs);
        try
        {
            token.WaitHandle.WaitOne(interval);
        }
        catch (ObjectDisposedException)
        {
            Thread.Sleep(interval);
        }
    }

    private void Publish(ComposerDockSnapshot snapshot)
    {
        var previous = _latest;
        if (snapshot == previous)
        {
            return;
        }

        _latest = snapshot;
        Interlocked.Increment(ref _revision);
        if (snapshot.HasReference != previous.HasReference || snapshot.IsDraft != previous.IsDraft
            || snapshot.WindowHandle != previous.WindowHandle || snapshot.RouteVersion != previous.RouteVersion)
        {
            DockStateChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ the walk

    /// <summary>
    /// One full discovery pass. Returns the best snapshot the ladder can produce, holding on to the
    /// AutomationElements so the next iterations can use the cheap fast path.
    /// </summary>
    private static ComposerDockSnapshot Walk(
        IntPtr hwnd,
        ref AutomationElement? composer,
        out AutomationElement? reference)
    {
        reference = null;
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null)
        {
            composer = null;
            return ComposerDockSnapshot.Empty with { WindowHandle = hwnd.ToInt64(), Failure = "no-root" };
        }

        var walker = TreeWalker.ControlViewWalker;

        // Re-acquire the composer when the held node died.
        if (composer is not null && !TryReadRect(composer, out _))
        {
            composer = null;
        }

        composer ??= FindComposer(root, walker);
        if (composer is null)
        {
            return ComposerDockSnapshot.Empty with
            {
                Source = ComposerReferenceSource.Window,
                WindowHandle = hwnd.ToInt64(),
                Failure = "no-composer"
            };
        }

        if (!TryReadRect(composer, out var composerRect))
        {
            composer = null;
            return ComposerDockSnapshot.Empty with { WindowHandle = hwnd.ToInt64(), Failure = "composer-stale" };
        }

        // Enumerate the composer's direct children once: the Context indicator, the model selector
        // and the left control cluster are all read out of this single list.
        var children = new List<ChildInfo>(16);
        var childElement = TryFirstChild(walker, composer);
        var index = 0;
        while (childElement is not null && index < 64)
        {
            if (TryReadRect(childElement, out var rect)
                && TryReadProperties(
                    childElement,
                    out var childControlType,
                    out var childName,
                    out var childAutomationId,
                    out var childClassName))
            {
                children.Add(new ChildInfo(
                    childElement,
                    index,
                    rect,
                    childControlType,
                    childName,
                    childAutomationId,
                    childClassName));
            }

            childElement = TryNextSibling(walker, childElement);
            index++;
        }

        // 草稿也可能有附件图片，只有能识别为上下文指示器的图标才能作为定位参考。
        var contextIndex = children.FindIndex(candidate => candidate.ControlType == "Image"
            && LooksLikeContextUsage(candidate.Name, candidate.ClassName));
        var contextRect = default(IntRect);
        if (contextIndex >= 0)
        {
            var element = children[contextIndex].Element;
            if (TryReadRect(element, out contextRect) && OverlapsComposer(contextRect, composerRect))
            {
                reference = element;
            }
            else
            {
                contextRect = default;
                contextIndex = -1;
            }
        }

        // Model selector: a Button (possibly one level down inside a display:contents group) that
        // exposes ExpandCollapse — i.e. it opens a menu — lying to the right of the Context icon.
        AutomationElement? model = null;
        var modelRect = default(IntRect);
        var toolbarRowHint = default(IntRect);
        var modelFound = false;
        var minimumModelLeft = MinimumModelAnchorLeft(composerRect, contextRect);
        foreach (var candidate in children)
        {
            if (!TryExpandableButton(candidate.Element, walker, out var button, out var buttonRect))
            {
                continue;
            }

            if (!OverlapsComposer(buttonRect, composerRect))
            {
                continue;
            }

            if (toolbarRowHint.IsEmpty || buttonRect.Bottom > toolbarRowHint.Bottom)
            {
                toolbarRowHint = buttonRect;
            }

            if (buttonRect.Left < minimumModelLeft || buttonRect.Left >= composerRect.Right)
            {
                continue;   // left-hand controls such as the permission button also expand
            }

            if (!modelFound || buttonRect.Left < modelRect.Left)
            {
                model = button;
                modelRect = buttonRect;
                modelFound = true;
            }
        }

        if (model is not null && (!TryReadRect(model, out modelRect)
            || !OverlapsComposer(modelRect, composerRect)
            || modelRect.Left < minimumModelLeft || modelRect.Left >= composerRect.Right))
        {
            model = null;
            modelRect = default;
            modelFound = false;
        }

        // A left-hand button can identify the toolbar row without being a valid right-hand anchor.
        var row = !contextRect.IsEmpty ? contextRect : !modelRect.IsEmpty ? modelRect : toolbarRowHint;
        var rowTop = row.Y;
        var rowBottom = row.Bottom;
        if (!modelRect.IsEmpty)
        {
            rowTop = Math.Min(rowTop, modelRect.Y);
            rowBottom = Math.Max(rowBottom, modelRect.Bottom);
        }

        // Real controls, not Chromium's synthetic display:contents group rectangles, are barriers.
        var contextSiblingIndex = contextIndex >= 0 ? children[contextIndex].Index : int.MaxValue;
        var toolbarControls = children
            .Where(candidate => candidate.Index < contextSiblingIndex)
            .Where(candidate => candidate.ControlType is "Button" or "Image")
            .Select(candidate => candidate.Rect);
        var clusterLimit = !contextRect.IsEmpty
            ? contextRect.Left
            : !modelRect.IsEmpty
                ? modelRect.Left
                : OverlayLayoutCalculator.ResolveComposerReferenceLeft(composerRect, CodexWindowLocator.ReadDpi(hwnd));
        var leftCluster = MeasureLeftCluster(composerRect, rowTop, rowBottom, clusterLimit, toolbarControls);

        var source = ComposerReferenceSource.None;
        var referenceRect = default(IntRect);
        var elementName = string.Empty;
        var elementAutomationId = string.Empty;
        var elementControlType = string.Empty;
        var elementClassName = string.Empty;

        if (!contextRect.IsEmpty)
        {
            source = ComposerReferenceSource.UiaContext;
            referenceRect = contextRect;
            if (contextIndex >= 0)
            {
                elementName = children[contextIndex].Name;
                elementAutomationId = children[contextIndex].AutomationId;
                elementControlType = children[contextIndex].ControlType;
                elementClassName = children[contextIndex].ClassName;
            }
        }
        else if (!modelRect.IsEmpty)
        {
            // Level 2: the model selector's left edge, minus the measured distance from the Context
            // indicator's left edge to it. The layout adds that reservation back.
            source = ComposerReferenceSource.UiaModel;
            referenceRect = modelRect;
            reference = model;
        }
        else
        {
            // Level 3: the composer card itself; the toolbar row is derived from its bottom edge.
            source = ComposerReferenceSource.UiaComposer;
            referenceRect = composerRect;
        }

        return new ComposerDockSnapshot(
            source,
            referenceRect,
            modelRect.IsEmpty ? row : modelRect,
            composerRect,
            leftCluster,
            elementName,
            elementAutomationId,
            elementControlType,
            elementClassName,
            hwnd.ToInt64(),
            contextIndex,
            null,
            IsDraft: contextRect.IsEmpty && IsDraftComposer(composer));
    }

    internal static int MinimumModelAnchorLeft(IntRect composer, IntRect context) =>
        context.IsEmpty ? composer.Left + composer.Width / 2 : context.Left;

    private static bool OverlapsComposer(IntRect button, IntRect composer) =>
        button.Left < composer.Right && button.Right > composer.Left
        && button.Y < composer.Bottom && button.Bottom > composer.Y;

    internal static IntRect MeasureLeftCluster(
        IntRect composer, int rowTop, int rowBottom, int clusterLimit, IEnumerable<IntRect> controls)
    {
        if (rowBottom <= rowTop)
        {
            return default;
        }

        // Screen coordinates are signed on monitors left of the primary display. Start at the
        // composer's edge, not x=0, so an empty or entirely negative cluster stays local to the card.
        var right = composer.Left;
        foreach (var control in controls)
        {
            if (!control.IsEmpty && control.Height > 1 && control.Bottom > rowTop && control.Y < rowBottom
                && control.Right > composer.Left && control.Left < clusterLimit)
            {
                right = Math.Max(right, Math.Min(control.Right, composer.Right));
            }
        }

        return right > composer.Left
            ? new IntRect(composer.Left, rowTop, right - composer.Left, rowBottom - rowTop)
            : default;
    }

    private readonly record struct ChildInfo(
        AutomationElement Element,
        int Index,
        IntRect Rect,
        string ControlType,
        string Name,
        string AutomationId,
        string ClassName);

    private static bool LooksLikeContextUsage(string name, string className)
    {
        foreach (var hint in ContextNameHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return className.Contains(ContextClassHint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The composer card: the ControlView parent of the ProseMirror contenteditable. The card has no
    /// AutomationId and no stable class of its own, but its editor does, and the relationship is
    /// structural rather than textual.
    /// </summary>
    private static AutomationElement? FindComposer(AutomationElement root, TreeWalker walker)
    {
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0 && visited < 4000)
        {
            var (element, depth) = stack.Pop();
            if (depth > 30)
            {
                continue;
            }

            var child = TryFirstChild(walker, element);
            while (child is not null)
            {
                visited++;
                if (TryReadProperties(child, out var childType, out _, out _, out var childClass)
                    && childType == "Edit" && IsComposerEditor(childClass) && TryReadRect(child, out _))
                {
                    return TryParent(walker, child);
                }

                stack.Push((child, depth + 1));
                child = TryNextSibling(walker, child);
            }
        }

        return null;
    }

    internal static bool IsComposerEditor(string className) => HasClass(className, ComposerEditorClass);

    private static bool IsDraftComposer(AutomationElement composer)
    {
        // 新建页和会话页共用编辑器与模型选择器，通过上层布局识别尚未发送的草稿。
        var walker = TreeWalker.RawViewWalker;
        for (AutomationElement? element = composer; element is not null; element = TryParent(walker, element))
        {
            if (!TryReadProperties(element, out var type, out _, out _, out var className)
                || type is "Document" or "Window")
            {
                return false;
            }

            if (HasClass(className, DraftComposerClass))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasClass(string className, string token) =>
        className.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);

    /// <summary>
    /// A menu-opening button inside a composer child. The model selector's AutomationId is a
    /// Radix-generated <c>radix-_r_NN_</c> that changes between renders, and its Name contains the
    /// model name, which changes when the user switches models — so neither is used. The
    /// ExpandCollapse pattern plus the geometry is the stable signal.
    /// </summary>
    private static bool TryExpandableButton(
        AutomationElement element,
        TreeWalker walker,
        out AutomationElement button,
        out IntRect rect)
    {
        button = element;
        rect = default;
        var type = TryControlType(element);
        if (type == "Button" && IsExpandable(element) && TryReadRect(element, out rect))
        {
            return true;
        }

        // One level down: the selector lives inside a `display: contents` group.
        var child = TryFirstChild(walker, element);
        var guard = 0;
        while (child is not null && guard < 8)
        {
            if (string.Equals(TryControlType(child), "Button", StringComparison.Ordinal)
                && IsExpandable(child)
                && TryReadRect(child, out rect))
            {
                button = child;
                return true;
            }

            child = TryNextSibling(walker, child);
            guard++;
        }

        return false;
    }

    private static bool IsExpandable(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ small UIA helpers
    //
    // Every property read crosses into the target process, so each one is individually guarded: a
    // node that disappears between two calls must degrade the snapshot, not abort the walk.

    private static AutomationElement? TryFirstChild(TreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static AutomationElement? TryNextSibling(TreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetNextSibling(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static AutomationElement? TryParent(TreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetParent(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string TryControlType(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadProperties(
        AutomationElement element,
        out string controlType,
        out string name,
        out string automationId,
        out string className)
    {
        controlType = string.Empty;
        name = string.Empty;
        automationId = string.Empty;
        className = string.Empty;
        try
        {
            var current = element.Current;
            controlType = current.ControlType.ProgrammaticName.Replace(
                "ControlType.",
                string.Empty,
                StringComparison.Ordinal);
            name = current.Name ?? string.Empty;
            automationId = current.AutomationId ?? string.Empty;
            className = current.ClassName ?? string.Empty;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadRect(AutomationElement element, out IntRect rect)
    {
        rect = default;
        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (double.IsNaN(bounds.X) || double.IsInfinity(bounds.X)
                || double.IsNaN(bounds.Y) || double.IsInfinity(bounds.Y)
                || double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width)
                || double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height))
            {
                return false;
            }

            rect = new IntRect(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height));

            // Windows parks a minimised or hidden window at (-32000, -32000), and Chromium lays its
            // tree out around the same sentinel. A reference there is not a reference.
            return !rect.IsEmpty && !OverlayLayoutCalculator.IsUnusableRect(rect);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
