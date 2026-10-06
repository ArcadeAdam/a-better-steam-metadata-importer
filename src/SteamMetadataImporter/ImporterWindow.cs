using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace SteamMetadataImporter;

public sealed class ImportJob : INotifyPropertyChanged
{
    public IGame Game { get; }
    public string Title => Game.Title;
    private bool selected = true;
    private string input;
    private string status = "Not previewed";
    private string match = "";
    public bool Selected { get => selected; set { selected = value; Changed(); } }
    public string AppInput { get => input; set { if (input == value) return; input = value; Details = null; Match = ""; Status = "Preview needed"; Changed(); } }
    public string Status { get => status; set { status = value; Changed(); } }
    public string Match { get => match; set { match = value; Changed(); } }
    public SteamDetails? Details { get; set; }
    public IReadOnlyList<FieldChange> Changes { get; set; } = Array.Empty<FieldChange>();
    public ImportJob(IGame game, string root)
    {
        Game = game;
        input = SteamIdentity.Resolve(game.ApplicationPath, game.CommandLine, root)?.ToString() ?? "";
        if (input.Length == 0) status = "Enter Steam app ID or URL";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ImporterWindow : Window
{
    private readonly ObservableCollection<ImportJob> jobs;
    private readonly string root;
    private readonly DataGrid grid = new();
    private readonly TextBox preview = new();
    private readonly TextBox log = new();
    private readonly ProgressBar queueProgress = new() { Minimum = 0, Maximum = 1, Height = 16 };
    private readonly TextBlock queueStatus = new() { Text = "Ready", Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap };
    private readonly Button previewButton = new() { Content = "Preview Steam data" };
    private readonly Button importButton = new() { Content = "Import missing data", IsEnabled = false };
    private readonly Button cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button closeButton = new() { Content = "Close" };
    private readonly CheckBox metadata = new() { Content = "Metadata", IsChecked = true };
    private readonly CheckBox artwork = new() { Content = "Artwork", IsChecked = true };
    private readonly CheckBox videos = new() { Content = "Trailers / Video", IsChecked = true };
    private readonly CheckBox theme = new() { Content = "Use primary trailer for missing Theme Video", IsChecked = true };
    private readonly Dictionary<string, TextBox> limitInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button resetLimits = new() { Content = "Reset all to 2", Padding = new Thickness(10, 4, 10, 4) };
    private readonly string limitsFile;
    private CancellationTokenSource? cancellation;
    private bool busy;

    public ImporterWindow(IGame[] games)
    {
        root = ImportEngine.FindLaunchBoxRoot();
        limitsFile = Path.Combine(root, "Plugins", "SteamMetadataImporter", "Data", "media-limits.json");
        var savedLimits = MediaLimits.CreateDefaults();
        string? settingsWarning = null;
        try { savedLimits = MediaLimits.Load(limitsFile); }
        catch (Exception error) { settingsWarning = "Could not read saved media limits; using 2 per type. " + error.Message; }
        jobs = new ObservableCollection<ImportJob>(games.GroupBy(g => g.Id).Select(g => new ImportJob(g.First(), root)));
        Title = "A Better Steam Metadata Importer";
        Width = 1080; Height = 860; MinWidth = 850; MinHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        var layout = new Grid { Margin = new Thickness(18) };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(100), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = h });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "A Better Steam Metadata Importer", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Fill missing metadata and add Steam media to selected games. Existing values, launch commands and database links are preserved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        layout.Children.Add(heading);
        grid.AutoGenerateColumns = false; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
        grid.SelectionMode = DataGridSelectionMode.Single; grid.ItemsSource = jobs;
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Import", Binding = new Binding(nameof(ImportJob.Selected)), Width = 58 });
        grid.Columns.Add(new DataGridTextColumn { Header = "LaunchBox game", Binding = new Binding(nameof(ImportJob.Title)), IsReadOnly = true, Width = 200 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Steam app ID or URL", Binding = new Binding(nameof(ImportJob.AppInput)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Steam match", Binding = new Binding(nameof(ImportJob.Match)), IsReadOnly = true, Width = 205 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(ImportJob.Status)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.SelectionChanged += (_, _) => UpdatePreview();
        Grid.SetRow(grid, 1); layout.Children.Add(grid);
        var settings = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        foreach (var check in Checks())
        {
            check.Style = new Style(typeof(CheckBox));
            check.Foreground = Brushes.Black;
            check.Margin = new Thickness(0, 3, 18, 3);
            check.Checked += (_, _) => UpdatePreview(); check.Unchecked += (_, _) => UpdatePreview();
            settings.Children.Add(check);
        }
        Grid.SetRow(settings, 2); layout.Children.Add(settings);
        var limitsPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var limitsGrid = new UniformGrid { Columns = 4 };
        foreach (string mediaType in MediaLimits.Types)
        {
            var entry = new Grid { Margin = new Thickness(0, 0, 14, 8) };
            entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            var label = new TextBlock { Text = mediaType, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0) };
            label.Style = new Style(typeof(TextBlock));
            var input = new TextBox { Text = savedLimits[mediaType]?.ToString() ?? "", Padding = new Thickness(5, 3, 5, 3), Foreground = Brushes.Black, Background = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center };
            input.Style = new Style(typeof(TextBox));
            input.ToolTip = "Maximum files per game, including existing media. 0 skips this type; blank means unlimited.";
            AutomationProperties.SetName(input, mediaType + " limit per game");
            limitInputs.Add(mediaType, input);
            input.TextChanged += (_, _) =>
            {
                input.BorderBrush = MediaLimits.TryParse(input.Text, out _) ? Brushes.Gray : Brushes.Firebrick;
                UpdatePreview();
            };
            entry.Children.Add(label); Grid.SetColumn(input, 1); entry.Children.Add(input);
            limitsGrid.Children.Add(entry);
        }
        limitsPanel.Children.Add(limitsGrid);
        var limitsFooter = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        resetLimits.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(resetLimits, Dock.Left); limitsFooter.Children.Add(resetLimits);
        limitsFooter.Children.Add(new TextBlock { Text = "Existing files count. 0 = skip; blank = unlimited. Extras are kept. Video and Theme Video share one limit. Limits are saved on preview or import.", Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        limitsPanel.Children.Add(limitsFooter);
        var limitsExpander = new Expander { Header = "Media limits per game", IsExpanded = true, Content = limitsPanel, Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8) };
        limitsExpander.Style = new Style(typeof(Expander));
        Grid.SetRow(limitsExpander, 3); layout.Children.Add(limitsExpander);
        resetLimits.Click += (_, _) => { foreach (var input in limitInputs.Values) input.Text = MediaLimits.DefaultLimit.ToString(); };
        // LaunchBox's implicit dark styles otherwise render white text on this light surface.
        preview.Style = new Style(typeof(TextBox)); preview.Foreground = Brushes.Black;
        preview.IsReadOnly = true; preview.TextWrapping = TextWrapping.Wrap; preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        preview.Padding = new Thickness(10); preview.Background = Brushes.White;
        Grid.SetRow(preview, 4); layout.Children.Add(preview);
        log.Style = new Style(typeof(TextBox)); log.Foreground = Brushes.Black;
        log.IsReadOnly = true; log.TextWrapping = TextWrapping.Wrap; log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        log.Margin = new Thickness(0, 10, 0, 8); log.Padding = new Thickness(8); log.Background = Brushes.White;
        Grid.SetRow(log, 5); layout.Children.Add(log);
        var progressArea = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        queueProgress.Style = new Style(typeof(ProgressBar));
        queueProgress.Foreground = Brushes.SeaGreen;
        queueProgress.Background = Brushes.Gainsboro;
        AutomationProperties.SetName(queueProgress, "Queue progress");
        queueStatus.Style = new Style(typeof(TextBlock));
        queueStatus.Margin = new Thickness(0, 4, 0, 0);
        progressArea.Children.Add(queueProgress); progressArea.Children.Add(queueStatus);
        Grid.SetRow(progressArea, 6); layout.Children.Add(progressArea);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { previewButton, importButton, cancelButton, closeButton })
        { button.Padding = new Thickness(16, 7, 16, 7); button.Margin = new Thickness(8, 4, 0, 0); buttons.Children.Add(button); }
        Grid.SetRow(buttons, 7); layout.Children.Add(buttons);
        Content = layout;
        previewButton.Click += async (_, _) => await PreviewAsync();
        importButton.Click += async (_, _) => await ImportAsync();
        cancelButton.Click += (_, _) => { cancellation?.Cancel(); Append("Cancellation requested. Completed work will be kept."); };
        closeButton.Click += (_, _) => Close();
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); Append("Cancelling the active operation before closing. Close again when it finishes."); } };
        foreach (var job in jobs) job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportJob.AppInput) || e.PropertyName == nameof(ImportJob.Selected)) { UpdateButtons(); UpdatePreview(); } };
        grid.SelectedIndex = 0;
        UpdatePreview();
        if (settingsWarning != null) Append(settingsWarning);
    }

    private IEnumerable<CheckBox> Checks() => new[] { metadata, artwork, videos, theme };
    private ImportOptions Options(Dictionary<string, int?> limits) => new() { Metadata = metadata.IsChecked == true, Artwork = artwork.IsChecked == true, Videos = videos.IsChecked == true, ThemeVideo = theme.IsChecked == true, MediaTypeLimits = limits };

    private bool TrySaveLimits(out Dictionary<string, int?> limits)
    {
        limits = MediaLimits.CreateDefaults();
        foreach (var pair in limitInputs)
        {
            if (!MediaLimits.TryParse(pair.Value.Text, out int? limit))
            {
                Append(pair.Key + ": enter a whole number from 0 to 9999, or leave blank for unlimited.");
                pair.Value.Focus(); pair.Value.SelectAll(); return false;
            }
            limits[pair.Key] = limit;
        }
        try { MediaLimits.Save(limitsFile, limits); return true; }
        catch (Exception error) { Append("Could not save media limits: " + error.Message); return false; }
    }

    private string LimitDescription(string mediaType)
    {
        if (!limitInputs.TryGetValue(mediaType, out var input)) return "limit 2 per game";
        if (!MediaLimits.TryParse(input.Text, out int? limit)) return "invalid limit — enter 0–9999 or leave blank";
        return limit switch { null => "unlimited", 0 => "skipped", _ => $"limit {limit} per game, including existing files" };
    }

    private async Task PreviewAsync()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = jobs.Where(j => j.Selected).ToArray();
        if (selected.Length == 0) return;
        if (!TrySaveLimits(out _)) return;
        int processed = 0, failed = 0;
        Start(selected.Length, "Preparing preview…");
        try
        {
            using var client = new SteamClient();
            foreach (var job in selected)
            {
                cancellation!.Token.ThrowIfCancellationRequested();
                SetQueueProgress(processed, $"Previewing {processed + 1} of {selected.Length} — {job.Title}");
                job.Details = null;
                int? appId = SteamIdentity.Parse(job.AppInput);
                if (!appId.HasValue)
                {
                    job.Status = "Enter a valid Steam app ID or URL"; failed++;
                    SetQueueProgress(++processed, $"Previewed {processed} of {selected.Length}");
                    UpdatePreview(); continue;
                }
                job.Status = "Reading Steam…";
                Append($"Looking up {job.Title} (Steam {appId}).");
                try
                {
                    // Steam HTTP and filesystem discovery must never block LaunchBox's UI.
                    var result = await Task.Run(() => client.GetDetailsAsync(appId.Value, cancellation.Token), cancellation.Token);
                    job.Details = result; job.Match = result.Name;
                    job.Changes = MetadataPlan.Build(job.Game, result);
                    job.Status = $"Ready: {job.Changes.Count} fields, {result.Assets.Count} media";
                    foreach (string warning in result.Warnings) Append(job.Title + ": " + warning);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { failed++; job.Status = "Lookup failed"; Append(job.Title + ": " + e.Message); }
                SetQueueProgress(++processed, $"Previewed {processed} of {selected.Length}");
                UpdatePreview();
            }
            SetQueueProgress(processed, $"Preview complete — {processed} of {selected.Length} processed" + (failed > 0 ? $"; {failed} need attention." : ". Ready to import."));
            Append("Preview complete. Check the Steam matches, then choose Import missing data.");
        }
        catch (OperationCanceledException) { SetQueueProgress(processed, $"Preview cancelled — {processed} of {selected.Length} processed."); Append("Preview cancelled."); }
        catch (Exception e) { SetQueueProgress(processed, $"Preview failed — {processed} of {selected.Length} processed. See log."); Append("Preview failed: " + e.Message); }
        finally { Finish(); }
    }

    private async Task ImportAsync()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = jobs.Where(j => j.Selected).ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(j => j.Details == null || SteamIdentity.Parse(j.AppInput) != j.Details.AppId))
        { Append("Preview every checked game first, or uncheck games whose lookup failed."); return; }
        if (!TrySaveLimits(out var limits)) return;
        var options = Options(limits);
        if (!options.Metadata && !options.Artwork && !options.Videos && !options.ThemeVideo) { Append("Select at least one item to import."); return; }
        int processed = 0, failed = 0;
        Start(selected.Length, "Preparing import…");
        var engine = new ImportEngine(root, Dispatcher);
        var progress = new Progress<string>(Append);
        try
        {
            foreach (var job in selected)
            {
                cancellation!.Token.ThrowIfCancellationRequested();
                SetQueueProgress(processed, $"Importing {processed + 1} of {selected.Length} — {job.Title}");
                job.Status = "Importing…";
                try
                {
                    string summary = await engine.ImportAsync(job.Game, job.Details!, options, progress, cancellation.Token);
                    job.Status = "Complete";
                    Append(job.Title + ": " + summary);
                    job.Changes = MetadataPlan.Build(job.Game, job.Details!);
                }
                catch (OperationCanceledException) { job.Status = "Cancelled"; throw; }
                catch (Exception error) { failed++; job.Status = "Error; see log"; Append(job.Title + ": " + error.Message); }
                SetQueueProgress(++processed, $"Processed {processed} of {selected.Length} games.");
            }
            string completion = $"Done! {processed} of {selected.Length} games processed." + (failed > 0 ? $" {failed} failed — see log." : " See the log for import results.");
            SetQueueProgress(processed, completion);
            Append(completion);
            Append("Press F5 in LaunchBox if artwork needs refreshing.");
        }
        catch (OperationCanceledException) { SetQueueProgress(processed, $"Cancelled — {processed} of {selected.Length} games processed."); Append("Import cancelled. Completed media files are kept; remaining games were not processed."); }
        catch (Exception error) { SetQueueProgress(processed, $"Import stopped — {processed} of {selected.Length} games processed. See log."); Append("Import failed: " + error.Message); }
        finally { Finish(); UpdatePreview(); }
    }

    private void UpdatePreview()
    {
        if (grid.SelectedItem is not ImportJob job) return;
        if (job.Details == null) { preview.Text = "Select Preview Steam data to see the exact Steam match and proposed changes.\n\nSteam links, numeric app IDs, steam:// launch commands, and local .url shortcuts are supported. Enter a Steam app ID or URL when one cannot be detected.\n\nMedia limits apply to each game and count existing files. Use the controls above to change the default of 2 per type, or leave a limit blank for unlimited. A primary trailer can fill both Video and Theme Video; this is the same trailer, not a separately designed theme."; return; }
        var text = new StringBuilder();
        var details = job.Details;
        text.AppendLine($"LaunchBox: {job.Title}\nSteam: {details.Name} — App {details.AppId}\n{details.StoreUrl}\n");
        if (metadata.IsChecked == true)
        {
            text.AppendLine($"METADATA — {job.Changes.Count} missing fields to fill");
            foreach (var field in job.Changes) text.AppendLine("• " + MetadataPlan.Describe(field));
            if (job.Changes.Count == 0) text.AppendLine("Existing metadata is already populated and will be kept.");
        }
        if (artwork.IsChecked == true)
        {
            text.AppendLine("\nARTWORK — available sources, with existing-file and duplicate checks during import");
            foreach (var group in details.Assets.Where(a => !a.IsVideo).GroupBy(a => a.ImageType)) text.AppendLine($"• {group.Key}: {group.Count()} sources; {LimitDescription(group.Key!)}");
        }
        if (videos.IsChecked == true || theme.IsChecked == true)
        {
            var trailers = details.Assets.Where(a => a.IsVideo).OrderByDescending(a => a.Highlight).ToArray();
            text.AppendLine($"\nVIDEO — {trailers.Length} trailer(s) found");
            foreach (var trailer in trailers) text.AppendLine("• " + trailer.Label);
            text.AppendLine("Video: " + LimitDescription("Video") + ". Theme Video shares this limit; a shared file counts once.");
            if (LimitDescription("Video") == "skipped") { text.AppendLine("Video downloads and Video/Theme Video assignments are disabled by the zero limit."); }
            else
            {
                if (videos.IsChecked == true) text.AppendLine(ImportEngine.HasExistingVideo(job.Game, false, root) ? "Video: existing video preserved." : "Video: fill with the first available primary trailer.");
                if (theme.IsChecked == true) text.AppendLine(ImportEngine.HasExistingVideo(job.Game, true, root) ? "Theme Video: existing video preserved." : "Theme Video: use the same primary trailer file.");
            }
        }
        foreach (var warning in details.Warnings) text.AppendLine("\nNote: " + warning);
        preview.Text = text.ToString();
    }

    private void Start(int total, string status)
    {
        busy = true; cancellation = new CancellationTokenSource();
        queueProgress.Maximum = Math.Max(1, total);
        SetQueueProgress(0, status); UpdateButtons();
    }
    private void SetQueueProgress(int processed, string status)
    {
        queueProgress.Value = Math.Clamp(processed, 0, queueProgress.Maximum);
        queueStatus.Text = status;
        AutomationProperties.SetHelpText(queueProgress, status);
    }
    private void Finish() { busy = false; cancellation?.Dispose(); cancellation = null; UpdateButtons(); }
    private void UpdateButtons()
    {
        grid.IsReadOnly = busy;
        previewButton.IsEnabled = !busy && jobs.Any(j => j.Selected);
        importButton.IsEnabled = !busy && jobs.Any(j => j.Selected) && jobs.Where(j => j.Selected).All(j => j.Details != null && SteamIdentity.Parse(j.AppInput) == j.Details.AppId);
        cancelButton.IsEnabled = busy; closeButton.IsEnabled = !busy;
        foreach (var check in Checks()) check.IsEnabled = !busy;
        foreach (var input in limitInputs.Values) input.IsEnabled = !busy;
        resetLimits.IsEnabled = !busy;
    }
    private void Append(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Append(message)); return; }
        if (log.Text.Length > 50000) log.Text = log.Text.Substring(log.Text.Length - 30000);
        log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        log.ScrollToEnd();
    }
}

public sealed class SteamImporterPlugin : IGameMenuItemPlugin
{
    private static ImporterWindow? active;
    public bool SupportsMultipleGames => true;
    public string Caption => "A Better Steam Metadata Importer…";
    public System.Drawing.Image IconImage => null!;
    public bool ShowInLaunchBox => true;
    public bool ShowInBigBox => false;
    public bool GetIsValidForGame(IGame game) => game != null;
    public bool GetIsValidForGames(IGame[] games) => games != null && games.Length > 0;
    public void OnSelected(IGame game) => OnSelected(new[] { game });
    public void OnSelected(IGame[] games)
    {
        void Open()
        {
            try
            {
                if (active != null) { active.Activate(); return; }
                active = new ImporterWindow(games);
                if (Application.Current?.MainWindow?.IsVisible == true) active.Owner = Application.Current.MainWindow;
                active.Closed += (_, _) => active = null;
                active.ShowDialog();
            }
            catch (Exception error) { active = null; MessageBox.Show(error.Message, "A Better Steam Metadata Importer", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        // Return the menu callback before entering the modal message loop so LaunchBox
        // can release its wait cursor. Keep ownership and the single-window guard.
        var ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        ui.BeginInvoke(DispatcherPriority.Background, new Action(Open));
    }
}
