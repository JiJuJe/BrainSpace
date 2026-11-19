using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BrainSpace;

internal sealed partial class Shell
{
    private sealed record HitResult(Guid Id, Guid Board, string Title, string Detail, string Kind, string Terms = "", string Target = "");

    private Content? indexedContent;
    private Task<HitResult[]>? searchIndex;
    private CancellationTokenSource? searchQuery;

    private void ShowSearch() => ShowSearch("");

    private void ShowSearch(string initialSearch)
    {
        Content data = journal.Current;
        if (indexedContent != data || searchIndex is null)
        {
            indexedContent = data;
            searchIndex = data.Brains.Count + data.Items.Count <= 240
                ? Task.FromResult(BuildSearchIndex(data)) : Task.Run(() => BuildSearchIndex(data));
        }
        Picker("Search", "Find a Brain, note, or link…", searchIndex, hit =>
        {
            if (hit.Board == Guid.Empty) Navigate(hit.Id);
            else { Navigate(hit.Board); canvas.FocusObject(hit.Id); }
        }, initialSearch);
    }

    private static HitResult[] BuildSearchIndex(Content content)
    {
        var entries = new List<HitResult>(content.Brains.Count + content.Items.Count);
        var paths = new Dictionary<Guid, string> { [Guid.Empty] = "Home" };
        string PathFor(Guid id)
        {
            if (paths.TryGetValue(id, out string? path)) return path;
            Guid current = id;
            var labels = new Stack<string>(7);
            while (labels.Count < 6 && content.Brains.TryGetValue(current, out Brain? ancestor))
            {
                labels.Push(ancestor.Title);
                current = ancestor.Parent;
            }
            if (current != Guid.Empty) labels.Push("…");
            return paths[id] = string.Join(" / ", labels);
        }
        foreach (Brain brain in content.Brains.Values)
            entries.Add(new(brain.Id, Guid.Empty, brain.Title, PathFor(brain.Parent), "Brain"));
        foreach (Item item in content.Items.Values)
        {
            if (item.Kind is not (ItemKind.Text or ItemKind.Link)) continue;
            string kind = item.Kind == ItemKind.Link ? "Link" : "Note";
            entries.Add(new(item.Id, item.Board, ShortHeading(item.Text, 112, $"Untitled {kind.ToLowerInvariant()}"), PathFor(item.Board), kind, item.Text, item.Target));
        }
        return entries.ToArray();
    }

    internal static string ShortHeading(string text, int max, string fallback)
    {
        ReadOnlySpan<char> line = text.AsSpan().TrimStart();
        int newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0) line = line[..newline];
        line = line.TrimEnd();
        if (line.IsEmpty) return fallback;
        if (line.Length <= max) return line.ToString();
        int length = char.IsHighSurrogate(line[max - 1]) ? max - 1 : max;
        return string.Concat(line[..length], "…");
    }

    private static HitResult[] Match(HitResult[] entries, string term, CancellationToken cancellation)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (term.Length == 0) return entries.Take(48).ToArray();
        string[] tokens = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<(HitResult Entry, int Rank)>();
        foreach (HitResult hit in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            bool found = true;
            foreach (string token in tokens)
                if (!hit.Title.Contains(token, comparison) && !hit.Detail.Contains(token, comparison) &&
                    !hit.Terms.Contains(token, comparison) && !hit.Target.Contains(token, comparison))
                { found = false; break; }
            if (!found) continue;
            int rank = hit.Title.Equals(term, comparison) ? 0 : hit.Title.StartsWith(term, comparison) ? 1 :
                hit.Title.Contains(term, comparison) ? 2 : hit.Terms.Contains(term, comparison) || hit.Target.Contains(term, comparison) ? 3 : 4;
            matches.Add((hit, rank));
        }
        return matches.OrderBy(match => match.Rank).Take(48).Select(match => match.Entry).ToArray();
    }

    private void Picker(string title, string hint, Task<HitResult[]> entries, Action<HitResult> action, string initialSearch = "")
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var query = new TextBox { Height = 40, FontSize = 15, MaxLength = 180, VerticalContentAlignment = VerticalAlignment.Center, Padding = new(12, 9, 12, 9) };
        query.Text = initialSearch;
        AutomationProperties.SetName(query, hint);
        var inputRow = new Grid { Margin = new(0, 0, 0, 9) };
        inputRow.Children.Add(query);
        TextBlock placeholder = Subtle(hint);
        placeholder.Margin = new(13, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.IsHitTestVisible = false;
        inputRow.Children.Add(placeholder);
        panel.Children.Add(inputRow);

        double resultsHeight = Math.Clamp(ActualHeight - 245, 132, 332);
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new(0), MaxHeight = resultsHeight,
            ItemContainerStyle = (Style)Application.Current.FindResource("ResultItem"),
            ItemTemplate = (DataTemplate)Application.Current.FindResource("SearchResult"), SelectionMode = SelectionMode.Single };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        Grid.SetRow(list, 1);
        panel.Children.Add(list);
        TextBlock status = Subtle("Searching…");
        status.Margin = new(10, 10, 0, 5);
        status.Visibility = Visibility.Collapsed;
        Grid.SetRow(status, 1);
        panel.Children.Add(status);

        bool pending = false;
        bool? needsWorker = null;
        async void Fill()
        {
            searchQuery?.Cancel();
            using var request = new CancellationTokenSource();
            searchQuery = request;
            pending = true;
            string term = query.Text.Trim();
            placeholder.Visibility = query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Items.Count == 0) ShowStatus("Searching…");
            try
            {
                HitResult[] available = await entries.WaitAsync(request.Token);
                needsWorker ??= available.Length > 240 || available.Sum(hit => (long)hit.Terms.Length) > 250_000;
                HitResult[] matches;
                if (needsWorker.Value && term.Length > 0)
                {
                    await Task.Delay(45, request.Token);
                    matches = await Task.Run(() => Match(available, term, request.Token), request.Token);
                }
                else matches = Match(available, term, request.Token);
                request.Token.ThrowIfCancellationRequested();
                list.ItemsSource = matches;
                list.SelectedIndex = matches.Length > 0 ? 0 : -1;
                if (matches.Length > 0) list.ScrollIntoView(matches[0]);
                ShowStatus(matches.Length == 0 ? (term.Length == 0 ? "Nothing here yet" : "No matching results") : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception) { ShowStatus("Search could not load the workspace."); }
            finally
            {
                if (ReferenceEquals(searchQuery, request)) { searchQuery = null; pending = false; }
            }
        }

        void ShowStatus(string message)
        {
            status.Text = message;
            status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            list.Visibility = message.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Activate(HitResult? hit)
        {
            if (pending || hit is null) return;
            CloseModal();
            action(hit);
        }

        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem { DataContext: HitResult hit })
            { e.Handled = true; Activate(hit); }
        };
        query.TextChanged += (_, _) => Fill();
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Activate(list.SelectedItem as HitResult); e.Handled = true; }
            if (e.Key is not (Key.Up or Key.Down) || list.Items.Count == 0) return;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
            list.ScrollIntoView(list.SelectedItem); e.Handled = true;
        };
        ShowModal(title, panel, 518, query);
        Fill();
    }
}
