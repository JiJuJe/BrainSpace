using System.Windows;

namespace BrainSpace;

internal static class BrainLayout
{
    private const double MapGutter = 30;
    private const double BoardGutter = 18;
    private const double GridTop = -180;
    private const double RowStep = 105;

    public static Size SizeOf(string title, bool root, bool home) => new(
        Math.Clamp(title.Length * (root ? 15 : 11) + 65, root ? 255 : 150, root ? 390 : 275),
        root ? 92 : home ? 62 : 52);

    public static Rect Bounds(Brain brain, bool home) => new(
        home ? new Point(brain.X, brain.Y) : new Point(brain.BoardX, brain.BoardY),
        SizeOf(brain.Title, home && brain.Parent == Guid.Empty, home));

    public static Point MapPosition(Content data, Brain parent, string title, IReadOnlySet<Guid>? moving = null)
    {
        Rect owner = Bounds(parent, home: true);
        Size size = SizeOf(title, root: false, home: true);
        Brain[] remaining = data.Brains.Values.Where(brain => moving?.Contains(brain.Id) != true).ToArray();
        Rect[] siblings = remaining.Where(brain => brain.Parent == parent.Id && brain.X >= owner.Right)
            .Select(brain => Bounds(brain, home: true)).ToArray();
        double x = owner.Right + 95;
        double y = owner.Y + (owner.Height - size.Height) / 2;
        if (siblings.Length > 0)
        {
            double[] columns = siblings.Select(sibling => sibling.X).Order().ToArray();
            x = columns[columns.Length / 2];
            y = siblings.Max(sibling => sibling.Bottom) + MapGutter;
        }
        Rect[] obstacles = remaining.Select(brain => Bounds(brain, home: true)).OrderBy(rect => rect.Top).ToArray();
        return new(x, FindFreeY(obstacles, size, x, y, MapGutter));
    }

    public static Point BoardPosition(Content data, Guid parent, string title, Point? preferred = null)
    {
        Size size = SizeOf(title, root: false, home: false);
        Rect[] obstacles = BoardObstacles(data, parent);
        if (preferred is Point point) return new(point.X, FindFreeY(obstacles, size, point.X, point.Y, BoardGutter));

        Point first = new(-230, double.PositiveInfinity);
        for (int column = 0; column < 3; column++)
        {
            double x = -230 + 245 * column;
            double y = FindFreeY(obstacles, size, x, GridTop, BoardGutter, grid: true);
            if (y < first.Y) first = new(x, y);
        }
        return first;
    }

    public static Point FreeRootPosition(Content data, string title)
    {
        Rect[] obstacles = data.Brains.Values.Select(brain => Bounds(brain, home: true)).OrderBy(rect => rect.Top).ToArray();
        return new(-420, FindFreeY(obstacles, SizeOf(title, root: true, home: true), -420, -130, MapGutter));
    }

    public static Point FreeNotePosition(Content data, Guid board, Size size) =>
        new(-180, FindFreeY(BoardObstacles(data, board), size, -180, -20, BoardGutter));

    private static Rect[] BoardObstacles(Content data, Guid board) => data.Brains.Values.Where(brain => brain.Parent == board)
        .Select(brain => Bounds(brain, home: false))
        .Concat(data.Items.Values.Where(item => item.Board == board).Select(item => new Rect(item.X, item.Y, item.Width, item.Height)))
        .OrderBy(rect => rect.Top).ToArray();

    private static double FindFreeY(Rect[] obstacles, Size size, double x, double y, double gutter, bool grid = false)
    {
        foreach (Rect occupied in obstacles)
        {
            if (occupied.Right + gutter <= x || occupied.Left - gutter >= x + size.Width || occupied.Bottom + gutter <= y) continue;
            if (occupied.Top - gutter >= y + size.Height) break;
            y = occupied.Bottom + gutter;
            if (grid) y = GridTop + Math.Ceiling((y - GridTop) / RowStep) * RowStep;
        }
        return y;
    }
}
