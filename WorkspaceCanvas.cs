using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas : FrameworkElement
{
    private readonly Journal journal;
    private readonly Storage storage;
    private readonly List<SceneObject> objects = [];
    private readonly Dictionary<Guid, SceneObject> byId = [];
    private readonly SpatialIndex<SceneObject> index = new();
    private readonly SpatialIndex<Curve> connections = new();
    private readonly HashSet<Guid> parents = [];
    private readonly Dictionary<(string, double, double, double, bool, bool, Brush), FormattedText> textCache = [];
    private readonly Dictionary<(Brush, double), Pen> pens = [];
    private readonly Dictionary<string, Brush> colors = [];
    private readonly Dictionary<string, BitmapSource?> images = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, int Width)> imageLoads = new();
    private readonly HashSet<string> pendingImages = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (Point[] Points, Geometry Geometry)> inkPaths = [];
    private readonly HashSet<SceneObject> visibleObjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Curve> visibleCurves = new(ReferenceEqualityComparer.Instance);
    private readonly List<SceneObject> drawOrder = [];
    private DrawingGroup? dotTile;
    private double dotSpacing, dotRadius;
    private bool decoding;
    private Theme theme;
    private Guid board;
    private Viewport view = new();
    public HashSet<Guid> Selection { get; } = [];
    public Viewport View => view;
    public Tool CurrentTool { get; private set; }
    public string StrokeColor { get; set; } = "#59CEE0";
    public double StrokeWeight { get; set; } = 3;
    public double StrokeOpacity { get; set; } = 1;
    public bool CursorZoom { get; set; } = true;
    public Guid? Connecting { get; set; }
    public event Action<Viewport>? ViewChanged;
    public event Action? SelectionChanged;
    public event Action<Guid>? OpenBrain;
    public event Action<Point?, Guid>? AddBrain;
    public event Action<Guid, Point>? EditItem;
    public event Action<Guid>? OpenLink;
    public event Action<Guid, Guid>? Connect;
    public event Action<Guid, Point>? ShowMenu;
    public event Action<string[], Point>? ImportFiles;
    public bool InlineEdit { get; set; }

    public WorkspaceCanvas(Journal journal, Storage storage, Theme theme)
    {
        this.journal = journal;
        this.storage = storage;
        this.theme = theme;
        ClipToBounds = true;
        Focusable = true;
        AllowDrop = true;
        Unloaded += (_, _) => StopMotion();
        SizeChanged += (_, _) => FitAfterWorkspaceChange();
        DragOver += (_, args) => { args.Effects = board != Guid.Empty && args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; };
        Drop += (_, args) => { if (board != Guid.Empty && args.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles?.Invoke(files, ToWorld(args.GetPosition(this))); };
        journal.Changed += RefreshScene;
    }

    public Point Center => new(ActualWidth / 2, ActualHeight / 2);
    public Point ToWorld(Point screen) => new((screen.X - Center.X - view.X) / view.Zoom, (screen.Y - Center.Y - view.Y) / view.Zoom);
    public Point ToScreen(Point world) => new(Center.X + view.X + world.X * view.Zoom, Center.Y + view.Y + world.Y * view.Zoom);
    public bool TryBounds(Guid id, out Rect bounds)
    {
        if (byId.TryGetValue(id, out SceneObject? placed)) { bounds = placed.Bounds; return true; }
        bounds = Rect.Empty;
        return false;
    }

    public void SetTheme(Theme value)
    {
        theme = value;
        textCache.Clear();
        overviewTextCache.Clear();
        overviewDrawing = null;
        pens.Clear();
        dotTile = null;
        InvalidateVisual();
    }

    public void SetBoard(Guid id, Viewport viewport)
    {
        StopMotion();
        CancelGesture();
        board = id;
        view = viewport.Zoom is > 0 and <= 4 && double.IsFinite(viewport.X) && double.IsFinite(viewport.Y) ? viewport : new();
        Connecting = null;
        Selection.Clear();
        SetTool(Tool.Select);
        RefreshScene();
        SelectionChanged?.Invoke();
    }

    public void Select(Guid id)
    {
        Selection.Clear();
        if (id != Guid.Empty && byId.ContainsKey(id)) Selection.Add(id);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void FocusObject(Guid id)
    {
        if (!TryBounds(id, out Rect rect)) return;
        Select(id);
        double zoom = Math.Max(.85, view.Zoom);
        AnimateTo(new(-(rect.X + rect.Width / 2) * zoom, -(rect.Y + rect.Height / 2) * zoom, zoom), 165, true);
    }

    public void Zoom(double multiplier, Point? anchor = null, bool animate = true) =>
        ZoomTo((motionTo?.Zoom ?? view.Zoom) * multiplier, anchor, animate);

    public void ZoomTo(double zoom, Point? anchor = null, bool animate = true)
    {
        if (zoom <= FullWorkspaceView.Zoom) { ShowFullWorkspace(animate); return; }
        Point screen = anchor ?? Center;
        Point world = ToWorld(screen);
        zoom = Math.Min(zoom, 4);
        if (motionTo?.Zoom == zoom) return;
        AnimateTo(new(screen.X - Center.X - world.X * zoom, screen.Y - Center.Y - world.Y * zoom, zoom), 175, animate);
    }

    public void PanFrom(Viewport origin, Vector screenDelta, bool notify = true) =>
        SetView(origin with { X = origin.X + screenDelta.X, Y = origin.Y + screenDelta.Y }, notify);

    public void Fit() => Fit(animate: true);

    public void Fit(bool animate)
    {
        if (objects.Count == 0) { ShowFullWorkspace(animate); return; }
        Rect bounds = Rect.Empty;
        foreach (SceneObject obj in objects) bounds.Union(obj.Bounds);
        double zoom = Math.Min(1.25, Math.Min((ActualWidth - 110) / Math.Max(1, bounds.Width), (ActualHeight - 172) / Math.Max(1, bounds.Height)));
        if (zoom <= FullWorkspaceView.Zoom) { ShowFullWorkspace(animate); return; }
        AnimateTo(new(-(bounds.X + bounds.Width / 2) * zoom, -(bounds.Y + bounds.Height / 2) * zoom, zoom), 175, animate);
    }

    private void SetView(Viewport viewport, bool notify = true)
    {
        view = ConstrainView(viewport);
        if (notify) ViewChanged?.Invoke(view);
        InvalidateVisual();
    }

    public void RefreshScene()
    {
        overviewBoxes.Clear();
        overviewLabels.Clear();
        overviewTextCache.Clear();
        overviewDrawing = null;
        objects.Clear(); byId.Clear(); index.Clear(); connections.Clear(); adjacency.Clear(); parents.Clear(); inkPaths.Clear();
        Content data = journal.Current;
        var children = data.Brains.Values.ToLookup(brain => brain.Parent);
        if (board == Guid.Empty)
        {
            var pending = new Queue<Brain>(children[Guid.Empty]);
            while (pending.TryDequeue(out Brain? brain))
            {
                Add(brain, false);
                if (!brain.Collapsed) foreach (Brain child in children[brain.Id]) pending.Enqueue(child);
            }
        }
        else
        {
            foreach (Item item in data.Items.Values)
                if (item.Board == board)
                {
                    string title = item.Kind is ItemKind.Text or ItemKind.Link ? Shell.ShortHeading(item.Text, 120, item.Kind == ItemKind.Text ? "Untitled note" : "Link") : "";
                    objects.Add(new(item.Id, new(item.X, item.Y, item.Width, item.Height), null, item, objects.Count, title));
                }
            foreach (Brain brain in children[board]) Add(brain, true);
        }
        foreach (SceneObject obj in objects)
        {
            byId.Add(obj.Id, obj);
            index.Add(obj.Bounds, obj);
        }
        UpdateWorkspaceRadius(data);
        if (board == Guid.Empty)
            foreach (SceneObject obj in objects)
                if (obj.Brain is { } brain && byId.TryGetValue(brain.Parent, out SceneObject? from)) AddCurve(from, obj, false);
        foreach (Edge edge in data.Edges.Values)
            if (byId.TryGetValue(edge.From, out SceneObject? source) && byId.TryGetValue(edge.To, out SceneObject? target)) AddCurve(source, target, true);
        if (Selection.RemoveWhere(id => !byId.ContainsKey(id)) > 0) SelectionChanged?.Invoke();
        InvalidateVisual();

        void Add(Brain brain, bool nested)
        {
            objects.Add(new(brain.Id, BrainLayout.Bounds(brain, home: !nested), brain, null, objects.Count, brain.Title));
            if (children.Contains(brain.Id)) parents.Add(brain.Id);
        }
    }

    private void AddCurve(SceneObject from, SceneObject to, bool manual)
    {
        Rect bounds = Rect.Union(from.Bounds, to.Bounds);
        bounds.Inflate(45, 10);
        var curve = new Curve(from, to, manual, MakeCurve(from.Bounds, to.Bounds));
        connections.Add(bounds, curve);
        if (!adjacency.TryGetValue(from.Id, out List<Curve>? fromEdges)) adjacency[from.Id] = fromEdges = [];
        if (!adjacency.TryGetValue(to.Id, out List<Curve>? toEdges)) adjacency[to.Id] = toEdges = [];
        fromEdges.Add(curve); toEdges.Add(curve);
    }

    private readonly Dictionary<Guid, List<Curve>> adjacency = [];

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var screen = new Rect(RenderSize);
        requestVisibleImages.Clear();
        if (screen.IsEmpty || screen.Width <= 0 || screen.Height <= 0) return;
        Item? draft = CurrentDraft();
        EllipseGeometry? boundary = DrawWorkspaceBackground(drawing, screen, draft);
        DrawDots(drawing);
        if (!DrawCachedOverview(drawing, screen)) DrawScene(drawing, screen, draft);
        if (boundary is not null)
        {
            drawing.Pop();
            drawing.DrawGeometry(null, Stroke(theme.Border, 1.25), boundary);
        }
    }

    private void DrawScene(DrawingContext drawing, Rect screen, Item? draft)
    {
        overviewDrawing = null;
        overviewProjection = Matrix.Identity;
        var visible = new Rect(ToWorld(new()), ToWorld(new(screen.Width, screen.Height)));
        bool overview = view.Zoom < OverviewZoom;
        visible.Inflate(overview ? OverviewOverscan / view.Zoom : 18, overview ? OverviewOverscan / view.Zoom : 18);
        index.Query(visible, visibleObjects);
        if (gesture == Gesture.Move)
            foreach (Guid id in Selection)
                if (byId.TryGetValue(id, out SceneObject? obj)) visibleObjects.Add(obj);
        drawOrder.Clear();
        drawOrder.AddRange(visibleObjects);
        if (board != Guid.Empty || overview) drawOrder.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        overviewLabels.Clear();
        overviewBoxes.Clear();
        bool cacheOverview = overview && board == Guid.Empty && drawOrder.Count > 160 && gesture is Gesture.None or Gesture.Pan;
        Rect labelArea = screen;
        if (cacheOverview) labelArea.Inflate(OverviewOverscan, OverviewOverscan);
        if (overview) ArrangeOverview(labelArea);
        drawing.PushTransform(new MatrixTransform(view.Zoom, 0, 0, view.Zoom, Center.X + view.X, Center.Y + view.Y));
        if (!overview) DrawConnections(drawing, visible, overview: false);
        int trailBudget = 18;
        foreach (SceneObject obj in drawOrder)
        {
            if (erased.Contains(obj.Id) || (overview && obj.Title.Length > 0)) continue;
            Rect rect = DisplayBounds(obj);
            if (!visible.IntersectsWith(rect)) continue;
            if (obj.Brain is { } brain)
            {
                if (trailBudget > 0 && DrawMotionTrail(drawing, ScreenBounds(rect), world: true)) trailBudget--;
                DrawBrain(drawing, brain, rect);
            }
            else if (obj.Item is { } item) DrawItem(drawing, item, rect, Selection.Contains(item.Id));
        }
        if (draft is not null) DrawItem(drawing, draft, new(draft.X, draft.Y, draft.Width, draft.Height), false);
        if (gesture == Gesture.Marquee) drawing.DrawRectangle(null, Stroke(theme.Accent, 1 / view.Zoom), new(startWorld, cursorWorld));
        drawing.Pop();
        if (cacheOverview) BuildCachedOverview(drawing, screen, visible);
        else if (overview) DrawOverview(drawing, visible, trailsEnabled: true);
    }

    private void DrawConnections(DrawingContext drawing, Rect visible, bool overview)
    {
        connections.Query(visible, visibleCurves);
        if (gesture == Gesture.Move)
            foreach (Guid id in Selection)
                if (adjacency.TryGetValue(id, out var associated)) visibleCurves.UnionWith(associated);
        foreach (Curve curve in visibleCurves)
        {
            if (overview && !overviewBoxes.ContainsKey(curve.From.Id) && !overviewBoxes.ContainsKey(curve.To.Id)) continue;
            bool moving = gesture == Gesture.Move && (Selection.Contains(curve.From.Id) || Selection.Contains(curve.To.Id));
            Geometry path = moving ? MakeCurve(DisplayBounds(curve.From), DisplayBounds(curve.To)) : curve.Path;
            double weight = curve.Manual ? 1.25 : 1.65;
            if (overview) weight = Math.Max(weight, .6 / view.Zoom);
            drawing.DrawGeometry(null, Stroke(curve.Manual ? theme.Accent : theme.Edge, weight), path);
        }
    }

    private void DrawDots(DrawingContext drawing)
    {
        double spacing = 31 * view.Zoom;
        while (spacing < 20) spacing *= 2;
        while (spacing > 56) spacing /= 2;
        double left = (Center.X + view.X) % spacing;
        double top = (Center.Y + view.Y) % spacing;
        double radius = view.Zoom > .8 ? .85 : .65;
        if (dotTile is null || spacing != dotSpacing || radius != dotRadius)
        {
            dotSpacing = spacing;
            dotRadius = radius;
            dotTile = new DrawingGroup();
            using (var tile = dotTile.Open())
                tile.DrawRectangle(theme.Dot, null, new(spacing / 2 - radius, spacing / 2 - radius, radius * 2, radius * 2));
            dotTile.Freeze();
        }
        var brush = new DrawingBrush(dotTile)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new(0, 0, spacing, spacing), Viewport = new(left - spacing / 2, top - spacing / 2, spacing, spacing)
        };
        brush.Freeze();
        drawing.DrawRectangle(brush, null, new Rect(RenderSize));
    }

    private static Geometry MakeCurve(Rect from, Rect to)
    {
        double direction = from.X <= to.X ? 1 : -1;
        Point start = new(direction > 0 ? from.Right : from.Left, from.Y + from.Height / 2);
        Point end = new(direction > 0 ? to.Left : to.Right, to.Y + to.Height / 2);
        double bend = Math.Max(35, Math.Abs(end.X - start.X) * .52);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.BezierTo(new(start.X + bend * direction, start.Y), new(end.X - bend * direction, end.Y), end, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawBrain(DrawingContext drawing, Brain brain, Rect rect)
    {
        bool root = board == Guid.Empty && brain.Parent == Guid.Empty;
        bool selected = Selection.Contains(brain.Id);
        bool hover = hovered == brain.Id;
        double rounded = rect.Height / 2;
        Brush edge = selected || Connecting == brain.Id ? theme.Accent : hover ? theme.Accent : root ? theme.Accent : theme.Edge;
        drawing.DrawRoundedRectangle(root || hover ? theme.Surface : theme.Node, Stroke(edge, selected ? 2.4 : root ? 1.9 : 1.15), rect, rounded, rounded);
        if (view.Zoom >= .38)
        {
            FormattedText label = Text(brain.Title, root ? 27 : 19, Math.Max(1, rect.Width - 40), theme.Text, root, false);
            drawing.DrawText(label, new(rect.X + (rect.Width - label.Width) / 2, rect.Y + (rect.Height - label.Height) / 2 - 1));
            if (selected && view.Zoom >= .65 && gesture == Gesture.None)
            {
                Point plus = PlusPoint(rect);
                drawing.DrawEllipse(theme.Surface, Stroke(theme.Accent, 1.15), plus, 10, 10);
                drawing.DrawLine(Stroke(theme.Accent, 1.4), plus + new Vector(-4, 0), plus + new Vector(4, 0));
                drawing.DrawLine(Stroke(theme.Accent, 1.4), plus + new Vector(0, -4), plus + new Vector(0, 4));
            }
            if (brain.Collapsed && parents.Contains(brain.Id))
                drawing.DrawEllipse(theme.Accent, null, new(rect.Right - 13, rect.Y + rect.Height / 2), 2.6, 2.6);
        }
    }

    private void DrawItem(DrawingContext drawing, Item item, Rect rect, bool selected)
    {
        if (view.Zoom < .24)
        {
            if (item.Kind is ItemKind.Text or ItemKind.Link or ItemKind.Image)
                drawing.DrawRoundedRectangle(theme.Surface, null, rect, 5, 5);
            return;
        }
        Brush ink = item.Color.Length == 0 ? theme.Accent : ColorFor(item.Color);
        if (item.Kind is ItemKind.Ink or ItemKind.Marker or ItemKind.Rectangle or ItemKind.Ellipse or ItemKind.Line)
        {
            if (item.Opacity < 1) drawing.PushOpacity(item.Opacity);
            if (item.Kind == ItemKind.Rectangle) drawing.DrawRoundedRectangle(null, Stroke(ink, item.Weight), rect, 4, 4);
            else if (item.Kind == ItemKind.Ellipse) drawing.DrawEllipse(null, Stroke(ink, item.Weight), new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
            else if (item.Points is { Length: > 0 } points)
            {
                if (points.Length == 1) drawing.DrawEllipse(ink, null, rect.TopLeft + (Vector)points[0], item.Weight / 2, item.Weight / 2);
                else
                {
                    if (!inkPaths.TryGetValue(item.Id, out var cached) || cached.Points != points)
                    {
                        cached = (points, InkGeometry(points));
                        if (item.Id != Guid.Empty) inkPaths[item.Id] = cached;
                    }
                    drawing.PushTransform(new TranslateTransform(rect.X, rect.Y));
                    drawing.DrawGeometry(null, Stroke(ink, item.Weight), cached.Geometry);
                    drawing.Pop();
                }
            }
            if (item.Opacity < 1) drawing.Pop();
        }
        else if (item.Kind == ItemKind.Image)
        {
            drawing.DrawRoundedRectangle(theme.Surface, Stroke(theme.Border, 1), rect, 4, 4);
            bool priority = requestVisibleImages.Count < 28 || requestVisibleImages.Contains(item.Target);
            if (priority) requestVisibleImages.Add(item.Target);
            if (images.TryGetValue(item.Target, out BitmapSource? bitmap))
            {
                if (bitmap is not null) drawing.DrawImage(bitmap, rect);
                else if (view.Zoom >= .45) drawing.DrawText(Text("Image unavailable", 14, Math.Max(1, rect.Width - 20), theme.Muted, false, false), rect.TopLeft + new Vector(10, 9));
            }
            else if (priority)
                QueueImage(item.Target, Math.Clamp((int)(Math.Max(rect.Width, rect.Height) * view.Zoom * 1.5), 160, 1300));
        }
        else
        {
            drawing.DrawRoundedRectangle(theme.Node, Stroke(theme.Border, 1), rect, item.Kind == ItemKind.Link ? 9 : 11, item.Kind == ItemKind.Link ? 9 : 11);
            if (view.Zoom >= .38 && !(InlineEdit && selected && item.Kind == ItemKind.Text))
            {
                bool link = item.Kind == ItemKind.Link;
                var clip = new RectangleGeometry(new(rect.X + 10, rect.Y + 8, Math.Max(0, rect.Width - 20), Math.Max(0, rect.Height - 16)));
                drawing.PushClip(clip);
                string visibleText = item.Text.Length <= 6000 ? item.Text : item.Text[..6000];
                var label = Text(visibleText, item.FontSize, Math.Max(1, rect.Width - 36), link ? theme.Accent : theme.Text, item.Bold, item.Italic, link);
                drawing.DrawText(label, new(rect.X + 18, rect.Y + (link ? Math.Max(8, (rect.Height - label.Height) / 2) : 14)));
                if (!link && item.Target.Length > 0)
                    drawing.DrawEllipse(theme.Accent, null, new(rect.Right - 12, rect.Bottom - 12), 2.8, 2.8);
                drawing.Pop();
            }
        }
        if (!selected) return;
        drawing.DrawRectangle(null, Stroke(theme.Accent, 1.35 / view.Zoom), rect);
        if (Selection.Count == 1 && CanResize(item) && gesture == Gesture.None)
            foreach (Point corner in Corners(rect))
            {
                double size = 7 / view.Zoom;
                drawing.DrawRectangle(theme.Text, Stroke(theme.Accent, 1 / view.Zoom), new(corner.X - size / 2, corner.Y - size / 2, size, size));
            }
    }

    private readonly HashSet<string> requestVisibleImages = new(StringComparer.Ordinal);

    private void QueueImage(string key, int width)
    {
        if (!pendingImages.Add(key)) return;
        imageLoads.Enqueue((key, width));
        if (!decoding) DecodeImages();
    }

    private async void DecodeImages()
    {
        decoding = true;
        while (imageLoads.TryDequeue(out var work))
        {
            if (!requestVisibleImages.Contains(work.Key)) { pendingImages.Remove(work.Key); continue; }
            BitmapSource? bitmap = null;
            try { bitmap = await Task.Run(() => storage.LoadImage(work.Key, work.Width)); }
            catch (Exception error) when (error is IOException or FileFormatException or NotSupportedException or ArgumentException) { }
            pendingImages.Remove(work.Key);
            while (images.Count >= 32 || images.Values.Sum(image => (long)(image?.PixelWidth ?? 0) * (image?.PixelHeight ?? 0)) +
                (long)(bitmap?.PixelWidth ?? 0) * (bitmap?.PixelHeight ?? 0) > 14_000_000)
            {
                string? evict = images.Keys.FirstOrDefault(key => !requestVisibleImages.Contains(key));
                if (evict is null) break;
                images.Remove(evict);
            }
            images[work.Key] = bitmap;
            InvalidateVisual();
        }
        decoding = false;
    }

    private FormattedText Text(string value, double size, double width, Brush brush, bool bold, bool italic, bool single = true)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var cacheKey = (value, size, width, single ? dpi : -dpi, bold, italic, brush);
        if (textCache.TryGetValue(cacheKey, out FormattedText? layout)) return layout;
        var type = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        layout = new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, type, size, brush, dpi)
        { MaxTextWidth = width, Trimming = TextTrimming.CharacterEllipsis };
        if (single) layout.MaxLineCount = 1;
        if (textCache.Count >= 800) textCache.Clear();
        textCache[cacheKey] = layout;
        return layout;
    }

    private Brush ColorFor(string value)
    {
        if (colors.TryGetValue(value, out Brush? brush)) return brush;
        if (colors.Count > 48) colors.Clear();
        return colors[value] = Theme.Brush(Theme.ValidColor(value, "#83C6FB"));
    }

    private Pen Stroke(Brush brush, double width)
    {
        if (pens.TryGetValue((brush, width), out Pen? pen)) return pen;
        pen = new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (pens.Count > 180) pens.Clear();
        pens[(brush, width)] = pen;
        return pen;
    }

    private static Geometry InkGeometry(Point[] points)
    {
        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length - 1; i++)
                context.QuadraticBezierTo(points[i], new((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2), true, true);
            context.LineTo(points[^1], true, true);
        }
        path.Freeze();
        return path;
    }

    private sealed record SceneObject(Guid Id, Rect Bounds, Brain? Brain, Item? Item, int Order, string Title);
    private sealed record Curve(SceneObject From, SceneObject To, bool Manual, Geometry Path);
}

internal sealed class SpatialIndex<T> where T : class
{
    private const double Cell = 384;
    private readonly Dictionary<(int, int), List<(Rect Box, T Value)>> cells = [];
    private readonly List<(Rect Box, T Value)> oversized = [];
    private readonly List<(Rect Box, T Value)> all = [];
    private static int Floor(double value) => (int)Math.Floor(value / Cell);

    public void Clear() { cells.Clear(); oversized.Clear(); all.Clear(); }
    public void Add(Rect box, T value)
    {
        int left = Floor(box.Left), right = Floor(box.Right), top = Floor(box.Top), bottom = Floor(box.Bottom);
        all.Add((box, value));
        if ((long)(right - left + 1) * (bottom - top + 1) > 48) { oversized.Add((box, value)); return; }
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                if (!cells.TryGetValue((x, y), out var entries)) cells.Add((x, y), entries = []);
                entries.Add((box, value));
            }
    }

    public HashSet<T> Query(Rect area)
    {
        var found = new HashSet<T>(ReferenceEqualityComparer.Instance);
        Query(area, found);
        return found;
    }

    public void Query(Rect area, HashSet<T> found)
    {
        int left = Floor(area.Left), right = Floor(area.Right), top = Floor(area.Top), bottom = Floor(area.Bottom);
        found.Clear();
        if ((long)(right - left + 1) * (bottom - top + 1) > 1200) { AddMatches(all); return; }
        AddMatches(oversized);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (cells.TryGetValue((x, y), out var entries)) AddMatches(entries);
        void AddMatches(List<(Rect Box, T Value)> entries)
        {
            foreach (var (box, value) in entries)
                if (box.IntersectsWith(area)) found.Add(value);
        }
    }
}
