using System.Text.Json.Nodes;

// One full-fight budget. A reservation survives errors/cancellation.
// No long-context header is used: default per-request cap is 64k tokens.
internal sealed class PaidBudget
{
    const double Rate = .042 / 1_000_000;
    const double Reserve = 65536 * Rate;
    readonly string path;
    readonly JsonObject ledger;
    JsonObject? pending;
    PaidBudget(string path, JsonObject ledger) { this.path = path; this.ledger = ledger; }
    internal static PaidBudget? Open()
    {
        var path = Environment.GetEnvironmentVariable("JEV_TRIAL_BUDGET_FILE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        double limit = data["limit_usd"]!.GetValue<double>();
        double spent = data["spent_upper_usd"]!.GetValue<double>();
        if (!double.IsFinite(limit) || limit <= 0 || limit > 1.0 || !double.IsFinite(spent) || spent < 0 || spent > limit || data["fight_started"] != null)
            throw new InvalidOperationException("Invalid or already used fight budget");
        if (data["calls"]!.AsArray().Any(x => x?["status"]?.GetValue<string>() != "complete"))
            throw new InvalidOperationException("Prior budget request incomplete");
        // Exclusive creation claims the ledger even if this process exits before Start.
        // Never reuse a ledger: give each independently authorized trial a new output folder.
        using (File.Open(path + ".claim", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return new(path, data);
    }
    internal void Start()
    {
        ledger["fight_started"] = DateTimeOffset.UtcNow.ToString("O"); Save();
    }
    internal bool Begin()
    {
        double spent = ledger["spent_upper_usd"]!.GetValue<double>();
        if (pending != null) throw new InvalidOperationException("Unsettled request");
        if (spent + Reserve > ledger["limit_usd"]!.GetValue<double>()) return false;
        pending = new JsonObject { ["phase"] = "fight", ["status"] = "pending", ["reserved_usd"] = Reserve };
        ledger["calls"]!.AsArray().Add(pending);
        ledger["spent_upper_usd"] = spent + Reserve; Save(); return true;
    }
    internal void Complete(JsonObject result)
    {
        int tokens = result["usage"]?["input_tokens"]?.GetValue<int>() ?? -1;
        if (tokens < 0 || tokens > 65536 || result["model"]?.GetValue<string>() != "jev-1.13.0")
            throw new InvalidOperationException("Unexpected model or missing token usage; stop with reservation retained");
        double cost = tokens * Rate;
        pending!["status"] = "complete"; pending["input_tokens"] = tokens; pending["cost_usd"] = cost;
        ledger["spent_upper_usd"] = ledger["spent_upper_usd"]!.GetValue<double>() - Reserve + cost;
        pending = null; Save();
    }
    void Save() => Controller.AtomicWrite(path, ledger);
    internal static void SelfTest()
    {
        string path = Path.Combine(Path.GetTempPath(), "jev-budget-" + Guid.NewGuid() + ".json");
        string? previous = Environment.GetEnvironmentVariable("JEV_TRIAL_BUDGET_FILE");
        try
        {
            Environment.SetEnvironmentVariable("JEV_TRIAL_BUDGET_FILE", path);
            var data = new JsonObject { ["limit_usd"] = 1.0, ["spent_upper_usd"] = .996, ["calls"] = new JsonArray() };
            Controller.AtomicWrite(path, data);
            var budget = Open()!;
            bool secondClaimRejected = false;
            try { Open(); } catch (IOException) { secondClaimRejected = true; }
            if (!secondClaimRejected) throw new Exception("Concurrent ledger claim accepted");
            budget.Start();
            bool rejected = false;
            try { Open(); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !budget.Begin()) throw new Exception("Reuse/reservation failure");
            double held = JsonNode.Parse(File.ReadAllText(path))!["spent_upper_usd"]!.GetValue<double>();
            if (held > 1.0 || held < .998) throw new Exception("Reservation not persisted");
            rejected = false;
            try { budget.Complete(new JsonObject()); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Unknown usage must retain reservation");
            budget.Complete(new JsonObject { ["model"] = "jev-1.13.0", ["usage"] = new JsonObject { ["input_tokens"] = 5000 } });
            int calls = 1;
            while (budget.Begin())
            {
                budget.Complete(new JsonObject { ["model"] = "jev-1.13.0", ["usage"] = new JsonObject { ["input_tokens"] = 5000 } });
                if (++calls > 30) throw new Exception("Budget failed to stop");
            }
            double spent = JsonNode.Parse(File.ReadAllText(path))!["spent_upper_usd"]!.GetValue<double>();
            if (spent > 1.0 || spent + Reserve <= 1.0) throw new Exception("Budget boundary wrong");
            File.Delete(path + ".claim");
            Controller.AtomicWrite(path, new JsonObject { ["limit_usd"] = .003, ["spent_upper_usd"] = 0, ["calls"] = new JsonArray() });
            var small = Open()!;
            small.Start();
            if (!small.Begin()) throw new Exception("Small valid budget refused");
            small.Complete(new JsonObject { ["model"] = "jev-1.13.0", ["usage"] = new JsonObject { ["input_tokens"] = 65536 } });
            if (small.Begin()) throw new Exception("Small budget exceeded");
            File.Delete(path + ".claim");
            foreach (double invalid in new[] { 0.0, -1.0, 1.01 })
            {
                Controller.AtomicWrite(path, new JsonObject { ["limit_usd"] = invalid, ["spent_upper_usd"] = 0, ["calls"] = new JsonArray() });
                bool invalidRejected = false;
                try { Open(); } catch (InvalidOperationException) { invalidRejected = true; }
                if (!invalidRejected || File.Exists(path + ".claim")) throw new Exception("Invalid limit accepted or claimed");
            }
            Console.WriteLine("PASS budget: reserves before HTTP, settles actual usage, retains unknown charge, blocks reuse/concurrent claims, validates limits, honors small caps; no network");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JEV_TRIAL_BUDGET_FILE", previous);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".claim")) File.Delete(path + ".claim");
        }
    }
}
