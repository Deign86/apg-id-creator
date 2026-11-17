// Register of issued IDs.
//
// Two layers:
//   local   this computer's own list. Always on. Instant.
//   shared  one Google Sheet behind a Google Apps Script web app (see
//           register/ at the top of the repository), so every HR computer
//           checks duplicates against the same list.
//
// The shared register is read whole, once, and every number typed after
// that is answered from memory. Asking the sheet about one number at a time
// meant a round trip to Google on every keystroke, and until the answer
// came back the number looked free. Issuing a card that duplicates one
// somebody else issued is the single worst thing this app can do, so the
// check cannot be the slowest part of the form.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IdMaker.Core;

public sealed record RegisterEntry
{
    public string Id { get; init; } = "";
    public int Year { get; init; }
    public int Seq { get; init; }
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>ISO 8601, UTC.</summary>
    public string IssuedAt { get; init; } = "";
    public bool Replacement { get; init; }
    public string Last { get; init; } = "";
    public string First { get; init; } = "";
    public string Middle { get; init; } = "";
    public string Suffix { get; init; } = "";
    public string Role1 { get; init; } = "";
    public string Role2 { get; init; } = "";

    /// <summary>"12 Nov 2026", or empty when the date cannot be read.</summary>
    public string IssuedOn =>
        DateTimeOffset.TryParse(IssuedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-PH"))
            : "";
}

/// <summary>A failure the operator can act on: its message is shown as it is.</summary>
public sealed class RegisterException : Exception
{
    public RegisterException(string message) : base(message) { }
}

public sealed record PingResult(bool Ok, int Rows, string Sheet, string Error);

public sealed partial class Register
{
    private const string Key = "register";
    private const string UrlKey = "register-url";
    private const string OutboxKey = "register-outbox";

    private readonly IKeyValueStore _store;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _savedUrl;
    private Dictionary<string, RegisterEntry>? _index;      // the shared register, once it has been read
    private Task<bool>? _loading;                           // the read in flight, so ten callers make one request
    private int _urlVersion;                                // goes up when the address changes, so an answer from the old one is not kept

    /// <summary>The address the app ships with. Tests point it somewhere harmless.</summary>
    public string BuiltInUrl { get; init; } = CardConfig.RegisterUrl;

    public Register(IKeyValueStore store, HttpMessageHandler? handler = null)
    {
        _store = store;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // the per-request timeout below is the one that speaks; this only stops it being cut short
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /* ---- local --------------------------------------------------- */

    private List<RegisterEntry> ReadLocal() => _store.Read(Key, new List<RegisterEntry>());

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RegisterEntry> All() =>
        ReadLocal().OrderByDescending(e => e.IssuedAt, StringComparer.Ordinal).ToList();

    /// <summary>The local entry for this number, or null.</summary>
    public RegisterEntry? Find(int year, int? seq)
    {
        if (seq is null) return null;
        var id = Naming.EmployeeId(year, seq.Value);
        return ReadLocal().FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Insert or replace by id.</summary>
    public void Record(RegisterEntry entry)
    {
        lock (_gate)
        {
            var list = ReadLocal().Where(e => e.Id != entry.Id).ToList();
            list.Add(entry);
            _store.Write(Key, list);
        }
    }

    /// <summary>
    /// Take in a register CSV downloaded from this app or the earlier web
    /// app. Entries already here are kept as they are. Returns how many were added.
    /// </summary>
    public int ImportCsv(string csvText)
    {
        var rows = Csv.Parse(csvText);
        if (rows.Count < 2) return 0;
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        string Cell(IReadOnlyList<string> r, string name)
        {
            var i = head.IndexOf(name);
            return i >= 0 && i < r.Count ? r[i].Trim() : "";
        }
        var added = 0;
        lock (_gate)
        {
            var list = ReadLocal();
            var have = list.Select(e => e.Id).ToHashSet();
            foreach (var r in rows.Skip(1))
            {
                var id = Cell(r, "id");
                if (id.Length == 0 || have.Contains(id)) continue;
                if (!int.TryParse(Cell(r, "year"), NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
                if (!int.TryParse(Cell(r, "seq"), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) continue;
                list.Add(new RegisterEntry
                {
                    Id = id, Year = year, Seq = seq, Name = Cell(r, "name"), Title = Cell(r, "title"),
                    IssuedAt = Cell(r, "issuedat"), Replacement = Cell(r, "replacement").Equals("yes", StringComparison.OrdinalIgnoreCase),
                    Last = Cell(r, "last"), First = Cell(r, "first"), Middle = Cell(r, "middle"), Suffix = Cell(r, "suffix"),
                    Role1 = Cell(r, "role1"), Role2 = Cell(r, "role2"),
                });
                have.Add(id);
                added++;
            }
            if (added > 0) _store.Write(Key, list);
        }
        return added;
    }

    /* ---- shared -------------------------------------------------- */

    public string Url
    {
        get
        {
            _savedUrl ??= _store.Get(UrlKey)?.Trim() ?? "";
            return _savedUrl.Length > 0 ? _savedUrl : BuiltInUrl;
        }
    }

    public bool Shared => Url.Length > 0;

    /// <summary>Save (or clear, with "") the shared-register address on this computer.</summary>
    public void SetUrl(string? url)
    {
        var clean = (url ?? "").Trim();
        _savedUrl = clean;
        _store.Set(UrlKey, clean.Length > 0 ? clean : null);
        lock (_gate) { _index = null; _loading = null; _urlVersion++; }
    }

    [GeneratedRegex(@"^http://(localhost|127\.0\.0\.1)([:/]|$)", RegexOptions.IgnoreCase)] private static partial Regex Localhost();
    [GeneratedRegex(@"/exec(\?|$)", RegexOptions.IgnoreCase)] private static partial Regex EndsInExec();

    /// <summary>What is wrong with this address, or "" when it looks like a web-app address.</summary>
    public static string UrlLooksWrong(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return "";
        if (!Localhost().IsMatch(u) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "The address must start with https://";
        if (u.Contains("docs.google.com/spreadsheets", StringComparison.OrdinalIgnoreCase))
            return "That is the spreadsheet itself. Deploy the Apps Script in it (register/README.md) and paste the web-app address, which ends in /exec.";
        if (!EndsInExec().IsMatch(u)) return "A web-app address ends in /exec. Check Deploy → Manage deployments in the Apps Script editor.";
        return "";
    }

    /// <summary>
    /// One request, with the reason for any failure said plainly, so the
    /// operator is not left guessing between a cold start, a sign-in page
    /// and a computer that is offline.
    /// </summary>
    private async Task<JsonNode?> Fetch(HttpRequestMessage request)
    {
        using var cts = new CancellationTokenSource(CardConfig.RegisterTimeout);
        HttpResponseMessage r;
        try
        {
            r = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new RegisterException($"No answer within {(int)CardConfig.RegisterTimeout.TotalSeconds} seconds. Google Apps Script is slow the first time it is woken — try once more.");
        }
        catch (HttpRequestException)
        {
            throw new RegisterException("This computer could not reach that address. Check it is online, and that Deploy → Manage deployments has Who has access set to “Anyone”.");
        }

        using (r)
        {
            if (!r.IsSuccessStatusCode)
            {
                throw new RegisterException(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"That address answered “not allowed” (HTTP {(int)r.StatusCode}). In Deploy → Manage deployments, set Who has access to “Anyone”."
                    : $"That address answered with an error (HTTP {(int)r.StatusCode}).");
            }
            string text;
            try { text = await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                throw new RegisterException($"No answer within {(int)CardConfig.RegisterTimeout.TotalSeconds} seconds. Try once more.");
            }
            try { return JsonNode.Parse(text); }
            catch (JsonException)
            {
                throw new RegisterException(text.Contains("<html", StringComparison.OrdinalIgnoreCase)
                    ? "That address answered with a web page instead of the register — almost always a Google sign-in page. In Deploy → Manage deployments, set Who has access to “Anyone”."
                    : "That address answered, but not with register data. Check the web-app address in Deploy → Manage deployments.");
            }
        }
    }

    private static string Text(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : "";
    private static int Int(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<double>(out var d) && (i = (int)d) == d)
            || int.TryParse(Text(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) ? i : 0;
    private static bool Bool(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : Text(n) is "yes" or "true");

    // Rows come from a spreadsheet people can type into, so nothing about
    // their shape is taken on trust: a row that will not read is skipped.
    private static RegisterEntry? ParseEntry(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var id = Text(o["id"]);
        if (id.Length == 0) return null;
        return new RegisterEntry
        {
            Id = id, Year = Int(o["year"]), Seq = Int(o["seq"]), Name = Text(o["name"]), Title = Text(o["title"]),
            IssuedAt = Text(o["issuedAt"]), Replacement = Bool(o["replacement"]),
            Last = Text(o["last"]), First = Text(o["first"]), Middle = Text(o["middle"]), Suffix = Text(o["suffix"]),
            Role1 = Text(o["role1"]), Role2 = Text(o["role2"]),
        };
    }

    /// <summary>Check the service is answering.</summary>
    public async Task<PingResult> Ping(string? url = null)
    {
        var u = url ?? Url;
        if (u.Length == 0) return new PingResult(false, 0, "", "No address set.");
        try
        {
            var data = await Fetch(new HttpRequestMessage(HttpMethod.Get, u + "?action=ping")).ConfigureAwait(false);
            if (data is not JsonObject o || Text(o["service"]) != "apg-id-register")
                return new PingResult(false, 0, "", "That address answered, but it is not the ID register script.");
            return new PingResult(true, Int(o["rows"]), Text(o["sheet"]), "");
        }
        catch (RegisterException e) { return new PingResult(false, 0, "", e.Message); }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException)
        {
            return new PingResult(false, 0, "", "That is not an address this computer can open.");
        }
    }

    /// <summary>
    /// Read the whole shared register. True when it is in memory afterwards,
    /// false when it could not be reached (or there is no shared register).
    /// </summary>
    public Task<bool> LoadIndex(bool force = false)
    {
        lock (_gate)
        {
            if (!Shared) return Task.FromResult(false);
            if (_index is not null && !force) return Task.FromResult(true);
            if (_loading is not null && !force) return _loading;
            var mine = _loading = LoadIndexCore(Url, _urlVersion);
            return mine;
        }
    }

    private async Task<bool> LoadIndexCore(string url, int version)
    {
        try
        {
            var rows = await Fetch(new HttpRequestMessage(HttpMethod.Get, url + "?action=list")).ConfigureAwait(false);
            if (rows is not JsonArray list) return false;
            var index = new Dictionary<string, RegisterEntry>();
            foreach (var row in list)
            {
                if (ParseEntry(row) is { } e) index[e.Id] = e;
            }
            lock (_gate)
            {
                // the address was changed while this was in flight: these rows belong to the old register
                if (version != _urlVersion) return false;
                _index = index;
            }
            return true;
        }
        catch (RegisterException) { return false; }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException) { return false; }
        finally { lock (_gate) { if (version == _urlVersion) _loading = null; } }
    }

    /// <summary>Has the shared register been read?</summary>
    public bool IndexLoaded { get { lock (_gate) return _index is not null; } }

    /// <summary>
    /// The shared entry for this number, without asking anybody. Known is
    /// false when the register has not been read yet.
    /// </summary>
    public (bool Known, RegisterEntry? Entry) FindSharedNow(int year, int? seq)
    {
        if (!Shared || seq is null) return (true, null);
        lock (_gate)
        {
            if (_index is null) return (false, null);
            return (true, _index.TryGetValue(Naming.EmployeeId(year, seq.Value), out var e) ? e : null);
        }
    }

    /// <summary>Keep the index true after issuing a card, without re-reading the sheet.</summary>
    public void NoteShared(RegisterEntry entry)
    {
        lock (_gate) { if (_index is not null && entry.Id.Length > 0) _index[entry.Id] = entry; }
    }

    /* ---- cards the shared register has not been told about yet ------
       A card saved while the sheet could not be reached is already issued:
       it is in the local list and in somebody's hand. If the shared write
       were simply dropped, no other computer would ever learn the number
       was taken. So it is kept here and sent again later.
       ---------------------------------------------------------------- */

    private List<RegisterEntry> ReadOutbox() => _store.Read(OutboxKey, new List<RegisterEntry>());

    /// <summary>How many issued cards are still waiting to be written to the shared register.</summary>
    public int WaitingForShared { get { lock (_gate) return ReadOutbox().Count; } }

    private void SetWaiting(RegisterEntry entry, bool waiting)
    {
        lock (_gate)
        {
            var list = ReadOutbox().Where(e => e.Id != entry.Id).ToList();
            if (waiting) list.Add(entry);
            _store.Set(OutboxKey, list.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(list, StoreJson.Options));
        }
    }

    /// <summary>Try again to write every waiting card. Returns how many are still waiting afterwards.</summary>
    public async Task<int> SendWaiting()
    {
        if (!Shared) return 0;
        List<RegisterEntry> waiting;
        lock (_gate) waiting = ReadOutbox();
        foreach (var entry in waiting)
        {
            if (!await RecordShared(entry).ConfigureAwait(false)) break;        // still down: no point trying the rest
        }
        return WaitingForShared;
    }

    /// <summary>
    /// Record in the shared register. True when it got there; when it did
    /// not, the card is kept to be sent again by SendWaiting.
    /// </summary>
    public async Task<bool> RecordShared(RegisterEntry entry)
    {
        if (!Shared) return false;
        var sent = await PostRecord(entry).ConfigureAwait(false);
        SetWaiting(entry, !sent);
        return sent;
    }

    private async Task<bool> PostRecord(RegisterEntry entry)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { action = "record", entry }, StoreJson.Options);
            var request = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                // text/plain, as the Apps Script expects: it reads the raw body
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            var data = await Fetch(request).ConfigureAwait(false);
            return data is JsonObject o && Bool(o["ok"]);
        }
        catch (RegisterException) { return false; }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException) { return false; }
    }

    /* ---- export -------------------------------------------------- */

    public string ToCsv()
    {
        static string Esc(object? v) => "\"" + (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
        var rows = new List<string>
        {
            string.Join(',', new[] { "id", "year", "seq", "last", "first", "middle", "suffix", "name", "title", "role1", "role2", "issuedAt", "replacement" }.Select(Esc)),
        };
        foreach (var e in All())
        {
            rows.Add(string.Join(',', new object[]
            {
                e.Id, e.Year, e.Seq, e.Last, e.First, e.Middle, e.Suffix, e.Name, e.Title, e.Role1, e.Role2, e.IssuedAt, e.Replacement ? "yes" : "no",
            }.Select(Esc)));
        }
        return "﻿" + string.Join("\r\n", rows);
    }
}

/// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, CR / LF / CRLF.</summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var s = text.StartsWith('﻿') ? text[1..] : text;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"') { field.Append('"'); i++; } else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); rows.Add(row); row = new List<string>(); field.Clear(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(v => v.Trim().Length > 0)).ToList();
    }
}
