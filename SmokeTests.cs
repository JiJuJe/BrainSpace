using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    internal double MeasureOverviewFrames()
    {
        Viewport baseline = View;
        var timings = new double[36];
        var drawing = new DrawingGroup();
        for (int frame = -3; frame < timings.Length; frame++)
        {
            PanFrom(baseline, new(frame * 5, frame * 2), notify: false);
            long before = Stopwatch.GetTimestamp();
            using (DrawingContext context = drawing.Open()) OnRender(context);
            if (frame >= 0) timings[frame] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
        }
        PanFrom(baseline, new(), notify: false);
        Array.Sort(timings);
        return timings[(int)Math.Ceiling(timings.Length * .95) - 1];
    }

    internal Dictionary<Guid, double> CheckLabelAnchors(int expected)
    {
        if (overviewLabels.Count != expected) throw new InvalidOperationException("Some sample labels were not drawn.");
        var heights = new Dictionary<Guid, double>();
        foreach (OverviewLabel label in overviewLabels)
        {
            Rect world = label.Object.Bounds;
            Point anchor = ToScreen(new(world.X + world.Width / 2, world.Y + world.Height / 2));
            Point labelCenter = new(label.Bounds.X + label.Bounds.Width / 2, label.Bounds.Y + label.Bounds.Height / 2);
            if ((anchor - labelCenter).Length > .001 || HitOverview(anchor)?.Id != label.Object.Id)
                throw new InvalidOperationException($"The overview moved or misidentified {label.Object.Title}.");
            heights.Add(label.Object.Id, label.Artwork.Height * label.TextScale);
        }
        return heights;
    }

    internal bool VerifyAnimatedZoom(Point anchor)
    {
        Point anchored = ToWorld(anchor);
        double zoom = View.Zoom;
        Zoom(1.2, anchor);
        if (motionTo is null) return Math.Abs(View.Zoom - zoom * 1.2) < .001 && (ToWorld(anchor) - anchored).Length < .001;
        AdvanceCamera(.45);
        bool inFlight = View.Zoom > zoom && View.Zoom < zoom * 1.2 && (ToWorld(anchor) - anchored).Length < .001;
        AdvanceCamera(1);
        return inFlight && Math.Abs(View.Zoom - zoom * 1.2) < .001 && (ToWorld(anchor) - anchored).Length < .001;
    }

    internal bool VerifyWholeWorkspace(bool expanded = false)
    {
        Viewport before = view;
        ZoomTo(0, new(35, 40), animate: false);
        double diameter = workspaceRadius * view.Zoom * 2;
        bool fits = view.WholeWorkspace && view.X == 0 && view.Y == 0 && diameter <= ActualWidth - 63 && diameter <= ActualHeight - 99;
        bool includesContent = objects.All(obj => FarCornerSquared(obj.Bounds) < workspaceRadius * workspaceRadius);
        PanFrom(view, new(220, -140));
        bool pinned = view.X == 0 && view.Y == 0;
        SetView(before);
        return fits && includesContent && pinned && (!expanded || workspaceRadius > StartingRadius);
    }

    internal bool VerifyGlobeRotation()
    {
        Viewport before = view;
        ZoomTo(0, animate: false);
        SetView(view with { Rotation = UprightGlobe }, notify: false);
        EnsureGlobeAnchors();
        SceneObject root = objects.First();
        Point3D anchor = globeAnchors[root.Order];
        double radius = GlobeRadius;
        DragGlobe(Center, Center + new Vector(.23 * radius, 0));
        Point3D horizontal = GlobeMatrix().Transform(anchor);
        DragGlobe(Center, Center + new Vector(0, -.23 * radius));
        Point3D vertical = GlobeMatrix().Transform(anchor);
        Point marker = GlobeScreen(vertical, Center, radius);
        bool rotated = horizontal.X > anchor.X + .05 && vertical.Y < horizontal.Y - .05 &&
            vertical.Z > 0 && view.WholeWorkspace && view.X == 0 && view.Y == 0 && HitGlobe(marker)?.Id == root.Id;

        Point world = new(root.Bounds.X + root.Bounds.Width / 2, root.Bounds.Y + root.Bounds.Height / 2);
        Zoom(1.6, marker, animate: false);
        bool zoomedToMarker = !view.WholeWorkspace && (ToWorld(marker) - world).Length < .001;
        ZoomTo(0, animate: false);
        SetView(view with { Rotation = UprightGlobe }, notify: false);
        RotateGlobe(new(0, 1, 0), 180, notify: false);
        bool backHidden = GlobeMatrix().Transform(anchor).Z < 0 && HitGlobe(Center) is null;
        SetView(before with { Rotation = before.Rotation ?? UprightGlobe });
        return rotated && zoomedToMarker && backHidden && TryBounds(root.Id, out Rect unchanged) && unchanged == root.Bounds;
    }

    internal double MeasureGlobeFrames()
    {
        Viewport before = view;
        ZoomTo(0, animate: false);
        var timings = new double[36];
        var drawing = new DrawingGroup();
        for (int frame = -3; frame < timings.Length; frame++)
        {
            RotateGlobe(new(.2, 1, 0), 2.3, notify: false);
            long started = Stopwatch.GetTimestamp();
            using (DrawingContext context = drawing.Open()) OnRender(context);
            if (frame >= 0) timings[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        SetView(before with { Rotation = before.Rotation ?? UprightGlobe });
        Array.Sort(timings);
        return timings[(int)Math.Ceiling(timings.Length * .95) - 1];
    }

    internal void RotateGlobePreview()
    {
        RotateGlobe(new(0, 1, 0), 32, notify: true);
        RotateGlobe(new(1, 0, 0), -22, notify: true);
    }
}

internal static class SmokeTests
{
    public static int Run(string? destination)
    {
        string output = destination ?? Path.Combine(Path.GetTempPath(), "BrainSpace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string dataPath = Path.Combine(output, Guid.NewGuid().ToString("N"));
        var report = new List<string>();
        try
        {
            Guid root, child, grandchild, note, stroke, image;
            var data = new Content();
            root = data.AddBrain(Guid.Empty, "Engineering", new(-420, -30), true);
            child = data.AddBrain(root, "Field notes", new(-20, -110), true);
            grandchild = data.AddBrain(child, "Latest", new(285, -80), true);
            note = Guid.NewGuid(); stroke = Guid.NewGuid(); image = Guid.NewGuid();
            var orientation = new GlobeOrientation(0, .5, 0, Math.Sqrt(.75));
            var session = new Session(grandchild, new(), new()
            {
                [grandchild] = new(19, -27, 1.2),
                [Guid.Empty] = new(0, 0, .08, WholeWorkspace: true, Rotation: orientation)
            });
            using (var store = new Storage(dataPath))
            {
                store.Load();
                var ink = new[] { new Point(2, 2), new Point(12, 18), new Point(25, 8) };
                data.Items[note] = new(note, grandchild, ItemKind.Text, -180, -105, 300, 130, "Bridge measured 42 meters.\nSecond line", Bold: true);
                data.Items[stroke] = new(stroke, grandchild, ItemKind.Ink, 10, 80, 32, 25, Color: "#59CEE0", Points: ink);
                var bitmap = BitmapSource.Create(24, 18, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)210, 24 * 18 * 4).ToArray(), 96);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
                var asset = store.StoreImage(stream);
                var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(bitmap));
                using var photo = new MemoryStream(); jpeg.Save(photo); photo.Position = 0;
                var jpegAsset = store.StoreImage(photo);
                Check(store.LoadImage(jpegAsset.Name, 24)?.PixelWidth == 24, "JPEG import and decoding work");
                data.Items[image] = new(image, grandchild, ItemKind.Image, 125, -105, 120, 90, Target: asset.Name);
                Guid edge = Guid.NewGuid(); data.Edges[edge] = new(edge, root, grandchild);
                store.Save(data, session);
            }
            using (var store = new Storage(dataPath))
            {
                var (loaded, state) = store.Load();
                Check(loaded.Trail(grandchild).Select(brain => brain.Id).SequenceEqual(new[] { root, child, grandchild }), "Three-level hierarchy survives restart");
                Check(loaded.Brains[grandchild].X == 285 && state?.Board == grandchild && state.Views[grandchild].Zoom == 1.2, "Node positions, active board, and zoom survive restart");
                Check(state?.Views[Guid.Empty] is { WholeWorkspace: true } saved && saved.Rotation == orientation, "The globe orientation survives a workspace restart");
                Check(loaded.Items[note].Text.Contains("Second line") && loaded.Items[note].Bold, "Multiline text and formatting survive restart");
                Check(loaded.Items[stroke].Points?[1] == new Point(12, 18), "Drawing points survive restart");
                Check(store.LoadImage(loaded.Items[image].Target, 24)?.PixelHeight == 18, "PNG assets are decoded after restart");
                var history = new Journal(loaded);
                history.Edit(content => content.Remove([child]));
                Check(history.Current.Brains.Count == 1 && history.Current.Items.Count == 0 && history.Current.Edges.Count == 0, "Deleting a subtree removes dependent objects and edges");
                history.Undo(); Check(history.Current.Brains.Count == 3 && history.Current.Items.Count == 3, "Undo restores a subtree");
                history.Redo(); Check(history.Current.Brains.Count == 1, "Redo removes the subtree again");
                history.Undo();
                Guid duplicate = Guid.Empty;
                history.Edit(content => duplicate = content.DuplicateBrain(child));
                Check(history.Current.Brains.Values.Count(brain => brain.Parent == duplicate) == 1 && history.Current.Items.Count == 6, "Duplicating a Brain copies its descendants and board objects");
                try { history.Edit(content => content.MoveBrain(root, grandchild)); throw new Exception("Cycle was accepted"); }
                catch (InvalidOperationException) { report.Add("PASS Cyclic moves are rejected"); }
                string archive = Path.Combine(output, "round-trip.brainspace");
                store.Export(archive, loaded, session);
                using var copy = new Storage(Path.Combine(dataPath, "copy"));
                copy.Load();
                var (imported, importedSession) = copy.Import(archive);
                Check(imported.Brains.Count == 3 && imported.Items.Count == 3 && imported.Edges.Count == 1 && importedSession?.Board == grandchild &&
                    copy.LoadImage(imported.Items[image].Target, 24) is not null, "Backup archive restores relationships, text, ink, camera, and assets");
                Content merged = loaded.Copy();
                Shell.Merge(merged, imported);
                Check(merged.Brains.Count == 6 && merged.Items.Count == 6 && merged.Brains.ContainsKey(root), "Import as copy retains existing records");
            }

            var layout = new Content();
            Guid parent = layout.AddBrain(Guid.Empty, "Sample space", new(-620, -40), true);
            Guid first = layout.AddBrain(parent, "First", new(-275, -225), true);
            Guid second = layout.AddBrain(parent, "Second", new(-275, -117), true);
            Guid third = layout.AddBrain(parent, "Third", new(-275, 10), true);
            Guid obstacle = layout.AddBrain(Guid.Empty, "Separate branch", new(-275, 100), true);
            Guid boardNote = Guid.NewGuid();
            layout.Items.Add(boardNote, new(boardNote, parent, ItemKind.Text, -230, -75, 152, 60, "Reserved"));
            var siblingLocations = new[] { layout.Brains[first], layout.Brains[second], layout.Brains[third] };
            Guid automatic = layout.AddBrain(parent, "New topic", null, false);
            Brain added = layout.Brains[automatic];
            Check(added.X == -275 && added.Y >= BrainLayout.Bounds(layout.Brains[obstacle], home: true).Bottom + 30,
                "A child created inside a board joins the sibling column and skips another branch occupying its slot");
            Check(added.BoardX == 15 && added.BoardY == -75, "A new child uses the first available board slot and avoids notes");
            Guid legacy = layout.AddBrain(parent, "Existing topic", new(480, 220), true);
            layout.PlaceOnParentBranch(legacy);
            Check(layout.Brains[legacy].X == added.X && layout.Brains[legacy].Y > added.Y && siblingLocations.All(sibling => layout.Brains[sibling.Id] == sibling),
                "Place with siblings repairs an existing child and leaves its siblings in place");

            var spatial = new SpatialIndex<object>();
            for (int i = 0; i < 12_000; i++) spatial.Add(new((i % 100) * 320, (i / 100) * 150, 170, 52), new());
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
            {
                var results = spatial.Query(new(i * 73, i * 31, 1240, 720));
                if (results.Count is < 1 or > 40) throw new Exception("Viewport index returned an invalid count.");
            }
            timer.Stop();
            report.Add($"PASS Spatial queries over 12,000 objects, 360 views in {timer.Elapsed.TotalMilliseconds:F1} ms (index only)");

            using (var benchmarkStore = new Storage(Path.Combine(dataPath, "large")))
            {
                var large = new Content();
                Guid main = Guid.NewGuid();
                large.Brains.Add(main, new(main, Guid.Empty, "Large map", -240, -45, 0, 0));
                for (int i = 1; i <= 4_000; i++)
                {
                    Guid id = Guid.NewGuid();
                    large.Brains.Add(id, new(id, main, $"Topic {i}", (i % 70) * 280, (i / 70) * 95, 0, 0));
                }
                var surface = new WorkspaceCanvas(new Journal(large), benchmarkStore, new Theme(new()));
                surface.Measure(new(1200, 740)); surface.Arrange(new(0, 0, 1200, 740));
                surface.SetBoard(Guid.Empty, new());
                surface.Fit(animate: false);
                Capture(surface, 1200, 740);
                Check(surface.View.Zoom < 1 && surface.TryBounds(main, out _), "4,001-node canvas builds and renders at far zoom");
                Check(surface.VerifyWholeWorkspace(expanded: true), "The workspace globe grows around distant content and fits the screen at farthest zoom");
                double milliseconds = surface.MeasureOverviewFrames();
                report.Add($"MEASURE 4,001-node map, p95 overview drawing commands over 36 pans: {milliseconds:F1} ms (CPU only)");
                double globeMilliseconds = surface.MeasureGlobeFrames();
                report.Add($"MEASURE 4,001-node map, p95 globe drawing commands over 36 rotations: {globeMilliseconds:F1} ms (CPU only)");
                Check(Shell.SearchIndexSmoke(large), "Search finds an exact Brain title and ranks it first in a 4,001-Brain map");
            }

            var shell = new Shell(new Storage(dataPath));
            shell.ShowActivated = false;
            shell.Show();
            shell.UpdateLayout();
            var created = shell.CheckUi(note, report);
            shell.Close();
            using (var reopened = new Storage(dataPath))
            {
                var (updated, updatedSession) = reopened.Load();
                Check(updated.Brains[created.Child].Parent == created.Root && updatedSession?.Board == created.Child, "New Brains and navigation flush when the window closes");
            }

            var demo = new Shell(new Storage(Path.Combine(dataPath, "preview")));
            demo.ShowActivated = false;
            demo.Show();
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "home.png"));
            demo.SetZoomPreview(.5);
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "mid-zoom.png"));
            var medium = demo.CheckPreviewAnchors();
            demo.SetZoomPreview(.1);
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "far-zoom.png"));
            var far = demo.CheckPreviewAnchors();
            Check(far.All(label => label.Value >= 5.5 && label.Value < medium[label.Key]),
                "At 10% all six sample names are drawn smaller on the same world anchors and each center selects its original Brain");
            Check(demo.VerifyWorkspaceCircle(), "The outer zoom centers the full circle and keeps it visible during a pan");
            Check(demo.VerifyGlobe(), "Dragging rotates the globe horizontally and vertically, back-side nodes are hidden, and zooming preserves the world position");
            demo.SetZoomPreview(0);
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "workspace-overview.png"));
            demo.RotateGlobePreview();
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "workspace-globe-rotated.png"));
            demo.SetZoomPreview(.1);
            demo.ShowSearchPreview();
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "search.png"));
            demo.PreviewMenu(Path.Combine(output, "context-menu.png"));
            demo.PreviewBoard();
            demo.UpdateLayout();
            SaveScreenshot(demo, Path.Combine(output, "board.png"));
            demo.Close();

            File.WriteAllLines(Path.Combine(output, "results.txt"), report);
            return 0;
        }
        catch (Exception error)
        {
            report.Add(error.ToString());
            File.WriteAllLines(Path.Combine(output, "results.txt"), report);
            return 1;
        }

        void Check(bool passed, string line)
        {
            if (!passed) throw new InvalidOperationException(line);
            report.Add("PASS " + line);
        }
    }

    internal static RenderTargetBitmap Capture(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    internal static void SaveScreenshot(FrameworkElement window, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Capture(window, (int)window.ActualWidth, (int)window.ActualHeight)));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}

internal sealed partial class Shell
{
    internal void PreviewBoard() => Navigate(journal.Current.Brains.Values.Single(brain => brain.Title == "Weekly review").Id);
    internal void SetZoomPreview(double zoom) => canvas.ZoomTo(zoom, animate: false);
    internal Dictionary<Guid, double> CheckPreviewAnchors() => canvas.CheckLabelAnchors(6);
    internal bool VerifyWorkspaceCircle() => canvas.VerifyWholeWorkspace();
    internal bool VerifyGlobe() => canvas.VerifyGlobeRotation();
    internal void RotateGlobePreview() => canvas.RotateGlobePreview();
    internal void ShowSearchPreview() => ShowSearch();
    internal static bool SearchIndexSmoke(Content content) => Match(BuildSearchIndex(content), "Topic 3999", CancellationToken.None) is [{ Title: "Topic 3999" }];
    internal void PreviewMenu(string path)
    {
        CloseModal();
        var menu = CreateMenu([("Open Brain", () => { }), ("New child Brain", () => { }), ("Rename", () => { }), ("Move to…", () => { })]);
        menu.Measure(new(220, 220));
        menu.Arrange(new(0, 0, 200, menu.DesiredSize.Height));
        menu.UpdateLayout();
        SmokeTests.SaveScreenshot(menu, path);
    }

    internal (Guid Root, Guid Child) CheckUi(Guid note, List<string> report)
    {
        if (board == Guid.Empty || !WindowChromeAvailable()) throw new InvalidOperationException("Window or saved board failed to open.");
        report.Add("PASS Application opens a native Windows window on the previously open workspace");
        Point anchor = new(350, 290);
        Point before = canvas.ToWorld(anchor);
        canvas.Zoom(1.2, anchor, animate: false);
        if ((canvas.ToWorld(anchor) - before).Length > .001) throw new InvalidOperationException("Cursor position changed under zoom.");
        report.Add("PASS Cursor-anchored zoom retains the same canvas location");
        if (!canvas.VerifyAnimatedZoom(anchor)) throw new InvalidOperationException("Animated zoom lost its anchor or final magnification.");
        report.Add("PASS Smoothed zoom preserves the cursor anchor and reaches its target");
        Guid originalBoard = board;
        Point location = new(17, -19);
        Point beforePan = canvas.ToScreen(location);
        var move = new Vector(82, -46);
        canvas.PanFrom(canvas.View, move);
        if ((canvas.ToScreen(location) - beforePan - move).Length > .001) throw new InvalidOperationException("Pan moved the camera by the wrong amount.");
        Viewport savedView = canvas.View;
        Navigate(Guid.Empty);
        Navigate(originalBoard);
        if (canvas.View != savedView) throw new InvalidOperationException("Returning to a board did not restore the panned camera.");
        report.Add("PASS Pan applies the expected offset and the viewport survives board navigation");
        ShowSearch();
        TextBox input = Find<TextBox>(modalHost) ?? throw new InvalidOperationException("Search input was not found.");
        input.Text = "42 meters";
        ListBox results = Find<ListBox>(modalHost) ?? throw new InvalidOperationException("Search results were not found.");
        if (results.Items.Count != 1 || results.SelectedIndex != 0) throw new InvalidOperationException("Nested text search did not return the expected note.");
        report.Add("PASS Global search finds nested note content");
        CloseModal();
        EditText(note, new());
        editor!.AppendText("\nEdited inline.");
        finishEditor!.Invoke(true);
        if (!journal.Current.Items[note].Text.Contains("Edited inline.")) throw new InvalidOperationException("Inline editing failed.");
        journal.Undo();
        if (journal.Current.Items[note].Text.Contains("Edited inline.")) throw new InvalidOperationException("Undo did not revert inline editing.");
        report.Add("PASS Inline text editing commits as one undo operation");
        Button maximizeButton = FindNamedButton("Maximize or restore");
        Click(maximizeButton);
        if (WindowState != WindowState.Maximized) throw new InvalidOperationException("Maximize failed.");
        Click(maximizeButton);
        if (WindowState != WindowState.Normal) throw new InvalidOperationException("Restore failed.");
        Click(FindNamedButton("Minimize"));
        if (WindowState != WindowState.Minimized) throw new InvalidOperationException("Minimize failed.");
        WindowState = WindowState.Normal;
        report.Add("PASS Title-bar minimize, maximize, and restore controls change the native window state");

        Navigate(Guid.Empty);
        Guid root = Create("Created through the UI");
        Navigate(root);
        Guid child = Create("Nested through the UI");
        Navigate(child);
        NavigateHistory(-1);
        if (board != root) throw new InvalidOperationException("Back navigation failed.");
        NavigateHistory(1);
        if (board != child) throw new InvalidOperationException("Forward navigation failed.");
        report.Add("PASS New Brain dialogs create roots and children, and back and forward navigate between them");
        return (root, child);

        Guid Create(string title)
        {
            NewBrain();
            TextBox name = Find<TextBox>(modalHost) ?? throw new InvalidOperationException("The creation dialog has no name input.");
            name.Text = title;
            Button submit = NamedButtons(modalHost).Single(button => button.Content as string == "Create");
            Click(submit);
            return journal.Current.Brains.Values.Single(brain => brain.Title == title).Id;
        }

        Button FindNamedButton(string name) => NamedButtons(this).Single(button => AutomationProperties.GetName(button) == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

        bool WindowChromeAvailable() => System.Windows.Shell.WindowChrome.GetWindowChrome(this) is not null && IsLoaded;
    }

    private static IEnumerable<Button> NamedButtons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject obj)
                foreach (Button found in NamedButtons(obj)) yield return found;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject obj && Find<T>(obj) is T match) return match;
        return null;
    }
}
