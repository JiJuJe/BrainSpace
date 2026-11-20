using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace BrainSpace;

internal sealed partial class Shell : Window
{
    private readonly Storage storage;
    private readonly Journal journal;
    private readonly WorkspaceCanvas canvas;
    private readonly Grid root = new();
    private readonly Grid body = new();
    private readonly Grid boardLayer = new();
    private readonly Grid modalHost = new();
    private readonly Canvas editLayer = new();
    private readonly StackPanel crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel rail = new() { Margin = new(5, 10, 5, 8) };
    private readonly StackPanel boardTools = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(12, 0, 12, 25) };
    private readonly Dictionary<Tool, Button> toolButtons = [];
    private readonly TextBlock zoomText = new() { Text = "100%", MinWidth = 42, TextAlignment = TextAlignment.Center };
    private readonly TextBlock toast = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border toastBox;
    private readonly Button undo, redo, maximize, home, boardSearch;
    private readonly Button inkColor, inkWeight, globeButton, mapButton;
    private readonly Border inkOptions, zoomBox;
    private readonly DispatcherTimer saveTimer, toastTimer;
    private readonly Dictionary<Guid, Viewport> views;
    private readonly List<Guid> navigation = [Guid.Empty];
    private int navigationIndex;
    private Guid board;
    private Preferences settings;
    private Theme theme;
    private bool dirty, closing;
    private Task save = Task.CompletedTask;
    private TextBox? editor;
    private Action<bool>? finishEditor;

    public Shell(Storage storage)
    {
        this.storage = storage;
        var (data, session) = storage.Load();
        if (session is null && data.Brains.Count == 0) Populate(data);
        journal = new(data);
        settings = session?.Settings ?? new();
        views = session?.Views ?? [];
        theme = new(settings);
        ApplyThemeResources();
        Title = "BrainSpace";
        Width = 1320; Height = 850; MinWidth = 710; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new(6), GlassFrameThickness = new(0), UseAeroCaptionButtons = false, CornerRadius = new(0) });
        Icon = (DrawingImage)Application.Current.FindResource("BrainSpaceMark");

        root.RowDefinitions.Add(new() { Height = new(45) });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        Content = root;
        var title = new Grid();
        root.Children.Add(Frame(title, "BarBrush", new Thickness(0, 0, 0, 1), 0));
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        heading.Children.Add(new Image { Source = Icon, Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text = "BrainSpace", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(9, 0, 14, 0) });
        heading.Children.Add(crumbs);
        title.Children.Add(heading);

        var history = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        undo = IconButton("undo", "Undo (Ctrl+Z)", () => journal.Undo(), 36, 32);
        redo = IconButton("redo", "Redo (Ctrl+Y)", () => journal.Redo(), 36, 32);
        history.Children.Add(undo); history.Children.Add(redo);
        title.Children.Add(history);

        var chrome = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        boardSearch = IconButton("search", "Search (Ctrl+K)", ShowSearch, 33, 40);
        chrome.Children.Add(boardSearch);
        var workspaceMenu = IconButton("more", "Workspace actions", () => { }, 34, 40);
        workspaceMenu.Margin = new(0, 0, 12, 0);
        workspaceMenu.Click += (_, _) => ShowWorkspaceMenu(workspaceMenu);
        chrome.Children.Add(workspaceMenu);
        chrome.Children.Add(IconButton("minimize", "Minimize", () => WindowState = WindowState.Minimized, 43, 40));
        maximize = IconButton("maximize", "Maximize or restore", ToggleMaximize, 43, 40);
        chrome.Children.Add(maximize);
        chrome.Children.Add(IconButton("close", "Close", Close, 43, 40));
        title.Children.Add(chrome);
        StateChanged += (_, _) => { maximize.Content = Icons.Draw(WindowState == WindowState.Maximized ? "restore" : "maximize", theme.Muted, 18); };

        body.ColumnDefinitions.Add(new() { Width = new(48) });
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); root.Children.Add(body);
        body.Children.Add(Frame(rail, "BarBrush", new Thickness(0, 0, 1, 0), 0));
        home = IconButton("home", "Home", () => Navigate(Guid.Empty), 37, 39);
        rail.Children.Add(home);
        rail.Children.Add(IconButton("search", "Search (Ctrl+K)", ShowSearch, 37, 39));
        rail.Children.Add(IconButton("plus", "New Brain (Ctrl+N)", NewBrain, 37, 39));
        Grid.SetColumn(boardLayer, 1); body.Children.Add(boardLayer);
        boardLayer.ClipToBounds = true;

        canvas = new(journal, storage, theme)
        {
            CursorZoom = settings.CursorZoom, StrokeColor = Theme.ValidColor(settings.InkColor, "#59CEE0"),
            StrokeWeight = settings.InkWidth is >= 1 and <= 16 ? settings.InkWidth : 3,
            StrokeOpacity = settings.InkOpacity is > 0 and <= 1 ? settings.InkOpacity : 1
        };
        boardLayer.Children.Add(canvas);
        boardLayer.Children.Add(editLayer);
        editLayer.Visibility = Visibility.Collapsed;
        canvas.ViewChanged += viewport => { views[board] = viewport; UpdateZoom(); RepositionEditor(); ScheduleSave(); };
        canvas.ZoomProgress += UpdateZoom;
        canvas.OpenBrain += id => Navigate(id);
        canvas.AddBrain += PromptBrain;
        canvas.EditItem += EditText;
        canvas.OpenLink += OpenItemLink;
        canvas.Connect += Connect;
        canvas.ShowMenu += ShowCanvasMenu;
        canvas.ImportFiles += ImportFiles;
        canvas.SizeChanged += (_, _) => RepositionEditor();
        canvas.SelectionChanged += () => RefreshToolbar();

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        AddTool(Tool.Select, "select", "Select (V)");
        AddTool(Tool.Pen, "pen", "Pen (P)");
        AddTool(Tool.Marker, "marker", "Highlighter (H)");
        AddTool(Tool.Eraser, "eraser", "Eraser (E)");
        AddTool(Tool.Text, "text", "Text (T)");
        tools.Children.Add(IconButton("image", "Import image", PickImages));
        tools.Children.Add(IconButton("link", "Add link", () => EditLink(Guid.Empty)));
        var shapes = IconButton("shape", "Shapes and lines", () => { });
        shapes.Click += (_, _) => ShowMenuAt(shapes,
            ("Rectangle (R)", () => SetTool(Tool.Rectangle)), ("Ellipse (O)", () => SetTool(Tool.Ellipse)), ("Line (L)", () => SetTool(Tool.Line)));
        tools.Children.Add(shapes);
        boardTools.Children.Add(Frame(tools, "BarBrush", new Thickness(1), 8, new Thickness(4)));

        var ink = new StackPanel { Orientation = Orientation.Horizontal };
        inkColor = TextButton("", () => { }, 33, 34);
        inkColor.Click += (_, _) => ShowInkColors();
        AutomationProperties.SetName(inkColor, "Stroke color");
        inkWeight = TextButton("3 px", () => { }, 51, 34);
        inkWeight.ToolTip = "Stroke width and opacity";
        inkWeight.Click += (_, _) => ShowInkWeights();
        ink.Children.Add(inkColor); ink.Children.Add(inkWeight);
        inkOptions = Frame(ink, "BarBrush", new Thickness(1), 12, new Thickness(4));
        inkOptions.Margin = new(8, 0, 0, 0);
        boardTools.Children.Add(inkOptions);
        boardLayer.Children.Add(boardTools);

        var zoom = new StackPanel { Orientation = Orientation.Horizontal };
        zoom.Children.Add(IconButton("minus", "Zoom out", () => canvas.Zoom(1 / 1.2), 30, 33));
        var resetZoom = TextButton(zoomText, canvas.ResetZoom, 51, 33);
        resetZoom.ToolTip = "Reset zoom";
        AutomationProperties.SetName(resetZoom, "Reset zoom to 100 percent");
        zoom.Children.Add(resetZoom);
        zoom.Children.Add(IconButton("plus", "Zoom in", () => canvas.Zoom(1.2), 30, 33));
        globeButton = IconButton("globe", "Workspace globe (Shift+F)", () => { canvas.Focus(); canvas.ShowFullWorkspace(); }, 32, 33);
        mapButton = IconButton("map", "Return to editable map", () => { canvas.Focus(); canvas.Fit(); }, 32, 33);
        zoom.Children.Add(new Grid { Children = { globeButton, mapButton } });
        zoomBox = Frame(zoom, "BarBrush", new Thickness(1), 8, new Thickness(3));
        zoomBox.HorizontalAlignment = HorizontalAlignment.Right;
        zoomBox.VerticalAlignment = VerticalAlignment.Bottom;
        zoomBox.Margin = new(0, 0, 25, 25);
        boardLayer.Children.Add(zoomBox);
        boardLayer.SizeChanged += (_, _) => PositionZoomBox();

        toastBox = Frame(toast, "SurfaceBrush", new Thickness(1), 9, new Thickness(13, 7, 13, 7));
        toastBox.HorizontalAlignment = HorizontalAlignment.Center;
        toastBox.VerticalAlignment = VerticalAlignment.Top;
        toastBox.Margin = new(12, 16, 12, 0);
        toastBox.Visibility = Visibility.Collapsed;
        toastBox.IsHitTestVisible = false;
        boardLayer.Children.Add(toastBox);

        Grid.SetRow(modalHost, 1);
        modalHost.Visibility = Visibility.Collapsed;
        root.Children.Add(modalHost);
        toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); toastBox.Visibility = Visibility.Collapsed; };
        saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); BeginSave(); };
        journal.Changed += () => { if (board != Guid.Empty && !journal.Current.Brains.ContainsKey(board)) Navigate(Guid.Empty); RefreshChrome(); ScheduleSave(); };
        Closing += OnClosing;
        PreviewKeyDown += Shortcuts;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { NavigateHistory(-1); e.Handled = true; }
            if (e.ChangedButton == MouseButton.XButton2) { NavigateHistory(1); e.Handled = true; }
        };
        SizeChanged += (_, _) => RefreshCrumbs();
        Guid initial = session?.Board ?? Guid.Empty;
        if (!data.Brains.ContainsKey(initial)) initial = Guid.Empty;
        Navigate(initial, focus: false);
        if (session is null) ScheduleSave();
        Loaded += (_, _) =>
        {
            if (session is null && canvas.ActualWidth < 1040) canvas.Fit();
            canvas.Focus();
        };

        void AddTool(Tool tool, string icon, string hint)
        {
            Button button = IconButton(icon, hint, () => SetTool(tool));
            toolButtons.Add(tool, button);
            tools.Children.Add(button);
        }
    }

    private void ApplyThemeResources()
    {
        var resources = Application.Current.Resources;
        resources["CanvasBrush"] = theme.Canvas; resources["BarBrush"] = theme.Bar;
        resources["SurfaceBrush"] = theme.Surface; resources["HoverBrush"] = theme.Hover;
        resources["TextBrush"] = theme.Text; resources["MutedBrush"] = theme.Muted;
        resources["BorderBrush"] = theme.Border; resources["AccentBrush"] = theme.Accent;
        resources["ActiveBrush"] = theme.Active;
    }

    private void SetSettings(Preferences updated)
    {
        bool appearanceChanged = settings.Light != updated.Light || settings.Accent != updated.Accent;
        settings = updated;
        if (appearanceChanged)
        {
            theme = new(settings);
            ApplyThemeResources();
            canvas.SetTheme(theme);
        }
        canvas.CursorZoom = settings.CursorZoom;
        canvas.StrokeColor = Theme.ValidColor(settings.InkColor, "#59CEE0");
        canvas.StrokeWeight = settings.InkWidth is >= 1 and <= 16 ? settings.InkWidth : 3;
        canvas.StrokeOpacity = settings.InkOpacity is > 0 and <= 1 ? settings.InkOpacity : 1;
        RefreshChrome();
        ScheduleSave();
    }

    private Button IconButton(string icon, string label, Action click, double width = 36, double height = 36)
    {
        Button button = TextButton(null, click, width, height);
        var brush = new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button };
        FrameworkElement content = Icons.Draw(icon, theme.Muted, 18);
        if (content is Viewbox { Child: Canvas iconCanvas } && iconCanvas.Children[0] is System.Windows.Shapes.Path path)
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, brush);
        button.SetResourceReference(ForegroundProperty, "MutedBrush");
        button.Content = content;
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Button TextButton(object? label, Action click, double width = double.NaN, double height = 36)
    {
        var button = new Button { Content = label, Width = width, Height = height, Padding = new(5, 0, 5, 0) };
        button.Click += (_, _) => click();
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        ToolTipService.SetInitialShowDelay(button, 420);
        return button;
    }

    private static Border Frame(UIElement element, string background, Thickness stroke, double radius, Thickness padding = default)
    {
        var result = new Border { Child = element, CornerRadius = new(radius), BorderThickness = stroke, Padding = padding };
        result.SetResourceReference(Border.BackgroundProperty, background);
        result.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return result;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Navigate(Guid destination, bool track = true, bool focus = true)
    {
        if (destination != Guid.Empty && !journal.Current.Brains.ContainsKey(destination)) return;
        finishEditor?.Invoke(true);
        canvas.StopMotion();
        if (track && navigation[navigationIndex] != destination)
        {
            navigation.RemoveRange(navigationIndex + 1, navigation.Count - navigationIndex - 1);
            navigation.Add(destination); navigationIndex++;
        }
        board = destination;
        canvas.SetBoard(board, views.GetValueOrDefault(board, new()));
        RefreshChrome();
        if (focus) { canvas.Focus(); ScheduleSave(); }
    }

    private void NavigateHistory(int step)
    {
        for (int next = navigationIndex + step; next >= 0 && next < navigation.Count; next += step)
        {
            Guid target = navigation[next];
            if (target != Guid.Empty && !journal.Current.Brains.ContainsKey(target)) continue;
            navigationIndex = next;
            Navigate(target, track: false);
            return;
        }
        if (step < 0 && board != Guid.Empty) Navigate(journal.Current.Brains[board].Parent);
    }

    private void RefreshChrome()
    {
        bool onBoard = board != Guid.Empty;
        bool showRail = settings.Rail && !onBoard;
        body.ColumnDefinitions[0].Width = new(showRail ? 48 : 0);
        rail.Visibility = showRail ? Visibility.Visible : Visibility.Collapsed;
        boardSearch.Visibility = onBoard ? Visibility.Visible : Visibility.Collapsed;
        home.SetResourceReference(BackgroundProperty, onBoard ? "BarBrush" : "ActiveBrush");
        home.SetResourceReference(ForegroundProperty, onBoard ? "MutedBrush" : "AccentBrush");
        undo.IsEnabled = journal.CanUndo; redo.IsEnabled = journal.CanRedo;
        RefreshCrumbs(); RefreshToolbar(); UpdateZoom();
    }

    private void RefreshCrumbs()
    {
        crumbs.Children.Clear();
        if (board == Guid.Empty)
        {
            if (!settings.Rail)
            {
                crumbs.Children.Add(IconButton("search", "Search (Ctrl+K)", ShowSearch, 29, 30));
                crumbs.Children.Add(IconButton("plus", "New Brain (Ctrl+N)", NewBrain, 29, 30));
            }
            return;
        }
        crumbs.Children.Add(IconButton("back", "Back (Alt+Left)", () => NavigateHistory(-1), 28, 30));
        var trail = journal.Current.Trail(board).ToArray();
        if (ActualWidth < 1050 || trail.Length > 3)
        {
            if (trail.Length > 1)
            {
                var parent = TextButton("…", () => { }, 25, 28);
                parent.ToolTip = "Parent Brains";
                parent.Click += (_, _) => ShowMenuAt(parent, trail.Take(trail.Length - 1).Prepend(new(Guid.Empty, Guid.Empty, "Home", 0, 0, 0, 0))
                    .Select(ancestor => (ancestor.Title, (Action)(() => Navigate(ancestor.Id)))).ToArray());
                crumbs.Children.Add(parent);
                trail = trail[^1..];
            }
        }
        foreach (Brain ancestor in trail)
        {
            Guid id = ancestor.Id;
            if (crumbs.Children.Count > 1) crumbs.Children.Add(new TextBlock { Text = "/", Foreground = theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(3, 0, 3, 0) });
            var label = new TextBlock { Text = ancestor.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ActualWidth < 850 ? 120 : 139, VerticalAlignment = VerticalAlignment.Center };
            Button part = TextButton(label, () => { }, double.NaN, 30);
            part.Click += (_, _) =>
            {
                if (id != board) Navigate(id);
                else ShowMenuAt(part, ("Home", () => Navigate(Guid.Empty)), ("New child Brain", NewBrain));
            };
            part.ToolTip = ancestor.Title;
            crumbs.Children.Add(part);
        }
    }

    private void UpdateZoom()
    {
        bool globe = canvas.View.WholeWorkspace;
        double scale = globe ? canvas.View.GlobeMagnification : canvas.View.Zoom;
        zoomText.Text = scale < .01 ? "<1%" : $"{Math.Round(scale * 100)}%";
        globeButton.Visibility = globe ? Visibility.Collapsed : Visibility.Visible;
        mapButton.Visibility = globe ? Visibility.Visible : Visibility.Collapsed;
        boardTools.Visibility = board != Guid.Empty && !globe ? Visibility.Visible : Visibility.Collapsed;
        PositionZoomBox();
    }

    private void SetTool(Tool tool)
    {
        finishEditor?.Invoke(true);
        canvas.SetTool(tool);
        RefreshToolbar();
        canvas.Focus();
    }

    private void RefreshToolbar()
    {
        foreach (var (tool, button) in toolButtons)
        {
            bool active = canvas.CurrentTool == tool;
            button.SetResourceReference(BackgroundProperty, active ? "ActiveBrush" : "BarBrush");
            button.SetResourceReference(ForegroundProperty, active ? "AccentBrush" : "MutedBrush");
        }
        inkOptions.Visibility = canvas.CurrentTool is Tool.Pen or Tool.Marker or Tool.Rectangle or Tool.Ellipse or Tool.Line ? Visibility.Visible : Visibility.Collapsed;
        inkColor.Content = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = Theme.Brush(canvas.StrokeColor) };
        inkWeight.Content = $"{canvas.StrokeWeight:g} px";
        PositionZoomBox();
    }

    private void PositionZoomBox()
    {
        double width = boardLayer.ActualWidth > 0 ? boardLayer.ActualWidth : ActualWidth;
        bool compact = board != Guid.Empty && !canvas.View.WholeWorkspace && (width < 565 || (width < 800 && inkOptions.Visibility == Visibility.Visible));
        zoomBox.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        zoomBox.Margin = compact ? new(0, 16, 18, 0) : new(0, 0, 25, 25);
    }

    private void Notify(string message)
    {
        toast.Text = message;
        toastBox.Visibility = Visibility.Visible;
        toastTimer.Stop(); toastTimer.Start();
    }

    private void ScheduleSave()
    {
        dirty = true;
        if (closing) return;
        saveTimer.Stop(); saveTimer.Start();
    }

    private Session SessionSnapshot() => new(board, settings, new(views));

    private async void BeginSave()
    {
        if (!dirty || closing) return;
        if (!save.IsCompleted) { saveTimer.Start(); return; }
        Content snapshot = journal.Current;
        Session session = SessionSnapshot();
        dirty = false;
        try
        {
            save = Task.Run(() => storage.Save(snapshot, session));
            await save;
        }
        catch (Exception)
        {
            dirty = true;
            Notify("Autosave failed. BrainSpace will retry on the next change or when you close it.");
        }
    }

    private void Flush()
    {
        try { save.GetAwaiter().GetResult(); }
        catch { dirty = true; }
        if (!dirty) return;
        storage.Save(journal.Current, SessionSnapshot());
        dirty = false;
        save = Task.CompletedTask;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        finishEditor?.Invoke(true);
        canvas.StopMotion(finish: true);
        closing = true;
        saveTimer.Stop();
        try { Flush(); }
        catch (Exception error)
        {
            args.Cancel = true;
            closing = false;
            MessageBox.Show(this, $"BrainSpace could not save your edits. The window is still open.\n\n{error.Message}", "BrainSpace", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        storage.Dispose();
    }

    private static void Populate(Content content)
    {
        Guid root = content.AddBrain(Guid.Empty, "My workspace", new(-497, -47), true);
        Guid projects = content.AddBrain(root, "Projects", new(-148, -145), true);
        content.AddBrain(root, "Notes", new(-148, -31), true);
        content.AddBrain(root, "Ideas", new(-148, 83), true);
        Guid review = content.AddBrain(projects, "Weekly review", new(118, -145), true);
        content.AddBrain(projects, "In progress", new(118, -52), true);
        content.Brains[review] = content.Brains[review] with { BoardX = -365, BoardY = -135 };
        Brain inProgress = content.Brains.Values.Single(brain => brain.Title == "In progress");
        content.Brains[inProgress.Id] = inProgress with { BoardX = -365, BoardY = -60 };
        Guid note = Guid.NewGuid();
        content.Items.Add(note, new(note, review, ItemKind.Text, -244, -132, 365, 173,
            "Weekly review\n\nWhat moved forward?\nWhat should I focus on next?", FontSize: 21));
        Guid marker = Guid.NewGuid();
        content.Items.Add(marker, new(marker, review, ItemKind.Marker, -226, -95, 156, 15,
            Color: "#64BDFA", Weight: 8, Points: [new(5, 8), new(60, 7), new(105, 6), new(147, 7)], Opacity: .48));
        Guid line = Guid.NewGuid();
        content.Items.Add(line, new(line, review, ItemKind.Ink, 155, -10, 116, 62, Color: "#65D8E4", Weight: 3,
            Points: [new(3, 58), new(31, 54), new(57, 44), new(80, 30), new(109, 5), new(87, 9), new(109, 5), new(106, 26)]));
        Guid focus = Guid.NewGuid();
        content.Items.Add(focus, new(focus, review, ItemKind.Text, 235, -110, 195, 77, "Next week\nPick one priority.", FontSize: 17, Bold: true));
    }
}
