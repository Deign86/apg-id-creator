// What the tests share: the repository's own files, a PNG reader (the core
// library deliberately has no image codec; the app uses Windows'), a text
// measurer made of arithmetic, and a stand-in for the Firebase project the
// shared register lives in. No test ever calls the real one.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdMaker.Core;

namespace IdMaker.Tests;

internal static class Repo
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test", "golden-front.png"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("The repository root (the folder holding assets/) was not found above the test binaries.");
        }
    }

    public static string Asset(string name) => Path.Combine(Root, "assets", name);

    /// <summary>Values captured from the web app this one replaces (see Fixtures/README.md).</summary>
    public static JsonElement WebReference { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "web-reference.json"))).RootElement;
}

/// <summary>Reads 8-bit RGB and RGBA PNGs that are not interlaced, which is all the repository holds.</summary>
internal static class Png
{
    public static RgbaImage Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int width = 0, height = 0, colorType = 0;
        using var idat = new MemoryStream();
        for (var p = 8; p < bytes.Length;)
        {
            var len = (bytes[p] << 24) | (bytes[p + 1] << 16) | (bytes[p + 2] << 8) | bytes[p + 3];
            var type = Encoding.ASCII.GetString(bytes, p + 4, 4);
            if (type == "IHDR")
            {
                width = (bytes[p + 8] << 24) | (bytes[p + 9] << 16) | (bytes[p + 10] << 8) | bytes[p + 11];
                height = (bytes[p + 12] << 24) | (bytes[p + 13] << 16) | (bytes[p + 14] << 8) | bytes[p + 15];
                colorType = bytes[p + 17];
                if (bytes[p + 16] != 8 || colorType is not (2 or 6) || bytes[p + 20] != 0) throw new NotSupportedException("Only 8-bit RGB/RGBA, non-interlaced PNGs.");
            }
            else if (type == "IDAT") idat.Write(bytes, p + 8, len);
            p += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();

        var bpp = colorType == 6 ? 4 : 3;
        var stride = width * bpp;
        var img = new RgbaImage(width, height);
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var o = y * (stride + 1);
            var filter = data[o];
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                int v = data[o + 1 + x];
                v += filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                    _ => Paeth(a, b, c),
                };
                cur[x] = (byte)v;
            }
            for (var x = 0; x < width; x++)
            {
                var t = (y * width + x) * 4;
                img.Data[t] = cur[x * bpp]; img.Data[t + 1] = cur[x * bpp + 1]; img.Data[t + 2] = cur[x * bpp + 2];
                img.Data[t + 3] = bpp == 4 ? cur[x * bpp + 3] : (byte)255;
            }
            (prev, cur) = (cur, prev);
        }
        return img;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>Every character is 0.55 em wide, capitals are 0.7 em tall, descenders reach 0.2 em.</summary>
internal sealed class FakeMeasurer : ITextMeasurer
{
    public double CharEm { get; init; } = 0.55;
    public double Width(CardFont font, double px, string text) => text.Length * CharEm * px;
    public double CapHeight(CardFont font, double px) => 0.7 * px;
    public double Descent(CardFont font, double px, bool uppercase) => (uppercase ? 0.15 : 0.2) * px;
}

/// <summary>Answers the register's requests from a function, and counts them.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, Task<HttpResponseMessage>> _answer;
    public List<string> Requests { get; } = new();

    public FakeHttp(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> answer) { _answer = answer; }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add($"{request.Method} {request.RequestUri}");
        return await _answer(request, body);
    }
}

/// <summary>Wraps a secret the way Windows would, only reversibly and in plain sight, and counts how often it is asked.</summary>
internal sealed class FakeKeeper : ISecretKeeper
{
    public bool Broken { get; set; }
    public string? Protect(string secret) => Broken ? null : "kept:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    public string? Unprotect(string kept)
    {
        if (Broken || !kept.StartsWith("kept:", StringComparison.Ordinal)) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(kept[5..])); }
        catch (FormatException) { return null; }
    }
}

/// <summary>
/// A Firebase project that exists only in memory: sign-in, renewing a
/// sign-in, and the register's list in Firestore, answering the same
/// requests with the same shapes and refusals as the real services, and
/// holding to the register's rules (members only; create, never change).
/// </summary>
internal sealed class FakeFirebase
{
    public const string Email = "hr@apg.test";
    public const string Password = "correct horse";
    public const string Uid = "uid-hr";

    public sealed record Account(string Password, string Uid, bool Member = true, bool Disabled = false);

    public Dictionary<string, Account> Accounts { get; } = new() { [Email] = new Account(Password, Uid) };
    /// <summary>The list, by path under idreg: "APG-2026-124", or "APG-2026-124/replacements/20261117T013000000Z".</summary>
    public SortedDictionary<string, JsonObject> Docs { get; } = new(StringComparer.Ordinal);
    /// <summary>The computer is offline: every request fails before it is answered.</summary>
    public bool Offline { get; set; }
    /// <summary>The register (not sign-in) answers every request with this.</summary>
    public HttpStatusCode? Failing { get; set; }
    /// <summary>Reads of the list wait for this before they answer.</summary>
    public Task? ListGate { get; set; }
    /// <summary>
    /// How a create of a document that is already there is refused: 409 when Firestore looks before asking the
    /// rules, 403 when the rules (which allow no change) answer first. The app must take either.
    /// </summary>
    public HttpStatusCode AlreadyThere { get; set; } = HttpStatusCode.Conflict;
    /// <summary>A write is made, but its answer is lost on the way back.</summary>
    public bool LoseNextAnswer { get; set; }
    public int SignIns { get; private set; }
    public int Renewals { get; private set; }
    public int Lists { get; private set; }
    public int Creates { get; private set; }
    public FakeHttp Http { get; }
    public List<string> Requests => Http.Requests;

    private readonly Dictionary<string, string> _passes = new();        // id token -> uid
    private readonly Dictionary<string, string> _renewals = new();      // refresh token -> uid
    private int _n;

    public FakeFirebase() { Http = new FakeHttp(Answer); }

    /// <summary>Every pass handed out so far stops working, as it does after an hour.</summary>
    public void ExpirePasses() => _passes.Clear();
    /// <summary>Every sign-in is cancelled, as when a password is changed or an account turned off.</summary>
    public void CancelSignIns() { _passes.Clear(); _renewals.Clear(); }

    public void Put(RegisterEntry e, string by = "uid-someone-else") => Docs[e.Id] = Fields(e, by);

    public RegisterEntry? Get(string path) => Docs.TryGetValue(path, out var f) ? Entry(path, f) : null;

    private static JsonObject Fields(RegisterEntry e, string by) => new()
    {
        ["id"] = S(e.Id), ["year"] = new JsonObject { ["integerValue"] = e.Year.ToString() }, ["seq"] = new JsonObject { ["integerValue"] = e.Seq.ToString() },
        ["name"] = S(e.Name), ["title"] = S(e.Title), ["issuedAt"] = S(e.IssuedAt), ["replacement"] = new JsonObject { ["booleanValue"] = e.Replacement },
        ["by"] = S(by),
    };

    private static JsonObject S(string v) => new() { ["stringValue"] = v };
    private static string V(JsonObject f, string name) =>
        f[name] is JsonObject o ? (o["stringValue"] ?? o["integerValue"] ?? o["booleanValue"])?.ToString() ?? "" : "";

    private static RegisterEntry Entry(string path, JsonObject f) => new()
    {
        Id = path.Split('/')[0], Name = V(f, "name"), Title = V(f, "title"), IssuedAt = V(f, "issuedAt"),
        Year = int.TryParse(V(f, "year"), out var y) ? y : 0, Seq = int.TryParse(V(f, "seq"), out var q) ? q : 0,
        Replacement = V(f, "replacement") == "true",
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string message, string state = "") =>
        FakeHttp.Json(new JsonObject { ["error"] = new JsonObject { ["code"] = (int)status, ["message"] = message, ["status"] = state } }.ToJsonString(), status);

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

    private async Task<HttpResponseMessage> Answer(HttpRequestMessage request, string body)
    {
        if (Offline) throw new HttpRequestException("no route to host");
        // SAFETY: HttpClient never sends a request without an address.
        var uri = request.RequestUri!;
        var query = Query(uri);

        if (uri.Host == "identitytoolkit.googleapis.com")
        {
            SignIns++;
            if (query.GetValueOrDefault("key") != Make.Project.ApiKey) return Error(HttpStatusCode.BadRequest, "API key not valid. Please pass a valid API key.", "INVALID_ARGUMENT");
            var asked = JsonNode.Parse(body)!;
            var email = asked["email"]?.ToString() ?? "";
            if (!Accounts.TryGetValue(email, out var account) || account.Password != asked["password"]?.ToString()) return Error(HttpStatusCode.BadRequest, "INVALID_LOGIN_CREDENTIALS");
            if (account.Disabled) return Error(HttpStatusCode.BadRequest, "USER_DISABLED");
            var (pass, renewal) = ($"pass-{++_n}", $"renewal-{_n}");
            _passes[pass] = account.Uid;
            _renewals[renewal] = account.Uid;
            return FakeHttp.Json(new JsonObject { ["idToken"] = pass, ["refreshToken"] = renewal, ["localId"] = account.Uid, ["email"] = email, ["expiresIn"] = "3600" }.ToJsonString());
        }

        if (uri.Host == "securetoken.googleapis.com")
        {
            Renewals++;
            var form = body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (form.GetValueOrDefault("grant_type") != "refresh_token" || !_renewals.TryGetValue(form.GetValueOrDefault("refresh_token") ?? "", out var uid))
                return Error(HttpStatusCode.BadRequest, "TOKEN_EXPIRED");
            var pass = $"pass-{++_n}";
            _passes[pass] = uid;
            return FakeHttp.Json(new JsonObject { ["id_token"] = pass, ["refresh_token"] = form["refresh_token"], ["expires_in"] = "3600", ["user_id"] = uid }.ToJsonString());
        }

        if (uri.Host != "firestore.googleapis.com") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (Failing is { } failing) return new HttpResponseMessage(failing);
        var root = $"/v1/projects/{Make.Project.ProjectId}/databases/(default)/documents/keystone/{Make.Project.Company}/idreg";
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith(root, StringComparison.Ordinal)) return Error(HttpStatusCode.Forbidden, "Missing or insufficient permissions.", "PERMISSION_DENIED");
        var bearer = request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } p } ? p : "";
        if (!_passes.TryGetValue(bearer, out var who)) return Error(HttpStatusCode.Unauthorized, "Request had invalid authentication credentials.", "UNAUTHENTICATED");
        if (!Accounts.Values.Any(a => a.Uid == who && a.Member)) return Error(HttpStatusCode.Forbidden, "Missing or insufficient permissions.", "PERMISSION_DENIED");
        var rest = path[root.Length..].Trim('/');
        string Name(string doc) => $"projects/{Make.Project.ProjectId}/databases/(default)/documents/keystone/{Make.Project.Company}/idreg/{doc}";

        if (request.Method == HttpMethod.Get && rest.Length == 0)
        {
            Lists++;
            if (ListGate is { } gate) await gate;
            var size = int.TryParse(query.GetValueOrDefault("pageSize"), out var n) ? n : 300;
            var from = int.TryParse(query.GetValueOrDefault("pageToken"), out var t) ? t : 0;
            var top = Docs.Where(d => !d.Key.Contains('/')).ToList();
            var answer = new JsonObject();
            var page = top.Skip(from).Take(size).ToList();
            if (page.Count > 0) answer["documents"] = new JsonArray(page.Select(d => (JsonNode)new JsonObject { ["name"] = Name(d.Key), ["fields"] = d.Value.DeepClone() }).ToArray());
            if (from + size < top.Count) answer["nextPageToken"] = (from + size).ToString();
            return FakeHttp.Json(answer.ToJsonString());
        }

        if (request.Method == HttpMethod.Get)
        {
            return Docs.TryGetValue(rest, out var found)
                ? FakeHttp.Json(new JsonObject { ["name"] = Name(rest), ["fields"] = found.DeepClone() }.ToJsonString())
                : Error(HttpStatusCode.NotFound, "Document not found.", "NOT_FOUND");
        }

        // the rules: a card is created, as the account asking, and never changed or removed
        if (request.Method != HttpMethod.Post) return Error(HttpStatusCode.Forbidden, "Missing or insufficient permissions.", "PERMISSION_DENIED");
        Creates++;
        var id = query.GetValueOrDefault("documentId") ?? "";
        var key = rest.Length == 0 ? id : $"{rest}/{id}";
        // SAFETY: the client always sends a document with its fields; a test that does not is wrong and should fail here.
        var fields = (JsonObject)JsonNode.Parse(body)!["fields"]!;
        if (id.Length == 0 || V(fields, "by") != who) return Error(HttpStatusCode.Forbidden, "Missing or insufficient permissions.", "PERMISSION_DENIED");
        if (Docs.ContainsKey(key))
        {
            return AlreadyThere == HttpStatusCode.Conflict
                ? Error(HttpStatusCode.Conflict, "Document already exists: " + Name(key), "ALREADY_EXISTS")
                : Error(HttpStatusCode.Forbidden, "Missing or insufficient permissions.", "PERMISSION_DENIED");
        }
        // SAFETY: DeepClone of a JsonObject is a JsonObject.
        Docs[key] = (JsonObject)fields.DeepClone();
        if (LoseNextAnswer) { LoseNextAnswer = false; throw new HttpRequestException("connection reset"); }
        return FakeHttp.Json(new JsonObject { ["name"] = Name(key), ["fields"] = fields.DeepClone() }.ToJsonString());
    }
}

internal static class Make
{
    /// <summary>A project that does not exist ("demo-" projects are never served by Firebase), so a test that slipped past FakeHttp could not reach anybody's data.</summary>
    public static readonly RegisterProject Project = new("demo-idmaker", "test-api-key");

    /// <summary>A register with no shared one behind it.</summary>
    public static Register LocalRegister(IKeyValueStore? store = null) => new(store ?? new MemoryStore()) { Project = null };

    /// <summary>A register talking to the pretend project, not yet signed in.</summary>
    public static Register SharedRegister(FakeFirebase firebase, IKeyValueStore? store = null, ISecretKeeper? keeper = null, Func<DateTimeOffset>? clock = null) =>
        clock is null
            ? new(store ?? new MemoryStore(), firebase.Http, keeper) { Project = Project }
            : new(store ?? new MemoryStore(), firebase.Http, keeper) { Project = Project, Clock = clock };

    /// <summary>The same, signed in as HR.</summary>
    public static async Task<Register> SignedIn(FakeFirebase firebase, IKeyValueStore? store = null, ISecretKeeper? keeper = null, Func<DateTimeOffset>? clock = null)
    {
        var r = SharedRegister(firebase, store, keeper, clock);
        var result = await r.SignIn(FakeFirebase.Email, FakeFirebase.Password);
        Assert.True(result.Ok, result.Error);
        return r;
    }

    public static Session Session(Register? register = null, IKeyValueStore? store = null, bool measurer = true)
    {
        store ??= new MemoryStore();
        var s = new Session(register ?? LocalRegister(store), new TitleList(store), store, () => new DateTime(2026, 11, 17, 9, 30, 0));
        if (measurer) s.Measurer = new FakeMeasurer();
        return s;
    }

    /// <summary>A card with everything on it except a real photograph.</summary>
    public static void FillForm(Session s, string seq = "124")
    {
        s.SetNamePart(Field.Last, "Reyes");
        s.SetNamePart(Field.First, "Ana");
        s.PickRole1("HR Associate");
        s.SetSeq(seq);
        s.AcceptPhoto(Photo());
        s.SetSignature(IdMaker.Core.Session.ExampleSignature());
    }

    public static PhotoResult Photo() => new(new RgbaImage(4, 4), "photo.png", PhotoBackground.Transparent,
        new PhotoCheck { Ok = true, Placement = new PhotoPlacement(10, 10, 30, 50, 0), Head = new HeadInfo(1, 2, 2, 3) });

    /// <summary>A person-shaped cut-out: an ellipse for the head, a neck, and a wide body to the bottom edge.</summary>
    public static RgbaImage Silhouette(int w, int h, int headTop, int headW, int headH, int neckW, int neckH, int bodyW,
        Action<RgbaImage>? extra = null, (byte R, byte G, byte B)? backdrop = null)
    {
        var img = new RgbaImage(w, h);
        if (backdrop is { } bg)
        {
            for (var i = 0; i < w * h; i++) { img.Data[i * 4] = bg.R; img.Data[i * 4 + 1] = bg.G; img.Data[i * 4 + 2] = bg.B; img.Data[i * 4 + 3] = 255; }
        }
        void Fill(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            var o = (y * w + x) * 4;
            // a little texture, so the sharpness measure has something to read
            var v = (byte)(120 + ((x * 7 + y * 13) % 60));
            img.Data[o] = v; img.Data[o + 1] = (byte)(v - 30); img.Data[o + 2] = (byte)(v - 50); img.Data[o + 3] = 255;
        }
        var cx = w / 2;
        for (var y = 0; y < headH; y++)
        {
            var t = (y + 0.5) / headH * 2 - 1;
            var half = (int)(headW / 2.0 * Math.Sqrt(Math.Max(0, 1 - t * t)));
            for (var x = cx - half; x <= cx + half; x++) Fill(x, headTop + y);
        }
        for (var y = headTop + headH - 2; y < headTop + headH + neckH; y++)
            for (var x = cx - neckW / 2; x <= cx + neckW / 2; x++) Fill(x, y);
        for (var y = headTop + headH + neckH; y < h; y++)
        {
            // shoulders slope out to full width over a few rows
            var grow = Math.Min(1.0, (y - (headTop + headH + neckH) + 1) / (h * 0.06));
            var half = (int)(neckW / 2 + (bodyW / 2 - neckW / 2) * grow);
            for (var x = cx - half; x <= cx + half; x++) Fill(x, y);
        }
        extra?.Invoke(img);
        return img;
    }
}
