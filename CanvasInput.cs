using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private enum Gesture { None, Pan, Move, Marquee, Resize, Draw, Erase }
    private Gesture gesture;
    private Point startScreen, startWorld, cursorWorld;
    private Viewport startView = new();
    private Guid hovered;
    private Vector delta;
    private Rect resized;
    private int resizeCorner;
    private bool moved;
    private Point lastPanPoint;
    private Vector panVelocity;
    private long lastPanSample;
    private Guid resizeId;
    private readonly List<Point> stroke = [];
    private readonly HashSet<Guid> erased = [];
    private HashSet<Guid> marqueeStart = [];
    public Point PointerWorld
    {
        get
        {
            Point location = ToWorld(IsMouseOver ? Mouse.GetPosition(this) : Center);
            return InsideWorkspace(location) ? location : new Point();
        }
    }

    public void SetTool(Tool tool)
    {
        CancelGesture();
        CurrentTool = tool;
        Cursor = tool == Tool.Select ? Cursors.Arrow : Cursors.Cross;
        InvalidateVisual();
    }

    public bool CancelGesture()
    {
        if (gesture == Gesture.None) return false;
        if (gesture == Gesture.Pan) SetView(startView);
        gesture = Gesture.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        erased.Clear(); stroke.Clear(); delta = new();
        cameraTrail = new();
        Cursor = CurrentTool == Tool.Select ? Cursors.Arrow : Cursors.Cross;
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (gesture != Gesture.None || InlineEdit) return;
        Zoom(Math.Pow(1.15, e.Delta / 120d), CursorZoom ? e.GetPosition(this) : null);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (gesture != Gesture.None) return;
        StopMotion();
        Focus();
        startScreen = e.GetPosition(this);
        startWorld = cursorWorld = ToWorld(startScreen);
        moved = false;
        delta = new();
        if (!InsideWorkspace(startWorld))
        {
            if (e.ChangedButton is MouseButton.Left or MouseButton.Middle && !view.WholeWorkspace) BeginPan();
            e.Handled = true;
            return;
        }
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool extend = shift || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        SceneObject? target = Hit(startWorld);

        if (e.ChangedButton == MouseButton.Right)
        {
            if (target is not null && !Selection.Contains(target.Id)) Select(target.Id);
            ShowMenu?.Invoke(target?.Id ?? Guid.Empty, startWorld);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            BeginPan();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        if (Connecting is Guid from && target?.Brain is not null)
        {
            Connect?.Invoke(from, target.Id);
            Connecting = null;
            InvalidateVisual();
            return;
        }

        if (board != Guid.Empty && CurrentTool != Tool.Select)
        {
            if (CurrentTool == Tool.Text) { EditItem?.Invoke(Guid.Empty, startWorld); return; }
            if (CurrentTool == Tool.Eraser) { Start(Gesture.Erase); EraseAt(startWorld); }
            else { stroke.Add(startWorld); Start(Gesture.Draw); }
            return;
        }

        if (Selection.Count == 1 && byId.TryGetValue(Selection.First(), out SceneObject? selected))
        {
            if (selected.Brain is not null && view.Zoom >= .65 && (startWorld - PlusPoint(selected.Bounds)).Length < 12)
            {
                AddBrain?.Invoke(null, selected.Id);
                return;
            }
            if (selected.Item is { } item && CanResize(item))
            {
                Point[] corners = Corners(selected.Bounds);
                for (int i = 0; i < corners.Length; i++)
                    if ((startWorld - corners[i]).Length < 9 / view.Zoom)
                    {
                        resizeCorner = i; resizeId = selected.Id; resized = selected.Bounds;
                        Start(Gesture.Resize);
                        return;
                    }
            }
        }

        if (target is null)
        {
            if (e.ClickCount == 2 && board == Guid.Empty) { AddBrain?.Invoke(startWorld, Guid.Empty); return; }
            if (extend) { marqueeStart = new(Selection); Start(Gesture.Marquee); }
            else { Select(Guid.Empty); BeginPan(); }
            return;
        }
        if (e.ClickCount == 2 && !extend)
        {
            if (target.Brain is not null) OpenBrain?.Invoke(target.Id);
            else if (target.Item?.Kind == ItemKind.Link) OpenLink?.Invoke(target.Id);
            else if (target.Item?.Kind == ItemKind.Text) EditItem?.Invoke(target.Id, startWorld);
            return;
        }
        if (extend)
        {
            if (!Selection.Add(target.Id)) Selection.Remove(target.Id);
            SelectionChanged?.Invoke();
        }
        else if (!Selection.Contains(target.Id)) Select(target.Id);
        if (Selection.Contains(target.Id)) Start(Gesture.Move);
        InvalidateVisual();
    }

    private void BeginPan()
    {
        if (view.WholeWorkspace) return;
        startView = view;
        lastPanPoint = startScreen;
        lastPanSample = Stopwatch.GetTimestamp();
        panVelocity = new();
        Cursor = Cursors.Hand;
        Start(Gesture.Pan);
    }

    private void Start(Gesture action)
    {
        gesture = action;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point screen = e.GetPosition(this);
        cursorWorld = ToWorld(screen);
        if (gesture == Gesture.None)
        {
            if (CurrentTool != Tool.Select) return;
            Guid next = Hit(cursorWorld)?.Id ?? Guid.Empty;
            if (next != hovered) { hovered = next; InvalidateVisual(); }
            return;
        }
        Vector screenDelta = screen - startScreen;
        moved |= screenDelta.LengthSquared >= 9;
        switch (gesture)
        {
            case Gesture.Pan:
                if (moved)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = Stopwatch.GetElapsedTime(lastPanSample, now).TotalMilliseconds;
                    Vector travel = screen - lastPanPoint;
                    if (elapsed > 1)
                    {
                        Vector instantaneous = travel / elapsed;
                        panVelocity = elapsed > 80 ? instantaneous : panVelocity * .60 + instantaneous * .40;
                        lastPanSample = now;
                        lastPanPoint = screen;
                    }
                    cameraTrail = SystemParameters.ClientAreaAnimation ? ClampLength(-panVelocity * 6, 9) : new();
                    PanFrom(startView, screenDelta, false);
                }
                return;
            case Gesture.Move:
                if (moved) { delta = screenDelta / view.Zoom; Cursor = Cursors.SizeAll; }
                break;
            case Gesture.Marquee:
                Selection.Clear(); Selection.UnionWith(marqueeStart);
                foreach (SceneObject obj in index.Query(new(startWorld, cursorWorld))) Selection.Add(obj.Id);
                break;
            case Gesture.Resize:
                if (moved) UpdateResize(cursorWorld);
                break;
            case Gesture.Draw:
                if (CurrentTool is Tool.Pen or Tool.Marker && (cursorWorld - stroke[^1]).Length * view.Zoom >= 1.4)
                    if (stroke.Count < 32_000) stroke.Add(cursorWorld);
                break;
            case Gesture.Erase:
                EraseAt(cursorWorld);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (gesture == Gesture.None || e.ChangedButton == MouseButton.Right || (e.ChangedButton == MouseButton.Middle && gesture != Gesture.Pan)) return;
        Gesture action = gesture;
        Item? draft = CurrentDraft();
        gesture = Gesture.None;
        ReleaseMouseCapture();
        Cursor = CurrentTool == Tool.Select ? Cursors.Arrow : Cursors.Cross;
        switch (action)
        {
            case Gesture.Pan:
                if (moved)
                {
                    double age = Stopwatch.GetElapsedTime(lastPanSample).TotalMilliseconds;
                    if (SystemParameters.ClientAreaAnimation && age < 75 && panVelocity.Length > .16)
                    {
                        Vector glide = ClampLength(panVelocity * (1 - age / 75) * 86, 103);
                        AnimateTo(view with { X = view.X + glide.X, Y = view.Y + glide.Y }, 210, true);
                    }
                    else { cameraTrail = new(); ViewChanged?.Invoke(view); }
                }
                break;
            case Gesture.Move:
                if (moved) CommitMove();
                else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Hit(startWorld) is { } target) Select(target.Id);
                break;
            case Gesture.Resize:
                if (moved) journal.Edit(data => data.Items[resizeId] = data.Items[resizeId] with { X = resized.X, Y = resized.Y, Width = resized.Width, Height = resized.Height });
                break;
            case Gesture.Draw:
                if (draft is not null && (moved || draft.Kind is ItemKind.Ink or ItemKind.Marker))
                {
                    Guid id = Guid.NewGuid();
                    journal.Edit(data => data.Items.Add(id, draft with { Id = id }));
                }
                break;
            case Gesture.Erase:
                if (erased.Count > 0) journal.Edit(data => { foreach (Guid id in erased) data.Items.Remove(id); });
                break;
            case Gesture.Marquee:
                SelectionChanged?.Invoke();
                break;
        }
        erased.Clear(); stroke.Clear(); delta = new();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelGesture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hovered == Guid.Empty) return;
        hovered = Guid.Empty;
        InvalidateVisual();
    }

    private void CommitMove()
    {
        journal.Edit(data =>
        {
            foreach (Guid id in Selection)
            {
                if (data.Brains.TryGetValue(id, out Brain? brain)) data.Brains[id] = board == Guid.Empty
                    ? brain with { X = brain.X + delta.X, Y = brain.Y + delta.Y }
                    : brain with { BoardX = brain.BoardX + delta.X, BoardY = brain.BoardY + delta.Y };
                else if (data.Items.TryGetValue(id, out Item? item)) data.Items[id] = item with { X = item.X + delta.X, Y = item.Y + delta.Y };
            }
        });
    }

    private Rect DisplayBounds(SceneObject obj)
    {
        if (gesture == Gesture.Resize && obj.Id == resizeId) return resized;
        Rect rect = obj.Bounds;
        if (gesture == Gesture.Move && Selection.Contains(obj.Id)) rect.Offset(delta);
        return rect;
    }

    private void UpdateResize(Point point)
    {
        SceneObject obj = byId[resizeId];
        Rect original = obj.Bounds;
        Point fixedCorner = Corners(original)[(resizeCorner + 2) % 4];
        bool left = resizeCorner is 0 or 3, top = resizeCorner is 0 or 1;
        double width = Math.Max(50, (point.X - fixedCorner.X) * (left ? -1 : 1));
        double height = Math.Max(37, (point.Y - fixedCorner.Y) * (top ? -1 : 1));
        if (obj.Item?.Kind == ItemKind.Image && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            double scale = Math.Max(width / original.Width, height / original.Height);
            width = original.Width * scale;
            height = original.Height * scale;
        }
        resized = new(left ? fixedCorner.X - width : fixedCorner.X, top ? fixedCorner.Y - height : fixedCorner.Y, width, height);
    }

    private Item? CurrentDraft()
    {
        if (gesture != Gesture.Draw || stroke.Count == 0) return null;
        var kind = CurrentTool switch { Tool.Pen => ItemKind.Ink, Tool.Marker => ItemKind.Marker, Tool.Rectangle => ItemKind.Rectangle,
            Tool.Ellipse => ItemKind.Ellipse, _ => ItemKind.Line };
        double weight = CurrentTool == Tool.Marker ? StrokeWeight * 4 : StrokeWeight;
        double opacity = CurrentTool == Tool.Marker ? Math.Min(StrokeOpacity, .45) : StrokeOpacity;
        var box = new Rect(startWorld, cursorWorld);
        Point[]? points = null;
        if (kind is ItemKind.Ink or ItemKind.Marker or ItemKind.Line)
        {
            List<Point> path = kind == ItemKind.Line ? [startWorld, cursorWorld] : stroke;
            box = new(path[0], path[0]);
            foreach (Point point in path) box.Union(point);
            box.Inflate(weight / 2, weight / 2);
            points = path.Select(point => new Point(point.X - box.X, point.Y - box.Y)).ToArray();
        }
        return new(Guid.Empty, board, kind, box.X, box.Y, box.Width, box.Height,
            Color: StrokeColor, Weight: weight, Points: points, Opacity: opacity);
    }

    private void EraseAt(Point point)
    {
        var area = new Rect(point, point);
        area.Inflate(7 / view.Zoom, 7 / view.Zoom);
        foreach (SceneObject obj in index.Query(area))
            if (obj.Item is { Kind: ItemKind.Ink or ItemKind.Marker or ItemKind.Line or ItemKind.Rectangle or ItemKind.Ellipse } item && HitItem(item, point, area.Width / 2))
                erased.Add(obj.Id);
    }

    private SceneObject? Hit(Point point)
    {
        bool overview = view.Zoom < OverviewZoom;
        if (overview && HitOverview(ToScreen(point)) is { } label) return label;
        double tolerance = 6 / view.Zoom;
        var area = new Rect(point, point);
        area.Inflate(tolerance, tolerance);
        return index.Query(area).Where(obj => (!overview || !overviewBoxes.ContainsKey(obj.Id)) &&
                (obj.Item is { } item ? HitItem(item, point, tolerance) : obj.Bounds.Contains(point)))
            .MaxBy(obj => obj.Order);
    }

    private static bool HitItem(Item item, Point point, double tolerance)
    {
        var bounds = new Rect(item.X, item.Y, item.Width, item.Height);
        if (item.Points is { Length: > 0 } points)
        {
            Point local = point - new Vector(item.X, item.Y);
            double radius = item.Weight / 2 + tolerance;
            if ((local - points[0]).LengthSquared <= radius * radius) return true;
            for (int i = 1; i < points.Length; i++)
            {
                Vector line = points[i] - points[i - 1];
                double fraction = line.LengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(local - points[i - 1], line) / line.LengthSquared, 0, 1);
                if ((local - (points[i - 1] + fraction * line)).LengthSquared <= radius * radius) return true;
            }
            return false;
        }
        if (item.Kind == ItemKind.Rectangle)
        {
            double gap = item.Weight / 2 + tolerance;
            bounds.Inflate(gap, gap);
            return bounds.Contains(point) && (Math.Min(point.X - bounds.Left, bounds.Right - point.X) <= gap * 2 ||
                Math.Min(point.Y - bounds.Top, bounds.Bottom - point.Y) <= gap * 2);
        }
        if (item.Kind == ItemKind.Ellipse)
        {
            double a = Math.Max(1, bounds.Width / 2), b = Math.Max(1, bounds.Height / 2);
            double x = (point.X - bounds.X - a) / a, y = (point.Y - bounds.Y - b) / b;
            return Math.Abs(Math.Sqrt(x * x + y * y) - 1) * Math.Min(a, b) <= item.Weight / 2 + tolerance;
        }
        return bounds.Contains(point);
    }

    private static bool CanResize(Item item) => item.Kind is ItemKind.Text or ItemKind.Image or ItemKind.Rectangle or ItemKind.Ellipse;
    private static Point[] Corners(Rect bounds) => [bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft];
    private static Point PlusPoint(Rect rect) => new(rect.Right + 11, rect.Y + rect.Height / 2);
}
