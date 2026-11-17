// The window: it shows what the Session holds and passes on what the
// operator does. It decides nothing about the card or the steps itself —
// Render() redraws everything from the Session each time something changes,
// the way the form has always worked, so no two parts of the screen can
// disagree about where things stand.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdMaker.Core;
using IdMaker.Vision;
using Microsoft.Win32;

namespace IdMaker.App;

public partial class MainWindow : Window
{
    private const string PictureFilter = "Pictures (PNG, JPG, WebP)|*.png;*.jpg;*.jpeg;*.webp";

    private readonly FileStore _files;
    private readonly Register _register;
    private readonly Session _session;
    private readonly TemplateStore _templates;

    private CardRenderer? _renderer;
    private CardArt? _builtInArt;
    private CardArt? _art;
    private string _loadError = "";
    private OnnxBackgroundRemover? _remover;
    private string _removerProblem = "";

    private bool _front = true;                 // which side the preview shows
    private bool _guides;                       // maintainer overlay (Ctrl+Shift+G)
    private bool _checkingPhoto;
    private bool _built;                        // false while the markup is still loading: controls announce their starting values before their neighbours exist
    private bool _syncing;                      // true while Render() writes to controls, so their handlers stand down
    private string? _tool;                      // which tool drawer is open, if any
    private bool _saved;                        // the print on screen has been saved
    private string _savedPath = "";
    private readonly DispatcherTimer _sheetTimer;
    private (Point Start, double Dx, double Dy)? _photoDrag;
    private readonly List<Button> _stepChips = new();
    private string _chipsBuiltFor = "";

    /// <summary>Asks a yes/no question. Replaced in tests, where nobody is there to answer.</summary>
    internal Func<string, string, bool> Confirm { get; set; } = (question, yes) =>
        MessageBox.Show(question, CardConfig.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>Opens the full-screen signing window. Replaced in tests.</summary>
    internal Func<string, DrawnSignature?, DrawnSignature?> AskForSignature { get; set; }

    /// <summary>Arriving at the signature step with nothing signed opens the signing screen by itself.</summary>
    internal bool OpenSigningOnArrival { get; set; } = true;

    internal Session Session => _session;
    internal TemplateStore Templates => _templates;
    internal CardRenderer? Renderer => _renderer;
    internal CardArt? Art => _art;

    public MainWindow() : this(new FileStore(FileStore.DefaultDirectory), null) { }

    internal MainWindow(FileStore files, Register? register)
    {
        _files = files;
        _register = register ?? new Register(files);
        _templates = new TemplateStore(files);
        _session = new Session(_register, new TitleList(files), files, post: action => Dispatcher.BeginInvoke(action));
        _session.Changed += Render;
        AskForSignature = (name, existing) =>
        {
            var window = new SignWindow(name, existing) { Owner = this };
            window.ShowDialog();
            return window.Result;
        };

        InitializeComponent();
        _built = true;
        MapOverlay.Store = _templates;
        MapOverlay.Changed += moved => { if (moved) { ApplyGeometry(); DrawPreview(); } DesignReadout.Text = MapOverlay.Readout(); };
        _sheetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _sheetTimer.Tick += (_, _) => { _sheetTimer.Stop(); DrawSheetPreview(); };

        AppVersion.Text = $"{CardConfig.AppName} {CardConfig.AppVersion}";
        TxtPrompt.Text = CardConfig.PhotoPrompt;
        SeqPrefix.Text = $"{CardConfig.EmployeePrefix} -";
        foreach (var y in _session.YearsOffered()) CmbYear.Items.Add(y);
        foreach (var p in CardConfig.Papers)
        {
            CmbPaper.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p.Key });
            CmbBatchPaper.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p.Key, IsSelected = p.Key == CardConfig.DefaultPaper });
        }
        BuildStepChips();
        RenderBatchLayout();
        UpdateBatchButton();

        PreviewKeyDown += Window_PreviewKeyDown;
        CardSurface.SizeChanged += (_, _) => DrawPreview();
        CardBox.SizeChanged += (_, _) => DrawPreview();
        Loaded += (_, _) => Boot();
        // light or dark, as Windows is set, and follow it if it changes while the app is open
        SourceInitialized += (_, _) => ThemeManager.Follow(this);
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        Closed += (_, _) => _remover?.Dispose();
        Render();
    }

    /* ---- starting up ---------------------------------------------- */

    private static string BesideProgram(params string[] parts) => Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());

    private string StoredArtwork(string side) => Path.Combine(_files.Directory, $"template-{side}.png");

    private void Boot()
    {
        try
        {
            var measurer = WpfTextMeasurer.Load(BesideProgram("Fonts"));
            _renderer = new CardRenderer(measurer);
            _builtInArt = new CardArt(ImageFiles.Load(BesideProgram("Assets", "front-template.png")), ImageFiles.Load(BesideProgram("Assets", "back.png")));
            BrandLogo.Source = ImageFiles.Load(BesideProgram("Assets", "logo.png"));
            _art = _builtInArt;

            // a design put in earlier is still the one in use
            if (_templates.Restore() && _templates.HasArtwork)
            {
                try { _art = new CardArt(ImageFiles.Load(StoredArtwork("front")), ImageFiles.Load(StoredArtwork("back"))); }
                catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
                {
                    // the pictures are gone: the record of them is no use without them
                    _templates.Clear();
                    _art = _builtInArt;
                }
            }
            ApplyGeometry();
            // Only now: whether a name fits the card cannot be answered
            // without the fonts, and until it is, an unfittable name counts
            // as finished.
            _session.Measurer = measurer;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            _loadError = $"The card could not be loaded: {e.Message} Install the app again to put its files back.";
        }

        // start reading the register now rather than on the first keystroke
        if (_register.Shared) _ = WarmRegister();
        // the model takes a moment to load; the form does not wait for it
        _ = LoadRemover();
        Render();
        TxtLast.Focus();
    }

    private async Task WarmRegister()
    {
        if (await _register.LoadIndex())
        {
            // cards saved while the register was out of reach are sent now that it answers
            await _register.SendWaiting();
        }
        _session.RefreshDuplicate();
        Render();
    }

    private async Task LoadRemover()
    {
        var (remover, problem) = await Task.Run(() =>
        {
            var r = OnnxBackgroundRemover.TryCreate(out var why);
            return (r, why);
        });
        _remover = remover;
        _removerProblem = problem;
        Render();
    }

    private void ApplyGeometry() => _session.Geometry = _templates.Geometry();

    // Everything named by DynamicResource has already changed colour. What is
    // left are the few pictures drawn from the window's colours — none at the
    // moment, but the preview is cheap to redraw and keeps that true.
    private void OnThemeChanged() => Render();

    /* ---- drawing the screen --------------------------------------- */

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    // An error under a field: the words, and the same words where a screen reader looks for them.
    private static void FieldMessage(TextBlock message, Control input, string error)
    {
        SetText(message, error);
        AutomationProperties.SetHelpText(input, error);
        Theme.Paint(input, Control.BorderBrushProperty, error.Length > 0 ? "Color.Danger" : "Color.ControlBorder");
    }

    internal void Render()
    {
        if (!_built) return;
        _syncing = true;
        try
        {
            var s = _session;
            var p = s.Problems();
            s.ClearBlockedIfFixed(p);
            string Shown(Field f) => s.Touched.Contains(f) ? p[f] : "";
            var onCheck = s.StepIndex == Session.LastStep;
            var loaded = _art is not null && _renderer is not null;

            // the stage shows the card while it is being made, the sheet at the end
            Show(SideTabs, !onCheck);
            SideFront.IsChecked = _front;
            SideBack.IsChecked = !_front;
            Show(CardLoading, !loaded);
            SetText(CardLoading, _loadError.Length > 0 ? _loadError : "Loading the card…");
            Show(CardBox, loaded && !onCheck);
            Show(SheetImage, loaded && onCheck);
            if (!onCheck) Show(SheetNav, false);
            SetText(Caption, onCheck
                ? (s.Layout == SheetMode.Duplex ? "Print both sides, then cut along the marks." : "Cut along the marks and stick each pair back to back.")
                : _front ? "" : "The back is the same on every card.");

            // name
            FieldMessage(MsgLast, TxtLast, Shown(Field.Last));
            FieldMessage(MsgFirst, TxtFirst, Shown(Field.First));
            FieldMessage(MsgMiddle, TxtMiddle, Shown(Field.Middle));
            FieldMessage(MsgSuffix, TxtSuffix, Shown(Field.Suffix));
            SyncText(TxtLast, s.Last);
            SyncText(TxtFirst, s.First);
            SyncText(TxtMiddle, s.Middle);
            SyncText(TxtSuffix, s.Suffix);
            var fitError = s.Touched.Contains(Field.Last) && s.Touched.Contains(Field.First) ? p[Field.NameFit] : "";
            NamePreview.Style = Theme.Style(fitError.Length > 0 ? "Text.Error" : "Text.Body");
            SetText(NamePreview, fitError.Length > 0 ? fitError : $"On the card: {(s.PrintedName.Length > 0 ? s.PrintedName : "—")}");

            // job title
            BuildTitleChips();
            SyncChips(TitleChips, s.Role1.Pick, null);
            Show(TxtTitleCustom, s.Role1.IsOther);
            SyncText(TxtTitleCustom, s.Role1.Custom);
            FieldMessage(MsgTitle, TxtTitleCustom, Shown(Field.Title));
            Show(FieldTitle2, s.ShowRole2);
            Show(BtnRoleAdd, !s.ShowRole2);
            // whatever the main job title is disappears from the second row
            SyncChips(Title2Chips, s.Role2.Pick, s.Role1.Value);
            Show(TxtTitle2Custom, s.ShowRole2 && s.Role2.IsOther);
            SyncText(TxtTitle2Custom, s.Role2.Custom);
            FieldMessage(MsgTitle2, TxtTitle2Custom, Shown(Field.Title2));
            var titleFit = s.Touched.Contains(Field.Title) ? p[Field.TitleFit] : "";
            Show(TitlePreview, titleFit.Length > 0);
            SetText(TitlePreview, titleFit);

            // number
            var dup = s.Duplicate();
            var seqError = Shown(Field.Seq);
            // nothing to say when the check is working: a taken number turns
            // the field red and names who has it. The only thing worth a
            // line here is the check not working.
            var seqHelp = dup is not null && s.Reissue ? $"Replacement for {dup.Value.Entry.Id} ({dup.Value.Entry.Name}, {dup.Value.Entry.IssuedOn})."
                : !_register.Shared ? "Checked against this computer only."
                : s.DupShared == SharedAnswer.Unreachable ? "Could not reach the shared register, so only this computer was checked."
                : "";
            MsgSeq.Style = Theme.Style(seqError.Length > 0 ? "Text.Error" : "Text.Note");
            FieldMessage(MsgSeq, TxtSeq, seqError);
            if (seqError.Length == 0) SetText(MsgSeq, seqHelp);
            SyncText(TxtSeq, s.Seq);
            if (!Equals(CmbYear.SelectedItem, s.Year)) CmbYear.SelectedItem = s.Year;
            Show(ReissueWrap, dup is not null && dup.Value.Source != Session.DuplicateSource.Queue);
            ChkReissue.IsChecked = s.Reissue;

            RenderPhotoStep(p);
            RenderSignStep(Shown(Field.Signature));
            RenderCheckStep(p);

            // one step at a time
            var steps = new FrameworkElement[] { StepName, StepTitle, StepNumber, StepPhoto, StepSign, StepCheck };
            for (var i = 0; i < steps.Length; i++) Show(steps[i], i == s.StepIndex);
            RenderStepChips(p);

            // the line under the buttons: a refusal while there is one,
            // otherwise the quiet standing hint
            var missingHere = Session.StepProblems(s.StepIndex, p);
            var missingAll = p.Where(kv => kv.Value.Length > 0).Select(kv => kv.Key).ToList();
            // after "stop adding" the form is empty on purpose and the job is whatever is queued
            var queueOnly = onCheck && s.Queue.Count > 0 && !s.StartedCard;
            var status = !loaded ? ""
                : onCheck ? (missingAll.Count > 0 && !queueOnly ? $"Still needed: {string.Join(", ", missingAll.Select(Session.FieldLabel))}." : "")
                : missingHere.Count > 0 ? $"Still needed: {string.Join(", ", missingHere.Select(Session.FieldLabel))}."
                : "";
            Show(Blocked, s.BlockedAbout is not null);
            SetText(Blocked, s.BlockedText);
            Show(Status, s.BlockedAbout is null && status.Length > 0);
            SetText(Status, status);

            Show(BtnBack, s.StepIndex > 0 && !(onCheck && _saved));
            Show(BtnNext, !onCheck);
            BtnNext.Content = s.StepIndex == Session.LastStep - 1 ? "Check the card" : "Next";
            // Next is never disabled for an unfinished step. A disabled
            // button is a dead end: it cannot be focused and it cannot
            // explain itself. Pressing it is how you ask what is missing.
            BtnNext.IsEnabled = loaded;

            RenderJob();
            RenderTools();
            DrawPreview();
        }
        finally { _syncing = false; }
    }

    private static void SyncText(TextBox box, string value)
    {
        if (box.Text == value) return;
        var caret = box.CaretIndex;
        box.Text = value;
        box.CaretIndex = Math.Min(caret, value.Length);
    }

    /* ---- the step chips ------------------------------------------- */

    private void BuildStepChips()
    {
        for (var i = 0; i < Session.Steps.Length; i++)
        {
            var index = i;
            var chip = new Button { Style = Theme.Style("Button.Ghost"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6, 12, 6), MinHeight = 36 };
            chip.Click += (_, _) => AfterMove(_session.StepIndex, _session.ChipPressed(index));
            StepsBar.Children.Add(chip);
            _stepChips.Add(chip);
        }
    }

    private void RenderStepChips(Dictionary<Field, string> p)
    {
        var s = _session;
        var stop = s.FurthestStep(p);
        for (var i = 0; i < _stepChips.Count; i++)
        {
            var label = Session.Steps[i].Label;
            var locked = i > stop;
            var current = i == s.StepIndex;
            // a step cannot be both finished and out of reach: locked wins
            var done = Session.StepDone(i, p) && i != Session.LastStep && !current && !locked;
            var chip = _stepChips[i];
            // the mark carries the state, so it does not depend on colour
            chip.Content = $"{(done ? "✓" : (i + 1).ToString(CultureInfo.InvariantCulture))}  {label}";
            chip.Style = Theme.Style(current ? "Button.Primary" : "Button.Ghost");
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Padding = new Thickness(10, 6, 12, 6);
            chip.MinHeight = 36;
            chip.Opacity = locked ? 0.55 : 1;
            chip.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            // not disabled: a locked step can still be asked why it will not open
            var name = locked ? $"{label}. Locked until step {stop + 1}, {Session.Steps[stop].Label}, is finished."
                : current ? $"{label}. Current step."
                : $"{label}{(done ? ". Finished." : "")}";
            AutomationProperties.SetName(chip, name);
            chip.ToolTip = locked ? $"Finish {Session.Steps[stop].Label.ToLowerInvariant()} first" : null;
        }
    }

    /* ---- moving between steps ------------------------------------- */

    private Control? InputFor(Field f) => f switch
    {
        Field.Last => TxtLast, Field.First or Field.NameFit => TxtFirst, Field.Middle => TxtMiddle, Field.Suffix => TxtSuffix,
        Field.Title or Field.TitleFit => TxtTitleCustom.IsVisible ? TxtTitleCustom : FirstChip(TitleChips),
        Field.Title2 => TxtTitle2Custom.IsVisible ? TxtTitle2Custom : FirstChip(Title2Chips),
        Field.Seq => TxtSeq, Field.Photo => BtnPhoto, Field.Signature => BtnSign,
        _ => null,
    };

    private static Control? FirstChip(Panel chips) => chips.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsVisible);

    // Where the cursor lands when a step opens: the control the step is
    // about, not merely the first thing in it. Landing on the year box when
    // the step is about the number sent the typing to the wrong field.
    private Control? LandingFor(int step) => step switch
    {
        0 => TxtLast,
        1 => TitleChips.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true) ?? FirstChip(TitleChips),
        2 => TxtSeq,
        3 => BtnPhoto,
        4 => BtnSign,
        _ => _saved ? BtnNewId : BtnDownload,
    };

    private void AfterMove(int from, MoveResult result)
    {
        if (_session.StepIndex != Session.LastStep) _front = true;
        if (_session.StepIndex != from) { _saved = false; StepScroll.ScrollToTop(); }
        Render();
        var target = result.FocusField is { } f ? InputFor(f) : _session.StepIndex != from ? LandingFor(_session.StepIndex) : null;
        // after the layout pass, so a control that has only just appeared can take the focus
        if (target is not null) Dispatcher.BeginInvoke(() => target.Focus(), DispatcherPriority.Input);
        if (_session.StepIndex == (int)Step.Sign && from != (int)Step.Sign && OpenSigningOnArrival && _session.Signature is null)
        {
            Dispatcher.BeginInvoke(SignNow, DispatcherPriority.ApplicationIdle);
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e) => AfterMove(_session.StepIndex, _session.NextStep());

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        var from = _session.StepIndex;
        _session.Back();
        AfterMove(from, new MoveResult(true, null));
    }

    private void SummaryEdit_Click(object sender, RoutedEventArgs e)
    {
        // SAFETY: the three Edit buttons carry "0", "1" and "2" in the markup.
        var step = int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
        var from = _session.StepIndex;
        _session.GoStep(step);
        AfterMove(from, new MoveResult(true, null));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _tool is not null) { CloseTool(); e.Handled = true; return; }
        if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _guides = !_guides;
            Render();
            e.Handled = true;
            return;
        }
        // Enter in a text box of the form moves on, rather than doing nothing
        if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox { AcceptsReturn: false } box && IsInForm(box) && _session.StepIndex != Session.LastStep)
        {
            e.Handled = true;
            AfterMove(_session.StepIndex, _session.NextStep());
        }
    }

    private bool IsInForm(DependencyObject element)
    {
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d)) if (ReferenceEquals(d, FormPanel)) return true;
        return false;
    }


    /* ---- step 1: name --------------------------------------------- */

    private static Field PartOf(object sender) =>
        // SAFETY: only the four name boxes use these handlers, and each carries its Field's name as its Tag.
        Enum.Parse<Field>((string)((FrameworkElement)sender).Tag);

    private void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        // SAFETY: wired only to TextBox elements in the markup.
        _session.SetNamePart(PartOf(sender), ((TextBox)sender).Text);
        Render();
    }

    private void Name_LostFocus(object sender, RoutedEventArgs e)
    {
        _session.LeaveNamePart(PartOf(sender));
        Render();
    }

    /* ---- step 2: job title ---------------------------------------- */

    // The chip rows are rebuilt only when the list of titles changes — not
    // on every keystroke — so focus and the arrow keys' place in the row survive.
    private void BuildTitleChips()
    {
        var titles = _session.TitleOptions();
        var key = string.Join("\n", titles);
        if (key == _chipsBuiltFor) return;
        _chipsBuiltFor = key;
        Fill(TitleChips, "role1", pick =>
        {
            _session.PickRole1(pick);
            Render();
            if (pick == RoleChoice.Other) Dispatcher.BeginInvoke(() => TxtTitleCustom.Focus(), DispatcherPriority.Input);
        });
        Fill(Title2Chips, "role2", pick =>
        {
            _session.PickRole2(pick);
            Render();
            if (pick == RoleChoice.Other) Dispatcher.BeginInvoke(() => TxtTitle2Custom.Focus(), DispatcherPriority.Input);
        });

        void Fill(Panel panel, string group, Action<string> onPick)
        {
            panel.Children.Clear();
            foreach (var title in titles) panel.Children.Add(Chip(title, title, group, onPick, removable: !TitleList.IsStandard(title)));
            panel.Children.Add(Chip(RoleChoice.Other, "Other…", group, onPick, removable: false));
        }
    }

    private RadioButton Chip(string value, string label, string group, Action<string> onPick, bool removable)
    {
        var chip = new RadioButton { Style = Theme.Style("Chip"), GroupName = group, Tag = value, Content = label };
        chip.Click += (_, _) => { if (!_syncing) onPick(value); };
        if (removable)
        {
            // a title this computer remembered can be dropped again
            var menu = new ContextMenu();
            var item = new MenuItem { Header = $"Remove “{label}” from the list" };
            item.Click += (_, _) => { _session.ForgetTitle(value); Render(); };
            menu.Items.Add(item);
            chip.ContextMenu = menu;
            chip.ToolTip = "Right-click to remove this title from the list";
        }
        return chip;
    }

    // Tick the chosen chip; `hide` removes one option from the row.
    private static void SyncChips(Panel panel, string selected, string? hide)
    {
        foreach (var chip in panel.Children.OfType<RadioButton>())
        {
            // SAFETY: every chip's Tag is the string value given to Chip().
            var value = (string)chip.Tag;
            var hidden = !string.IsNullOrEmpty(hide) && value != RoleChoice.Other && Naming.SameText(value, hide);
            Show(chip, !hidden);
            chip.IsChecked = !hidden && value == selected;
        }
    }

    private void TitleCustom_TextChanged(object sender, TextChangedEventArgs e) { if (_syncing) return; _session.TypeRole1(TxtTitleCustom.Text); Render(); }
    private void Title2Custom_TextChanged(object sender, TextChangedEventArgs e) { if (_syncing) return; _session.TypeRole2(TxtTitle2Custom.Text); Render(); }
    private void TitleCustom_LostFocus(object sender, RoutedEventArgs e) { _session.Touch(Field.Title); Render(); }
    private void Title2Custom_LostFocus(object sender, RoutedEventArgs e) { _session.Touch(Field.Title2); Render(); }

    private void BtnRoleAdd_Click(object sender, RoutedEventArgs e)
    {
        _session.AddSecondRole();
        Render();
        Dispatcher.BeginInvoke(() => FirstChip(Title2Chips)?.Focus(), DispatcherPriority.Input);
    }

    private void BtnRoleRemove_Click(object sender, RoutedEventArgs e)
    {
        _session.RemoveSecondRole();
        Render();
        Dispatcher.BeginInvoke(() => BtnRoleAdd.Focus(), DispatcherPriority.Input);
    }

    /* ---- step 3: number ------------------------------------------- */

    private void TxtSeq_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _session.SetSeq(TxtSeq.Text);
        Render();
    }

    private void TxtSeq_LostFocus(object sender, RoutedEventArgs e) { _session.Touch(Field.Seq); Render(); }

    private void CmbYear_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CmbYear.SelectedItem is not int year) return;
        _session.SetYear(year);
        Render();
    }

    private void ChkReissue_Click(object sender, RoutedEventArgs e) { _session.SetReissue(ChkReissue.IsChecked == true); Render(); }

    /* ---- step 4: photo -------------------------------------------- */

    private void RenderPhotoStep(Dictionary<Field, string> p)
    {
        var s = _session;
        var photo = s.Photo;
        var error = s.Touched.Contains(Field.Photo) || s.PhotoError.Length > 0 ? p[Field.Photo] : "";
        var warnings = photo?.Check.Warnings ?? Array.Empty<string>();
        var (style, text) = _checkingPhoto ? ("Text.Note", "Looking at the photo…")
            : error.Length > 0 ? ("Text.Error", error)
            : warnings.Count > 0 ? ("Text.Warning", string.Join(" ", warnings))
            : photo is { Background: PhotoBackground.Removed } ? ("Text.Note", "The background was removed on this computer. Check the edges on the card.")
            : photo is not null ? ("Text.Note", "")
            : ("Text.Note", _remover is not null
                ? "Any clear photo of the person. The app removes the background itself."
                : _removerProblem.Length > 0
                    ? "A cut-out PNG, or a photo on a plain green background. (The background remover is not available on this computer.)"
                    : "Any clear photo of the person.");
        PhotoMsg.Style = Theme.Style(style);
        SetText(PhotoMsg, text);
        BtnPhoto.Content = _checkingPhoto ? "Looking at the photo…" : photo is not null ? "Choose a different photo" : "Choose a photo";
        BtnPhoto.IsEnabled = !_checkingPhoto;
        SetText(PhotoName, photo?.FileName ?? "");
        Show(PhotoName, photo is not null);
        PhotoThumb.Source = photo is null ? null : ThumbFor(photo);

        Show(AdjustBox, photo is not null);
        BtnAdjustReset.IsEnabled = !s.Adjust.IsNone;
        var zoom = Math.Round(s.Adjust.Zoom * 100);
        if (Math.Abs(SldZoom.Value - zoom) > 0.01) SldZoom.Value = zoom;
        SetText(ZoomOut, $"{zoom:0} %");
        CardSurface.Cursor = photo is not null && _front && !EditingMap ? Cursors.SizeAll : null;

        // When the photo was turned away for something the prompt fixes,
        // put the fix in front of the operator instead of leaving it folded away.
        var wantsPrompt = error.Length > 0 && s.PhotoFixIsPrompt;
        SetText(GuideLabel, wantsPrompt ? "Get a photo the ID will accept" : "If the cut-out looks wrong");
        if (wantsPrompt && !GuideBox.IsExpanded) GuideBox.IsExpanded = true;
    }

    private PhotoResult? _thumbOf;
    private BitmapSource? _thumb;

    private BitmapSource ThumbFor(PhotoResult photo)
    {
        if (!ReferenceEquals(_thumbOf, photo) || _thumb is null)
        {
            _thumbOf = photo;
            var k = Math.Min(1.0, 184.0 / photo.Image.Height);
            _thumb = CardRenderer.ToBitmap(photo.Image.Shrink(Math.Max(1, (int)(photo.Image.Width * k)), Math.Max(1, (int)(photo.Image.Height * k))));
        }
        return _thumb;
    }

    private void BtnPhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the photo", Filter = PictureFilter };
        if (dialog.ShowDialog(this) == true) _ = AcceptPhotoFile(dialog.FileName);
    }

    private void Photo_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Photo_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) _ = AcceptPhotoFile(paths[0]);
    }

    /// <summary>Open a picture file and put it through the photo pipeline.</summary>
    internal async Task AcceptPhotoFile(string path)
    {
        if (_checkingPhoto) return;
        var name = Path.GetFileName(path);
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { size = 0; }
        var problem = PhotoPipeline.FileProblem(name, size);
        if (problem.Length > 0) { _session.PhotoFailed(problem); _front = true; Render(); return; }

        _checkingPhoto = true;
        Render();
        var card = _session.CardSerial;
        var spec = _session.Geometry.Photo;
        var remover = _remover;
        (PhotoResult? Result, string Error) outcome;
        try
        {
            outcome = await Task.Run(() =>
            {
                var (image, error) = ImageFiles.Decode(path);
                return image is null ? ((PhotoResult?)null, error) : (PhotoPipeline.Analyse(image, name, spec, remover), "");
            });
        }
        finally { _checkingPhoto = false; }

        // The card this photo was for has been put aside while it was being
        // looked at (the operator went back to the print). It must not land
        // on the blank card that replaced it.
        if (card != _session.CardSerial) { Render(); return; }

        if (outcome.Result is null) _session.PhotoFailed(outcome.Error);
        else _session.AcceptPhoto(outcome.Result);
        _front = true;
        Render();
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        // SAFETY: the four move buttons carry "dx,dy" as their Tag in the markup.
        var parts = ((string)((FrameworkElement)sender).Tag).Split(',');
        var step = CardConfig.Photo.NudgeStepMm;
        _session.Nudge(int.Parse(parts[0], CultureInfo.InvariantCulture) * step, int.Parse(parts[1], CultureInfo.InvariantCulture) * step);
        _front = true;
        Render();
    }

    private void SldZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _session.SetZoom(SldZoom.Value / 100);
        _front = true;
        Render();
    }

    private void BtnAdjustReset_Click(object sender, RoutedEventArgs e) { _session.ResetAdjust(); Render(); }

    // dragging the photo on the card itself, and the wheel to resize it
    private bool CanDragPhoto => _session.Photo is not null && _front && !EditingMap && _session.StepIndex != Session.LastStep;

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanDragPhoto) return;
        _photoDrag = (e.GetPosition(CardSurface), _session.Adjust.Dx, _session.Adjust.Dy);
        CardSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_photoDrag is not { } d) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPhotoDrag(); return; }
        var now = e.GetPosition(CardSurface);
        var mmPerUnit = CardConfig.CardWidthMm / CardSurface.Width;
        _session.SetAdjustOffset(d.Dx + (now.X - d.Start.X) * mmPerUnit, d.Dy + (now.Y - d.Start.Y) * mmPerUnit);
        DrawPreview();
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e) => EndPhotoDrag();

    private void EndPhotoDrag()
    {
        if (_photoDrag is null) return;
        _photoDrag = null;
        CardSurface.ReleaseMouseCapture();
        Render();
    }

    private void Card_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanDragPhoto) return;
        var step = CardConfig.Photo.ZoomStep;
        _session.SetZoom(_session.Adjust.Zoom * (e.Delta > 0 ? 1 + step : 1 - step));
        e.Handled = true;
        Render();
    }

    private void BtnCopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CardConfig.PhotoPrompt);
            CopyStatus.Text = "Copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // another program is holding the clipboard: select it so Ctrl+C still works
            TxtPrompt.Focus();
            TxtPrompt.SelectAll();
            CopyStatus.Text = "Selected. Press Ctrl+C to copy.";
        }
    }

    /* ---- step 5: signature ---------------------------------------- */

    private ScannedSignature? _scanShown;

    private void RenderSignStep(string error)
    {
        var sig = _session.Signature;
        var scan = sig as ScannedSignature;
        Theme.Paint(SigFrame, Border.BorderBrushProperty, error.Length > 0 ? "Color.Danger" : "Color.ControlBorder");
        Show(SigPreview, scan is null);
        Show(SigImage, scan is not null);
        if (scan is not null && !ReferenceEquals(_scanShown, scan))
        {
            _scanShown = scan;
            SigImage.Source = CardRenderer.TintedScan(scan, Color.FromRgb(0x1D, 0x1C, 0x1A));
        }
        if (scan is null) { _scanShown = null; SigImage.Source = null; }
        if (!ReferenceEquals(_padShows, sig)) { _padShows = sig; SigPreview.Value = sig as DrawnSignature; }

        BtnSign.Content = sig is null ? "Sign now" : "Sign again";
        BtnClearSig.IsEnabled = sig is not null;
        SetText(SigHelp, scan is not null
            ? $"Scanned: {scan.FileName}. It prints in white on the card."
            : "Signing opens on the whole screen, so the pen has room. It prints in white on the card.");
        Show(SigMsg, error.Length > 0 || _sigError.Length > 0);
        SetText(SigMsg, _sigError.Length > 0 ? _sigError : error);
        SetText(TabletNote, PenTablet.DriverInstalled
            ? "A pen tablet draws thicker where you press harder."
            : "No pen tablet driver is installed on this computer, so sign with the mouse or use a scan. The installer can add the XP-Pen driver.");
    }

    private Signature? _padShows;
    private string _sigError = "";

    private void BtnSign_Click(object sender, RoutedEventArgs e) => SignNow();

    private void SignNow()
    {
        if (_session.StepIndex != (int)Step.Sign) return;
        var result = AskForSignature(_session.First.Trim(), _session.Signature as DrawnSignature);
        if (result is null) { Render(); return; }               // cancelled: whatever was there stays
        _sigError = "";
        _session.SetSignature(result);
        _front = true;
        Render();
        // signed: the next thing to do is move on
        Dispatcher.BeginInvoke(() => BtnNext.Focus(), DispatcherPriority.Input);
    }

    private void BtnClearSig_Click(object sender, RoutedEventArgs e)
    {
        _sigError = "";
        _session.SetSignature(null);
        Render();
        BtnSign.Focus();
    }

    private void BtnSigScan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the scanned signature", Filter = PictureFilter };
        if (dialog.ShowDialog(this) == true) _ = AcceptSignatureFile(dialog.FileName);
    }

    internal async Task AcceptSignatureFile(string path)
    {
        var name = Path.GetFileName(path);
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { size = 0; }
        var problem = PhotoPipeline.FileProblem(name, size, "picture of the signature");
        (ScannedSignature? Signature, string Error) outcome = (null, problem);
        var card = _session.CardSerial;
        if (problem.Length == 0)
        {
            SigHelp.Text = "Reading the signature…";
            outcome = await Task.Run(() =>
            {
                var (image, error) = ImageFiles.Decode(path);
                return image is null ? (null, error) : SignatureInk.FromImage(image, name);
            });
        }
        if (card != _session.CardSerial) { Render(); return; }      // as with a photo: the card it was for is gone
        if (outcome.Signature is null)
        {
            // a scan that cannot be read leaves whatever signature was there
            _sigError = outcome.Error;
        }
        else
        {
            _sigError = "";
            _session.SetSignature(outcome.Signature);
            _front = true;
        }
        Render();
    }

    /* ---- the preview ---------------------------------------------- */

    private void DrawPreview()
    {
        if (_art is null || _renderer is null) return;
        if (_session.StepIndex == Session.LastStep) { _sheetTimer.Stop(); _sheetTimer.Start(); return; }

        // as many pixels as the card really occupies on this monitor
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var onScreen = CardBox.ActualWidth > 0 ? CardBox.ActualWidth : CardSurface.Width;
        var ppm = Math.Clamp(onScreen * dpi / CardConfig.CardWidthMm, 4, CardConfig.PrintPxPerMm);
        CardImage.Source = _front
            ? _renderer.RenderFront(_art, _session.Geometry, _session.CardData(), ppm, _guides)
            : _renderer.RenderBack(_art, ppm, _session.RotateBack);
        AutomationProperties.SetName(CardImage, _front
            ? $"Front of the ID card for {(_session.PrintedName.Length > 0 ? _session.PrintedName : "a new hire")}"
            : "Back of the ID card");
    }

    private const double SheetPreviewPpm = 96 / 25.4;

    private void DrawSheetPreview()
    {
        if (_art is null || _renderer is null || _session.StepIndex != Session.LastStep) return;
        var print = _session.PrintPages();
        if (print is not { } pr || pr.Pages.Count == 0) { SheetImage.Source = null; Show(SheetNav, false); return; }
        var s = _session;
        s.PreviewPage = Math.Clamp(s.PreviewPage, 0, pr.Pages.Count - 1);
        var page = pr.Pages[s.PreviewPage];
        var art = _art;
        var renderer = _renderer;
        var geo = s.Geometry;
        var backUp = s.BackUpsideDown(pr.Plan);
        // every card on a sheet has the same back; draw it once
        BitmapSource? back = null;
        SheetImage.Source = renderer.RenderSheet(pr.Plan, page, SheetPreviewPpm,
            i => renderer.RenderFront(art, geo, pr.Entries[i], SheetPreviewPpm),
            _ => back ??= renderer.RenderBack(art, SheetPreviewPpm, backUp));

        var side = pr.Plan.Mode != SheetMode.Duplex ? "" : page.Backs ? "Backs, reversed to match. " : "Fronts. ";
        AutomationProperties.SetName(SheetImage, $"Preview of page {s.PreviewPage + 1} of the {s.Paper} sheet. {side}".Trim());
        SetText(PreviewCaption, $"{side}Page {s.PreviewPage + 1} of {pr.Pages.Count} · {s.Paper}");
        Show(SheetNav, pr.Pages.Count > 1);
        PrevPage.IsEnabled = s.PreviewPage > 0;
        NextPage.IsEnabled = s.PreviewPage < pr.Pages.Count - 1;
    }

    private void Side_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _front = SideFront.IsChecked == true;
        Render();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) { _session.PreviewPage--; DrawSheetPreview(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _session.PreviewPage++; DrawSheetPreview(); }
}
