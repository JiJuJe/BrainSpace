using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrainSpace;

internal sealed partial class Shell
{
    private const string ClipboardItems = "BrainSpace.Items.v1";

    private void NewBrain() => PromptBrain(board == Guid.Empty ? canvas.PointerWorld : null, board);

    private void PromptBrain(Point? where, Guid parent)
    {
        bool home = board == Guid.Empty;
        Point? preferred = home || parent == board ? where : null;
        if (preferred is Point position && home && parent == Guid.Empty && !canvas.IsMouseOver)
        {
            if (journal.Current.Brains.Count != 0) position += new Vector(-115, 140);
            while (journal.Current.Brains.Values.Any(brain => canvas.TryBounds(brain.Id, out Rect bounds) &&
                bounds.IntersectsWith(new Rect(position.X - 12, position.Y - 12, 270, 100)))) position.Y += 112;
            preferred = position;
        }
        Prompt("New Brain", "Name", "", "Create", 160, title =>
        {
            Guid id = Guid.Empty;
            journal.Edit(data => id = data.AddBrain(parent, title, preferred, home));
            canvas.Select(id);
            if (canvas.TryBounds(id, out Rect placed) && !new Rect(canvas.RenderSize).Contains(canvas.ToScreen(new(placed.X + placed.Width / 2, placed.Y + placed.Height / 2))))
                canvas.FocusObject(id);
        });
    }

    private void AddChild(Guid id) => PromptBrain(null, id);

    private void Rename(Guid id)
    {
        if (journal.Current.Brains.TryGetValue(id, out Brain? brain))
            Prompt("Rename Brain", "Name", brain.Title, "Rename", 160, title => journal.Edit(data => data.Brains[id] = brain with { Title = title }));
        else if (journal.Current.Items.TryGetValue(id, out Item? item))
        {
            if (item.Kind == ItemKind.Link) EditLink(id);
            else if (item.Kind == ItemKind.Text) EditText(id, canvas.PointerWorld);
        }
    }

    private void DeleteSelection()
    {
        Guid[] selection = canvas.Selection.ToArray();
        if (selection.Length == 0) return;
        int count = selection.Count(journal.Current.Brains.ContainsKey);
        void Remove() { journal.Edit(data => data.Remove(selection)); canvas.Select(Guid.Empty); }
        if (count > 0) Confirm(count == 1 ? "Delete Brain?" : $"Delete {count} Brains?", "This removes the selected Brains and the workspaces and children inside them. Undo is available.", "Delete", Remove);
        else Remove();
    }

    private void Duplicate(Guid id)
    {
        if (journal.Current.Brains.ContainsKey(id))
        {
            Guid copy = Guid.Empty;
            journal.Edit(data => copy = data.DuplicateBrain(id));
            canvas.Select(copy);
        }
        else PasteItems(SelectedItems(), keepPosition: true);
    }

    private Item[] SelectedItems() => canvas.Selection.Select(id => journal.Current.Items.GetValueOrDefault(id)).OfType<Item>().ToArray();

    private void MoveBrain(Guid id)
    {
        var data = journal.Current;
        var destinations = data.Brains.Values.Where(brain => !data.Trail(brain.Id).Any(parent => parent.Id == id))
            .Select(brain => new HitResult(brain.Id, Guid.Empty, PathOf(brain.Id), "Move under this Brain", "Brain"))
            .OrderBy(hit => hit.Title, StringComparer.CurrentCultureIgnoreCase).Prepend(new HitResult(Guid.Empty, Guid.Empty, "Home", "Move to the main map", "Root"));
        Picker("Move Brain", "Find a destination…", Task.FromResult(destinations.ToArray()), result =>
        {
            journal.Edit(next => next.MoveBrain(id, result.Id));
            Notify("Brain moved.");
        });
    }

    private void ToggleGroup(Guid id)
    {
        Brain brain = journal.Current.Brains[id];
        journal.Edit(data => data.Brains[id] = brain with { Collapsed = !brain.Collapsed });
    }

    private void Connect(Guid from, Guid to)
    {
        if (from == to) return;
        Edge? existing = journal.Current.Edges.Values.FirstOrDefault(edge => (edge.From == from && edge.To == to) || (edge.From == to && edge.To == from));
        journal.Edit(data =>
        {
            if (existing is not null) data.Edges.Remove(existing.Id);
            else { Guid id = Guid.NewGuid(); data.Edges.Add(id, new(id, from, to)); }
        });
        Notify(existing is null ? "Brains connected." : "Connection removed.");
    }

    private void ShowCanvasMenu(Guid id, Point where)
    {
        var actions = new List<(string, Action)>();
        if (journal.Current.Brains.TryGetValue(id, out Brain? brain))
        {
            actions.Add(("Open Brain", () => Navigate(id)));
            actions.Add(("New child Brain", () => AddChild(id)));
            actions.Add(("Rename", () => Rename(id)));
            actions.Add(("Duplicate", () => Duplicate(id)));
            actions.Add(("Move to…", () => MoveBrain(id)));
            if (brain.Parent != Guid.Empty)
                actions.Add(("Place with siblings on map", () => journal.Edit(data => data.PlaceOnParentBranch(id))));
            if (journal.Current.Brains.Values.Any(next => next.Parent == id)) actions.Add((brain.Collapsed ? "Expand group" : "Collapse group", () => ToggleGroup(id)));
            actions.Add(("Connect or unlink…", () =>
            {
                canvas.Connecting = id;
                canvas.InvalidateVisual();
                Notify("Select another Brain to toggle the connection.");
            }));
            actions.Add(("Delete", DeleteSelection));
        }
        else if (journal.Current.Items.TryGetValue(id, out Item? item))
        {
            if (item.Kind == ItemKind.Text)
            {
                actions.Add(("Edit text", () => EditText(id, where)));
                actions.Add(("Text style…", () => TextStyle(item)));
                actions.Add((item.Target.Length > 0 ? "Edit attached link…" : "Attach a link…", () => EditLink(id, attach: true)));
            }
            if (item.Kind == ItemKind.Link) actions.Add(("Edit link…", () => EditLink(id)));
            if (item.Target.Length > 0 && item.Kind is ItemKind.Link or ItemKind.Text) actions.Add(("Open link", () => OpenItemLink(id)));
            actions.Add(("Copy", CopySelection));
            actions.Add(("Duplicate", () => Duplicate(id)));
            actions.Add(("Delete", DeleteSelection));
        }
        else
        {
            actions.Add((board == Guid.Empty ? "New Brain" : "New child Brain", () => PromptBrain(where, board)));
            if (board != Guid.Empty)
            {
                actions.Add(("Add text", () => EditText(Guid.Empty, where)));
                actions.Add(("Paste", () => Paste(where)));
                actions.Add(("Import image…", PickImages));
            }
            actions.Add(("Fit to view", canvas.Fit));
        }
        var menu = CreateMenu(actions);
        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = canvas;
        menu.IsOpen = true;
    }

    private void EditText(Guid id, Point where)
    {
        if (board == Guid.Empty) return;
        finishEditor?.Invoke(true);
        canvas.SetTool(Tool.Select);
        RefreshToolbar();
        Item? existing = journal.Current.Items.GetValueOrDefault(id);
        Item text = existing ?? new(Guid.NewGuid(), board, ItemKind.Text, where.X, where.Y, 330, 150);
        if (existing is not null) canvas.Select(id);
        if (canvas.View.Zoom < .65) canvas.ZoomTo(.72, canvas.ToScreen(new(text.X, text.Y)), animate: false);

        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = text.Text, MaxLength = 120_000,
            FontWeight = text.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStyle = text.Italic ? FontStyles.Italic : FontStyles.Normal,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editLayer.Children.Add(input);
        editLayer.Visibility = Visibility.Visible;
        editor = input;
        editorBounds = new(text.X, text.Y, text.Width, text.Height);
        editorFont = text.FontSize;
        canvas.InlineEdit = true;
        canvas.InvalidateVisual();
        RepositionEditor();
        finishEditor = commit =>
        {
            finishEditor = null;
            editor = null;
            canvas.InlineEdit = false;
            editLayer.Children.Remove(input);
            editLayer.Visibility = Visibility.Collapsed;
            if (commit && (existing is not null || !string.IsNullOrWhiteSpace(input.Text)))
            {
                double height = text.Height;
                if (existing is null || (input.Text != existing.Text && input.LineCount > 4))
                    height = Math.Max(height, Math.Min(700, (input.LineCount + 1) * text.FontSize * 1.4 + 23));
                journal.Edit(data => data.Items[text.Id] = text with { Text = input.Text, Height = height });
                canvas.Select(text.Id);
            }
            canvas.InvalidateVisual();
        };
        input.LostKeyboardFocus += (_, _) => finishEditor?.Invoke(true);
        input.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { finishEditor?.Invoke(false); canvas.Focus(); args.Handled = true; }
            if (args.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { finishEditor?.Invoke(true); canvas.Focus(); args.Handled = true; }
        };
        input.Focus();
        input.Select(text.Text.Length, 0);
    }

    private Rect editorBounds;
    private double editorFont;

    private void RepositionEditor()
    {
        if (editor is null) return;
        Point at = canvas.ToScreen(editorBounds.TopLeft);
        double scale = canvas.View.Zoom;
        Canvas.SetLeft(editor, at.X); Canvas.SetTop(editor, at.Y);
        editor.Width = Math.Max(70, editorBounds.Width * scale);
        editor.Height = Math.Max(48, editorBounds.Height * scale);
        editor.FontSize = editorFont * scale;
        editor.Padding = new(17 * scale, 13 * scale, 17 * scale, 10 * scale);
    }

    private void TextStyle(Item item)
    {
        var panel = new StackPanel();
        TextBox size = Field(panel, "Font size", item.FontSize.ToString(System.Globalization.CultureInfo.CurrentCulture), 6);
        CheckBox bold = Check("Bold", item.Bold), italic = Check("Italic", item.Italic);
        panel.Children.Add(bold); panel.Children.Add(italic);
        Action submit = () =>
        {
            if (!double.TryParse(size.Text, out double value) || !double.IsFinite(value) || value is < 8 or > 144) { size.Focus(); size.SelectAll(); return; }
            CloseModal();
            journal.Edit(data => data.Items[item.Id] = item with { FontSize = value, Bold = bold.IsChecked == true, Italic = italic.IsChecked == true });
        };
        panel.Children.Add(DialogActions("Apply", submit));
        ShowModal("Text style", panel, 395, size);
        size.KeyDown += (_, e) => { if (e.Key == Key.Enter) submit(); };
    }

    private void EditLink(Guid id, bool attach = false)
    {
        if (board == Guid.Empty) return;
        Item? existing = journal.Current.Items.GetValueOrDefault(id);
        Point position = canvas.PointerWorld;
        var panel = new StackPanel();
        TextBox url = Field(panel, "URL", existing?.Target ?? "", 2048);
        TextBox? label = attach ? null : Field(panel, "Label", existing?.Text ?? "", 160);
        var error = Subtle("");
        panel.Children.Add(error);
        Action submit = () =>
        {
            string address = url.Text.Trim();
            if (!TryUrl(address, out Uri? target) && !(attach && address.Length == 0))
            { error.Text = "Enter a full http:// or https:// URL."; url.Focus(); return; }
            CloseModal();
            Guid itemId = existing?.Id ?? Guid.NewGuid();
            if (attach && existing is not null)
                journal.Edit(data => data.Items[itemId] = existing with { Target = target?.AbsoluteUri ?? "" });
            else if (target is not null)
            {
                string title = label!.Text.Trim();
                if (title.Length == 0) title = target.Host;
                double width = Math.Clamp(title.Length * 8 + 38, 110, 290);
                Item item = existing ?? new(itemId, board, ItemKind.Link, position.X, position.Y, width, 42, FontSize: 15);
                journal.Edit(data => data.Items[itemId] = item with { Text = title, Target = target.AbsoluteUri, Width = width });
            }
            canvas.Select(itemId);
        };
        panel.Children.Add(DialogActions(existing is null ? "Add link" : "Apply", submit));
        ShowModal(attach ? "Attach link" : existing is null ? "Add link" : "Edit link", panel, 440, url);
        panel.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { submit(); e.Handled = true; } };
    }

    private static bool TryUrl(string value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;

    private void OpenItemLink(Guid id)
    {
        if (!journal.Current.Items.TryGetValue(id, out Item? item) || !TryUrl(item.Target, out Uri? uri)) return;
        try { Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { Notify(error.Message); }
    }

    private void PickImages()
    {
        Point position = canvas.PointerWorld;
        var dialog = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp", Multiselect = true, Title = "Import images" };
        if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames, position);
    }

    private async void ImportFiles(string[] paths, Point where)
    {
        if (board == Guid.Empty || paths.Length == 0) return;
        Guid destination = board;
        var imported = new List<Item>();
        string? failure = null;
        foreach (string path in paths)
        {
            try
            {
                var file = await Task.Run(() => { using var input = File.OpenRead(path); return storage.StoreImage(input); });
                imported.Add(ImageItem(file, destination, where + new Vector(imported.Count * 26, imported.Count * 26)));
            }
            catch (Exception error) { failure ??= error.Message; }
        }
        if (closing || !journal.Current.Brains.ContainsKey(destination)) return;
        if (imported.Count > 0)
        {
            journal.Edit(data => { foreach (Item item in imported) data.Items.Add(item.Id, item); });
            if (board == destination && imported.Count == 1) canvas.Select(imported[0].Id);
        }
        if (failure is not null) Notify($"Couldn’t import an image. {failure}");
    }

    private static Item ImageItem((string Name, int Width, int Height) image, Guid destination, Point where)
    {
        double scale = Math.Min(1, 420d / Math.Max(image.Width, image.Height));
        return new(Guid.NewGuid(), destination, ItemKind.Image, where.X, where.Y, image.Width * scale, image.Height * scale, Target: image.Name);
    }

    private void CopySelection()
    {
        Item[] copied = SelectedItems();
        if (copied.Length == 0) return;
        try
        {
            var data = new DataObject();
            data.SetData(ClipboardItems, JsonSerializer.Serialize(copied));
            if (copied.Length == 1 && copied[0].Kind is ItemKind.Text or ItemKind.Link)
                data.SetText(copied[0].Kind == ItemKind.Link ? copied[0].Target : copied[0].Text);
            Clipboard.SetDataObject(data, true);
            Notify(copied.Length == 1 ? "Copied." : $"Copied {copied.Length} objects.");
        }
        catch (Exception error) { Notify($"Clipboard unavailable. {error.Message}"); }
    }

    private void Paste(Point where)
    {
        if (board == Guid.Empty) return;
        try
        {
            if (Clipboard.GetData(ClipboardItems) is string json && json.Length <= 12_000_000 && JsonSerializer.Deserialize<Item[]>(json) is { Length: > 0 } items)
            {
                if (items.Length > 500 || items.Any(item => item is null || !Storage.ValidItem(item) ||
                    (item.Kind == ItemKind.Image && !File.Exists(Path.Combine(storage.Images, item.Target)))))
                { Notify("Some clipboard objects aren’t available in this data folder."); return; }
                PasteItems(items, where: where);
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream); stream.Position = 0;
                Item image = ImageItem(storage.StoreImage(stream), board, where);
                journal.Edit(data => data.Items.Add(image.Id, image)); canvas.Select(image.Id);
            }
            else if (Clipboard.ContainsFileDropList()) ImportFiles(Clipboard.GetFileDropList().Cast<string>().ToArray(), where);
            else if (Clipboard.ContainsText())
            {
                string plain = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(plain)) return;
                bool link = TryUrl(plain.Trim(), out Uri? uri);
                Guid id = Guid.NewGuid();
                var item = new Item(id, board, link ? ItemKind.Link : ItemKind.Text, where.X, where.Y, link ? 185 : 350, link ? 42 : 170,
                    link ? uri!.Host : plain[..Math.Min(120_000, plain.Length)], link ? uri!.AbsoluteUri : "", FontSize: link ? 15 : 19);
                journal.Edit(data => data.Items.Add(id, item)); canvas.Select(id);
            }
        }
        catch (Exception error) { Notify($"Couldn’t paste. {error.Message}"); }
    }

    private void PasteItems(Item[] items, Point? where = null, bool keepPosition = false)
    {
        if (board == Guid.Empty || items.Length == 0) return;
        Vector offset = keepPosition ? new(24, 24) : (where ?? canvas.PointerWorld) - new Point(items.Min(item => item.X), items.Min(item => item.Y));
        Guid[] ids = items.Select(_ => Guid.NewGuid()).ToArray();
        journal.Edit(data =>
        {
            for (int i = 0; i < items.Length; i++)
                data.Items.Add(ids[i], items[i] with { Id = ids[i], Board = board, X = items[i].X + offset.X, Y = items[i].Y + offset.Y });
        });
        canvas.Select(Guid.Empty);
        canvas.Selection.UnionWith(ids);
        canvas.InvalidateVisual();
    }

    private void ShowInkColors()
    {
        var palette = new WrapPanel { Width = 184, Margin = new(6) };
        foreach (string hex in new[] { "#59CEE0", "#83C6FB", "#A996FF", "#EC8CB3", "#F5A485", "#F5D875", "#78D8AA", "#F4F5F7", "#8590A3", "#334258" })
        {
            var paletteButton = TextButton(new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = Theme.Brush(hex) }, () =>
            {
                SetSettings(settings with { InkColor = hex });
            }, 35, 33);
            AutomationProperties.SetName(paletteButton, hex);
            palette.Children.Add(paletteButton);
        }
        var menu = CreateMenu([]);
        menu.Items.Add(palette);
        foreach (Button button in palette.Children) button.Click += (_, _) => menu.IsOpen = false;
        menu.PlacementTarget = inkColor; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
    }

    private void ShowInkWeights() => ShowMenuAt(inkWeight, new (string, Action)[]
    {
        ("1.5 px", () => SetStrokeWidth(1.5)), ("3 px", () => SetStrokeWidth(3)), ("6 px", () => SetStrokeWidth(6)), ("10 px", () => SetStrokeWidth(10)),
        ("Light opacity", () => SetStrokeOpacity(.30)), ("Medium opacity", () => SetStrokeOpacity(.65)), ("Full opacity", () => SetStrokeOpacity(1))
    });

    private void SetStrokeWidth(double value) => SetSettings(settings with { InkWidth = value });
    private void SetStrokeOpacity(double value) => SetSettings(settings with { InkOpacity = value });

    private void Shortcuts(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (modalHost.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) { e.Handled = true; CloseModal(); }
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;
        Action? action = (ctrl, alt, key) switch
        {
            (true, false, Key.K) => ShowSearch, (true, false, Key.N) => NewBrain,
            (true, false, Key.Z) => shift ? journal.Redo : journal.Undo, (true, false, Key.Y) => journal.Redo,
            (true, false, Key.C) => CopySelection, (true, false, Key.V) => () => Paste(canvas.PointerWorld),
            (true, false, Key.D) when canvas.Selection.Count > 0 => () => Duplicate(canvas.Selection.First()),
            (true, false, Key.D0) => () => canvas.ZoomTo(1),
            (false, true, Key.Left) => () => NavigateHistory(-1), (false, true, Key.Right) => () => NavigateHistory(1),
            (false, true, Key.Home) => () => Navigate(Guid.Empty),
            (false, false, Key.Delete) => DeleteSelection,
            (false, false, Key.Escape) => () => { if (!canvas.CancelGesture()) { canvas.Connecting = null; canvas.Select(Guid.Empty); } },
            (false, false, Key.Enter) when canvas.View.WholeWorkspace && canvas.Selection.Count == 1 && Keyboard.FocusedElement == canvas =>
                () => { Guid id = canvas.Selection.First(); if (journal.Current.Brains.ContainsKey(id)) Navigate(id); else canvas.FocusObject(id); },
            (false, false, Key.F2 or Key.Enter) when !canvas.View.WholeWorkspace && canvas.Selection.Count == 1 && Keyboard.FocusedElement == canvas => () => Rename(canvas.Selection.First()),
            (false, false, Key.Tab) when !shift && canvas.Selection.Count == 1 && Keyboard.FocusedElement == canvas && journal.Current.Brains.ContainsKey(canvas.Selection.First()) => () => AddChild(canvas.Selection.First()),
            (false, false, Key.V) when board != Guid.Empty => () => SetTool(Tool.Select),
            (false, false, Key.P) when board != Guid.Empty => () => SetTool(Tool.Pen),
            (false, false, Key.H) when board != Guid.Empty => () => SetTool(Tool.Marker),
            (false, false, Key.E) when board != Guid.Empty => () => SetTool(Tool.Eraser),
            (false, false, Key.T) when board != Guid.Empty => () => SetTool(Tool.Text),
            (false, false, Key.R) when board != Guid.Empty => () => SetTool(Tool.Rectangle),
            (false, false, Key.O) when board != Guid.Empty => () => SetTool(Tool.Ellipse),
            (false, false, Key.L) when board != Guid.Empty => () => SetTool(Tool.Line),
            (false, false, Key.F) => shift ? canvas.ShowFullWorkspace : canvas.Fit,
            _ => null
        };
        if (action is null) return;
        e.Handled = true;
        action();
    }
}
