using System.Net;
using IdMaker.Core;

namespace IdMaker.Tests;

public class StepTests
{
    [Fact]
    public void No_step_opens_over_an_unfinished_one()
    {
        var s = Make.Session();
        Assert.Equal(0, s.FurthestStep());

        // every way forward from an empty form stays on step 1
        Assert.False(s.NextStep().Moved);
        Assert.Equal(0, s.StepIndex);
        for (var i = 1; i <= Session.LastStep; i++)
        {
            Assert.False(s.ChipPressed(i).Moved);
            Assert.Equal(0, s.StepIndex);
        }
        s.GoStep(4);
        Assert.Equal(0, s.StepIndex);
    }

    [Fact]
    public void A_refusal_names_the_reason_and_points_at_the_field()
    {
        var s = Make.Session();
        var r = s.NextStep();
        Assert.Equal(Field.Last, r.FocusField);
        Assert.Equal("Can’t continue yet. Enter the last name.", s.BlockedText);
        Assert.Equal(0, s.BlockedAbout);

        // pressing a locked chip from a finished step names the step in the way
        s.SetNamePart(Field.Last, "Reyes");
        s.SetNamePart(Field.First, "Ana");
        Assert.True(s.NextStep().Moved);
        Assert.Equal(1, s.StepIndex);
        var chip = s.ChipPressed(4);
        Assert.False(chip.Moved);
        Assert.Equal(1, s.StepIndex);
        Assert.Equal(Field.Title, chip.FocusField);
        Assert.StartsWith("Can’t continue yet.", s.BlockedText);

        // and it clears once the thing it named is fixed
        s.PickRole1("HR Associate");
        s.ClearBlockedIfFixed(s.Problems());
        Assert.Null(s.BlockedAbout);
        Assert.Equal("", s.BlockedText);
    }

    [Fact]
    public void A_finished_form_walks_to_the_end_and_back_is_always_allowed()
    {
        var s = Make.Session();
        Make.FillForm(s);
        for (var i = 0; i < Session.LastStep; i++) Assert.True(s.NextStep().Moved);
        Assert.Equal(Session.LastStep, s.StepIndex);
        Assert.NotNull(s.ReviewEntry);
        Assert.Equal("APG-2026-124", s.ReviewEntry!.Id);
        s.Back();
        Assert.Equal(Session.LastStep - 1, s.StepIndex);
        s.GoStep(0);
        Assert.Equal(0, s.StepIndex);
    }

    [Fact]
    public void Something_behind_coming_undone_sends_the_form_back_to_it()
    {
        var s = Make.Session();
        Make.FillForm(s);
        s.GoStep(4);
        Assert.Equal(4, s.StepIndex);
        // the name stops fitting the card
        s.SetNamePart(Field.First, "Bartholomew Maximilian Featherstonehaugh Cholmondeley Marjoribanks");
        var r = s.NextStep();
        Assert.False(r.Moved);
        Assert.Equal(0, s.StepIndex);
        Assert.Equal(Field.NameFit, r.FocusField);
    }

    [Fact]
    public void Until_the_fonts_are_there_a_name_is_not_judged_for_fit()
    {
        var s = Make.Session(measurer: false);
        s.SetNamePart(Field.Last, "Marjoribanks");
        s.SetNamePart(Field.First, "Bartholomew Maximilian Featherstonehaugh Cholmondeley");
        Assert.Equal("", s.Problems()[Field.NameFit]);
        s.Measurer = new FakeMeasurer();
        Assert.NotEqual("", s.Problems()[Field.NameFit]);
    }

    [Fact]
    public void Fields_say_what_is_wrong_with_them()
    {
        var s = Make.Session();
        s.SetNamePart(Field.Last, "Reyes2");
        s.SetNamePart(Field.Suffix, "Jr!");
        s.PickRole1(RoleChoice.Other);
        var p = s.Problems();
        Assert.Contains("letters", p[Field.Last]);
        Assert.Contains("Jr., Sr.", p[Field.Suffix]);
        Assert.Equal("Type the job title.", p[Field.Title]);
        s.TypeRole1("R&D <lead>");
        Assert.Contains("letters, numbers", s.Problems()[Field.Title]);
        s.TypeRole1("R&D Lead (Acting)");
        Assert.Equal("", s.Problems()[Field.Title]);

        Assert.Equal("12", s.SetSeq("1a2"));
        Assert.Equal("1234", s.SetSeq("123456"));
        Assert.Equal("", s.Problems()[Field.Seq]);
        s.SetSeq("0");
        Assert.Contains("1 to 4 digits", s.Problems()[Field.Seq]);
    }

    [Fact]
    public void A_second_role_cannot_repeat_the_first()
    {
        var s = Make.Session();
        s.PickRole1("IT Associate");
        s.AddSecondRole();
        Assert.Contains("Choose the second role", s.Problems()[Field.Title2]);
        s.PickRole2("HR Associate");
        Assert.Equal("IT/HR Associate", s.JobTitle);
        s.PickRole1("HR Associate");                 // now the same as the second: the second is cleared
        Assert.Equal("", s.Role2.Pick);
        s.PickRole2(RoleChoice.Other);
        s.TypeRole2("hr associate");
        Assert.Contains("same as the job title", s.Problems()[Field.Title2]);
        s.RemoveSecondRole();
        Assert.Equal("", s.Problems()[Field.Title2]);
        Assert.Equal("HR Associate", s.JobTitle);
    }

    [Fact]
    public void Leaving_a_name_field_tidies_capitals_typed_without_shift()
    {
        var s = Make.Session();
        s.SetNamePart(Field.Last, "dela cruz");
        Assert.Equal("Dela Cruz", s.LeaveNamePart(Field.Last));
        s.SetNamePart(Field.Last, "dela Cruz");
        Assert.Equal("dela Cruz", s.LeaveNamePart(Field.Last));
        s.SetNamePart(Field.Suffix, "iii");
        Assert.Equal("III", s.LeaveNamePart(Field.Suffix));

        // and moving on tidies them too, for when the field was never "left"
        var t = Make.Session();
        t.SetNamePart(Field.Last, "reyes");
        t.SetNamePart(Field.First, "ana maria");
        Assert.True(t.NextStep().Moved);
        Assert.Equal("Reyes", t.Last);
        Assert.Equal("Ana Maria", t.First);
    }

    [Fact]
    public void A_photo_that_cannot_be_used_keeps_the_step_shut_and_says_why()
    {
        var s = Make.Session();
        s.AcceptPhoto(new PhotoResult(new RgbaImage(2, 2), "a.jpg", PhotoBackground.Other, new PhotoCheck { Problem = "Nope.", FixIsPrompt = true }));
        Assert.Null(s.Photo);
        Assert.Equal("Nope.", s.Problems()[Field.Photo]);
        Assert.True(s.PhotoFixIsPrompt);
        s.PhotoFailed("Could not open.");
        Assert.Equal("Could not open.", s.Problems()[Field.Photo]);
        s.AcceptPhoto(Make.Photo());
        Assert.Equal("", s.Problems()[Field.Photo]);
    }

    [Fact]
    public void Manual_adjustment_is_clamped_and_resets()
    {
        var s = Make.Session();
        s.Nudge(1, 1);
        Assert.True(s.Adjust.IsNone);                // nothing to adjust without a photo
        s.AcceptPhoto(Make.Photo());
        s.Nudge(0.5, -0.5);
        s.SetZoom(9);
        Assert.Equal(new PhotoAdjust(0.5, -0.5, CardConfig.Photo.ZoomMax), s.Adjust);
        s.SetZoom(0.01);
        Assert.Equal(CardConfig.Photo.ZoomMin, s.Adjust.Zoom);
        s.ResetAdjust();
        Assert.True(s.Adjust.IsNone);
    }
}

public class DuplicateTests
{
    private const string Taken = "{\"id\":\"APG-2026-124\",\"year\":2026,\"seq\":124,\"name\":\"Juan dela Cruz\",\"title\":\"Driver\",\"issuedAt\":\"2026-11-03T02:00:00.000Z\",\"replacement\":false}";

    private static (Session Session, FakeHttp Http, Register Register) Shared(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer)
    {
        var store = new MemoryStore();
        var http = new FakeHttp((req, _) => answer(req));
        var register = new Register(store, http) { BuiltInUrl = Make.RegisterUrl };
        return (Make.Session(register, store), http, register);
    }

    [Fact]
    public async Task A_number_in_the_shared_register_is_refused_and_names_who_has_it()
    {
        var (s, http, _) = Shared(_ => Task.FromResult(FakeHttp.Json($"[{Taken}]")));
        s.SetSeq("124");
        await s.DuplicateCheck;
        Assert.Equal(SharedAnswer.Taken, s.DupShared);
        var problem = s.Problems()[Field.Seq];
        Assert.Contains("APG-2026-124 was already issued to Juan dela Cruz", problem);
        Assert.Contains("in the shared register", problem);

        // a replacement card is the way through
        s.SetReissue(true);
        Assert.Equal("", s.Problems()[Field.Seq]);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_free_number_opens_and_later_numbers_are_answered_from_memory()
    {
        var (s, http, _) = Shared(_ => Task.FromResult(FakeHttp.Json($"[{Taken}]")));
        s.SetSeq("125");
        await s.DuplicateCheck;
        Assert.Equal(SharedAnswer.Free, s.DupShared);
        Assert.Equal("", s.Problems()[Field.Seq]);

        s.SetSeq("126");
        s.SetSeq("124");
        Assert.Equal(SharedAnswer.Taken, s.DupShared);       // straight away: no waiting, no request
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task While_the_register_has_not_answered_the_number_is_not_cleared()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var (s, _, _) = Shared(_ => gate.Task);
        Make.FillForm(s, "124");
        Assert.Equal("Checking the shared register for this number…", s.Problems()[Field.Seq]);
        // a fast typist pressing Next cannot get past the number step
        s.GoStep(5);
        Assert.Equal(2, s.StepIndex);

        gate.SetResult(FakeHttp.Json("[]"));
        await s.DuplicateCheck;
        Assert.Equal("", s.Problems()[Field.Seq]);
        s.GoStep(5);
        Assert.Equal(5, s.StepIndex);
    }

    [Fact]
    public async Task A_register_that_cannot_be_reached_warns_but_lets_work_carry_on()
    {
        var (s, _, _) = Shared(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        s.SetSeq("124");
        await s.DuplicateCheck;
        Assert.Equal(SharedAnswer.Unreachable, s.DupShared);
        Assert.Equal("", s.Problems()[Field.Seq]);
    }

    [Fact]
    public async Task An_answer_for_a_number_that_has_since_changed_is_ignored()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var (s, _, _) = Shared(_ => gate.Task);
        s.SetSeq("124");
        var first = s.DuplicateCheck;
        s.SetSeq("");
        gate.SetResult(FakeHttp.Json($"[{Taken}]"));
        await first;
        Assert.Equal(SharedAnswer.Free, s.DupShared);       // an empty box has no duplicate
        Assert.Contains("Enter the employee number", s.Problems()[Field.Seq]);
    }

    [Fact]
    public async Task A_duplicate_found_after_the_form_moved_on_brings_it_back_to_the_number()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var (s, _, register) = Shared(_ => gate.Task);
        // the register was unreachable a moment ago, so the form was let through
        Make.FillForm(s, "124");
        var changed = 0;
        s.Changed += () => changed++;
        s.GoStep(4, force: true);
        gate.SetResult(FakeHttp.Json($"[{Taken}]"));
        await s.DuplicateCheck;
        Assert.Equal(2, s.StepIndex);
        Assert.Equal(2, s.BlockedAbout);
        Assert.True(changed > 0);
        Assert.True(register.IndexLoaded);
    }

    [Fact]
    public async Task A_late_answer_does_not_throw_the_operator_out_of_a_print_that_is_already_queued()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var (s, _, _) = Shared(_ => gate.Task);
        // one card banked (the register was not answering, so it was let through), then "stop adding"
        Make.FillForm(s, "200");
        s.GoStep(Session.LastStep, force: true);
        s.Queue.Add(s.JobCards().Count > 0 ? s.JobCards()[0] with { Current = false }
            : new JobCard("APG-2026-200", "Ana Reyes", "HR Associate", s.CardData(), new RegisterEntry { Id = "APG-2026-200", Year = 2026, Seq = 200, Name = "Ana Reyes" }, false));
        s.CancelAdding();
        Assert.Equal(Session.LastStep, s.StepIndex);
        Assert.False(s.StartedCard);

        gate.SetResult(FakeHttp.Json("[]"));
        await s.DuplicateCheck;
        // the answer was for a card that has been put aside: it is dropped, not applied to the blank one
        Assert.Equal(Session.LastStep, s.StepIndex);
        Assert.Equal(SharedAnswer.Free, s.DupShared);
        Assert.Null(s.BlockedAbout);
    }

    [Fact]
    public void Putting_the_card_aside_changes_its_serial_so_late_work_for_it_can_stand_down()
    {
        var s = Make.Session();
        var before = s.CardSerial;
        s.SetNamePart(Field.Last, "Reyes");
        Assert.Equal(before, s.CardSerial);          // typing is the same card
        s.ClearCard();
        Assert.NotEqual(before, s.CardSerial);
        var second = s.CardSerial;
        s.NewId();
        Assert.NotEqual(second, s.CardSerial);
    }

    [Fact]
    public void A_number_issued_on_this_computer_is_caught_without_any_network()
    {
        var store = new MemoryStore();
        var register = Make.LocalRegister(store);
        register.Record(new RegisterEntry { Id = "APG-2026-124", Year = 2026, Seq = 124, Name = "Ana Reyes", IssuedAt = "2026-11-01T00:00:00.000Z" });
        var s = Make.Session(register, store);
        s.SetSeq("124");
        Assert.Contains("on this computer", s.Problems()[Field.Seq]);
        s.SetYear(2025);
        Assert.Equal("", s.Problems()[Field.Seq]);
    }
}

public class PrintJobTests
{
    [Fact]
    public void Adding_another_id_banks_the_card_and_its_number_cannot_be_used_twice_in_one_print()
    {
        var s = Make.Session();
        Make.FillForm(s, "124");
        s.GoStep(Session.LastStep);
        Assert.True(s.AddAnother());
        Assert.Single(s.Queue);
        Assert.Equal(0, s.StepIndex);
        Assert.False(s.StartedCard);
        Assert.False(s.DesignAllowed);                   // a print is in progress

        Make.FillForm(s, "124");
        Assert.Contains("already waiting in this print", s.Problems()[Field.Seq]);
        s.SetSeq("125");
        s.GoStep(Session.LastStep);
        Assert.Equal(new[] { "APG-2026-124", "APG-2026-125" }, s.JobCards().Select(c => c.Id));
        Assert.True(s.JobCards()[1].Current);
        Assert.Equal("apg-ids-2-2026-11-17.pdf", s.PrintFile().FileName);
    }

    [Fact]
    public void Cancelling_an_extra_id_returns_to_the_print_that_was_waiting()
    {
        var s = Make.Session();
        Make.FillForm(s, "124");
        s.GoStep(Session.LastStep);
        s.AddAnother();
        s.SetNamePart(Field.Last, "Half");
        s.CancelAdding();
        Assert.Equal(Session.LastStep, s.StepIndex);
        Assert.False(s.StartedCard);
        Assert.Single(s.JobCards());
        Assert.Equal("APG-2026-124_Reyes.pdf", s.PrintFile().FileName);

        // taking the last card out leaves nothing to print, so the form starts again
        s.RemoveFromQueue(0);
        Assert.Equal(0, s.StepIndex);
    }

    [Fact]
    public void Saving_issues_every_card_in_the_print_and_empties_the_queue()
    {
        var store = new MemoryStore();
        var register = Make.LocalRegister(store);
        var s = Make.Session(register, store);
        Make.FillForm(s, "124");
        s.GoStep(Session.LastStep);
        s.AddAnother();
        Make.FillForm(s, "125");
        s.PickRole1(RoleChoice.Other);
        s.TypeRole1("Messenger");
        s.GoStep(Session.LastStep);

        var issued = s.MarkIssued();
        Assert.Equal(2, issued.Count);
        Assert.Empty(s.Queue);
        Assert.Equal(new[] { "APG-2026-124", "APG-2026-125" }, register.All().Select(e => e.Id).OrderBy(x => x));
        Assert.Contains("Messenger", new TitleList(store).All());
        Assert.Equal("Reyes", issued[0].Last);
        Assert.False(issued[0].Replacement);

        // the next card sees them at once
        s.NewId();
        s.SetSeq("125");
        Assert.Contains("already issued", s.Problems()[Field.Seq]);
    }

    [Fact]
    public void The_example_card_is_never_written_to_the_register()
    {
        var store = new MemoryStore();
        var register = Make.LocalRegister(store);
        var s = Make.Session(register, store);
        s.LoadExample(Make.Photo(), 2026);
        Assert.Equal(Session.LastStep, s.StepIndex);
        Assert.Equal("Christopher Joseph V. Aureo", s.PrintedName);
        Assert.True(s.Ready());
        Assert.Empty(s.MarkIssued());
        Assert.Empty(register.All());
    }

    [Fact]
    public void One_copy_each_unless_more_are_asked_for_and_never_more_than_twenty_sheets()
    {
        var s = Make.Session();
        Make.FillForm(s);
        s.GoStep(Session.LastStep);
        var plan = s.CurrentPlan()!;
        Assert.Equal(SheetMode.Pairs, plan.Mode);
        Assert.Equal(1, s.PrintCopies(plan));
        Assert.Equal("1 page of A4", s.PrintFileLine());

        s.SetCopies(5);
        Assert.Equal("2 pages of A4", s.PrintFileLine());     // four pairs to a sheet
        var print = s.PrintPages()!.Value;
        Assert.Equal(5, print.Entries.Count);
        Assert.Equal(2, print.Pages.Count);

        s.SetCopies(100000);
        Assert.Equal(plan.PerSheet * 20, s.PrintCopies(plan));
        s.NudgeCopies(1);
        Assert.Equal(plan.PerSheet * 20, s.Copies);

        s.SetLayout(SheetMode.Duplex);
        Assert.Null(s.Copies);                                // the two layouts hold different numbers
        Assert.Equal("2 pages of A4", s.PrintFileLine());     // one sheet, both sides
    }

    [Fact]
    public void Printer_settings_are_remembered_and_a_short_edge_turn_draws_the_backs_the_other_way_up()
    {
        var store = new MemoryStore();
        var s = Make.Session(store: store);
        s.SetLayout(SheetMode.Duplex);
        s.SetFlip(DuplexFlip.ShortEdge);
        s.SetRotateBack(true);

        var again = Make.Session(store: store);
        Assert.Equal(SheetMode.Duplex, again.Layout);
        Assert.Equal(DuplexFlip.ShortEdge, again.Flip);
        Assert.True(again.RotateBack);
        Assert.False(again.BackUpsideDown(again.CurrentPlan()!));     // rotated, then turned back by the short-edge flip
        again.SetFlip(DuplexFlip.LongEdge);
        Assert.True(again.BackUpsideDown(again.CurrentPlan()!));
    }

    [Fact]
    public void The_design_can_only_be_changed_before_an_id_is_started()
    {
        var s = Make.Session();
        Assert.True(s.DesignAllowed);
        s.SetNamePart(Field.Last, "R");
        Assert.False(s.DesignAllowed);
        s.ClearCard();
        Assert.True(s.DesignAllowed);
    }
}

public class RegisterTests
{
    [Fact]
    public void Entries_are_kept_newest_first_and_replaced_by_id()
    {
        var store = new MemoryStore();
        var r = Make.LocalRegister(store);
        r.Record(new RegisterEntry { Id = "APG-2026-001", Year = 2026, Seq = 1, Name = "A", IssuedAt = "2026-01-01T00:00:00.000Z" });
        r.Record(new RegisterEntry { Id = "APG-2026-002", Year = 2026, Seq = 2, Name = "B", IssuedAt = "2026-02-01T00:00:00.000Z" });
        r.Record(new RegisterEntry { Id = "APG-2026-001", Year = 2026, Seq = 1, Name = "A again", IssuedAt = "2026-03-01T00:00:00.000Z", Replacement = true });
        var all = Make.LocalRegister(store).All();
        Assert.Equal(new[] { "A again", "B" }, all.Select(e => e.Name));
        Assert.Equal("A again", r.Find(2026, 1)!.Name);
        Assert.Null(r.Find(2026, null));
        Assert.Null(r.Find(2025, 1));
    }

    [Fact]
    public void The_register_exports_as_csv_and_reads_its_own_export_back()
    {
        var r = Make.LocalRegister();
        r.Record(new RegisterEntry { Id = "APG-2026-124", Year = 2026, Seq = 124, Name = "Ana \"Annie\" Reyes", Title = "HR, Admin", Last = "Reyes", First = "Ana", IssuedAt = "2026-11-17T01:30:00.000Z" });
        var csv = r.ToCsv();
        Assert.StartsWith("\uFEFF\"id\",\"year\",\"seq\"", csv);
        Assert.Contains("\"Ana \"\"Annie\"\" Reyes\"", csv);

        var other = Make.LocalRegister();
        Assert.Equal(1, other.ImportCsv(csv));
        Assert.Equal(0, other.ImportCsv(csv));                 // already there: nothing added twice
        var e = other.Find(2026, 124)!;
        Assert.Equal("Ana \"Annie\" Reyes", e.Name);
        Assert.Equal("HR, Admin", e.Title);
        Assert.Equal("Reyes", e.Last);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("https://script.google.com/macros/s/abc/exec", "")]
    [InlineData("http://localhost:8787/exec", "")]
    [InlineData("http://example.com/exec", "must start with https://")]
    [InlineData("https://docs.google.com/spreadsheets/d/abc/edit", "spreadsheet itself")]
    [InlineData("https://script.google.com/macros/s/abc/dev", "ends in /exec")]
    public void A_wrong_looking_address_is_explained(string url, string expect)
    {
        var problem = Register.UrlLooksWrong(url);
        if (expect.Length == 0) Assert.Equal("", problem); else Assert.Contains(expect, problem);
    }

    [Fact]
    public async Task Each_way_the_sheet_can_fail_gets_its_own_explanation()
    {
        async Task<string> Error(HttpResponseMessage response) =>
            (await new Register(new MemoryStore(), new FakeHttp((_, _) => Task.FromResult(response))) { BuiltInUrl = Make.RegisterUrl }.Ping()).Error;

        Assert.Contains("not allowed", await Error(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.Contains("HTTP 500", await Error(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Contains("sign-in page", await Error(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<HTML><body>Sign in</body></html>") }));
        Assert.Contains("not the ID register script", await Error(FakeHttp.Json("{\"ok\":true}")));

        var offline = new Register(new MemoryStore(), new FakeHttp((_, _) => throw new HttpRequestException("no route"))) { BuiltInUrl = Make.RegisterUrl };
        Assert.Contains("could not reach", (await offline.Ping()).Error);

        var ok = new Register(new MemoryStore(), new FakeHttp((_, _) => Task.FromResult(FakeHttp.Json("{\"ok\":true,\"service\":\"apg-id-register\",\"rows\":12,\"sheet\":\"IDs\"}")))) { BuiltInUrl = Make.RegisterUrl };
        var ping = await ok.Ping();
        Assert.True(ping.Ok);
        Assert.Equal(12, ping.Rows);
        Assert.Equal("IDs", ping.Sheet);
    }

    [Fact]
    public async Task Rows_typed_into_the_sheet_by_hand_cannot_break_the_read()
    {
        const string rows = "[{\"id\":\"APG-2026-001\",\"year\":\"2026\",\"seq\":1.0,\"name\":\"A\",\"replacement\":\"yes\"},"
                          + "{\"id\":\"\",\"name\":\"no id\"},null,\"junk\","
                          + "{\"id\":\"APG-2026-002\",\"year\":null,\"seq\":null,\"name\":42}]";
        var r = new Register(new MemoryStore(), new FakeHttp((_, _) => Task.FromResult(FakeHttp.Json(rows)))) { BuiltInUrl = Make.RegisterUrl };
        Assert.True(await r.LoadIndex());
        var (known, entry) = r.FindSharedNow(2026, 1);
        Assert.True(known);
        Assert.Equal("A", entry!.Name);
        Assert.True(entry.Replacement);
        Assert.Equal(2026, entry.Year);
        Assert.Equal("42", r.FindSharedNow(2026, 2).Entry!.Name);
        Assert.Null(r.FindSharedNow(2026, 3).Entry);
    }

    [Fact]
    public async Task Ten_callers_asking_at_once_make_one_request_and_recording_posts_the_entry()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var posted = "";
        var http = new FakeHttp((req, body) =>
        {
            if (req.Method == HttpMethod.Post) { posted = body; return Task.FromResult(FakeHttp.Json("{\"ok\":true}")); }
            return gate.Task;
        });
        var r = new Register(new MemoryStore(), http) { BuiltInUrl = Make.RegisterUrl };
        var loads = Enumerable.Range(0, 10).Select(_ => r.LoadIndex()).ToArray();
        gate.SetResult(FakeHttp.Json("[]"));
        Assert.All(await Task.WhenAll(loads), Assert.True);
        Assert.Single(http.Requests);

        var entry = new RegisterEntry { Id = "APG-2026-009", Year = 2026, Seq = 9, Name = "Peña" };
        Assert.True(await r.RecordShared(entry));
        Assert.Contains("\"action\":\"record\"", posted);
        Assert.Contains("\"id\":\"APG-2026-009\"", posted);
        r.NoteShared(entry);
        Assert.Equal("Peña", r.FindSharedNow(2026, 9).Entry!.Name);
    }

    [Fact]
    public async Task Changing_the_address_while_a_read_is_in_flight_does_not_keep_the_old_register_s_rows()
    {
        var oldGate = new TaskCompletionSource<HttpResponseMessage>();
        var http = new FakeHttp((req, _) => req.RequestUri!.Host == "register.test"
            ? oldGate.Task
            : Task.FromResult(FakeHttp.Json("[{\"id\":\"APG-2026-002\",\"year\":2026,\"seq\":2,\"name\":\"New register\"}]")));
        var r = new Register(new MemoryStore(), http) { BuiltInUrl = Make.RegisterUrl };
        var oldRead = r.LoadIndex();
        r.SetUrl("https://other.test/exec");
        oldGate.SetResult(FakeHttp.Json("[{\"id\":\"APG-2026-001\",\"year\":2026,\"seq\":1,\"name\":\"Old register\"}]"));
        Assert.False(await oldRead);                               // its answer is not kept
        Assert.False(r.IndexLoaded);

        Assert.True(await r.LoadIndex());
        Assert.Null(r.FindSharedNow(2026, 1).Entry);
        Assert.Equal("New register", r.FindSharedNow(2026, 2).Entry!.Name);
    }

    [Fact]
    public async Task A_card_saved_while_the_sheet_is_down_is_kept_and_sent_when_it_is_back()
    {
        var up = false;
        var posts = new List<string>();
        var store = new MemoryStore();
        FakeHttp Http() => new((req, body) =>
        {
            if (!up) throw new HttpRequestException("offline");
            if (req.Method == HttpMethod.Post) posts.Add(body);
            return Task.FromResult(FakeHttp.Json(req.Method == HttpMethod.Post ? "{\"ok\":true}" : "[]"));
        });
        var r = new Register(store, Http()) { BuiltInUrl = Make.RegisterUrl };
        var a = new RegisterEntry { Id = "APG-2026-010", Year = 2026, Seq = 10, Name = "A" };
        var b = new RegisterEntry { Id = "APG-2026-011", Year = 2026, Seq = 11, Name = "B" };
        Assert.False(await r.RecordShared(a));
        Assert.False(await r.RecordShared(b));
        Assert.False(await r.RecordShared(a));                     // trying again does not queue it twice
        Assert.Equal(2, r.WaitingForShared);
        Assert.Equal(2, await r.SendWaiting());                    // still down

        // the app is closed and opened again later, with the sheet back
        up = true;
        var later = new Register(store, Http()) { BuiltInUrl = Make.RegisterUrl };
        Assert.Equal(2, later.WaitingForShared);
        Assert.Equal(0, await later.SendWaiting());
        Assert.Equal(2, posts.Count);
        Assert.Contains("APG-2026-010", posts[0]);
        Assert.Null(store.Get("register-outbox"));
    }

    [Fact]
    public void An_address_saved_on_this_computer_overrides_the_built_in_one()
    {
        var store = new MemoryStore();
        var r = new Register(store) { BuiltInUrl = Make.RegisterUrl };
        Assert.Equal(Make.RegisterUrl, r.Url);
        r.SetUrl(" https://other.test/exec ");
        Assert.Equal("https://other.test/exec", new Register(store) { BuiltInUrl = Make.RegisterUrl }.Url);
        r.SetUrl("");
        Assert.Equal(Make.RegisterUrl, r.Url);
        Assert.False(Make.LocalRegister().Shared);
    }
}

public class BatchTests
{
    private static Batch.Services Services(Register? register = null) => new(
        register ?? Make.LocalRegister(), new FakeMeasurer(), CardGeometry.BuiltIn,
        path => path.EndsWith("broken.png", StringComparison.Ordinal)
            ? (null, "This file couldn’t be opened as an image.")
            : path.Contains("sign", StringComparison.Ordinal)
                ? (SignScan(), "")
                : (Make.Silhouette(600, 1000, 60, 200, 260, 90, 40, 520), ""),
        null, () => new DateTime(2026, 11, 17));

    private static RgbaImage SignScan()
    {
        var img = new RgbaImage(400, 200);
        Array.Fill(img.Data, (byte)240);
        for (var y = 80; y < 100; y++)
            for (var x = 100; x < 300; x++)
                for (var c = 0; c < 3; c++) img.Data[(y * 400 + x) * 4 + c] = 20;
        return img;
    }

    [Fact]
    public void A_csv_without_the_columns_it_needs_says_which()
    {
        Assert.Equal("This CSV is empty.", Batch.ReadRows("").Error);
        Assert.Contains("needs a “seq” column", Batch.ReadRows("last,first,title\nA,B,C").Error);
        Assert.Contains("“last” and “first”", Batch.ReadRows("title,seq\nA,1").Error);
        Assert.Contains("no people", Batch.ReadRows("name,title,seq\n").Error);
        var many = "name,title,seq\n" + string.Concat(Enumerable.Range(1, 201).Select(i => $"Person {i},Driver,{i}\n"));
        Assert.Contains("201 rows", Batch.ReadRows(many).Error);
    }

    [Fact]
    public void Quoted_fields_commas_and_line_endings_are_read_properly()
    {
        var rows = Csv.Parse("\uFEFFa,\"b,1\",\"say \"\"hi\"\"\"\r\n\r\n\"two\nlines\",x,y\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b,1", "say \"hi\"" }, rows[0]);
        Assert.Equal("two\nlines", rows[1][0]);
    }

    [Fact]
    public void Files_are_matched_by_name_whatever_the_case_folder_or_extension()
    {
        var find = Batch.FileIndex(new[] { @"C:\Photos\Aureo.JPG", "/home/x/reyes.png" });
        Assert.Equal(@"C:\Photos\Aureo.JPG", find("aureo.png"));
        Assert.Equal("/home/x/reyes.png", find("photos/REYES.PNG"));
        Assert.Null(find("cruz.png"));
        Assert.Null(find(""));
    }

    [Fact]
    public void Good_rows_become_cards_and_bad_rows_are_reported_not_half_issued()
    {
        var register = Make.LocalRegister();
        register.Record(new RegisterEntry { Id = "APG-2026-130", Year = 2026, Seq = 130, Name = "Earlier Person" });
        const string csv =
            "last,first,middle,title,title2,seq,year,photo,signature,replacement\n" +
            "Aureo,Christopher Joseph,Villanueva,IT Associate,,122,2026,aureo.png,aureo-sign.jpg,\n" +     // good, signed
            "Reyes,Ana,,HR Associate,Admin Associate,123,,reyes.png,,\n" +                                  // good, no signature
            "Reyes,Ana,,HR Associate,,123,,reyes.png,,\n" +                                                 // same number twice
            ",Juan,,Driver,,124,,cruz.png,,\n" +                                                            // no last name
            "Cruz,Juan,,Driver,,abc,,cruz.png,,\n" +                                                        // not a number
            "Cruz,Juan,,Driver,,130,,cruz.png,,\n" +                                                        // already issued
            "Cruz,Juan,,Driver,,130,,cruz.png,,yes\n" +                                                     // …unless it is a replacement
            "Lim,Bea,,Driver,,131,,missing.png,,\n" +                                                       // no such photo
            "Tan,Cy,,Driver,,132,,broken.png,,\n" +                                                         // photo will not open
            "Uy,Di,,,,133,,uy.png,,\n";                                                                     // no title
        var files = new[] { "aureo.png", "reyes.png", "cruz.png", "broken.png", "uy.png" };
        var outcome = Batch.Run(csv, files, new[] { "aureo-sign.jpg" }, Services(register));

        Assert.Equal("", outcome.Error);
        Assert.Equal(10, outcome.Results.Count);
        Assert.Equal(new[] { "APG-2026-122", "APG-2026-123", "APG-2026-130" }, outcome.Good.Select(r => r.Id));

        var first = outcome.Results[0];
        Assert.Equal("Christopher Joseph V. Aureo", first.Data!.Name);
        Assert.False(first.NoSignature);
        Assert.IsType<ScannedSignature>(first.Data.Signature);
        Assert.Equal("IT/Admin Associate".Replace("IT/Admin", "HR/Admin"), outcome.Results[1].Data!.Title);
        Assert.True(outcome.Results[1].NoSignature);
        Assert.Equal(2026, outcome.Results[1].Entry!.Year);

        Assert.Contains("appears twice", outcome.Results[2].Problem);
        Assert.Equal("No last name.", outcome.Results[3].Problem);
        Assert.Contains("not an employee number", outcome.Results[4].Problem);
        Assert.Contains("already issued to Earlier Person", outcome.Results[5].Problem);
        Assert.True(outcome.Results[6].Entry!.Replacement);
        Assert.Contains("No photo file named “missing.png”", outcome.Results[7].Problem);
        Assert.Contains("broken.png: This file couldn’t be opened", outcome.Results[8].Problem);
        Assert.Equal("No job title.", outcome.Results[9].Problem);
        Assert.Equal(5, outcome.Results[4].Line + 0 - 1);
    }

    [Fact]
    public void An_older_list_with_whole_names_still_works_and_nothing_usable_is_an_error()
    {
        var outcome = Batch.Run("name,title,seq\nJuan dela Cruz Jr.,Driver,7\n", new[] { "Juan dela Cruz Jr..png" }, null, Services());
        Assert.Single(outcome.Good);
        Assert.Equal("Cruz", outcome.Good[0].Entry!.Last);
        Assert.Equal("Juan dela Cruz Jr.", outcome.Good[0].Data!.Name);

        var none = Batch.Run("name,title,seq\nJuan,Driver,7\n", Array.Empty<string>(), null, Services());
        Assert.Contains("None of the rows", none.Error);
        Assert.Equal(("apg-id-sheets-2026-11-17.pdf", "Alpha Premier IDs — 1 card — 2026-11-17"), Batch.FileFor(1, true, new DateTime(2026, 11, 17)));
    }
}
