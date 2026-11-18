// The last step and the four tools: checking and saving the print, batches
// from a list, the register of issued IDs, the printer setting and the card
// design.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdMaker.Core;
using Microsoft.Win32;

namespace IdMaker.App;

public partial class MainWindow
{
    /* ---- step 6: check and print ---------------------------------- */

    private void RenderCheckStep(CheckView v)
    {
        var s = _session;
        if (s.StepIndex != Session.LastStep) return;

        Show(ReviewStage, !v.Saved);
        Show(SavedStage, v.Saved);
        SetText(StepCheckHeading, v.Heading);
        Show(ReviewSub, v.SubShown);
        SetText(ReviewSub, v.Sub);

        // a plain summary of the card on screen, each line one press from being edited
        Show(Summary, v.SummaryShown);
        if (v.SummaryShown && _art is not null && _renderer is not null)
        {
            SummaryImage.Source = _renderer.RenderFront(_art, s.Geometry, s.CardData(), 132 * 2 / CardConfig.CardWidthMm);
            SetText(SumName, v.SummaryName);
            SetText(SumTitle, v.SummaryTitle);
            SetText(SumNumber, v.SummaryNumber);
        }
        Show(ReviewWarning, v.WarningShown);
        SetText(ReviewWarning, v.Warning);

        LayPairs.IsChecked = s.Layout == SheetMode.Pairs;
        LayDuplex.IsChecked = s.Layout == SheetMode.Duplex;
        // SAFETY: CmbPaper's items are made in the constructor, each with its paper's key as a string Tag.
        foreach (var item in CmbPaper.Items.OfType<ComboBoxItem>()) if ((string)item.Tag == s.Paper && !item.IsSelected) item.IsSelected = true;

        if (v.HasPlan)
        {
            if (!TxtCopies.IsKeyboardFocusWithin) SyncText(TxtCopies, v.CopiesText);
            CopiesDown.IsEnabled = v.CopiesDownEnabled;
            CopiesUp.IsEnabled = v.CopiesUpEnabled;
            SetText(CopiesLabel, v.CopiesLabel);
            SetText(PrintNote, v.PrintNote);
        }
        SetText(ReviewFile, v.FileLine);
        BtnDownload.IsEnabled = v.SaveEnabled;
        BtnDownload.Content = v.SaveTitle;
        BtnPng.IsEnabled = v.PicturesEnabled;
        BtnAddAnother.IsEnabled = v.AddAnotherEnabled;
    }

    private bool _saving;
    private int _savedCount;

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _session.SetLayout(LayDuplex.IsChecked == true ? SheetMode.Duplex : SheetMode.Pairs);
        Render();
    }

    private void CmbPaper_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CmbPaper.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        _session.SetPaper(key);
        Render();
    }

    private void TxtCopies_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _session.SetCopies(int.TryParse(TxtCopies.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null);
        Render();
    }

    private void TxtCopies_LostFocus(object sender, RoutedEventArgs e) => Render();     // show what was understood
    private void CopiesDown_Click(object sender, RoutedEventArgs e) { _session.NudgeCopies(-1); Render(); }
    private void CopiesUp_Click(object sender, RoutedEventArgs e) { _session.NudgeCopies(1); Render(); }

    private void BtnAddAnother_Click(object sender, RoutedEventArgs e)
    {
        var from = _session.StepIndex;
        _session.AddAnother();
        AfterMove(from);
    }

    private void BtnCancelAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_session.StartedCard && !Confirm("Discard the ID you have started? The cards already in this print are kept.", "Discard")) return;
        var from = _session.StepIndex;
        _session.CancelAdding();
        AfterMove(from);
    }

    private void RenderJob(JobView v)
    {
        Show(QueueStrip, v.Shown);
        if (QueueStrip.Visibility != Visibility.Visible) return;

        if (v.Key != _jobShown)
        {
            _jobShown = v.Key;
            JobList.Children.Clear();
            for (var i = 0; i < v.Rows.Count; i++)
            {
                var c = v.Rows[i];
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                if (c.Current)
                {
                    var tag = new TextBlock { Text = "on screen", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                    Theme.Paint(tag, TextBlock.ForegroundProperty, "Color.StageMuted");
                    DockPanel.SetDock(tag, Dock.Right);
                    row.Children.Add(tag);
                }
                else
                {
                    var index = i;
                    var remove = new Button { Style = Theme.Style("Button.Ghost"), Content = "Remove", MinHeight = 32, Padding = new Thickness(10, 4, 10, 4), FontSize = 13 };
                    System.Windows.Automation.AutomationProperties.SetName(remove, c.RemoveSays);
                    remove.Click += (_, _) => { var from = _session.StepIndex; _session.RemoveFromQueue(index); AfterMove(from); };
                    DockPanel.SetDock(remove, Dock.Right);
                    row.Children.Add(remove);
                }
                var who = new TextBlock
                {
                    Text = c.Text, FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = c.ToolTip,
                };
                Theme.Paint(who, TextBlock.ForegroundProperty, "Color.StageInk");
                row.Children.Add(who);
                JobList.Children.Add(row);
            }
        }
        SetText(JobCount, v.Count);
        SetText(JobNote, v.Note);
        Show(JobNote, v.NoteShown);
        Show(BtnCancelAdd, v.CancelAddShown);
    }

    private string _jobShown = "";

    private static string DocumentsFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        var (fileName, _) = _session.PrintFile();
        var dialog = new SaveFileDialog { Title = "Save the print file", FileName = fileName, Filter = "PDF|*.pdf", DefaultExt = ".pdf", InitialDirectory = DocumentsFolder };
        if (dialog.ShowDialog(this) == true) _ = SavePrint(dialog.FileName);
    }

    /// <summary>
    /// Write the PDF, and only once it is safely on disk mark its cards as
    /// issued. A print that failed to save has issued nothing.
    /// </summary>
    internal async Task SavePrint(string path)
    {
        if (_saving || _art is null || _renderer is null) return;
        var print = _session.PrintPages();
        if (print is not { } pr || pr.Entries.Count == 0) return;
        var (_, title) = _session.PrintFile();
        var pages = _renderer.SheetPdfPages(_art, _session.Geometry, pr.Plan, pr.Entries, pr.Pages, _session.BackUpsideDown(pr.Plan));
        var count = _session.JobCards().Count;

        _saving = true;
        Show(ReviewError, false);
        Render();
        var error = await WritePdf(path, pages, title, null);
        _saving = false;
        if (error.Length > 0)
        {
            ReviewError.Text = error;
            Show(ReviewError, true);
            Render();
            return;
        }

        var issued = _session.MarkIssued();
        _saved = true;
        _savedCount = count;
        _savedPath = path;
        SavedName.Text = Path.GetFileName(path);
        Show(SavedNote, false);
        Render();
        BtnNewId.Focus();

        var report = await _session.RecordShared(issued);
        if (report.Message() is { Length: > 0 } trouble)
        {
            SavedNote.Text = trouble;
            Show(SavedNote, true);
            Render();
        }
    }

    /// <summary>
    /// Write a PDF to disk. Pages are drawn here, on the window's own thread,
    /// one at a time as they are asked for; the compressing and writing
    /// happen off it, so the window stays alive through a long batch.
    /// Returns "" or what went wrong, in words for the operator.
    /// </summary>
    private async Task<string> WritePdf(string path, List<Func<PdfPage>> pages, string title, Action<int, int>? progress)
    {
        var onWindow = pages.Select(p => (Func<PdfPage>)(() => Dispatcher.Invoke(p, DispatcherPriority.Background))).ToList();
        var tmp = path + ".part";
        try
        {
            await Task.Run(() =>
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    PdfWriter.Write(stream, onWindow, title, DateTime.Now,
                        progress is null ? null : (done, total) => Dispatcher.BeginInvoke(() => progress(done, total)));
                }
                File.Move(tmp, path, overwrite: true);
            });
            return "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or OutOfMemoryException)
        {
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { /* nothing more to be done about a file that will not go */ }
            return e is OutOfMemoryException
                ? "This print is too large for this computer’s memory. Print fewer cards at a time."
                : $"The file couldn’t be saved there ({e.Message.TrimEnd('.')}). If it is open in another program, close it, or choose a different folder.";
        }
    }

    private void BtnPng_Click(object sender, RoutedEventArgs e)
    {
        if (_art is null || _renderer is null || !_session.Ready()) return;
        var stem = Path.GetFileNameWithoutExtension(_session.PrintFile().FileName);
        var dialog = new SaveFileDialog { Title = "Save the front as a picture", FileName = stem + "-front.png", Filter = "PNG picture|*.png", DefaultExt = ".png", InitialDirectory = DocumentsFolder };
        if (dialog.ShowDialog(this) != true) return;
        var front = dialog.FileName;
        var back = Path.Combine(Path.GetDirectoryName(front) ?? DocumentsFolder,
            Path.GetFileNameWithoutExtension(front).Replace("-front", "", StringComparison.OrdinalIgnoreCase) + "-back.png");
        try
        {
            var ppm = CardConfig.PrintPxPerMm;
            ImageFiles.SavePng(_renderer.RenderFront(_art, _session.Geometry, _session.CardData(), ppm), front);
            ImageFiles.SavePng(_renderer.RenderBack(_art, ppm, _session.RotateBack), back);
            // pictures are a proof to look at, not a print: the card is issued when the PDF is saved
            SetText(ReviewFile, $"Saved {Path.GetFileName(front)} and {Path.GetFileName(back)}. The card is not issued until you save the PDF.");
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ReviewError.Text = $"The pictures couldn’t be saved there ({x.Message.TrimEnd('.')}). Choose a different folder.";
            Show(ReviewError, true);
        }
    }

    private void BtnNewId_Click(object sender, RoutedEventArgs e)
    {
        var from = _session.StepIndex;
        _saved = false;
        _session.NewId();
        AfterMove(from);
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e) => ShowInExplorer(_savedPath);

    // Saving a file silently would leave the operator with no idea where the print went.
    private static void ShowInExplorer(string path)
    {
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { /* the file is saved either way */ }
    }

    /* ---- the tool rail --------------------------------------------
       The register, batches, the printer setting and the card design are
       occasional, and they belong beside the work rather than under it.
       One opens at a time.
       ---------------------------------------------------------------- */

    private void Rail_Click(object sender, RoutedEventArgs e)
    {
        // SAFETY: the four rail buttons carry their tool's name as their Tag in the markup.
        var name = (string)((FrameworkElement)sender).Tag;
        _tool = _tool == name ? null : name;
        if (_tool == ToolKey.Register) RegisterUrlMsg.Text = "";
        Render();
    }

    private void DrawerClose_Click(object sender, RoutedEventArgs e) => CloseTool();

    private void CloseTool()
    {
        var was = _tool;
        _tool = null;
        Render();
        // focus goes back to the button that opened the drawer
        (was switch { ToolKey.Register => RailRegister, ToolKey.Batch => RailBatch, ToolKey.Printer => RailPrinter, ToolKey.Design => RailDesign, _ => null })?.Focus();
    }

    private void RenderTools(Screen m)
    {
        // the design can only be changed before an ID is started: Core takes
        // its button away and shuts its drawer once one is
        Show(RailDesign, m.Tools.DesignOffered);
        _tool = m.Tools.Open;

        // Editing the map and having the Card design drawer open are the
        // same state, and this is where that is enforced rather than in each
        // of the handlers that can close it. The Keep button lives in that
        // drawer, so any route that closed the drawer without going through
        // it — Close, Escape, another tool in the rail — would otherwise
        // leave the boxes on the card with no button anywhere to finish with.
        if (EditingMap && _tool != ToolKey.Design) StopAdjusting();

        Show(Drawer, _tool is not null);
        if (_tool is not null) SetText(DrawerTitle, m.Tools.DrawerTitle);
        Show(ToolRegister, _tool == ToolKey.Register);
        Show(ToolBatch, _tool == ToolKey.Batch);
        Show(ToolPrinter, _tool == ToolKey.Printer);
        Show(ToolDesign, _tool == ToolKey.Design);
        RailRegister.IsChecked = _tool == ToolKey.Register;
        RailBatch.IsChecked = _tool == ToolKey.Batch;
        RailPrinter.IsChecked = _tool == ToolKey.Printer;
        RailDesign.IsChecked = _tool == ToolKey.Design;

        // a badge on the rail, and nothing at all when there is nothing to count
        SetText(RailRegisterCount, m.Tools.Issued.ToString(CultureInfo.InvariantCulture));
        Show(RailRegisterBadge, m.Tools.Issued > 0);

        if (m.Register is { } register) RenderRegister(register);
        if (_tool == ToolKey.Printer)
        {
            foreach (var item in CmbFlip.Items.OfType<ComboBoxItem>())
                item.IsSelected = (string)item.Tag == (_session.Flip == DuplexFlip.ShortEdge ? "short" : "long");
            ChkRotate.IsChecked = _session.RotateBack;
        }
        if (_tool == ToolKey.Design) RenderDesign();
        // the form stands down while the boxes are being dragged
        FormPanel.IsEnabled = !EditingMap;
        FitColumns();
    }

    // What the four columns need side by side: the rail, an open tool, the
    // least the card can be shown in, and the form, with the gaps between.
    private const double RoomForAllFour = 176 + 12 + 320 + 12 + 300 + 12 + 440;

    /// <summary>
    /// In a window too narrow for all four columns, an open tool takes the
    /// form's place, and closing it brings the form back. The card is never
    /// the one to give way: every tool is used while looking at it.
    /// </summary>
    private void FitColumns()
    {
        var crowded = _tool is not null && Columns.ActualWidth > 0 && Columns.ActualWidth < RoomForAllFour;
        Show(FormPanel, !crowded);
    }

    /* ---- issued IDs ----------------------------------------------- */

    private string _registerShown = "\u0000";

    /// <summary>The issued IDs, who is signed in to the shared register on this computer, and what the register has refused.</summary>
    private void RenderRegister(RegisterView v)
    {
        BtnRegisterCsv.IsEnabled = v.ExportEnabled;
        SetText(RegisterAccount, v.Account);
        Show(SignInForm, v.SignInShown);
        Show(SignedInActions, v.SignedInShown);
        Show(RefusedPanel, v.RefusedShown);
        SetText(RefusedNote, v.RefusedNote);
        SetText(RegisterNote, v.Note);
        if (v.Key == _registerShown) return;
        _registerShown = v.Key;
        RegisterList.Children.Clear();
        foreach (var e in v.Rows)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var head = new DockPanel();
            var when = new TextBlock { Text = e.When, Style = Theme.Style("Text.Note") };
            DockPanel.SetDock(when, Dock.Right);
            head.Children.Add(when);
            head.Children.Add(new TextBlock { Text = e.Head, Style = Theme.Style("Text.Body"), FontWeight = FontWeights.SemiBold });
            row.Children.Add(head);
            row.Children.Add(new TextBlock { Text = e.Detail, Style = Theme.Style("Text.Note") });
            RegisterList.Children.Add(row);
        }
        if (v.More.Length > 0) RegisterList.Children.Add(new TextBlock { Text = v.More, Style = Theme.Style("Text.Note") });
    }

    private void BtnRegisterCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the list of issued IDs", FileName = $"apg-id-register-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV|*.csv", DefaultExt = ".csv", InitialDirectory = DocumentsFolder,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _register.ToCsv());
            RegisterUrlMsg.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
            ShowInExplorer(dialog.FileName);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            RegisterUrlMsg.Text = $"The list couldn’t be saved there ({x.Message.TrimEnd('.')}).";
        }
    }

    private void BtnRegisterImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a saved list of issued IDs", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var added = _register.ImportCsv(File.ReadAllText(dialog.FileName));
            RegisterUrlMsg.Text = added > 0 ? $"Added {added} card{(added == 1 ? "" : "s")} from that list." : "Nothing new in that list.";
            _session.RefreshDuplicate();
            Render();
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            RegisterUrlMsg.Text = $"That file couldn’t be read ({x.Message.TrimEnd('.')}).";
        }
    }

    private void BtnRefusedDone_Click(object sender, RoutedEventArgs e)
    {
        _register.ClearRefused();
        Render();
    }

    private void TxtSignInPassword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        BtnSignIn_Click(sender, e);
    }

    private async void BtnSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!BtnSignIn.IsEnabled) return;
        BtnSignIn.IsEnabled = false;
        RegisterUrlMsg.Text = "Signing in…";
        try
        {
            var result = await _register.SignIn(TxtSignInEmail.Text, TxtSignInPassword.Password);
            // the password is not kept anywhere, this box included
            TxtSignInPassword.Clear();
            if (!result.Ok)
            {
                RegisterUrlMsg.Text = result.Error;
                TxtSignInPassword.Focus();
                return;
            }
            var read = await _register.LoadIndex();
            // cards saved before anyone signed in are sent now
            var left = read ? await _register.SendWaiting() : _register.WaitingForShared;
            _session.RefreshDuplicate();
            RegisterUrlMsg.Text = !read ? $"Signed in, but the register could not be read just now. {_register.LastProblem}"
                : left > 0 ? $"Signed in. {left} card{(left == 1 ? "" : "s")} could not be written yet and will be tried again."
                : "Signed in. This computer now checks every number against the shared register.";
            Render();
        }
        finally { BtnSignIn.IsEnabled = true; }
    }

    private void BtnSignOut_Click(object sender, RoutedEventArgs e)
    {
        _register.SignOut();
        _session.RefreshDuplicate();
        RegisterUrlMsg.Text = "Signed out. Numbers are checked against this computer only until somebody signs in.";
        Render();
    }

    private async void BtnRegisterTest_Click(object sender, RoutedEventArgs e)
    {
        BtnRegisterTest.IsEnabled = false;
        RegisterUrlMsg.Text = "Asking the register…";
        try
        {
            var ping = await _register.Ping();
            if (ping.Ok) await _register.SendWaiting();
            _session.RefreshDuplicate();
            RegisterUrlMsg.Text = ping.Ok
                ? $"The shared register is answering: {ping.Rows} card{(ping.Rows == 1 ? "" : "s")} in it."
                : ping.Error;
            Render();
        }
        finally { BtnRegisterTest.IsEnabled = true; }
    }

    /* ---- printer setting ------------------------------------------ */

    private void CmbFlip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CmbFlip.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _session.SetFlip(tag == "short" ? DuplexFlip.ShortEdge : DuplexFlip.LongEdge);
        Render();
    }

    private void ChkRotate_Click(object sender, RoutedEventArgs e) { _session.SetRotateBack(ChkRotate.IsChecked == true); Render(); }

    /* ---- batch from CSV ------------------------------------------- */

    private string? _batchCsv;
    private string[] _batchPhotos = Array.Empty<string>();
    private string[] _batchSigs = Array.Empty<string>();
    private bool _batchRunning;

    private void UpdateBatchButton()
    {
        BtnBatch.IsEnabled = !_batchRunning && _batchCsv is not null && _batchPhotos.Length > 0 && _art is not null;
        var chosen = Screen.BatchChosen(_batchCsv, _batchPhotos.Length, _batchSigs.Length);
        SetText(CsvMsg, chosen.List);
        SetText(PhotosMsg, chosen.Photos);
        SetText(SigsMsg, chosen.Signatures);
    }

    private void BtnPickCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the list", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog(this) == true) { _batchCsv = dialog.FileName; UpdateBatchButton(); }
    }

    private void BtnPickPhotos_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the photos", Filter = PictureFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) { _batchPhotos = dialog.FileNames; UpdateBatchButton(); }
    }

    private void BtnPickSigs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the scanned signatures", Filter = PictureFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) { _batchSigs = dialog.FileNames; UpdateBatchButton(); }
    }

    private void BtnCsvTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Save the example list", FileName = "apg-id-batch-template.csv", Filter = "CSV|*.csv", DefaultExt = ".csv", InitialDirectory = DocumentsFolder };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, Batch.CsvTemplate); BatchStatus.Text = $"Saved {Path.GetFileName(dialog.FileName)}. Fill it in with Excel and save it as CSV."; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { BatchStatus.Text = $"The example couldn’t be saved there ({x.Message.TrimEnd('.')})."; }
    }

    private string BatchLayoutChoice => CmbBatchLayout.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : Batch.LayoutDuplex;
    private string BatchPaperChoice => CmbBatchPaper.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : CardConfig.DefaultPaper;

    private void BatchLayout_Changed(object sender, SelectionChangedEventArgs e) => RenderBatchLayout();

    private void RenderBatchLayout()
    {
        if (LayoutMsg is null || BatchPaperField is null || CmbBatchPaper is null) return;      // the markup is still loading
        var layout = BatchLayoutChoice;
        Show(BatchPaperField, layout != Batch.LayoutCards);
        LayoutMsg.Text = Screen.BatchLayoutLine(layout, BatchPaperChoice);
    }

    private void BtnBatch_Click(object sender, RoutedEventArgs e)
    {
        var sheets = BatchLayoutChoice != Batch.LayoutCards;
        var dialog = new SaveFileDialog
        {
            Title = "Save the batch", FileName = Batch.FileFor(1, sheets, DateTime.Now).FileName, Filter = "PDF|*.pdf", DefaultExt = ".pdf", InitialDirectory = DocumentsFolder,
        };
        if (dialog.ShowDialog(this) == true) _ = RunBatch(dialog.FileName);
    }

    internal async Task RunBatch(string savePath)
    {
        if (_batchRunning || _batchCsv is null || _art is null || _renderer is null) return;
        _batchRunning = true;
        UpdateBatchButton();
        BatchReport.Children.Clear();
        BatchStatus.Text = "Reading the list…";
        try
        {
            string csv;
            try { csv = await File.ReadAllTextAsync(_batchCsv); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { BatchStatus.Text = $"The list couldn’t be read ({x.Message.TrimEnd('.')})."; return; }

            // duplicates across computers are only caught if the shared register has been read
            if (_register.Shared && !await _register.LoadIndex())
                BatchStatus.Text = "The shared register could not be reached, so numbers are checked against this computer only.";

            var geometry = _session.Geometry;
            var remover = _remover;
            // its own measurer: the batch runs off the window's thread, and the window keeps drawing with the other one
            var services = new Batch.Services(_register, WpfTextMeasurer.Load(BesideProgram("Fonts")), geometry, ImageFiles.Decode, remover, () => DateTime.Now);
            var photos = _batchPhotos;
            var sigs = _batchSigs;
            var outcome = await Task.Run(() => Batch.Run(csv, photos, sigs, services,
                (i, n) => Dispatcher.BeginInvoke(() => BatchStatus.Text = $"Checking {i} of {n}…")));

            RenderBatchReport(outcome.Results);
            if (outcome.Error.Length > 0) { BatchStatus.Text = outcome.Error; return; }

            var good = outcome.Good;
            var entries = good.Select(r => r.Data!).ToList();
            var layout = BatchLayoutChoice;
            List<Func<PdfPage>> pages;
            if (layout == Batch.LayoutCards) pages = _renderer.CardPdfPages(_art, geometry, entries, _session.RotateBack);
            else
            {
                var plan = Sheets.Plan(BatchPaperChoice, layout == Batch.LayoutPairs ? SheetMode.Pairs : SheetMode.Duplex)!;
                var backUp = plan.Mode == SheetMode.Duplex && _session.Flip == DuplexFlip.ShortEdge ? !_session.RotateBack : _session.RotateBack;
                pages = _renderer.SheetPdfPages(_art, geometry, plan, entries, Sheets.Pages(entries.Count, plan, _session.Flip), backUp);
            }
            var (_, title) = Batch.FileFor(good.Count, layout != Batch.LayoutCards, DateTime.Now);
            var error = await WritePdf(savePath, pages, title, (done, total) => BatchStatus.Text = $"Building the PDF: page {done} of {total}…");
            if (error.Length > 0) { BatchStatus.Text = error; return; }

            // only the rows that made it into the PDF are issued
            var issued = good.Select(r => r.Entry!).ToList();
            foreach (var entry in issued) { _register.Record(entry); _register.NoteShared(entry); }
            _session.RefreshDuplicate();
            Render();
            var skipped = outcome.Results.Count - good.Count;
            BatchStatus.Text = $"{good.Count} card{(good.Count == 1 ? "" : "s")} saved as {Path.GetFileName(savePath)}"
                + (skipped > 0 ? $" · {skipped} row{(skipped == 1 ? "" : "s")} skipped, see below." : ". Print at 100%.");
            ShowInExplorer(savePath);

            var report = await _session.RecordShared(issued);
            if (report.Message() is { Length: > 0 } trouble)
            {
                BatchStatus.Text += " " + trouble;
                _session.RefreshDuplicate();
                Render();
            }
        }
        finally
        {
            _batchRunning = false;
            UpdateBatchButton();
        }
    }

    private void RenderBatchReport(IReadOnlyList<BatchRowResult> results)
    {
        BatchReport.Children.Clear();
        foreach (var r in results)
        {
            var (tone, head, text) = Screen.BatchRow(r);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = head, Style = Theme.Style("Text.Body"), FontWeight = FontWeights.SemiBold });
            // the words say which it is; the colour only agrees with them
            row.Children.Add(new TextBlock { Text = text, Style = Theme.Style(TextStyle(tone)) });
            BatchReport.Children.Add(row);
        }
    }

    /* ---- the card design -------------------------------------------
       Moving the map, or swapping the artwork, changes where everything on
       the card belongs, so both are offered on the first step of a blank
       card and nowhere else: once a photo has been placed against one map,
       the placement is already wrong for another.
       ---------------------------------------------------------------- */

    private string? _frontFile;
    private string? _backFile;

    // Asked of the overlay's own setting rather than of what is on screen:
    // "is it drawn right now" is also false while the window is hidden or minimised.
    private bool EditingMap => MapOverlay.Visibility == Visibility.Visible;
    private (Dictionary<string, RectMm> Boxes, RectMm PhotoRect)? _editSnapshot;

    private void RenderDesign()
    {
        var editing = EditingMap;
        DesignName.Text = _templates.DesignName;
        Show(DesignHome, !editing);
        Show(DesignAdjust, editing);
        Show(BtnTplReset, _templates.IsCustom && !editing);
        BtnTplUse.IsEnabled = _frontFile is not null && _backFile is not null;
        SetText(FrontFileMsg, _frontFile is null ? "No front chosen." : Path.GetFileName(_frontFile));
        SetText(BackFileMsg, _backFile is null ? "No back chosen." : Path.GetFileName(_backFile));
        if (editing) SetText(DesignReadout, MapOverlay.Readout());
    }

    private void BtnPickFront_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the front artwork", Filter = PictureFilter };
        if (dialog.ShowDialog(this) == true) { _frontFile = dialog.FileName; Render(); }
    }

    private void BtnPickBack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the back artwork", Filter = PictureFilter };
        if (dialog.ShowDialog(this) == true) { _backFile = dialog.FileName; Render(); }
    }

    private void BtnTplUse_Click(object sender, RoutedEventArgs e)
    {
        if (_frontFile is not null && _backFile is not null) UseArtwork(_frontFile, _backFile);
    }

    internal void UseArtwork(string frontPath, string backPath)
    {
        try
        {
            var front = ImageFiles.Load(frontPath);
            var back = ImageFiles.Load(backPath);
            foreach (var (side, picture) in new[] { ("front", front), ("back", back) })
            {
                var problem = TemplateStore.ArtworkShapeProblem(side, picture.PixelWidth, picture.PixelHeight);
                if (problem.Length > 0) { DesignMsg.Text = problem; return; }
            }
            // stored at the printing size, so everything downstream agrees
            var art = new CardArt(
                ImageFiles.Resize(front, TemplateStore.ArtworkWidth, TemplateStore.ArtworkHeight),
                ImageFiles.Resize(back, TemplateStore.ArtworkWidth, TemplateStore.ArtworkHeight));
            Directory.CreateDirectory(_files.Directory);
            ImageFiles.SavePng(art.Front, StoredArtwork("front"));
            ImageFiles.SavePng(art.Back, StoredArtwork("back"));

            var name = Path.GetFileNameWithoutExtension(frontPath).Replace('-', ' ').Replace('_', ' ').Trim();
            _templates.AdoptArtwork(name, (front.PixelWidth, front.PixelHeight), (back.PixelWidth, back.PixelHeight));
            _templates.Save();
            _art = art;
            ApplyGeometry();
            DesignMsg.Text = _templates.Note.Length > 0 ? _templates.Note : "In use. Check the map against the new artwork.";
            ShowContrastWarnings();
            // the map is wherever it was, which is unlikely to suit new artwork
            StartAdjusting();
        }
        catch (Exception x) when (x is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            DesignMsg.Text = $"That artwork couldn’t be used ({x.Message.TrimEnd('.')}). Save it as PNG and try again.";
        }
    }

    /// <summary>
    /// Text that will not read against the artwork. One sentence names the
    /// fields and the fix; the ratios are there for whoever wants to take
    /// them back to the designer.
    /// </summary>
    private void ShowContrastWarnings()
    {
        var warnings = _art is null ? Array.Empty<ContrastWarning>() : _templates.Warnings(CardRenderer.ToRgba(_art.Front));
        var (head, lines) = Screen.ContrastReport(warnings);
        Show(DesignWarn, head.Length > 0);
        DesignWarnList.Children.Clear();
        if (head.Length == 0) return;
        DesignWarnHead.Text = head;
        foreach (var line in lines) DesignWarnList.Children.Add(new TextBlock { Text = line, Style = Theme.Style("Text.Note") });
    }

    private void BtnTplMap_Click(object sender, RoutedEventArgs e) => StartAdjusting();

    internal void StartAdjusting()
    {
        if (!_session.DesignAllowed) return;
        // what Cancel puts back: the map as it was when the editor opened
        _editSnapshot = _templates.Snapshot();
        _front = true;
        _tool = ToolKey.Design;
        MapOverlay.Visibility = Visibility.Visible;
        MapOverlay.Refresh();
        Render();
    }

    // Leaving the editor by any route keeps the changes: the preview has
    // been showing the result the whole time, and throwing away work on a
    // stray click is the worse of the two mistakes. Discard is right there
    // for the other direction.
    private void StopAdjusting()
    {
        MapOverlay.Visibility = Visibility.Collapsed;
        if (_templates.IsPristine) _templates.Clear();      // looked, moved nothing
        else _templates.Save();
        ApplyGeometry();
    }

    private void BtnTplDone_Click(object sender, RoutedEventArgs e)
    {
        StopAdjusting();
        ShowContrastWarnings();                             // the boxes may have been moved somewhere lighter
        DesignMsg.Text = _templates.IsCustom ? "Saved on this computer." : "Nothing was moved.";
        Render();
        BtnTplMap.Focus();
    }

    private void BtnTplCancel_Click(object sender, RoutedEventArgs e)
    {
        _templates.RestoreSnapshot(_editSnapshot);
        StopAdjusting();
        ShowContrastWarnings();
        DesignMsg.Text = "Left as it was.";
        Render();
        BtnTplMap.Focus();
    }

    private void BtnTplDefaults_Click(object sender, RoutedEventArgs e)
    {
        _templates.ResetBoxes();
        ApplyGeometry();
        MapOverlay.Refresh();
        Render();
    }

    private void BtnTplReset_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Go back to the card the app was built with? The artwork and the map you set up here are removed from this computer.", "Go back")) return;
        MapOverlay.Visibility = Visibility.Collapsed;
        _templates.Clear();
        foreach (var side in new[] { "front", "back" })
        {
            try { File.Delete(StoredArtwork(side)); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* an orphaned picture is harmless: nothing points at it any more */ }
        }
        _art = _builtInArt;
        _frontFile = _backFile = null;
        ApplyGeometry();
        ShowContrastWarnings();
        DesignMsg.Text = "Back to the built-in design.";
        Render();
    }
}
