// What is sent to Firestore, to the letter. The register's rules live in
// another repository (firestore.rules in Deign86/apg-keystone) and accept
// exactly these fields with exactly these types, so a change here that is
// not made there leaves every card waiting in the outbox. These tests fail
// on any change to a field's name, its type, or the order they are sent in.

using System.Net;
using IdMaker.Core;

namespace IdMaker.Tests;

public class WireFormatTests
{
    private static readonly RegisterEntry Juan = new()
    {
        Id = "APG-2026-124", Year = 2026, Seq = 124, Name = "Juan S. dela Cruz Jr.", Title = "IT/Admin Associate",
        IssuedAt = "2026-11-17T01:30:00.000Z", Replacement = false,
        Last = "dela Cruz", First = "Juan", Middle = "Santos", Suffix = "Jr.",
        Role1 = "IT Associate", Role2 = "Admin Associate", Kind = PersonKind.Intern,
    };

    private const string JuanFields =
        "{\"id\":{\"stringValue\":\"APG-2026-124\"},"
        + "\"year\":{\"integerValue\":\"2026\"},"
        + "\"seq\":{\"integerValue\":\"124\"},"
        + "\"name\":{\"stringValue\":\"Juan S. dela Cruz Jr.\"},"
        + "\"title\":{\"stringValue\":\"IT/Admin Associate\"},"
        + "\"issuedAt\":{\"stringValue\":\"2026-11-17T01:30:00.000Z\"},"
        + "\"replacement\":{\"booleanValue\":false},"
        + "\"last\":{\"stringValue\":\"dela Cruz\"},"
        + "\"first\":{\"stringValue\":\"Juan\"},"
        + "\"middle\":{\"stringValue\":\"Santos\"},"
        + "\"suffix\":{\"stringValue\":\"Jr.\"},"
        + "\"role1\":{\"stringValue\":\"IT Associate\"},"
        + "\"role2\":{\"stringValue\":\"Admin Associate\"},"
        + "\"by\":{\"stringValue\":\"uid-hr\"},"
        + "\"byEmail\":{\"stringValue\":\"hr@apg.test\"},"
        + "\"kind\":{\"stringValue\":\"intern\"}}";

    private const string ListUrl = "https://firestore.googleapis.com/v1/projects/demo-idmaker/databases/(default)/documents/keystone/apg/idreg";

    [Fact]
    public async Task An_issued_id_is_sent_as_exactly_these_fields_of_exactly_these_types()
    {
        var firebase = new FakeFirebase();
        var r = await Make.SignedIn(firebase);

        Assert.Equal(SharedOutcome.Sent, (await r.RecordShared(Juan)).Outcome);
        Assert.Equal(JuanFields, firebase.Docs["APG-2026-124"].ToJsonString());
        Assert.Contains($"POST {ListUrl}?documentId=APG-2026-124", firebase.Requests);
    }

    [Fact]
    public async Task A_card_from_before_the_form_asked_who_it_is_for_is_sent_without_a_kind()
    {
        var firebase = new FakeFirebase();
        var r = await Make.SignedIn(firebase);
        var old = Juan with { Id = "APG-2026-125", Seq = 125, Kind = "", Middle = "", Suffix = "", Role2 = "" };

        Assert.Equal(SharedOutcome.Sent, (await r.RecordShared(old)).Outcome);
        Assert.Equal(
            "{\"id\":{\"stringValue\":\"APG-2026-125\"},"
            + "\"year\":{\"integerValue\":\"2026\"},"
            + "\"seq\":{\"integerValue\":\"125\"},"
            + "\"name\":{\"stringValue\":\"Juan S. dela Cruz Jr.\"},"
            + "\"title\":{\"stringValue\":\"IT/Admin Associate\"},"
            + "\"issuedAt\":{\"stringValue\":\"2026-11-17T01:30:00.000Z\"},"
            + "\"replacement\":{\"booleanValue\":false},"
            + "\"last\":{\"stringValue\":\"dela Cruz\"},"
            + "\"first\":{\"stringValue\":\"Juan\"},"
            + "\"middle\":{\"stringValue\":\"\"},"
            + "\"suffix\":{\"stringValue\":\"\"},"
            + "\"role1\":{\"stringValue\":\"IT Associate\"},"
            + "\"role2\":{\"stringValue\":\"\"},"
            + "\"by\":{\"stringValue\":\"uid-hr\"},"
            + "\"byEmail\":{\"stringValue\":\"hr@apg.test\"}}",
            firebase.Docs["APG-2026-125"].ToJsonString());
    }

    [Fact]
    public async Task A_replacement_is_sent_as_the_same_fields_under_the_card_it_replaces()
    {
        var firebase = new FakeFirebase();
        var r = await Make.SignedIn(firebase);
        Assert.Equal(SharedOutcome.Sent, (await r.RecordShared(Juan)).Outcome);

        var again = Juan with { IssuedAt = "2026-12-01T02:00:00.000Z", Replacement = true };
        Assert.Equal(SharedOutcome.Sent, (await r.RecordShared(again)).Outcome);
        Assert.Equal(
            JuanFields.Replace("2026-11-17T01:30:00.000Z", "2026-12-01T02:00:00.000Z").Replace("\"booleanValue\":false", "\"booleanValue\":true"),
            firebase.Docs["APG-2026-124/replacements/20261201T020000000Z"].ToJsonString());
        Assert.Contains($"POST {ListUrl}/APG-2026-124/replacements?documentId=20261201T020000000Z", firebase.Requests);
        // the first card is as it was
        Assert.Equal(JuanFields, firebase.Docs["APG-2026-124"].ToJsonString());
        Assert.DoesNotContain(firebase.Requests, q => q.StartsWith("PATCH ") || q.StartsWith("PUT ") || q.StartsWith("DELETE "));
    }

    [Fact]
    public async Task What_the_form_issues_is_what_is_sent()
    {
        var firebase = new FakeFirebase();
        var register = await Make.SignedIn(firebase);
        var s = Make.Session(register);
        s.SetNamePart(Field.Last, "dela Cruz");
        s.SetNamePart(Field.First, "Juan");
        s.SetNamePart(Field.Middle, "Santos");
        s.SetNamePart(Field.Suffix, "Jr.");
        s.PickKind(PersonKind.Intern);
        s.PickRole1("IT Associate");
        s.AddSecondRole();
        s.PickRole2(RoleChoice.Other);
        s.TypeRole2("Admin Associate");
        s.SetSeq("124");
        await s.DuplicateCheck;
        s.AcceptPhoto(Make.Photo());
        s.SetSignature(Session.ExampleSignature());
        s.GoStep(Session.LastStep);
        Assert.Equal(Session.LastStep, s.StepIndex);

        var report = await s.RecordShared(s.MarkIssued());
        Assert.Equal("", report.Message());
        // 9:30 in the morning of 17 Nov 2026 on the test's clock, written as UTC
        var stamp = new DateTime(2026, 11, 17, 9, 30, 0).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(JuanFields.Replace("2026-11-17T01:30:00.000Z", stamp), firebase.Docs["APG-2026-124"].ToJsonString());
    }
}
