using System.Windows;

namespace BrainSpace;

internal enum ItemKind { Text, Ink, Marker, Rectangle, Ellipse, Line, Image, Link }
internal enum Tool { Select, Pen, Marker, Eraser, Text, Rectangle, Ellipse, Line }

internal sealed record Brain(Guid Id, Guid Parent, string Title, double X, double Y,
    double BoardX, double BoardY, bool Collapsed = false);

internal sealed record Item(Guid Id, Guid Board, ItemKind Kind, double X, double Y, double Width, double Height,
    string Text = "", string Target = "", string Color = "", double Weight = 3, double FontSize = 19,
    bool Bold = false, bool Italic = false, Point[]? Points = null, double Opacity = 1);

internal sealed record Edge(Guid Id, Guid From, Guid To);
internal readonly record struct GlobeOrientation(double X, double Y, double Z, double W);
internal sealed record Viewport(double X = 0, double Y = 0, double Zoom = 1, bool WholeWorkspace = false, GlobeOrientation? Rotation = null);
internal sealed record Preferences(bool Light = false, string Accent = "#83C6FB", bool CursorZoom = true,
    bool Rail = true, string InkColor = "#59CEE0", double InkWidth = 3, double InkOpacity = 1);
internal sealed record Session(Guid Board, Preferences Settings, Dictionary<Guid, Viewport> Views);
internal sealed record Content(Dictionary<Guid, Brain> Brains, Dictionary<Guid, Item> Items, Dictionary<Guid, Edge> Edges)
{
    public Content() : this(new(), new(), new()) { }
    public Content Copy() => new(new(Brains), new(Items), new(Edges));

    public IEnumerable<Brain> Trail(Guid id)
    {
        var path = new Stack<Brain>();
        while (Brains.TryGetValue(id, out Brain? brain))
        {
            path.Push(brain);
            id = brain.Parent;
        }
        return path;
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var removal = ids.ToHashSet();
        var children = Brains.Values.ToLookup(brain => brain.Parent);
        var pending = new Queue<Guid>(removal);
        while (pending.TryDequeue(out Guid id))
            foreach (Brain child in children[id])
                if (removal.Add(child.Id)) pending.Enqueue(child.Id);
        foreach (Guid id in removal) { Brains.Remove(id); Items.Remove(id); }
        foreach (Item item in Items.Values.Where(item => removal.Contains(item.Board)).ToArray()) Items.Remove(item.Id);
        foreach (Edge edge in Edges.Values.Where(edge => removal.Contains(edge.From) || removal.Contains(edge.To)).ToArray()) Edges.Remove(edge.Id);
    }

    public Guid AddBrain(Guid parent, string title, Point? position, bool home)
    {
        Guid id = Guid.NewGuid();
        Brains.TryGetValue(parent, out Brain? owner);
        Point map = home && position is Point mapClick ? mapClick : owner is null ? new Point() : BrainLayout.MapPosition(this, owner, title);
        Point board = owner is null ? new Point() : BrainLayout.BoardPosition(this, parent, title, home ? null : position);
        Brains.Add(id, new(id, parent, title, map.X, map.Y, board.X, board.Y));
        if (owner is { Collapsed: true }) Brains[parent] = owner with { Collapsed = false };
        return id;
    }

    public Guid DuplicateBrain(Guid id)
    {
        Brain source = Brains[id];
        var children = Brains.Values.ToLookup(brain => brain.Parent);
        var mapping = new Dictionary<Guid, Guid>();
        var pending = new Queue<Brain>();
        pending.Enqueue(source);
        while (pending.TryDequeue(out Brain? brain))
        {
            Guid copy = Guid.NewGuid();
            mapping.Add(brain.Id, copy);
            bool root = brain.Id == source.Id;
            Brains.Add(copy, brain with { Id = copy, Parent = root ? source.Parent : mapping[brain.Parent],
                Title = root ? brain.Title + " copy" : brain.Title, X = brain.X + 36, Y = brain.Y + 64,
                BoardX = brain.BoardX + (root ? 36 : 0), BoardY = brain.BoardY + (root ? 64 : 0) });
            foreach (Brain child in children[brain.Id]) pending.Enqueue(child);
        }
        foreach (Item item in Items.Values.ToArray())
            if (mapping.TryGetValue(item.Board, out Guid board))
            {
                Guid copy = Guid.NewGuid();
                Items.Add(copy, item with { Id = copy, Board = board });
            }
        foreach (Edge edge in Edges.Values.ToArray())
            if (mapping.TryGetValue(edge.From, out Guid from) && mapping.TryGetValue(edge.To, out Guid to))
            {
                Guid copy = Guid.NewGuid();
                Edges.Add(copy, new(copy, from, to));
            }
        return mapping[id];
    }

    public void MoveBrain(Guid id, Guid parent)
    {
        if (!Brains.TryGetValue(id, out Brain? brain)) return;
        if (parent != Guid.Empty && (!Brains.ContainsKey(parent) || Trail(parent).Any(ancestor => ancestor.Id == id)))
            throw new InvalidOperationException("A Brain can’t be moved into itself or one of its children.");
        if (brain.Parent == parent) return;
        Point board = parent == Guid.Empty ? new Point() : BrainLayout.BoardPosition(this, parent, brain.Title);
        Brains[id] = brain with { Parent = parent, BoardX = board.X, BoardY = board.Y };
        if (!Brains.TryGetValue(parent, out Brain? owner)) return;
        PlaceOnParentBranch(id);
        Brains[parent] = owner with { Collapsed = false };
    }

    public void PlaceOnParentBranch(Guid id)
    {
        if (!Brains.TryGetValue(id, out Brain? brain) || !Brains.TryGetValue(brain.Parent, out Brain? parent)) return;
        var branch = new HashSet<Guid> { id };
        var children = Brains.Values.ToLookup(candidate => candidate.Parent);
        var pending = new Queue<Guid>();
        pending.Enqueue(id);
        while (pending.TryDequeue(out Guid next))
            foreach (Brain child in children[next])
                if (branch.Add(child.Id)) pending.Enqueue(child.Id);
        Point slot = BrainLayout.MapPosition(this, parent, brain.Title, branch);
        var shift = new Vector(slot.X - brain.X, slot.Y - brain.Y);
        foreach (Guid member in branch)
        {
            Brain item = Brains[member];
            Brains[member] = item with { X = item.X + shift.X, Y = item.Y + shift.Y };
        }
    }
}

internal sealed class Journal
{
    private const int Limit = 60;
    private readonly List<(Content Before, Content After)> undo = [];
    private readonly Stack<(Content Before, Content After)> redo = new();
    public Content Current { get; private set; }
    public bool CanUndo => undo.Count != 0;
    public bool CanRedo => redo.Count != 0;
    public event Action? Changed;

    public Journal(Content content) => Current = content;

    public void Edit(Action<Content> edit)
    {
        Content next = Current.Copy();
        edit(next);
        if (Same(Current.Brains, next.Brains) && Same(Current.Items, next.Items) && Same(Current.Edges, next.Edges)) return;
        undo.Add((Current, next));
        if (undo.Count > Limit) undo.RemoveAt(0);
        redo.Clear();
        Current = next;
        Changed?.Invoke();
    }

    private static bool Same<T>(Dictionary<Guid, T> left, Dictionary<Guid, T> right) where T : class =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out T? value) && Equals(pair.Value, value));

    public void Undo()
    {
        if (!CanUndo) return;
        var entry = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Push(entry);
        Current = entry.Before;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!redo.TryPop(out var entry)) return;
        undo.Add(entry);
        Current = entry.After;
        Changed?.Invoke();
    }

    public void Reset(Content content)
    {
        undo.Clear();
        redo.Clear();
        Current = content;
        Changed?.Invoke();
    }
}
