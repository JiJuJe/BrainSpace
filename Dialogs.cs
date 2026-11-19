using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BrainSpace;

internal sealed partial class Shell
{
    private ContextMenu CreateMenu(IEnumerable<(string Label, Action Run)> actions)
    {
        var menu = new ContextMenu { MinWidth = 170, MaxWidth = 330, FontFamily = FontFamily, FontSize = 13,
            Style = (Style)Application.Current.FindResource("CanvasMenu"), SnapsToDevicePixels = true, StaysOpen = false };
        foreach (var (label, run) in actions)
        {
            var item = new MenuItem { Header = label, Style = (Style)Application.Current.FindResource("CanvasMenuItem") };
            item.Click += (_, _) => { menu.IsOpen = false; run(); };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void ShowMenuAt(FrameworkElement target, params (string Label, Action Run)[] actions)
    {
        var menu = CreateMenu(actions);
        menu.PlacementTarget = target;
        menu.Placement = target == inkWeight || boardTools.IsAncestorOf(target) ? PlacementMode.Top : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ShowModal(string title, FrameworkElement content, double width, Control? focus = null)
    {
        finishEditor?.Invoke(true);
        canvas.CancelGesture();
        canvas.StopMotion();
        modalHost.Children.Clear();
        var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(116, 4, 8, 13)) };
        scrim.MouseDown += (_, _) => CloseModal();
        modalHost.Children.Add(scrim);
        var panel = new StackPanel();
        if (title.Length > 0)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new(0, 0, 0, 15) };
            var close = IconButton("close", "Close dialog (Esc)", CloseModal, 28, 28);
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
            row.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
        panel.Children.Add(content);
        var dialog = Frame(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "SurfaceBrush", new Thickness(1), 13, new Thickness(18));
        dialog.Width = Math.Min(width, Math.Max(280, ActualWidth - 46));
        dialog.MaxHeight = Math.Max(260, ActualHeight - 115);
        dialog.HorizontalAlignment = HorizontalAlignment.Center;
        dialog.VerticalAlignment = VerticalAlignment.Top;
        dialog.Margin = new(0, Math.Clamp(ActualHeight * .11, 20, 86), 0, 14);
        KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
        modalHost.Children.Add(dialog);
        modalHost.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            var slide = new TranslateTransform();
            dialog.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(145))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!modalHost.Children.Contains(dialog)) return;
            if (focus is not null) { focus.Focus(); if (focus is TextBox text) text.SelectAll(); }
            else dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Input);
    }

    private void CloseModal()
    {
        searchQuery?.Cancel();
        modalHost.Children.Clear();
        modalHost.Visibility = Visibility.Collapsed;
        canvas.Focus();
    }

    private static TextBlock Subtle(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return label;
    }

    private static TextBox Field(Panel panel, string label, string value, int max)
    {
        var title = Subtle(label);
        title.Margin = new(1, 0, 0, 6);
        panel.Children.Add(title);
        var input = new TextBox { Text = value, MaxLength = max, FontSize = 14, MinHeight = 37, Margin = new(0, 0, 0, 12) };
        AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        return input;
    }

    private static CheckBox Check(string label, bool value)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new(0, 5, 0, 7), VerticalContentAlignment = VerticalAlignment.Center };
        check.SetResourceReference(ForegroundProperty, "TextBrush");
        return check;
    }

    private Button Primary(string title, Action run)
    {
        var button = TextButton(title, run, double.NaN, 33);
        button.Padding = new(12, 0, 12, 0);
        button.SetResourceReference(BackgroundProperty, "ActiveBrush");
        button.SetResourceReference(BorderBrushProperty, "BorderBrush");
        return button;
    }

    private StackPanel DialogActions(string label, Action accept)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 13, 0, 0) };
        var cancel = TextButton("Cancel", CloseModal, 68, 33);
        cancel.Margin = new(0, 0, 7, 0);
        buttons.Children.Add(cancel); buttons.Children.Add(Primary(label, accept));
        return buttons;
    }

    private void Prompt(string title, string label, string value, string command, int max, Action<string> action)
    {
        var panel = new StackPanel();
        var input = Field(panel, label, value, max);
        Action submit = () =>
        {
            string text = input.Text.Trim();
            if (text.Length == 0) { input.Focus(); return; }
            CloseModal();
            action(text);
        };
        panel.Children.Add(DialogActions(command, submit));
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { submit(); e.Handled = true; } };
        ShowModal(title, panel, 405, input);
    }

    private void Confirm(string title, string message, string command, Action action)
    {
        var panel = new StackPanel();
        panel.Children.Add(Subtle(message));
        panel.Children.Add(DialogActions(command, () => { CloseModal(); action(); }));
        ShowModal(title, panel, 435);
    }

    private string PathOf(Guid id) => string.Join(" / ", journal.Current.Trail(id).Select(brain => brain.Title));
    private void ShowWorkspaceMenu(FrameworkElement anchor) => ShowMenuAt(anchor,
        ("Export backup…", ExportBackup), ("Import as copy…", ImportBackup), ("Restore backup…", RestoreBackup),
        ("Open data folder", OpenDataFolder), ("Keyboard shortcuts", ShowShortcutList));

    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(storage.Folder) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "BrainSpace", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ShowShortcutList()
    {
        var panel = new StackPanel();
        foreach (var (keys, description) in new (string, string)[]
        {
            ("Ctrl+K", "Search"), ("Ctrl+N / Tab", "New Brain / child of the selected Brain"), ("Enter / F2", "Rename or edit selection"),
            ("Alt+Left / Right", "Back / forward"), ("Ctrl+Z / Y", "Undo / redo"), ("Ctrl+C / V / D", "Copy / paste / duplicate"),
            ("Shift+drag", "Select objects in an area"), ("Space+drag / middle drag", "Pan"), ("F / Shift+F", "Fit content / show full workspace"),
            ("Ctrl+0", "Reset zoom to 100%"),
            ("V / P / H / E / T", "Select / pen / highlighter / eraser / text"), ("R / O / L", "Rectangle / ellipse / line")
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = keys, Width = 158, FontWeight = FontWeights.SemiBold });
            row.Children.Add(Subtle(description));
            panel.Children.Add(row);
        }
        ShowModal("Keyboard shortcuts", panel, 494);
    }

    private async void ExportBackup()
    {
        var choose = new SaveFileDialog { FileName = "BrainSpace", DefaultExt = ".brainspace", Filter = "BrainSpace archive (*.brainspace)|*.brainspace" };
        if (choose.ShowDialog(this) != true) return;
        CloseModal();
        Content data = journal.Current;
        Session session = SessionSnapshot();
        try { await Task.Run(() => storage.Export(choose.FileName, data, session)); Notify("Backup exported."); }
        catch (Exception error) { Notify($"Export failed. {error.Message}"); }
    }

    private void ImportBackup() => ReadBackup(restore: false);
    private void RestoreBackup() => ReadBackup(restore: true);

    private void ReadBackup(bool restore)
    {
        var choose = new OpenFileDialog { Filter = "BrainSpace archive (*.brainspace)|*.brainspace", Title = restore ? "Restore backup" : "Import backup" };
        if (choose.ShowDialog(this) != true) return;
        if (restore)
        {
            Confirm("Restore this backup?", "The imported backup will replace the current workspace. BrainSpace saves a recovery copy of the current data first.", "Restore", () => ApplyBackup(choose.FileName, true));
            return;
        }
        CloseModal();
        ApplyBackup(choose.FileName, false);
    }

    private async void ApplyBackup(string path, bool restore)
    {
        try
        {
            (Content source, Session? session) = await Task.Run(() => storage.Import(path));
            if (closing) return;
            if (restore)
            {
                string backups = Path.Combine(storage.Folder, "backups");
                Directory.CreateDirectory(backups);
                string recovery = Path.Combine(backups, $"Before restore {DateTime.Now:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid():N}.brainspace");
                storage.Export(recovery, journal.Current, SessionSnapshot());
                Guid target = session?.Board ?? Guid.Empty;
                var restoredViews = session?.Views ?? [];
                var restoredSettings = session?.Settings ?? settings;
                if (!source.Brains.ContainsKey(target)) target = Guid.Empty;
                try { save.GetAwaiter().GetResult(); } catch { }
                storage.Save(source, new Session(target, restoredSettings, restoredViews));
                save = Task.CompletedTask;
                views.Clear();
                foreach (var view in restoredViews) views[view.Key] = view.Value;
                journal.Reset(source);
                navigation.Clear(); navigation.Add(Guid.Empty); navigationIndex = 0;
                SetSettings(restoredSettings);
                Navigate(target);
                Notify("Backup restored.");
                return;
            }
            journal.Edit(data => Merge(data, source));
            Navigate(Guid.Empty);
            canvas.Fit();
            Notify("Imported as a separate copy.");
        }
        catch (Exception error) { if (!closing) Notify($"Couldn’t read the backup. {error.Message}"); }
    }

    internal static void Merge(Content data, Content imported)
    {
        var ids = imported.Brains.Keys.ToDictionary(old => old, _ => Guid.NewGuid());
        double shift = data.Brains.Count == 0 || imported.Brains.Count == 0 ? 0 :
            data.Brains.Values.Max(brain => brain.X) - imported.Brains.Values.Min(brain => brain.X) + 440;
        foreach (Brain brain in imported.Brains.Values)
        {
            Guid id = ids[brain.Id];
            data.Brains.Add(id, brain with { Id = id, Parent = ids.GetValueOrDefault(brain.Parent), X = brain.X + shift });
        }
        foreach (Item item in imported.Items.Values)
        {
            Guid id = Guid.NewGuid();
            data.Items.Add(id, item with { Id = id, Board = ids[item.Board] });
        }
        foreach (Edge edge in imported.Edges.Values)
        {
            Guid id = Guid.NewGuid();
            data.Edges.Add(id, new(id, ids[edge.From], ids[edge.To]));
        }
    }
}
