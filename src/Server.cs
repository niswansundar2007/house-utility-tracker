// =====================================================================
//  House Utility Tracker - BACKEND
//  - Tiny web server (HttpListener) on http://localhost:5050
//  - Stores everything in data\db.json (acts as the database)
//  - REST API used by the frontend (public\index.html)
//  - Background scheduler that sends WhatsApp reminders via CallMeBot
//  Build: run build.bat (uses the C# compiler that ships with Windows)
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// ---------------------------- Data models ----------------------------
public class Settings
{
    public string Phone { get; set; }            // e.g. +919876543210
    public string ApiKey { get; set; }           // CallMeBot API key
    public string Provider { get; set; }         // "callmebot" or "greenapi"
    public string GreenApiUrl { get; set; }      // Green-API "apiUrl", e.g. https://7103.api.greenapi.com
    public string GreenIdInstance { get; set; }  // Green-API "idInstance"
    public string GreenApiToken { get; set; }    // Green-API "apiTokenInstance"
    public int Members { get; set; }             // people in the house
    public double CylinderKg { get; set; }       // 14.2 kg domestic LPG by default
    public double KgPerPersonPerDay { get; set; }// normal usage per person
    public int GasReminderDays { get; set; }     // remind N days before gas runs out
    public int ElecCycleDays { get; set; }       // billing cycle (2 months = 60 days)
    public int ElecReminderDays { get; set; }    // remind N days before the cycle ends

    public Settings()
    {
        Phone = ""; ApiKey = ""; Provider = "callmebot";
        GreenApiUrl = "https://api.green-api.com"; GreenIdInstance = ""; GreenApiToken = "";
        Members = 4; CylinderKg = 14.2; KgPerPersonPerDay = 0.1;
        GasReminderDays = 5; ElecCycleDays = 60; ElecReminderDays = 7;
    }
}

public class GasEntry
{
    public string Id { get; set; }
    public string Date { get; set; }      // yyyy-MM-dd  (date the cylinder was received)
    public string Note { get; set; }
    public bool Notified { get; set; }
}

public class ElecEntry
{
    public string Id { get; set; }
    public string Date { get; set; }      // yyyy-MM-dd  (date the bill was generated)
    public double Amount { get; set; }
    public bool Paid { get; set; }
    public bool Notified { get; set; }
}

public class LogEntry
{
    public string Time { get; set; }
    public string Type { get; set; }
    public string Message { get; set; }
    public bool Ok { get; set; }
    public string Response { get; set; }
}

public class Database
{
    public Settings Settings { get; set; }
    public List<GasEntry> Gas { get; set; }
    public List<ElecEntry> Electricity { get; set; }
    public List<LogEntry> Log { get; set; }

    public Database()
    {
        Settings = new Settings();
        Gas = new List<GasEntry>();
        Electricity = new List<ElecEntry>();
        Log = new List<LogEntry>();
    }
}

// ------------------------------ Server -------------------------------
public static class Program
{
    static int Port = 5050;   // override with:  HouseUtility.exe --port=5051
    static readonly object Lock = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string BaseDir, PublicDir, DataFile;
    static Database Db;
    static Timer Scheduler;
    static string AppPassword;                 // env APP_PASSWORD: protects the site with a login prompt
    static readonly string GistId = Environment.GetEnvironmentVariable("GIST_ID");
    static readonly string GistToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    static bool UseGist { get { return !string.IsNullOrEmpty(GistId) && !string.IsNullOrEmpty(GistToken); } }

    public static void Main(string[] args)
    {
        // Required so HTTPS calls to CallMeBot work on older .NET Framework defaults (TLS 1.2)
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        foreach (string a in args)
        {
            int p;
            if (a.StartsWith("--port=") && int.TryParse(a.Substring(7), out p)) Port = p;
        }

        BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        PublicDir = Path.GetFullPath(Path.Combine(BaseDir, "public"));
        string dataDir = Path.Combine(BaseDir, "data");
        Directory.CreateDirectory(dataDir);
        DataFile = Path.Combine(dataDir, "db.json");
        LoadDb();

        // Cloud hosts (Render, etc.) pass the port in the PORT variable and need all interfaces
        string envPort = Environment.GetEnvironmentVariable("PORT");
        bool cloud = !string.IsNullOrEmpty(envPort);
        int ep;
        if (cloud && int.TryParse(envPort, out ep)) Port = ep;
        AppPassword = Environment.GetEnvironmentVariable("APP_PASSWORD");
        if (cloud && string.IsNullOrEmpty(AppPassword))
            Console.WriteLine("WARNING: APP_PASSWORD is not set - anyone with the URL can see and change your data.");

        var listener = new HttpListener();
        string url = "http://localhost:" + Port + "/";
        listener.Prefixes.Add(cloud ? "http://*:" + Port + "/" : url);
        try { listener.Start(); }
        catch (Exception ex)
        {
            Console.WriteLine("Could not start server on " + url + " : " + ex.Message);
            Console.WriteLine("Is the app already running? Press Enter to exit.");
            Console.ReadLine();
            return;
        }

        Console.WriteLine("==============================================");
        Console.WriteLine("  House Utility Tracker is running");
        Console.WriteLine("  Open: " + url);
        Console.WriteLine("  Database file: " + DataFile);
        Console.WriteLine("  Keep this window open for WhatsApp reminders.");
        Console.WriteLine("  Close this window to stop the app.");
        Console.WriteLine("==============================================");

        // Check reminders 10s after start and then every 30 minutes
        Scheduler = new Timer(delegate { SafeCheck(); }, null, 10000, 30 * 60 * 1000);

        if (!cloud && !args.Contains("--no-browser"))
        {
            try { Process.Start(url); } catch { }
        }

        while (true)
        {
            HttpListenerContext ctx = listener.GetContext();
            ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
        }
    }

    // --------------------------- Persistence ---------------------------
    // Optional cloud storage: keeps db.json in a private GitHub Gist so data survives
    // restarts on free hosts whose disk is wiped (set GIST_ID and GITHUB_TOKEN).
    static WebClient GistClient()
    {
        var wc = new WebClient();
        wc.Encoding = Encoding.UTF8;
        wc.Headers[HttpRequestHeader.UserAgent] = "house-utility-tracker";
        wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + GistToken;
        wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
        return wc;
    }

    static string GistRead()
    {
        using (var wc = GistClient())
        {
            var gist = Json.Deserialize<Dictionary<string, object>>(wc.DownloadString("https://api.github.com/gists/" + GistId));
            var files = gist["files"] as Dictionary<string, object>;
            if (files == null || !files.ContainsKey("db.json")) return null;
            var f = (Dictionary<string, object>)files["db.json"];
            string content = Convert.ToString(f["content"]);
            return string.IsNullOrWhiteSpace(content) || content.Trim() == "{}" ? null : content;
        }
    }

    static void GistWrite(string json)
    {
        var payload = new Dictionary<string, object> {
            { "files", new Dictionary<string, object> { { "db.json", new Dictionary<string, object> { { "content", json } } } } } };
        using (var wc = GistClient())
        {
            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            wc.UploadString("https://api.github.com/gists/" + GistId, "PATCH", Json.Serialize(payload));
        }
    }

    static void LoadDb()
    {
        lock (Lock)
        {
            if (UseGist)
            {
                try
                {
                    string remote = GistRead();
                    if (remote != null) File.WriteAllText(DataFile, remote, Encoding.UTF8);
                    Console.WriteLine("Loaded data from GitHub Gist " + GistId);
                }
                catch (Exception ex) { Console.WriteLine("Could not read GitHub Gist: " + ex.Message); }
            }
            if (File.Exists(DataFile))
            {
                try { Db = Json.Deserialize<Database>(File.ReadAllText(DataFile, Encoding.UTF8)); }
                catch (Exception ex)
                {
                    Console.WriteLine("db.json was unreadable, backing it up and starting fresh: " + ex.Message);
                    File.Copy(DataFile, DataFile + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
            }
            if (Db == null) Db = new Database();
            if (Db.Settings == null) Db.Settings = new Settings();
            if (Db.Gas == null) Db.Gas = new List<GasEntry>();
            if (Db.Electricity == null) Db.Electricity = new List<ElecEntry>();
            if (Db.Log == null) Db.Log = new List<LogEntry>();
            SaveDb();
        }
    }

    static void SaveDb()
    {
        lock (Lock)
        {
            string json = Json.Serialize(Db);
            string tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(DataFile)) File.Delete(DataFile);
            File.Move(tmp, DataFile);
            if (UseGist)
            {
                try { GistWrite(json); }
                catch (Exception ex) { Console.WriteLine("Could not save to GitHub Gist: " + ex.Message); }
            }
        }
    }

    // Login prompt (HTTP Basic auth) when APP_PASSWORD is set. Any username works.
    static bool Authorized(HttpListenerRequest req)
    {
        if (string.IsNullOrEmpty(AppPassword)) return true;
        string h = req.Headers["Authorization"];
        if (h == null || !h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h.Substring(6).Trim()));
            int i = decoded.IndexOf(':');
            return i >= 0 && decoded.Substring(i + 1) == AppPassword;
        }
        catch { return false; }
    }

    // ----------------------------- Helpers -----------------------------
    static DateTime ParseDate(string s)
    {
        return DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    static string Fmt(DateTime d) { return d.ToString("yyyy-MM-dd"); }
    static string Nice(DateTime d) { return d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture); }

    static GasEntry LatestGas()
    {
        return Db.Gas.OrderByDescending(g => g.Date).FirstOrDefault();
    }

    static ElecEntry LatestElec()
    {
        return Db.Electricity.OrderByDescending(e => e.Date).FirstOrDefault();
    }

    static int GasDaysPerCylinder(Settings s)
    {
        double perDay = Math.Max(1, s.Members) * Math.Max(0.01, s.KgPerPersonPerDay);
        return (int)Math.Floor(s.CylinderKg / perDay);
    }

    // Builds the full status object that the frontend shows
    static Dictionary<string, object> BuildStatus()
    {
        lock (Lock)
        {
            var s = Db.Settings;
            DateTime today = DateTime.Today;
            var result = new Dictionary<string, object>();
            result["settings"] = s;
            result["gas"] = Db.Gas.OrderByDescending(g => g.Date).ToList();
            result["electricity"] = Db.Electricity.OrderByDescending(e => e.Date).ToList();
            result["log"] = Enumerable.Reverse(Db.Log).Take(30).ToList();
            result["today"] = Fmt(today);

            // ---- Gas status ----
            var gas = new Dictionary<string, object>();
            int daysPerCyl = GasDaysPerCylinder(s);
            gas["daysPerCylinder"] = daysPerCyl;
            var lg = LatestGas();
            if (lg != null)
            {
                DateTime last = ParseDate(lg.Date);
                DateTime end = last.AddDays(daysPerCyl);
                gas["lastDate"] = lg.Date;
                gas["expectedEnd"] = Fmt(end);
                gas["daysLeft"] = (end - today).Days;
                gas["daysUsed"] = (today - last).Days;
                gas["reminderOn"] = Fmt(end.AddDays(-s.GasReminderDays));
            }
            // Average real interval from your own history (if 2+ cylinders recorded)
            var dates = Db.Gas.Select(g => ParseDate(g.Date)).OrderBy(d => d).ToList();
            if (dates.Count >= 2)
            {
                double avg = 0;
                for (int i = 1; i < dates.Count; i++) avg += (dates[i] - dates[i - 1]).Days;
                gas["actualAvgDays"] = Math.Round(avg / (dates.Count - 1), 1);
            }
            result["gasStatus"] = gas;

            // ---- Electricity status ----
            var el = new Dictionary<string, object>();
            el["cycleDays"] = s.ElecCycleDays;
            var le = LatestElec();
            if (le != null)
            {
                DateTime last = ParseDate(le.Date);
                DateTime due = last.AddDays(s.ElecCycleDays);
                el["lastDate"] = le.Date;
                el["amount"] = le.Amount;
                el["paid"] = le.Paid;
                el["dueDate"] = Fmt(due);
                el["daysLeft"] = (due - today).Days;
                el["reminderOn"] = Fmt(due.AddDays(-s.ElecReminderDays));
            }
            result["elecStatus"] = el;
            return result;
        }
    }

    // ---------------------------- WhatsApp -----------------------------
    static bool IsGreen(Settings s) { return string.Equals(s.Provider, "greenapi", StringComparison.OrdinalIgnoreCase); }

    // Returns an error text if the chosen provider is not fully configured, else null
    static string MissingConfig(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.Phone)) return "WhatsApp number is missing. Save it in Settings first.";
        if (IsGreen(s))
        {
            if (string.IsNullOrWhiteSpace(s.GreenApiUrl) || string.IsNullOrWhiteSpace(s.GreenIdInstance) || string.IsNullOrWhiteSpace(s.GreenApiToken))
                return "Green-API apiUrl, idInstance or apiTokenInstance is missing. Save them in Settings first.";
        }
        else if (string.IsNullOrWhiteSpace(s.ApiKey)) return "CallMeBot API key is missing. Save it in Settings first.";
        return null;
    }

    static string Clean(string body)
    {
        string text = Regex.Replace(body ?? "", "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 300 ? text.Substring(0, 300) : text;
    }

    static LogEntry SendWhatsApp(string type, string message)
    {
        Settings s;
        lock (Lock) { s = Db.Settings; }
        var log = new LogEntry { Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Type = type + (IsGreen(s) ? " (Green-API)" : " (CallMeBot)"), Message = message };

        string missing = MissingConfig(s);
        if (missing != null)
        {
            log.Ok = false;
            log.Response = missing;
        }
        else if (IsGreen(s))
        {
            // Green-API: POST {apiUrl}/waInstance{idInstance}/sendMessage/{apiTokenInstance}
            string digits = Regex.Replace(s.Phone, @"\D", "");
            string url = s.GreenApiUrl.Trim().TrimEnd('/') + "/waInstance" + s.GreenIdInstance.Trim()
                       + "/sendMessage/" + s.GreenApiToken.Trim();
            string payload = Json.Serialize(new Dictionary<string, object> { { "chatId", digits + "@c.us" }, { "message", message } });
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    string body = wc.UploadString(url, "POST", payload);
                    log.Ok = body.Contains("idMessage");
                    log.Response = log.Ok ? "Message accepted by Green-API (" + Clean(body) + ")" : Clean(body);
                }
            }
            catch (WebException ex)
            {
                string detail = ex.Message;
                if (ex.Response != null)
                    using (var r = new StreamReader(ex.Response.GetResponseStream())) detail += " - " + Clean(r.ReadToEnd());
                log.Ok = false;
                log.Response = Clean(detail);
            }
        }
        else
        {
            // CallMeBot (free): https://www.callmebot.com/blog/free-api-whatsapp-messages/
            string url = "https://api.callmebot.com/whatsapp.php?phone=" + Uri.EscapeDataString(s.Phone.Trim())
                       + "&text=" + Uri.EscapeDataString(message)
                       + "&apikey=" + Uri.EscapeDataString(s.ApiKey.Trim());
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    string body = wc.DownloadString(url);
                    string text = Regex.Replace(body, "<[^>]+>", " ");
                    text = Regex.Replace(text, @"\s+", " ").Trim();
                    if (text.Length > 300) text = text.Substring(0, 300);
                    string low = text.ToLowerInvariant();
                    log.Ok = (low.Contains("queued") || low.Contains("sent")) && !low.Contains("error") && !low.Contains("invalid");
                    log.Response = text;
                }
            }
            catch (WebException ex)
            {
                string detail = ex.Message;
                if (ex.Response != null)
                {
                    using (var r = new StreamReader(ex.Response.GetResponseStream()))
                        detail += " - " + Regex.Replace(Regex.Replace(r.ReadToEnd(), "<[^>]+>", " "), @"\s+", " ").Trim();
                }
                log.Ok = false;
                log.Response = detail.Length > 300 ? detail.Substring(0, 300) : detail;
            }
        }

        lock (Lock)
        {
            Db.Log.Add(log);
            if (Db.Log.Count > 200) Db.Log.RemoveRange(0, Db.Log.Count - 200);
            SaveDb();
        }
        Console.WriteLine("[" + log.Time + "] WhatsApp " + type + " -> " + (log.Ok ? "OK" : "FAILED") + " : " + log.Response);
        return log;
    }

    // ---------------------------- Scheduler ----------------------------
    static void SafeCheck()
    {
        try { CheckReminders(); } catch (Exception ex) { Console.WriteLine("Reminder check failed: " + ex.Message); }
    }

    static List<LogEntry> CheckReminders()
    {
        var sent = new List<LogEntry>();
        Settings s; GasEntry lg; ElecEntry le;
        lock (Lock) { s = Db.Settings; lg = LatestGas(); le = LatestElec(); }
        if (MissingConfig(s) != null) return sent;
        DateTime today = DateTime.Today;

        if (lg != null && !lg.Notified)
        {
            DateTime end = ParseDate(lg.Date).AddDays(GasDaysPerCylinder(s));
            int left = (end - today).Days;
            if (left <= s.GasReminderDays)
            {
                string msg = left >= 0
                    ? "GAS REMINDER: Your LPG cylinder (received " + Nice(ParseDate(lg.Date)) + ") is expected to run out on "
                      + Nice(end) + " (" + left + " day(s) left) for " + s.Members + " members. Please book a refill."
                    : "GAS ALERT: Your LPG cylinder was expected to run out on " + Nice(end) + ". Please book a refill now.";
                var log = SendWhatsApp("gas", msg);
                sent.Add(log);
                if (log.Ok) lock (Lock) { lg.Notified = true; SaveDb(); }
            }
        }

        if (le != null && !le.Notified && !le.Paid)
        {
            DateTime due = ParseDate(le.Date).AddDays(s.ElecCycleDays);
            int left = (due - today).Days;
            if (left <= s.ElecReminderDays)
            {
                string msg = left >= 0
                    ? "ELECTRICITY REMINDER: Your last bill was on " + Nice(ParseDate(le.Date))
                      + (le.Amount > 0 ? " (Rs. " + le.Amount.ToString("0.##") + ")" : "")
                      + ". The 2-month period ends on " + Nice(due) + " (" + left + " day(s) left). Please pay the bill."
                    : "ELECTRICITY ALERT: Your billing period ended on " + Nice(due) + ". Please pay the bill now.";
                var log = SendWhatsApp("electricity", msg);
                sent.Add(log);
                if (log.Ok) lock (Lock) { le.Notified = true; SaveDb(); }
            }
        }
        return sent;
    }

    // ----------------------------- Routing -----------------------------
    static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            string path = req.Url.AbsolutePath.TrimEnd('/');
            if (path == "") path = "/";
            string method = req.HttpMethod.ToUpperInvariant();

            // Health check for the host / uptime pinger (no login needed, reveals nothing)
            if (path == "/healthz")
            {
                WriteJson(res, 200, new Dictionary<string, object> { { "ok", true } });
                return;
            }
            if (!Authorized(req))
            {
                res.AddHeader("WWW-Authenticate", "Basic realm=\"House Utility Tracker\", charset=\"UTF-8\"");
                WriteJson(res, 401, new Dictionary<string, object> { { "error", "Password required" } });
                return;
            }

            string body = "";
            if (req.HasEntityBody)
                using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();

            if (path.StartsWith("/api/"))
            {
                object result = Api(method, path, body);
                WriteJson(res, 200, result);
            }
            else ServeStatic(res, path);
        }
        catch (ArgumentException ex) { WriteJson(res, 400, new Dictionary<string, object> { { "error", ex.Message } }); }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
            try { WriteJson(res, 500, new Dictionary<string, object> { { "error", ex.Message } }); } catch { }
        }
    }

    static object Api(string method, string path, string body)
    {
        // GET /api/data  -> everything the UI needs
        if (method == "GET" && path == "/api/data") return BuildStatus();

        // POST /api/settings
        if (method == "POST" && path == "/api/settings")
        {
            var s = Json.Deserialize<Settings>(body);
            if (s.Members < 1) throw new ArgumentException("Members must be at least 1.");
            if (s.CylinderKg <= 0 || s.KgPerPersonPerDay <= 0) throw new ArgumentException("Cylinder size and usage must be positive.");
            if (s.ElecCycleDays < 1) throw new ArgumentException("Billing cycle must be at least 1 day.");
            s.Phone = (s.Phone ?? "").Trim().Replace(" ", "");
            s.ApiKey = (s.ApiKey ?? "").Trim();
            s.Provider = IsGreen(s) ? "greenapi" : "callmebot";
            s.GreenApiUrl = (s.GreenApiUrl ?? "").Trim();
            s.GreenIdInstance = (s.GreenIdInstance ?? "").Trim();
            s.GreenApiToken = (s.GreenApiToken ?? "").Trim();
            lock (Lock)
            {
                bool gasRuleChanged = s.Members != Db.Settings.Members || s.CylinderKg != Db.Settings.CylinderKg
                    || s.KgPerPersonPerDay != Db.Settings.KgPerPersonPerDay || s.GasReminderDays != Db.Settings.GasReminderDays;
                bool elecRuleChanged = s.ElecCycleDays != Db.Settings.ElecCycleDays || s.ElecReminderDays != Db.Settings.ElecReminderDays;
                Db.Settings = s;
                // If the rules changed, allow the latest reminder to be re-evaluated
                var lg = LatestGas(); if (gasRuleChanged && lg != null) lg.Notified = false;
                var le = LatestElec(); if (elecRuleChanged && le != null) le.Notified = false;
                SaveDb();
            }
            return BuildStatus();
        }

        // POST /api/gas  {date, note}
        if (method == "POST" && path == "/api/gas")
        {
            var g = Json.Deserialize<GasEntry>(body);
            ParseDateOrThrow(g.Date);
            g.Id = Guid.NewGuid().ToString("N");
            g.Notified = false;
            lock (Lock) { Db.Gas.Add(g); SaveDb(); }
            return BuildStatus();
        }

        // POST /api/electricity  {date, amount}
        if (method == "POST" && path == "/api/electricity")
        {
            var e = Json.Deserialize<ElecEntry>(body);
            ParseDateOrThrow(e.Date);
            e.Id = Guid.NewGuid().ToString("N");
            e.Notified = false;
            lock (Lock) { Db.Electricity.Add(e); SaveDb(); }
            return BuildStatus();
        }

        // POST /api/electricity/paid  {id}
        if (method == "POST" && path == "/api/electricity/paid")
        {
            string id = GetId(body);
            lock (Lock)
            {
                var e = Db.Electricity.FirstOrDefault(x => x.Id == id);
                if (e == null) throw new ArgumentException("Bill not found.");
                e.Paid = !e.Paid;
                SaveDb();
            }
            return BuildStatus();
        }

        // POST /api/gas/delete, /api/electricity/delete  {id}
        if (method == "POST" && path == "/api/gas/delete")
        {
            string id = GetId(body);
            lock (Lock) { Db.Gas.RemoveAll(x => x.Id == id); SaveDb(); }
            return BuildStatus();
        }
        if (method == "POST" && path == "/api/electricity/delete")
        {
            string id = GetId(body);
            lock (Lock) { Db.Electricity.RemoveAll(x => x.Id == id); SaveDb(); }
            return BuildStatus();
        }

        // POST /api/test-whatsapp  -> sends a test message right now
        if (method == "POST" && path == "/api/test-whatsapp")
        {
            var log = SendWhatsApp("test", "Test message from your House Utility Tracker. WhatsApp notifications are working!");
            return new Dictionary<string, object> { { "result", log }, { "status", BuildStatus() } };
        }

        // POST /api/check-now  -> run the reminder check immediately
        if (method == "POST" && path == "/api/check-now")
        {
            var sent = CheckReminders();
            return new Dictionary<string, object> { { "sent", sent }, { "status", BuildStatus() } };
        }

        throw new ArgumentException("Unknown endpoint: " + method + " " + path);
    }

    static void ParseDateOrThrow(string d)
    {
        DateTime tmp;
        if (d == null || !DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out tmp))
            throw new ArgumentException("Date must be in yyyy-MM-dd format.");
    }

    static string GetId(string body)
    {
        var d = Json.Deserialize<Dictionary<string, object>>(body);
        if (d == null || !d.ContainsKey("id")) throw new ArgumentException("id is required.");
        return Convert.ToString(d["id"]);
    }

    // --------------------------- Responses -----------------------------
    static void WriteJson(HttpListenerResponse res, int code, object data)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(data));
        res.StatusCode = code;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    static void ServeStatic(HttpListenerResponse res, string path)
    {
        if (path == "/") path = "/index.html";
        string full = Path.GetFullPath(Path.Combine(PublicDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(PublicDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            byte[] nf = Encoding.UTF8.GetBytes("Not found");
            res.StatusCode = 404;
            res.OutputStream.Write(nf, 0, nf.Length);
            res.OutputStream.Close();
            return;
        }
        string ext = Path.GetExtension(full).ToLowerInvariant();
        string type = ext == ".html" ? "text/html; charset=utf-8"
                    : ext == ".css" ? "text/css; charset=utf-8"
                    : ext == ".js" ? "application/javascript; charset=utf-8"
                    : ext == ".png" ? "image/png"
                    : ext == ".svg" ? "image/svg+xml"
                    : "application/octet-stream";
        byte[] bytes = File.ReadAllBytes(full);
        res.ContentType = type;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }
}
