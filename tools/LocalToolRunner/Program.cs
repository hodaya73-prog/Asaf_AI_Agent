// Local stand-in for the orchestrator (SPEC.md §7) until the real one is built.
// Watches the Outfit Agent's sessions on Claude Platform and answers its custom
// tool calls, so sessions started in the Claude Console don't hang.
//
// - One local test user. State: data/local-runner/state.json (git-ignored).
// - The daily outfit is the only email. It is always written to data/local-runner/outbox/,
//   and also sent through Gmail SMTP when OUTFIT_SMTP_USER and OUTFIT_SMTP_APP_PASSWORD
//   are set (user environment variables; never stored in the repo).
// - Talks to Claude Platform through the `ant` CLI and its signed-in profile.
//
// - Only one runner at a time (a named mutex), so two copies never answer the same call.
// - Everything it prints also goes to data/local-runner/runner.log.
//
// Run:  dotnet run --project tools/LocalToolRunner        (Ctrl+C to stop)
//       dotnet run --project tools/LocalToolRunner -- --reset   (start over as a new user)
// Auto-start: a shortcut in the Windows Startup folder runs the built exe at sign-in (SPEC.md §14).

using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\OutfitAgentToolRunner", out var isOnlyRunner);
if (!isOnlyRunner)
{
    Console.WriteLine("Another Outfit Agent tool runner is already running on this PC. Exiting.");
    return;
}

var repoRoot = FindRepoRoot();
var agentId = ReadAgentId(repoRoot);
var stateDir = Path.Combine(repoRoot, "data", "local-runner");
Directory.CreateDirectory(stateDir);

Console.OutputEncoding = Encoding.UTF8;
var log = new StreamWriter(Path.Combine(stateDir, "runner.log"), append: true, Encoding.UTF8) { AutoFlush = true };
Console.SetOut(new TeeWriter(Console.Out, log));
Console.WriteLine($"=== started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

var store = new Store(stateDir, reset: args.Contains("--reset"));
var ant = new Ant();
var tools = new Tools(store);

Console.WriteLine($"Local tool runner for {agentId}");
Console.WriteLine($"State and outbox: {stateDir}");
Console.WriteLine(Mailer.Configured
    ? $"Email: ON, daily outfits are sent from {Mailer.From} via Gmail SMTP"
    : "Email: OFF (set OUTFIT_SMTP_USER and OUTFIT_SMTP_APP_PASSWORD, then restart). Outfits go to the outbox only.");
Console.WriteLine("Start a session for this agent in the Claude Console and chat with it. Ctrl+C to stop.\n");

while (true)
{
    try
    {
        foreach (var sid in ant.IdleSessions(agentId))
        {
            var events = ant.Events(sid);
            var answered = events.Where(e => Str(e, "type") == "user.custom_tool_result")
                                 .Select(e => Str(e, "custom_tool_use_id")).ToHashSet();
            var pending = events.Where(e => Str(e, "type") == "agent.custom_tool_use" && !answered.Contains(Str(e, "id")));
            var date = SessionDate(events);
            foreach (var call in pending)
            {
                var name = Str(call, "name");
                var input = call["input"] as JsonObject ?? new JsonObject();
                JsonNode result; bool isError;
                try { (result, isError) = tools.Run(name, input, date); }
                catch (Exception ex) { (result, isError) = (new JsonObject { ["error"] = ex.Message }, true); }
                ant.SendToolResult(sid, Str(call, "id"), result.ToJsonString(), isError);
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {sid[^6..]} {name} -> {(isError ? "ERROR " + result.ToJsonString() : "ok")}");
            }
        }
    }
    catch (Exception ex) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} warning: {ex.Message}"); }
    Thread.Sleep(2000);
}

static string Str(JsonNode? n, string key) => n?[key]?.GetValue<string>() ?? "";

// The date the run is for: "date: YYYY-MM-DD" in the first user message, else today in Israel.
static string SessionDate(List<JsonObject> events)
{
    var first = events.FirstOrDefault(e => Str(e, "type") == "user.message");
    var text = first?["content"]?.AsArray().Select(c => c?["text"]?.GetValue<string>()).FirstOrDefault() ?? "";
    var m = Regex.Match(text, @"date:\s*(\d{4}-\d{2}-\d{2})");
    if (m.Success) return m.Groups[1].Value;
    var il = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time"));
    return il.ToString("yyyy-MM-dd");
}

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "claude-lock.json"))) return d.FullName;
    throw new InvalidOperationException("claude-lock.json not found; run from inside the repo.");
}

static string ReadAgentId(string root)
{
    var lockFile = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "claude-lock.json")))!;
    return lockFile["resources"]!["./agents/outfit-agent.md"]!["id"]!.GetValue<string>();
}

/// Writes console output to the screen and the log file.
class TeeWriter(TextWriter screen, TextWriter file) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { screen.Write(value); file.Write(value); }
    public override void Write(string? value) { screen.Write(value); file.Write(value); }
    public override void WriteLine(string? value) { screen.WriteLine(value); file.WriteLine(value); }
    public override void Flush() { screen.Flush(); file.Flush(); }
}

/// Calls Claude Platform through the `ant` CLI.
class Ant
{
    readonly string _exe = FindAnt();

    static string FindAnt()
    {
        var env = Environment.GetEnvironmentVariable("ANT_PATH");
        if (!string.IsNullOrEmpty(env)) return env;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin", "ant.exe");
        return File.Exists(home) ? home : "ant";
    }

    string Run(string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ant {args[0]} failed: {error.Trim()}");
        return output;
    }

    static IEnumerable<JsonObject> Lines(string jsonl) =>
        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(l => JsonNode.Parse(l)!.AsObject());

    public IEnumerable<string> IdleSessions(string agentId) =>
        Lines(Run(null, "beta:sessions", "list", "--max-items", "25", "--format", "jsonl",
                  "--transform", "{id,status,agent:agent.id}"))
            .Where(s => s["agent"]?.GetValue<string>() == agentId && s["status"]?.GetValue<string>() == "idle")
            .Select(s => s["id"]!.GetValue<string>());

    public List<JsonObject> Events(string sessionId) =>
        Lines(Run(null, "beta:sessions:events", "list", "--session-id", sessionId, "--format", "jsonl")).ToList();

    public void SendToolResult(string sessionId, string toolUseId, string text, bool isError)
    {
        var body = new JsonObject
        {
            ["events"] = new JsonArray(new JsonObject
            {
                ["type"] = "user.custom_tool_result",
                ["custom_tool_use_id"] = toolUseId,
                ["is_error"] = isError,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            }),
        };
        Run(body.ToJsonString(), "beta:sessions:events", "send", "--session-id", sessionId);
    }
}

/// Sends the daily outfit through Gmail SMTP (smtp.gmail.com:587, STARTTLS, app password).
static class Mailer
{
    public static readonly string? From = Env("OUTFIT_SMTP_USER")?.Trim();
    // Gmail shows app passwords in groups of four; copying can add line breaks or other whitespace.
    static readonly string? Password = Env("OUTFIT_SMTP_APP_PASSWORD") is { } p ? Regex.Replace(p, @"\s", "") : null;

    // Process environment first; on Windows also the saved user variables, which
    // processes started before the variable was set don't inherit.
    static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        : null;
    public static bool Configured => !string.IsNullOrWhiteSpace(From) && !string.IsNullOrWhiteSpace(Password);

    public static void Send(string to, string subject, string body)
    {
        using var smtp = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(From, Password),
        };
        using var msg = new MailMessage(new MailAddress(From!, "Outfit Agent"), new MailAddress(to))
        {
            Subject = subject, Body = body, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8,
        };
        smtp.Send(msg);
    }
}

/// The local test user's memory, saved as one JSON file.
class Store
{
    readonly string _file;
    public readonly string Outbox;
    public JsonObject Root { get; private set; }

    public Store(string dir, bool reset)
    {
        Directory.CreateDirectory(dir);
        Outbox = Path.Combine(dir, "outbox");
        Directory.CreateDirectory(Outbox);
        _file = Path.Combine(dir, "state.json");
        Root = !reset && File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file))!.AsObject() : NewUser();
        Save();
    }

    static JsonObject NewUser() => new()
    {
        ["profile"] = new JsonObject
        {
            ["email"] = null, ["name"] = null, ["language"] = null, ["city"] = null,
            ["latitude"] = null, ["longitude"] = null, ["timezone"] = "Asia/Jerusalem",
            ["lifestyle"] = null, ["commute"] = null, ["temp_sensitivity"] = "neutral",
            ["onboarding_status"] = "new",
        },
        ["preferences"] = new JsonArray(), ["wardrobe"] = new JsonArray(),
        ["outfits"] = new JsonArray(), ["next_id"] = 1,
    };

    public JsonObject Profile => Root["profile"]!.AsObject();
    public JsonArray Preferences => Root["preferences"]!.AsArray();
    public JsonArray Wardrobe => Root["wardrobe"]!.AsArray();
    public JsonArray Outfits => Root["outfits"]!.AsArray();
    public int NextId() { var id = Root["next_id"]!.GetValue<int>(); Root["next_id"] = id + 1; return id; }
    public void Save() => File.WriteAllText(_file, Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// Writes the email to the outbox, then sends it if SMTP is configured. Returns a status line.
    public string WriteEmail(string subject, string body)
    {
        var to = Profile["email"]?.GetValue<string>() ?? "(no email on file)";
        var n = Directory.GetFiles(Outbox).Length + 1;
        var slug = Regex.Replace(subject.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var path = Path.Combine(Outbox, $"{n:000}-{(slug.Length > 40 ? slug[..40] : slug)}.txt");
        File.WriteAllText(path, $"To: {to}\nSubject: {subject}\n\n{body}\n");
        string status;
        if (!Mailer.Configured) status = "not sent: email delivery isn't switched on yet (saved to the outbox)";
        else
        {
            try { Mailer.Send(to, subject, body); status = "sent by email"; }
            catch (Exception ex) { status = $"saved to the outbox, but sending failed: {ex.Message}"; }
        }
        Console.WriteLine($"\n--- daily outfit email: {status} ({Path.GetFileName(path)}) ---\nSubject: {subject}\n\n{body}\n---\n");
        return status;
    }
}

/// The custom tools declared in agents/outfit-agent.md.
class Tools(Store store)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly string[] NoEmailTools = ["get_user_profile", "set_user_email"];
    static readonly string[] ActiveOnlyTools = ["get_weather", "submit_outfit"];
    static readonly string[] Topics =
        ["name_and_language", "city", "typical_day", "commute", "style", "colours", "fit", "never_wear",
         "temperature", "shoes", "accessories", "requirements", "weekend", "clothes_list"];
    static readonly string[] Categories = ["top", "bottom", "one_piece", "outerwear", "shoes", "accessory"];

    JsonObject P => store.Profile;
    static string TodayInIsrael() =>
        TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time")).ToString("yyyy-MM-dd");
    string Status => P["onboarding_status"]!.GetValue<string>();

    static (JsonNode, bool) Ok(JsonNode n) => (n, false);
    static (JsonNode, bool) Err(string message) => (new JsonObject { ["error"] = message }, true);
    static string? S(JsonObject o, string k) => o[k]?.GetValue<string>();

    public (JsonNode result, bool isError) Run(string name, JsonObject input, string date)
    {
        if (P["email"] == null && !NoEmailTools.Contains(name))
            return Err("No email on file yet. Ask for it in the setup chat and save it with set_user_email first.");
        if (Status == "new" && ActiveOnlyTools.Contains(name))
            return Err("Not available until setup is complete (onboarding_status is new).");
        var r = name switch
        {
            "get_user_profile" => Ok(ProfileView()),
            "set_user_email" => SetEmail(input),
            "complete_onboarding" => CompleteOnboarding(input),
            "update_user_profile" => UpdateProfile(input),
            "geocode_city" => Geocode(input),
            "list_preferences" => Ok(Filter(store.Preferences, "category", S(input, "category"))),
            "upsert_preference" => UpsertPreference(input),
            "remove_preference" => RemoveById(store.Preferences, input),
            "list_wardrobe" => Ok(ListWardrobe(input)),
            "add_wardrobe_item" => AddItem(input),
            "update_wardrobe_item" => UpdateItem(input),
            "get_recent_outfits" => Ok(RecentOutfits(input, date)),
            "record_feedback" => RecordFeedback(input),
            "get_weather" => Weather(input),
            "submit_outfit" => SubmitOutfit(input, date),
            _ => Err($"Unknown tool {name}"),
        };
        store.Save();
        return r;
    }

    JsonObject ProfileView()
    {
        var view = P.DeepClone().AsObject();
        view.Remove("email");
        view.Remove("onboarding_answers");
        view["email_on_file"] = P["email"] != null;
        view["today"] = TodayInIsrael();
        return view;
    }

    (JsonNode, bool) SetEmail(JsonObject input)
    {
        if (P["email"] != null) return Err("An email is already on file; it can't be changed by the agent.");
        var email = (S(input, "email") ?? "").Trim();
        if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")) return Err("That doesn't look like a valid email address.");
        P["email"] = email;
        return Ok(new JsonObject { ["saved"] = true });
    }

    (JsonNode, bool) CompleteOnboarding(JsonObject input)
    {
        if (Status != "new") return Err($"Setup is already complete (onboarding_status is {Status}).");
        var answers = input["answers"] as JsonObject ?? new JsonObject();
        var missing = Topics.Where(t => string.IsNullOrWhiteSpace(S(answers, t))).ToList();
        if (missing.Count > 0) return Err("Still missing answers for: " + string.Join(", ", missing) + ". Ask about them first.");
        var items = store.Wardrobe.Select(w => w!.AsObject()).Where(w => S(w, "status") != "removed").ToList();
        var active = items.Count;
        var outer = items.Where(w => S(w, "category") == "outerwear").ToList();
        var gaps = new List<string>();
        if (active < 25) gaps.Add($"only {active} items (a starting wardrobe needs 25-40)");
        if (!outer.Any(w => w["warmth_level"]?.GetValue<int>() >= 4)) gaps.Add("no warm outer layer (warmth_level 4-5)");
        if (!outer.Any(w => w["waterproof"]?.GetValue<bool>() == true)) gaps.Add("no waterproof outer layer");
        if (gaps.Count > 0)
            return Err($"The wardrobe isn't complete yet: {string.Join("; ", gaps)}. Infer the missing typical items for their style and climate with add_wardrobe_item (status 'assumed', source 'inferred'), show the person the added items marked as your guesses, apply their corrections, then call complete_onboarding again.");
        P["onboarding_answers"] = answers.DeepClone();
        P["onboarding_status"] = "active";
        var dailyEmail = Mailer.Configured
            ? "Daily outfit emails start tomorrow morning at about 07:00 Israel time."
            : "Daily outfit emails aren't switched on yet. The person will start getting them once email delivery is set up; don't promise a date.";
        Console.WriteLine($"\n--- setup complete: {store.Preferences.Count} preferences, {active} wardrobe items. Email {(Mailer.Configured ? "ON" : "OFF")} ---\n");
        return Ok(new JsonObject { ["ok"] = true, ["onboarding_status"] = "active", ["wardrobe_items"] = active, ["daily_email"] = dailyEmail });
    }

    (JsonNode, bool) UpdateProfile(JsonObject input)
    {
        foreach (var k in new[] { "name", "language", "lifestyle", "commute", "temp_sensitivity" })
            if (input[k] != null) P[k] = input[k]!.DeepClone();
        return Ok(new JsonObject { ["ok"] = true });
    }

    (JsonNode, bool) Geocode(JsonObject input)
    {
        var city = S(input, "name") ?? "";
        var json = Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}").Result;
        var hit = JsonNode.Parse(json)?["results"]?[0];
        if (hit == null) return Err($"No place found for '{city}'. Ask the user for a nearby city.");
        P["city"] = $"{hit["name"]}, {hit["country"]}";
        P["latitude"] = hit["latitude"]!.DeepClone();
        P["longitude"] = hit["longitude"]!.DeepClone();
        P["timezone"] = hit["timezone"]?.DeepClone() ?? "Asia/Jerusalem";
        return Ok(new JsonObject { ["ok"] = true, ["city"] = P["city"]!.DeepClone(), ["latitude"] = P["latitude"]!.DeepClone(), ["longitude"] = P["longitude"]!.DeepClone(), ["timezone"] = P["timezone"]!.DeepClone() });
    }

    static JsonArray Filter(JsonArray items, string key, string? value) =>
        new(items.Where(i => value == null || S(i!.AsObject(), key) == value).Select(i => i!.DeepClone()).ToArray());

    (JsonNode, bool) UpsertPreference(JsonObject input)
    {
        var existing = store.Preferences.FirstOrDefault(p => S(p!.AsObject(), "category") == S(input, "category") && S(p!.AsObject(), "key") == S(input, "key"));
        if (existing != null) store.Preferences.Remove(existing);
        var pref = input.DeepClone().AsObject();
        pref["id"] = existing?["id"]?.GetValue<int>() ?? store.NextId();
        store.Preferences.Add(pref);
        return Ok(new JsonObject { ["ok"] = true, ["id"] = pref["id"]!.DeepClone() });
    }

    static (JsonNode, bool) RemoveById(JsonArray items, JsonObject input)
    {
        var id = input["id"]?.GetValue<int>();
        var item = items.FirstOrDefault(i => i?["id"]?.GetValue<int>() == id);
        if (item == null) return Err($"No entry with id {id}.");
        items.Remove(item);
        return Ok(new JsonObject { ["ok"] = true });
    }

    JsonArray ListWardrobe(JsonObject input)
    {
        var status = S(input, "status");
        var cat = S(input, "category");
        return new(store.Wardrobe.Select(w => w!.AsObject())
            .Where(w => (cat == null || S(w, "category") == cat)
                     && (status == null ? S(w, "status") != "removed" : S(w, "status") == status))
            .Select(w => w.DeepClone()).ToArray());
    }

    (JsonNode, bool) AddItem(JsonObject input)
    {
        var item = input.DeepClone().AsObject();
        item["id"] = store.NextId();
        item["last_worn_date"] = null;
        item["waterproof"] ??= false;
        store.Wardrobe.Add(item);
        return Ok(new JsonObject { ["ok"] = true, ["id"] = item["id"]!.DeepClone() });
    }

    (JsonNode, bool) UpdateItem(JsonObject input)
    {
        var id = input["id"]?.GetValue<int>();
        var item = store.Wardrobe.FirstOrDefault(w => w?["id"]?.GetValue<int>() == id)?.AsObject();
        if (item == null) return Err($"No wardrobe item with id {id}.");
        foreach (var (k, v) in input) if (k != "id") item[k] = v?.DeepClone();
        return Ok(new JsonObject { ["ok"] = true });
    }

    JsonArray RecentOutfits(JsonObject input, string date)
    {
        var days = input["days"]?.GetValue<int>() ?? 14;
        var since = DateOnly.Parse(date).AddDays(-days);
        return new(store.Outfits.Select(o => o!.AsObject())
            .Where(o => DateOnly.Parse(S(o, "local_date")!) >= since)
            .Select(o => o.DeepClone()).ToArray());
    }

    (JsonNode, bool) RecordFeedback(JsonObject input)
    {
        var id = input["suggestion_id"]?.GetValue<int>();
        var outfit = store.Outfits.FirstOrDefault(o => o?["suggestion_id"]?.GetValue<int>() == id)?.AsObject();
        if (outfit == null) return Err($"No outfit suggestion with id {id}.");
        outfit["feedback"]!.AsArray().Add(new JsonObject { ["rating"] = input["rating"]?.DeepClone(), ["comment"] = input["comment"]?.DeepClone(), ["source"] = "reply" });
        return Ok(new JsonObject { ["ok"] = true });
    }

    (JsonNode, bool) Weather(JsonObject input)
    {
        if (P["latitude"] == null) return Err("No location stored yet.");
        var date = S(input, "date");
        var url = "https://api.open-meteo.com/v1/forecast?" +
                  $"latitude={P["latitude"]}&longitude={P["longitude"]}&timezone={Uri.EscapeDataString(S(P, "timezone") ?? "Asia/Jerusalem")}" +
                  $"&start_date={date}&end_date={date}" +
                  "&hourly=temperature_2m,apparent_temperature,precipitation_probability,precipitation,wind_speed_10m,relative_humidity_2m,uv_index" +
                  "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,uv_index_max,sunrise,sunset";
        var raw = JsonNode.Parse(Http.GetStringAsync(url).Result)!;
        var hourly = raw["hourly"]!.AsObject();
        var slim = new JsonObject();
        foreach (var (k, v) in hourly) slim[k] = new JsonArray(v!.AsArray().Skip(7).Take(17).Select(x => x?.DeepClone()).ToArray());
        return Ok(new JsonObject { ["date"] = date, ["location"] = P["city"]?.DeepClone(), ["hours_07_to_23"] = slim, ["daily"] = raw["daily"]!.DeepClone() });
    }

    (JsonNode, bool) SubmitOutfit(JsonObject input, string date)
    {
        if (Status != "active") return Err($"Onboarding isn't finished (onboarding_status is {Status}); don't pick an outfit yet.");
        if (store.Outfits.Any(o => S(o!.AsObject(), "local_date") == date))
            return Err($"An outfit for {date} was already sent. Only one outfit email per day.");
        var problems = new List<string>();
        var picked = input["items"]?.AsArray() ?? [];
        var neverWear = store.Preferences.Where(p => S(p!.AsObject(), "category") == "never_wear").Select(p => S(p!.AsObject(), "key")!.ToLowerInvariant()).ToList();
        var names = new List<string>();
        foreach (var it in picked)
        {
            var id = it?["item_id"]?.GetValue<int>();
            var w = store.Wardrobe.FirstOrDefault(x => x?["id"]?.GetValue<int>() == id)?.AsObject();
            if (w == null) { problems.Add($"item_id {id} is not in the wardrobe"); continue; }
            if (S(w, "status") == "removed") problems.Add($"item_id {id} ({S(w, "name")}) was removed by the user");
            var text = $"{S(w, "name")} {S(w, "subtype")}".ToLowerInvariant();
            foreach (var nw in neverWear.Where(nw => text.Contains(nw))) problems.Add($"item_id {id} ({S(w, "name")}) matches never_wear '{nw}'");
            names.Add($"{it?["layer"]}: {S(w, "name")}{(it?["note"] is { } note ? $" ({note})" : "")}");
        }
        var layers = picked.Select(i => i?["layer"]?.GetValue<string>()).ToList();
        if (!layers.Contains("bottom")) problems.Add("no bottom layer");
        if (!layers.Contains("shoes")) problems.Add("no shoes");
        if (problems.Count > 0) return (new JsonObject { ["accepted"] = false, ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p!).ToArray()) }, true);

        var sid = store.NextId();
        store.Outfits.Add(new JsonObject
        {
            ["suggestion_id"] = sid, ["local_date"] = date, ["headline"] = input["headline"]?.DeepClone(),
            ["items"] = input["items"]!.DeepClone(), ["feedback"] = new JsonArray(),
        });
        foreach (var it in picked)
            store.Wardrobe.FirstOrDefault(x => x?["id"]?.GetValue<int>() == it?["item_id"]?.GetValue<int>())!["last_worn_date"] = date;
        var tips = string.Join("\n", (input["weather_tips"]?.AsArray() ?? []).Select(t => $"- {t}"));
        var greeting = P["name"] is { } n ? $"Good morning, {n}!" : "Good morning!";
        var swap = input["alternative"] is JsonObject alt ? $"\nIf it's different from the forecast: {alt["when"]}" : "";
        var status = store.WriteEmail($"{S(input, "headline")} [outfit#{sid}]",
            $"{greeting} Here's your outfit for {date}:\n\n{string.Join("\n", names)}\n\nWhy this works: {S(input, "reasoning")}\n\nTips:\n{tips}{swap}\n\nWant changes? Tell me in your next chat with me on Claude Platform.");
        return Ok(new JsonObject { ["accepted"] = true, ["suggestion_id"] = sid, ["email"] = status });
    }
}
