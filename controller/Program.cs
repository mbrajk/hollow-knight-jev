using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

return await Controller.MainAsync(args);

internal static partial class Controller
{
    internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    internal static readonly Dictionary<string, Buttons> Actions = BuildActions();
    internal static readonly Dictionary<string, Buttons> Locomotion = new()
    {
        ["left"] = new(-1, 0, false, false), ["left_jump"] = new(-1, 0, true, false),
        ["still"] = new(0, 0, false, false), ["still_jump"] = new(0, 0, true, false),
        ["right"] = new(1, 0, false, false), ["right_jump"] = new(1, 0, true, false)
    };
    const string Endpoint = "https://api.typesafe.ai/v1/systemone";

    static Dictionary<string, Buttons> BuildActions()
    {
        var actions = new Dictionary<string, Buttons> { ["release"] = new(0, 0, false, false) };
        foreach (var (name, move) in new[] { ("left", -1), ("still", 0), ("right", 1) })
        {
            if (move != 0) actions[name] = new(move, 0, false, false);
            actions[name + "_jump"] = new(move, 0, true, false);
            actions[name + "_rejump"] = new(move, 0, true, false, true);
            if(move!=0) actions[name + "_dash"] = new(move, 0, false, false, false, true);
            foreach (var (aim, look) in new[] { ("forward", 0), ("up", 1), ("down", -1) })
            {
                actions[name + "_attack_" + aim] = new(move, look, false, true);
                actions[name + "_jump_attack_" + aim] = new(move, look, true, true);
                actions[name + "_rejump_attack_" + aim] = new(move, look, true, true, true);
                if(move!=0) actions[name + "_dash_attack_" + aim] = new(move, look, false, true, false, true);
            }
        }
        actions["look_up"] = new(0, 1, false, false);
        actions["look_down"] = new(0, -1, false, false);
        return actions;
    }

    internal static async Task<int> MainAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help") { Help(); return 0; }
            if (args[0] == "budget-self-test") { PaidBudget.SelfTest(); return 0; }
            if (args[0] == "forecast-replay") { foreach(var line in File.ReadLines(args[1])) { var e=JsonNode.Parse(line)!.AsObject(); var f=Forecast(e["observation"]!.AsObject()); f["sequence"]=e["sequence"]!.DeepClone(); Console.WriteLine(f.ToJsonString()); } return 0; }
            if (args[0] == "forecast") { var e = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject(); Console.WriteLine(Forecast(e["observation"]!.AsObject()).ToJsonString()); return 0; }
            if (args[0] == "self-test") { ForecastTests.Run(); GroupedTests.Run(); TargetedTests.Run(); return await Tests.RunAsync(); }
            var options = Options.Parse(args.Skip(1).ToArray(), args[0] == "watch" ? 3600 : 600);
            Directory.CreateDirectory(options.Output);
            if (args[0] == "decode")
            {
                var response = ReadState(options.Input ?? throw new ArgumentException("decode requires --input"));
                Console.WriteLine(new JsonObject { ["action"] = SelectedAction(response, options), ["groups"] = options.DecisionMode == "grouped" ? GroupedDecision(response) : null }.ToJsonString());
                return 0;
            }
            if (args[0] == "watch") { await Dashboard.RunAsync(options); return 0; }
            if (args[0] == "export") { await Dashboard.ExportAsync(options); return 0; }
            if (args[0] == "preview")
            {
                var state = ReadState(options.State);
                Console.WriteLine(Request(state, options).ToJsonString(Pretty));
                return 0;
            }
            if (args[0] == "replay")
            {
                if (options.Input == null) throw new ArgumentException("replay requires --input <JSONL file>");
                var destination = Path.Combine(options.Output, "replay-preview.jsonl");
                using var output = new StreamWriter(destination, false, new UTF8Encoding(false));
                int count = 0;
                var history = new List<JsonObject>();
                foreach (var line in File.ReadLines(options.Input).Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var item = JsonNode.Parse(line)!.AsObject();
                    var state = (item["snapshot"] ?? item).AsObject();
                    if (state["observation"] == null) continue;
                    output.WriteLine(Request(state, options, history).ToJsonString());
                    Remember(history, state);
                    count++;
                }
                Console.WriteLine($"Prepared {count} offline requests: {destination}");
                return 0;
            }
            if (args[0] is "observe" or "trial")
            {
                await RunAsync(options, args[0] == "trial");
                return 0;
            }
            throw new ArgumentException("Unknown command. Use help.");
        }
        catch (Exception e)
        {
            // Never echo server bodies or HTTP headers into error diagnostics.
            Console.Error.WriteLine($"Stopped: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    static void Help() => Console.WriteLine("""
Hollow Knight Jev controller
  preview   Print the exact request; offline, no control.
  observe   Record raw snapshots and request previews; offline, no control.
  replay    Rebuild requests from --input <recording.jsonl>; offline, no control.
  trial     Call Jev and apply its selected actions (requires TYPESAFE_API_KEY).
  watch     Serve a read-only live dashboard on a free 127.0.0.1 port.
  export    Write standalone replay HTML from --input <recording.jsonl>.
  self-test Run offline contract and safety tests.

Options:
  --state <path>            Default ../runtime/state.json (relative to controller)
  --command <path>          Default ../runtime/command.json
  --output <directory>      Default ./artifacts
  --seconds <1..600>        Default 15; hard wall-clock limit (watch allows 3600)
  --max-calls <1..6000>     Default 10; no automatic HTTP retries
  --duration-ms <1..300>    Default 300; full button-state lease
  --cadence-ms <1..2000>    Default 100; minimum request-start interval
  --max-age-ms <1..2000>    Default 500; observation freshness cutoff
  --model <model>           Default jev-1.13.0; all live calls require this pinned model
  --decision-mode <mode>    combined (default), compositional, locomotion, or grouped
  --scenario <name>         tutorial (default) or gruz-mother (defaults locomotion)
  --combat-relative <bool>  Default false; express combat positions around player
  --combat-compact <bool>   Default false; compact combat edges and three questions
  --danger-margin <0..20>   Default 0; compact enemy avoidance-zone padding in world units
  --attack-mode <mode>      hold (default) or pulse (50ms attack press)
  --terrain-format <mode>   grid (default), spans, rectangles, or geometry
  --history <0..4>          Default 0; earlier raw player/held observations
  --stall-seconds <0..120>  Default 0 disabled; local no-displacement stop
  --selection <mode>       argmax, sample, sample-jump, or sample-locomotion
  --seed <integer>         Default 42; deterministic sampling seed per run
  --jump-threshold <0..1>  Default 0.5; local Noul cutoff for deterministic decoding
  --jump-decision <mode>  noul (default) or choice between jump/release
  --jump-framing <mode>   buttons (default) or factual press/hold/release mechanics
  --goal-position <x,y>   Optional measured door center in world coordinates
  --spatial-labels <bool> Default false; coordinate-only surface/goal relations
  --visit-memory <bool>   Default false; include raw visit counts and dwell times
  --input <path>            Replay source

Pass explicit game runtime paths; defaults are relative to the current directory.
Use scripts/Run-Trial.ps1 for a paid trial with a spend ledger. Ctrl+C releases inputs.
Create STOP beside command.json to stop a trial; restart the game deliberately afterward.
No credentials are written to request previews or recordings.
""");

    internal static JsonObject ReadState(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonNode.Parse(stream)!.AsObject();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4) throw new IOException($"Reading local JSON file '{Path.GetFullPath(path)}' failed after 5 attempts.", error);
                Thread.Sleep(10);
            }
        }
    }

    // Construct a new object at every level. Unknown fields never cross this boundary.
    internal static JsonObject ModelState(JsonObject envelope, string terrainFormat = "grid", bool visitMemory = false)
    {
        if (envelope["schema_version"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported state schema.");
        var raw = envelope["observation"]?.AsObject() ?? throw new InvalidDataException("Missing observation.");
        var state = new JsonObject
        {
            ["player"] = Fields(raw["player"], "x", "y", "vx", "vy", "width", "height", "grounded", "facing", "health", "touching_wall_left", "touching_wall_right"),
            ["terrain"] = Terrain(raw["terrain"], terrainFormat),
            ["enemies"] = Objects(raw["enemies"], 32, "x", "y", "width", "height", "vx", "vy"),
            ["objects"] = Objects(raw["objects"], 32, "x", "y", "width", "height", "kind"),
            ["held"] = Fields(raw["held"], "move", "look", "jump", "attack")
        };
        if (raw["abilities"] is JsonObject)
            state["abilities"] = Fields(raw["abilities"], "run_speed", "jump_speed", "jump_sustain_steps", "fixed_timestep_seconds", "gravity_y", "gravity_scale", "has_double_jump", "has_dash", "has_wall_jump", "jump_speed_sustain_seconds");
        if (raw["surfaces"] is JsonArray)
            state["surfaces"] = Objects(raw["surfaces"], 64, "left_x", "right_x", "top_y", "height_above_feet", "normal_x", "normal_y");
        if (visitMemory && raw["exploration"] is JsonObject exploration)
        {
            var visits = Fields(exploration, "cell_size", "seconds_since_new_cell", "current_cell_seconds", "current_cell_entries");
            visits["nearby_visited_cells"] = Objects(exploration["nearby_visited_cells"], 64, "x", "y", "entries", "seconds");
            state["exploration"] = visits;
        }
        return state;
    }

    internal static JsonObject CombatState(JsonObject envelope, bool relative = false)
    {
        if (envelope["schema_version"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported state schema.");
        var raw = envelope["observation"]?.AsObject() ?? throw new InvalidDataException("Missing observation.");
        var state = new JsonObject
        {
            ["player"] = Fields(raw["player"], "x", "y", "vx", "vy", "width", "height", "grounded", "facing", "health", "touching_wall_left", "touching_wall_right", "attacking", "attack_ready", "double_jumped"),
            ["held"] = Fields(raw["held"], "move", "look", "jump", "attack"),
            ["enemies"] = Objects(raw["enemies"], 32, "x", "y", "width", "height", "vx", "vy", "hp", "max_hp", "boss", "animation", "state"),
            ["arena"] = Fields(raw["arena"], "left", "right", "floor", "ceiling")
        };
        if (raw["abilities"] is JsonObject)
            state["abilities"] = Fields(raw["abilities"], "run_speed", "jump_speed", "jump_sustain_steps", "fixed_timestep_seconds", "gravity_y", "gravity_scale", "has_double_jump", "has_dash", "has_wall_jump", "jump_speed_sustain_seconds");
        if (raw["boss_state"] is JsonValue bossState && bossState.TryGetValue<string>(out var label)) state["boss_state"] = label;
        if (relative)
        {
            var x = raw["player"]!["x"]!.GetValue<double>();
            var y = raw["player"]!["y"]!.GetValue<double>();
            state["player"]!["x"] = 0.0;
            state["player"]!["y"] = 0.0;
            foreach (var bound in new[] { "left", "right", "floor", "ceiling" })
                if (state["arena"]![bound] is JsonValue value)
                    state["arena"]![bound] = value.GetValue<double>() - (bound is "left" or "right" ? x : y);
        }
        return state;
    }

    internal static string AllowedScene(Options options) => options.Scenario == "gruz-mother" ? "GG_Gruz_Mother" : "Tutorial_01";

    internal static JsonObject CompactCombatState(JsonObject envelope, double dangerMargin = 0)
    {
        if (!double.IsFinite(dangerMargin) || dangerMargin < 0 || dangerMargin > 20)
            throw new ArgumentOutOfRangeException(nameof(dangerMargin), "Must be finite and 0..20 world units.");
        var state = CombatState(envelope, true);
        foreach (var flag in new[] { "blocked_left", "blocked_right" })
            if (envelope["observation"]?["player"]?[flag] is JsonValue value && value.TryGetValue<bool>(out var blocked))
                state["player"]![flag] = blocked;
        void Edges(JsonObject body)
        {
            var x = body["x"]!.GetValue<double>(); var y = body["y"]!.GetValue<double>();
            var w = body["width"]!.GetValue<double>(); var h = body["height"]!.GetValue<double>();
            body.Remove("x"); body.Remove("y"); body.Remove("width"); body.Remove("height");
            body["left"] = Math.Round(x - w / 2, 3); body["right"] = Math.Round(x + w / 2, 3);
            body["bottom"] = Math.Round(y - h / 2, 3); body["top"] = Math.Round(y + h / 2, 3);
        }
        Edges(state["player"]!.AsObject());
        for (int index = 0; index < state["enemies"]!.AsArray().Count; index++)
        {
            var enemy = state["enemies"]![index]!.AsObject();
            Edges(enemy);
            if (envelope["observation"]!["enemies"]![index]?["state_elapsed_seconds"] is JsonValue elapsedValue
                && elapsedValue.TryGetValue<double>(out var elapsed) && double.IsFinite(elapsed) && elapsed >= 0)
                enemy["state_elapsed_seconds"] = elapsed;
            if (dangerMargin > 0)
                enemy["avoidance_zone"] = new JsonObject
                {
                    ["left"] = Math.Round(enemy["left"]!.GetValue<double>() - dangerMargin, 3),
                    ["right"] = Math.Round(enemy["right"]!.GetValue<double>() + dangerMargin, 3),
                    ["bottom"] = Math.Round(enemy["bottom"]!.GetValue<double>() - dangerMargin, 3),
                    ["top"] = Math.Round(enemy["top"]!.GetValue<double>() + dangerMargin, 3),
                    ["margin"] = dangerMargin
                };
        }
        foreach (var bound in state["arena"]!.AsObject().ToArray())
            state["arena"]![bound.Key] = Math.Round(bound.Value!.GetValue<double>(), 3);
        foreach (var speed in new[] { "run_speed", "jump_speed" })
            if (state["abilities"]?[speed] is JsonValue value) state[speed] = value.DeepClone();
        state.Remove("abilities"); state.Remove("boss_state");
        return state;
    }

    static JsonObject CompactCombatRequest(JsonObject envelope, Options options)
    {
        if (options.Scenario != "gruz-mother" || options.DecisionMode != "compositional" || options.JumpDecision != "noul")
            throw new ArgumentException("combat-compact requires gruz-mother, compositional decisions, and jump-decision noul.");
        var legend = "All coordinates are relative to the player center. X increases right; Y increases upward. Velocities are world units per second; positive vx moves right and positive vy moves up. left/right/bottom/top are current collision-box edges, rounded to 0.001 world units. Contact between enemy and player collision boxes causes damage. state/animation are current game labels; optional state_elapsed_seconds is elapsed game time in the current state, not a future deadline. ";
        var avoidance = options.DangerMargin > 0 ? "entering or remaining inside expanded enemy avoidance zones" : "enemy contact";
        legend += "Charge Antic is windup before Charge at speed 26. Slam Antic winds up alternating floor/ceiling motion; Launch Up/Down use speed 50. Slam Up/Down are stationary impacts followed by the opposite launch. Antic states are windups; current velocity can change at the next state. Animation completion or a timer can end a windup, so elapsed state time is not a fixed countdown. ";
        if (options.DangerMargin > 0)
            legend += "Each avoidance_zone expands the actual enemy collision box by margin world units on every side. It is a configured clearance region, not a measured larger hitbox or predicted trajectory. Prioritize keeping the player collision box outside these zones. ";
        var timing = $"Selected inputs last up to {options.DurationMs} ms or until replaced. ";
        return new JsonObject
        {
            ["model"] = options.Model, ["state"] = CompactCombatState(envelope, options.DangerMargin),
            ["questions"] = new JsonObject
            {
                ["move"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = legend + $"Choose horizontal movement to avoid {avoidance} over the next 0.5 seconds. Remain still when evasion is unnecessary. A held direction moves the player at run_speed; still stops horizontal movement. blocked_left/right report current solid-wall contact for horizontal translation. Moving into a solid wall does not translate the player. " + timing,
                    ["criteria"] = new JsonObject { ["left"] = $"Moving left can translate the player and is the best way to avoid {avoidance}.", ["right"] = $"Moving right can translate the player and is the best way to avoid {avoidance}.", ["still"] = options.DangerMargin > 0 ? "Remaining still keeps the player outside avoidance zones; no horizontal evasion is needed." : "Remaining still avoids contact; no horizontal evasion is needed." }
                },
                ["jump"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = legend + "Set the jump button for the next input interval. Should its state be ON? " + (options.DangerMargin > 0 ? "Use jumping only to evade enemy avoidance zones. " : "Use jumping only to evade enemy contact. ") + "ON with held.jump=false creates a fresh press. ON with held.jump=true only continues the current hold: it cannot start another jump. After landing with jump still held, OFF is needed to re-arm a later fresh press, even if another jump is desirable. While rising, holding ON sustains the existing jump; OFF shortens ascent. Holding ON while falling cannot start a double jump; a fresh press after OFF is required. Choose OFF when a release is needed or jumping is unnecessary. " + $"Inputs last at most {options.DurationMs}ms or until replaced.",
                    ["criteria"] = new JsonObject { ["true"] = "Set jump ON: a fresh press or continuation of current ascent helps evasion.", ["false"] = "Set jump OFF: release to re-arm a new press, shorten ascent, or stay grounded safely." }
                },
                ["attack"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = "Choose a nail strike that can hit an enemy from the current player position. Coordinates are collision-box edges relative to the player center; x right, y up. Attacking does not walk toward the enemy. Movement and jump are chosen separately for evasion. avoidance_zone is extra clearance for movement, not a damaging body and not a prohibition on attacking. Judge the actual enemy box for a strike. Prefer dealing damage whenever a strike is available; choose none when the enemy is out of reach or attack_ready is false. Forward attacks toward player.facing (-1 left, 1 right); up attacks above; down attacks below while airborne. " + (options.AttackMode == "pulse" ? "A selected attack is a 50ms button press; the native attack animation and cooldown follow normally. " : "A selected attack holds the button for the input lease; the native attack animation and cooldown follow normally. ") + "Do not require moving into the enemy to attack.",
                    ["criteria"] = new JsonObject { ["none"] = "No available nail strike can reach an enemy from here, or the attack is on cooldown.", ["forward"] = "An enemy is within nail reach in the direction the player faces; strike forward.", ["up"] = "An enemy is within nail reach above the player; strike upward.", ["down"] = "The player is airborne and an enemy is within nail reach below; strike downward." }
                }
            }
        };
    }
    static string Goal(Options options) => options.Scenario == "gruz-mother"
        ? "Defeat Gruz Mother in Hollow Knight without taking any hits. Default to staying still. Move or jump only to avoid incoming enemy contact, and release evasive movement once it is no longer needed. Attack when there is a safe opening from the current position; do not chase the enemy or move merely to make progress. Avoiding contact has priority over dealing damage. Healing does not erase a hit."
        : "Actively progress through the Hollow Knight tutorial and reach the giant door at the end of King's Pass alive. Avoid unnecessary waiting. Optional collectibles are not part of the objective.";

    static JsonObject Terrain(JsonNode? source, string format)
    {
        var result = Fields(source, "origin_x", "origin_y", "cell_size", "width", "height");
        if (source?["rows"] is not JsonArray rows || rows.Count > 64) throw new InvalidDataException("Invalid terrain rows.");
        var output = new JsonArray();
        foreach (var row in rows)
        {
            var value = row?.GetValue<string>() ?? throw new InvalidDataException("Missing grid row.");
            if (value.Length > 64 || value.Any(c => c is not ('.' or '#' or '^' or '?'))) throw new InvalidDataException("Invalid terrain sample.");
            output.Add(value);
        }
        result["rows"] = output;
        return format switch { "spans" => TerrainSpans(result), "rectangles" => TerrainRectangles(result), "geometry" => TerrainGeometry(result), _ => result };
    }

    internal static JsonObject TerrainSpans(JsonObject grid)
    {
        var result = Fields(grid, "origin_x", "origin_y", "cell_size", "width", "height");
        var originX = grid["origin_x"]!.GetValue<double>();
        var originY = grid["origin_y"]!.GetValue<double>();
        var cell = grid["cell_size"]!.GetValue<double>();
        var width = grid["width"]!.GetValue<int>();
        var height = grid["height"]!.GetValue<int>();
        var sourceRows = grid["rows"]!.AsArray();
        if (sourceRows.Count != height || cell <= 0) throw new InvalidDataException("Terrain dimensions do not match samples.");
        var rows = new JsonArray();
        for (int r = 0; r < height; r++)
        {
            var samples = sourceRows[r]!.GetValue<string>();
            if (samples.Length != width) throw new InvalidDataException("Terrain row width does not match samples.");
            var row = new JsonObject { ["y"] = originY + (height - 1 - r) * cell };
            foreach (var (symbol, name) in new[] { ('#', "solid"), ('^', "damage"), ('?', "unknown") })
            {
                var spans = new JsonArray();
                for (int column = 0; column < width; column++)
                {
                    if (samples[column] != symbol) continue;
                    var first = column;
                    while (column + 1 < width && samples[column + 1] == symbol) column++;
                    spans.Add(new JsonArray(JsonValue.Create(originX + first * cell), JsonValue.Create(originX + column * cell)));
                }
                if (spans.Count > 0) row[name] = spans;
            }
            if (row.Count > 1) rows.Add(row);
        }
        result["rows"] = rows;
        return result;
    }

    internal static JsonObject TerrainRectangles(JsonObject grid)
    {
        // Validate dimensions using the same raw sample contract as spans.
        _ = TerrainSpans(grid);
        var result = Fields(grid, "origin_x", "origin_y", "cell_size", "width", "height");
        var ox = grid["origin_x"]!.GetValue<double>();
        var oy = grid["origin_y"]!.GetValue<double>();
        var cell = grid["cell_size"]!.GetValue<double>();
        var height = grid["height"]!.GetValue<int>();
        var active = new Dictionary<(char kind, int first, int last), JsonArray>();
        var groups = new Dictionary<char, JsonArray> { ['#'] = new(), ['^'] = new(), ['?'] = new() };
        for (int r = 0; r < height; r++)
        {
            var samples = grid["rows"]![r]!.GetValue<string>();
            var y = oy + (height - 1 - r) * cell;
            var next = new Dictionary<(char kind, int first, int last), JsonArray>();
            for (int c = 0; c < samples.Length; c++)
            {
                var kind = samples[c];
                if (kind == '.') continue;
                var first = c;
                while (c + 1 < samples.Length && samples[c + 1] == kind) c++;
                var key = (kind, first, c);
                if (active.TryGetValue(key, out var rectangle)) rectangle[2] = y;
                else
                {
                    rectangle = new JsonArray(JsonValue.Create(ox + first * cell), JsonValue.Create(ox + c * cell), JsonValue.Create(y), JsonValue.Create(y));
                    groups[kind].Add(rectangle);
                }
                next[key] = rectangle;
            }
            active = next;
        }
        foreach (var (symbol, name) in new[] { ('#', "solid"), ('^', "damage"), ('?', "unknown") })
            if (groups[symbol].Count > 0) result[name] = groups[symbol];
        return result;
    }

    internal static JsonObject TerrainGeometry(JsonObject grid)
    {
        var rectangles = TerrainRectangles(grid);
        var result = Fields(rectangles, "origin_x", "origin_y", "cell_size", "width", "height");
        foreach (var kind in new[] { "solid", "damage", "unknown" })
        {
            if (rectangles[kind] is not JsonArray source) continue;
            result[kind] = new JsonArray(source.Select(rectangle => (JsonNode)new JsonObject
            {
                ["left_x"] = rectangle![0]!.DeepClone(), ["right_x"] = rectangle[1]!.DeepClone(),
                ["bottom_y"] = rectangle[2]!.DeepClone(), ["top_y"] = rectangle[3]!.DeepClone()
            }).ToArray());
        }
        return result;
    }

    static JsonObject Fields(JsonNode? source, params string[] names)
    {
        var result = new JsonObject();
        if (source is not JsonObject obj) throw new InvalidDataException("Missing observation object.");
        foreach (var name in names)
        {
            if (obj[name] is JsonValue value) result[name] = value.DeepClone();
        }
        return result;
    }

    static JsonArray Objects(JsonNode? source, int max, params string[] names)
    {
        if (source is not JsonArray array) throw new InvalidDataException("Missing observation array.");
        if (array.Count > max) throw new InvalidDataException("Observation exceeds local object limit.");
        var result = new JsonArray();
        foreach (var item in array) result.Add(Fields(item, names));
        return result;
    }

    internal static JsonObject Request(JsonObject envelope, Options options, IReadOnlyList<JsonObject>? history = null)
    {
        if (options.DecisionMode == "grouped") return GroupedCombatRequest(envelope, options);
        if (options.CombatCompact) return CompactCombatRequest(envelope, options);
        var combat = options.Scenario == "gruz-mother";
        // Tutorial perception/route profiles never cross into the isolated combat scenario.
        if (combat) options = options with { History = 0, VisitMemory = false, SpatialLabels = false, GoalPosition = null, TerrainFormat = "grid" };
        var criteria = new JsonObject();
        foreach (var (name, buttons) in Actions)
            criteria[name] = JsonSerializer.SerializeToNode(buttons);
        criteria["release"]!["meaning"] = combat ? "Briefly release controls." : "Briefly release controls for maneuver or combat timing while continuing progress through the level.";
        var request = new JsonObject
        {
            ["model"] = options.Model,
            ["state"] = combat ? CombatState(envelope, options.CombatRelative) : ModelState(envelope, options.TerrainFormat, options.VisitMemory),
            ["questions"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = new JsonObject
                    {
                        ["question"] = combat ? Goal(options) + " Choose the next controller input." : "Choose the next controller input to actively progress through the Hollow Knight tutorial and reach the giant door at the end of King's Pass alive. Avoid unnecessary waiting. Optional collectibles are not part of the objective.",
                        ["coordinates"] = "World units, x right, y up; player x/y are world coordinates of the collision bounds center; enemy/object x/y are bounds centers relative to the player. Velocities are world units per second. Player/enemy/object width and height are bounds sizes. Terrain is a local sample grid: origin_x/origin_y are the bottom-left sample CENTER relative to player, cell_size is sample spacing in world units, width/height are sample counts. Rows run from top to bottom, characters left to right. Each sample checks a square of side 0.9 * cell_size: .=empty, #=solid, ^=contact damage, ?=unobserved.",
                        ["controls"] = "Each option specifies button inputs: move -1 left/0 released/1 right; look -1 down/0 released/1 up. No buttons are pressed beyond those specified. Move, look and jump last up to max_duration_ms, or until the next chosen state replaces them. " + AttackSemantics(options) + " Forward attacks use facing; up/down attacks use look.",
                        ["max_duration_ms"] = options.DurationMs
                    },
                    ["criteria"] = criteria
                }
            }
        };
        if (combat)
            request["questions"]!["action"]!["instructions"]!["coordinates"] = "World units, x right, y up. Player x/y are absolute collision-bounds centers; enemy x/y are collision-bounds centers relative to the player. Width/height are bounds sizes; velocities are world units per second. Enemy hp/max_hp are measured health; boss identifies the boss. Optional animation/state and boss_state are raw current game labels. Arena left/right/floor/ceiling are absolute world coordinates. Player touching_wall_left/right and attacking are current engine flags; attack_ready is the game's current CanAttack result, not a hit prediction; double_jumped is the engine's consumed-double-jump flag. Available inputs are horizontal movement, jump, vertical aim, and nail attack. Dash, spells, and healing inputs are unavailable to this controller even when owned; upgrade flags report inventory, not additional inputs. Keeping jump=true does not issue another press. A new ground, double, or wall jump input requires releasing jump and then pressing it again, subject to the game's normal eligibility and input buffering.";
        if (combat && options.CombatRelative)
        {
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>()
                .Replace("Player x/y are absolute collision-bounds centers; enemy x/y are collision-bounds centers relative to the player.", "All positions are relative to the player's current collision-bounds center: player x/y are zero; enemy x/y remain relative collision-bounds centers.")
                .Replace("Arena left/right/floor/ceiling are absolute world coordinates.", "Arena left/right/floor/ceiling are relative to the same player center.");
        }
        if (options.History > 0)
        {
            request["state"]!["recent_player_states"] = RecentPlayerStates(envelope, history ?? [], options.History);
            request["questions"]!["action"]!["instructions"]!["history"] = "recent_player_states contains earlier raw player and held-button observations in chronological order. age_ms is milliseconds before the current observation.";
        }
        if (options.TerrainFormat == "spans")
            request["questions"]!["action"]!["instructions"]!["coordinates"] = "World units, x right, y up; player x/y are absolute collision-bounds centers; enemy/object x/y are bounds centers relative to player. Velocities are world units per second. Player/enemy/object width/height are bounds sizes. Terrain is a lossless encoding of raw grid samples: origin_x/origin_y are the bottom-left sample CENTER relative to player; cell_size is sample spacing; width/height are sample counts. Rows are ordered top to bottom. Every row y and every span x coordinate is relative to the player collision-bounds center. Each solid/damage/unknown pair [first_x,last_x] includes sample centers from first_x through last_x at cell_size spacing. Unlisted cells and omitted rows are empty samples. Each sample checks a square of side 0.9 * cell_size. Solid means collision, damage means contact damage, unknown means unobserved.";
        if (options.TerrainFormat == "rectangles")
            request["questions"]!["action"]!["instructions"]!["coordinates"] = "World units, x right, y up; player x/y are absolute collision-bounds centers; enemy/object x/y are bounds centers relative to player. Velocities are world units per second. Player/enemy/object width/height are bounds sizes. Terrain is a lossless encoding of raw grid samples: origin_x/origin_y are the bottom-left sample CENTER relative to player; cell_size is sample spacing; width/height are sample counts. Each solid/damage/unknown rectangle [min_x,max_x,min_y,max_y] includes sample centers on both axes at cell_size spacing. All rectangle coordinates are relative to the player collision-bounds center. Unlisted samples are empty. Each sample checks a square of side 0.9 * cell_size. Solid means collision, damage means contact damage, unknown means unobserved.";
        if (options.TerrainFormat == "geometry")
            request["questions"]!["action"]!["instructions"]!["coordinates"] = "World units, x right, y up; player x/y are absolute collision-bounds centers; enemy/object x/y are bounds centers relative to player. Velocities are world units per second. Player/enemy/object width/height are bounds sizes. Terrain is a lossless encoding of raw grid samples: origin_x/origin_y are the bottom-left sample CENTER relative to player; cell_size is sample spacing; width/height are sample counts. Each solid/damage/unknown rectangle includes sample centers from left_x through right_x and bottom_y through top_y at cell_size spacing. All rectangle coordinates are relative to the player collision-bounds center. Unlisted samples are empty. Each sample checks a square of side 0.9 * cell_size. Solid means collision, damage means contact damage, unknown means unobserved.";
        if (!combat)
        {
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>() + " Solid terrain blocks movement; landing occurs on upper collision surfaces.";
        }
        if (request["state"]!["abilities"] != null)
        {
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>() + " abilities contains raw movement constants and owned upgrades: speeds are world units per second; gravity_y is world units per second squared and gravity_scale is the normal player multiplier. jump_sustain_steps counts fixed ticks that set the upward jump speed; fixed_timestep_seconds is seconds per tick; jump_speed_sustain_seconds is their product. Normal gravity follows the sustained-speed ticks. has_double_jump, has_dash and has_wall_jump are inventory flags.";
        }
        if (request["state"]!["surfaces"] != null)
        {
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>() + " surfaces contains contiguous runs of sampled upward-facing top-edge raycast hits grouped by collider and flat height. left_x/right_x are the first/last sampled hit coordinates and top_y is the hit height, all relative to the player collision-bounds center. height_above_feet equals top_y plus half the player height. normal_x/normal_y are the hit surface normal. These runs do not guarantee the full platform extent, headroom, or reachability.";
        }
        if (options.GoalPosition != null)
        {
            request["state"]!["objective"] = JsonSerializer.SerializeToNode(options.GoalPosition);
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>() + " objective.destination_x and objective.destination_y are the measured giant door center in absolute world coordinates.";
        }
        if (options.SpatialLabels)
        {
            AddSpatialLabels(request["state"]!.AsObject());
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>() + " Surface horizontal_relation compares the sampled run with the player's half-width: left, right, or overlapping_player_x. Surface vertical_relation compares height_above_feet with zero: above_feet, at_feet, or below_feet, with a 0.1 world-unit tolerance. Optional objective.relative_x/relative_y equal destination minus player coordinates; its horizontal_relation is left/right/aligned and vertical_relation is below/above/level with the same 0.1-unit tolerance. These are coordinate relations only.";
        }
        if (request["state"]!["exploration"] != null)
        {
            var instructions = request["questions"]!["action"]!["instructions"]!;
            instructions["coordinates"] = instructions["coordinates"]!.GetValue<string>() + " exploration contains observed visits and unpaused dwell times during this tutorial session. cell_size is the world-unit bin size; seconds_since_new_cell is elapsed active time since entering a previously unvisited bin; current_cell_seconds/current_cell_entries are observed dwell and entry count for the current bin. nearby_visited_cells x/y are visited bin centers relative to the player, with observed entries and seconds.";
        }
        if (options.DecisionMode is "compositional" or "locomotion")
        {
            string Instructions(string question)
            {
                var common = request["questions"]!["action"]!["instructions"]!.AsObject();
                return Goal(options) + " " + question + "\n"
                    + common["coordinates"]!.GetValue<string>() + "\n"
                    + (options.History > 0 ? common["history"]!.GetValue<string>() + "\n" : "")
                    + $"Each control interval lasts up to {options.DurationMs} ms or until the next chosen inputs replace it. "
                    + "In held controls, move -1 means left, 0 released, and 1 right; look -1 means down, 0 released, and 1 up. "
                    + AttackSemantics(options) + " Forward attack uses facing, up/down attack uses vertical aim.";
            }
            var questions = new JsonObject
            {
                ["move"] = new JsonObject
                {
                    ["type"] = "choice", ["instructions"] = Instructions("Choose the horizontal input to hold for the next control interval."),
                    ["criteria"] = new JsonObject { ["left"] = "Hold left to move or steer left.", ["still"] = combat ? "Release horizontal movement; stay in place." : "Briefly release horizontal movement for maneuver or combat timing while continuing progress through the level.", ["right"] = "Hold right to move or steer right." }
                },
                ["jump"] = new JsonObject
                {
                    ["type"] = "noul", ["instructions"] = Instructions("Should the jump button be held during the next control interval?"),
                    ["criteria"] = new JsonObject { ["true"] = "Hold the jump button.", ["false"] = "Release the jump button." }
                },
                ["attack"] = new JsonObject
                {
                    ["type"] = "choice", ["instructions"] = Instructions("Choose the nail attack input for the next control interval."),
                    ["criteria"] = new JsonObject { ["none"] = "Release attack and up/down buttons.", ["forward"] = "Hold attack; release up/down buttons.", ["up"] = "Hold attack and up buttons.", ["down"] = "Hold attack and down buttons." }
                }
            };
            if (options.JumpDecision == "choice")
                questions["jump"] = new JsonObject
                {
                    ["type"] = "choice", ["instructions"] = Instructions("Choose the jump-button input for the next control interval."),
                    ["criteria"] = new JsonObject { ["jump"] = "Hold the jump button.", ["release"] = "Release the jump button." }
                };
            if (options.JumpFraming == "mechanics")
            {
                questions["jump"]!["instructions"] = Instructions(options.JumpDecision == "choice"
                    ? "Choose whether to press or keep holding jump, or release jump, for the next control interval."
                    : "Should the jump button be pressed or kept held during the next control interval?")
                    + " A new jump-button press starts a jump when grounded and normal player control permits it. Keeping it pressed sustains the same jump's ascent up to the game's duration limit; it does not create another press. Releasing can shorten ascent and allows a later press to be a new input.";
                questions["jump"]!["criteria"] = options.JumpDecision == "choice"
                    ? new JsonObject { ["jump"] = "Press or keep holding the jump button.", ["release"] = "Release the jump button." }
                    : new JsonObject { ["true"] = "Press or keep holding the jump button.", ["false"] = "Release the jump button." };
            }
            if (options.AttackMode == "pulse")
            {
                questions["attack"]!["criteria"] = new JsonObject
                {
                    ["none"] = "Release the nail attack button and vertical aim.",
                    ["forward"] = "Press nail attack for 50 ms with no vertical aim, then release attack.",
                    ["up"] = "Press nail attack for 50 ms while aiming up, then release attack.",
                    ["down"] = "Press nail attack for 50 ms while aiming down, then release attack."
                };
            }
            if (options.DecisionMode == "locomotion")
            {
                var locomotionInstructions = Instructions("Choose the combined horizontal and jump-button input for the next control interval.");
                if (options.JumpFraming == "mechanics") locomotionInstructions += " A new jump-button press starts a jump when grounded and normal player control permits it. Keeping it pressed sustains the same jump's ascent up to the game's duration limit; it does not create another press. Releasing can shorten ascent and allows a later press to be a new input.";
                questions.Remove("move");
                questions.Remove("jump");
                questions["locomotion"] = new JsonObject
                {
                    ["type"] = "choice", ["instructions"] = locomotionInstructions,
                    ["criteria"] = new JsonObject
                    {
                        ["left"] = "Hold left; release jump.",
                        ["left_jump"] = "Hold left; press or keep holding jump.",
                        ["still"] = combat ? "Release horizontal movement and jump; stay in place." : "Briefly release horizontal movement and jump.",
                        ["still_jump"] = "Release horizontal movement; press or keep holding jump.",
                        ["right"] = "Hold right; release jump.",
                        ["right_jump"] = "Hold right; press or keep holding jump."
                    }
                };
            }
            request["questions"] = questions;
        }
        if (options.VisitMemory)
        {
            const string visitPolicy = " Use observed visits and dwell times to deprioritize directions that repeatedly remain in the same areas without reaching new terrain; allow temporary backtracking.";
            if (options.DecisionMode == "combined")
            {
                var instructions = request["questions"]!["action"]!["instructions"]!;
                instructions["question"] = instructions["question"]!.GetValue<string>() + visitPolicy;
            }
            else
            {
                var navigation = request["questions"]![options.DecisionMode == "locomotion" ? "locomotion" : "move"]!;
                navigation["instructions"] = navigation["instructions"]!.GetValue<string>() + visitPolicy;
            }
        }
        return request;
    }

    internal static void AddSpatialLabels(JsonObject state)
    {
        static double Number(JsonNode? value)
        {
            var number = value?.GetValue<double>() ?? throw new InvalidDataException("Missing geometry coordinate.");
            return double.IsFinite(number) ? number : throw new InvalidDataException("Nonfinite geometry coordinate.");
        }
        static string Axis(double value, string negative, string positive, string aligned)
            => value > .1 + 1e-9 ? positive : value < -.1 - 1e-9 ? negative : aligned;
        var player = state["player"]!;
        var halfWidth = Number(player["width"]) / 2;
        if (state["surfaces"] is JsonArray surfaces)
            foreach (var surface in surfaces)
            {
                var left = Number(surface!["left_x"]);
                var right = Number(surface["right_x"]);
                surface["horizontal_relation"] = right < -halfWidth ? "left" : left > halfWidth ? "right" : "overlapping_player_x";
                surface["vertical_relation"] = Axis(Number(surface["height_above_feet"]), "below_feet", "above_feet", "at_feet");
            }
        if (state["objective"] is JsonObject objective)
        {
            var dx = Number(objective["destination_x"]) - Number(player["x"]);
            var dy = Number(objective["destination_y"]) - Number(player["y"]);
            objective["relative_x"] = dx;
            objective["relative_y"] = dy;
            objective["horizontal_relation"] = Axis(dx, "left", "right", "aligned");
            objective["vertical_relation"] = Axis(dy, "below", "above", "level");
        }
    }

    internal static JsonArray RecentPlayerStates(JsonObject current, IReadOnlyList<JsonObject> history, int count)
    {
        var result = new JsonArray();
        var currentTime = DateTimeOffset.Parse(current["observed_utc"]!.GetValue<string>());
        var currentSequence = current["sequence"]!.GetValue<long>();
        var session = current["session_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(session)) return result;
        foreach (var prior in history.Where(h => h["session_id"]?.GetValue<string>() == session
                    && h["sequence"]!.GetValue<long>() < currentSequence
                    && DateTimeOffset.TryParse(h["observed_utc"]?.GetValue<string>(), out var time) && time <= currentTime)
                .OrderBy(h => h["sequence"]!.GetValue<long>()).TakeLast(count))
        {
            var observation = prior["observation"]!.AsObject();
            result.Add(new JsonObject
            {
                ["age_ms"] = (currentTime - DateTimeOffset.Parse(prior["observed_utc"]!.GetValue<string>())).TotalMilliseconds,
                ["player"] = Fields(observation["player"], "x", "y", "vx", "vy", "width", "height", "grounded", "facing", "health", "touching_wall_left", "touching_wall_right"),
                ["held"] = Fields(observation["held"], "move", "look", "jump", "attack")
            });
        }
        return result;
    }

    static void Remember(List<JsonObject> history, JsonObject state)
    {
        if (history.Count > 0 && JsonNode.DeepEquals(history[^1]["session_id"], state["session_id"])
            && JsonNode.DeepEquals(history[^1]["sequence"], state["sequence"])) return;
        history.Add(state.DeepClone().AsObject());
        while (history.Count > 4) history.RemoveAt(0);
    }

    static string AttackSemantics(Options options) => options.AttackMode == "pulse"
        ? "Jump=true holds jump and jump=false releases it. Attack=true presses attack for 50 ms from command creation, then releases attack; selecting attack again creates another 50 ms press. Attack=false releases attack. Move, look and jump retain the selected state for the remaining control interval."
        : "Return held button states. Repeated true jump or attack continues its hold; false releases it.";

    internal static bool PulseReady(JsonObject? previousCommand, DateTimeOffset now, Options options)
        => options.AttackMode != "pulse" || previousCommand == null || (now - DateTimeOffset.Parse(previousCommand["created_utc"]!.GetValue<string>())).TotalMilliseconds >= 100;

    internal static string SelectedAction(JsonObject result, Options options)
    {
        if (options.DecisionMode == "grouped") return SelectedGroupedAction(result);
        if (options.DecisionMode == "combined")
        {
            var action = result["answers"]?["action"]?["choice"]?.GetValue<string>();
            return action != null && Actions.ContainsKey(action) ? action : throw new InvalidDataException("Missing or unknown action answer.");
        }
        var answers = result["answers"]?.AsObject() ?? throw new InvalidDataException("Missing answers.");
        if (options.DecisionMode == "locomotion")
            return ComposeLocomotion(answers["locomotion"]?["choice"]?.GetValue<string>(), answers["attack"]?["choice"]?.GetValue<string>());
        var move = answers["move"]?["choice"]?.GetValue<string>() switch { "left" => -1, "still" => 0, "right" => 1, _ => throw new InvalidDataException("Unknown movement answer.") };
        bool jump;
        if (options.JumpDecision == "choice")
            jump = answers["jump"]?["choice"]?.GetValue<string>() switch { "jump" => true, "release" => false, _ => throw new InvalidDataException("Unknown jump choice.") };
        else
        {
            var probability = answers["jump"]?["noul"]?.GetValue<double>() ?? throw new InvalidDataException("Missing jump answer.");
            if (!double.IsFinite(probability) || probability < 0 || probability > 1) throw new InvalidDataException("Invalid jump probability.");
            jump = probability > options.JumpThreshold;
        }
        var attack = answers["attack"]?["choice"]?.GetValue<string>() ?? throw new InvalidDataException("Missing attack answer.");
        var look = attack switch { "none" or "forward" => 0, "up" => 1, "down" => -1, _ => throw new InvalidDataException("Unknown attack answer.") };
        var buttons = new Buttons(move, look, jump, attack != "none");
        return Actions.Single(pair => pair.Value == buttons).Key;
    }

    internal static string ComposeLocomotion(string? locomotion, string? attack)
    {
        if (locomotion == null || !Locomotion.TryGetValue(locomotion, out var movement)) throw new InvalidDataException("Missing or unknown locomotion choice.");
        var look = attack switch { "none" or "forward" => 0, "up" => 1, "down" => -1, _ => throw new InvalidDataException("Unknown attack answer.") };
        var buttons = movement with { look = look, attack = attack != "none" };
        return Actions.Single(pair => pair.Value == buttons).Key;
    }

    internal static Selection Select(JsonObject result, Options options, Random random)
    {
        if (options.Scenario == "gruz-mother" && options.SelectionMode == "sample") throw new InvalidDataException("Boss scenario requires deterministic attack selection.");
        if (options.SelectionMode == "sample-jump" && options.DecisionMode != "compositional") throw new InvalidDataException("sample-jump requires compositional decisions.");
        if (options.SelectionMode == "sample-locomotion" && options.DecisionMode != "locomotion") throw new InvalidDataException("sample-locomotion requires locomotion decisions.");
        var argmax = SelectedAction(result, options);
        var draws = new JsonObject();
        if (options.SelectionMode == "argmax") return new Selection(argmax, argmax, draws);
        var answers = result["answers"]!.AsObject();
        if (options.DecisionMode == "combined")
        {
            var action = DrawCategorical(answers["action"]!["probabilities"]!.AsObject(), Actions.Keys.ToArray(), random, "action", draws);
            return new Selection(action, argmax, draws);
        }
        if (options.DecisionMode == "locomotion")
        {
            var locomotion = DrawCategorical(answers["locomotion"]!["probabilities"]!.AsObject(), Locomotion.Keys.ToArray(), random, "locomotion", draws);
            var attackChoice = options.SelectionMode == "sample-locomotion" ? answers["attack"]!["choice"]!.GetValue<string>()
                : DrawCategorical(answers["attack"]!["probabilities"]!.AsObject(), ["none", "forward", "up", "down"], random, "attack", draws);
            return new Selection(ComposeLocomotion(locomotion, attackChoice), argmax, draws);
        }
        var move = options.SelectionMode == "sample-jump" ? answers["move"]!["choice"]!.GetValue<string>()
            : DrawCategorical(answers["move"]!["probabilities"]!.AsObject(), ["left", "still", "right"], random, "move", draws);
        bool jump;
        if (options.JumpDecision == "choice")
            jump = DrawCategorical(answers["jump"]!["probabilities"]!.AsObject(), ["jump", "release"], random, "jump", draws) == "jump";
        else
        {
            var jumpProbability = answers["jump"]!["noul"]!.GetValue<double>();
            // SelectedAction validated the Noul before any draw was consumed.
            var jumpDraw = random.NextDouble();
            jump = jumpDraw < jumpProbability;
            draws["jump"] = new JsonObject { ["value"] = jumpDraw, ["probability"] = jumpProbability, ["selected"] = jump };
        }
        var attack = options.SelectionMode == "sample-jump" ? answers["attack"]!["choice"]!.GetValue<string>()
            : DrawCategorical(answers["attack"]!["probabilities"]!.AsObject(), ["none", "forward", "up", "down"], random, "attack", draws);
        var buttons = new Buttons(move == "left" ? -1 : move == "right" ? 1 : 0, attack == "up" ? 1 : attack == "down" ? -1 : 0, jump, attack != "none");
        return new Selection(Actions.Single(pair => pair.Value == buttons).Key, argmax, draws);
    }

    internal static string DrawCategorical(JsonObject probabilities, string[] options, Random random, string name, JsonObject draws)
    {
        var weights = new Dictionary<string, double>();
        foreach (var pair in probabilities)
        {
            if (!options.Contains(pair.Key)) throw new InvalidDataException("Unknown option in returned probability distribution.");
            var weight = pair.Value?.GetValue<double>() ?? throw new InvalidDataException("Missing probability value.");
            if (!double.IsFinite(weight) || weight < 0 || weight > 1) throw new InvalidDataException("Invalid probability weight.");
            weights[pair.Key] = weight;
        }
        var total = weights.Values.Sum();
        if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("Probability distribution has no finite positive mass.");
        var normalized = new JsonObject();
        foreach (var option in options) normalized[option] = weights.GetValueOrDefault(option) / total;
        var draw = random.NextDouble();
        double cumulative = 0;
        string? selected = null;
        foreach (var option in options)
        {
            var probability = weights.GetValueOrDefault(option) / total;
            if (probability <= 0) continue;
            selected = option;
            cumulative += probability;
            if (draw < cumulative) break;
        }
        draws[name] = new JsonObject { ["value"] = draw, ["selected"] = selected, ["normalized_probabilities"] = normalized };
        return selected!;
    }

    internal static string? Rejection(JsonObject state, DateTimeOffset now, int maxAgeMs)
    {
        if (EpisodeStop(state) is string terminal) return terminal;
        if (state["gameplay"]?.GetValue<bool>() != true) return "not_gameplay";
        if (state["paused"]?.GetValue<bool>() != false) return "paused";
        if (!DateTimeOffset.TryParse(state["observed_utc"]?.GetValue<string>(), out var timestamp)) return "missing_timestamp";
        var age = (now - timestamp).TotalMilliseconds;
        if (age < -100 || age > maxAgeMs) return "stale_observation";
        if (state["observation"]?["player"]?["health"]?.GetValue<int>() is not > 0) return "dead_or_missing_health";
        return null;
    }

    internal static string? EpisodeStop(JsonObject state) => state["episode"]?["status"]?.GetValue<string>() switch
    {
        "success" => "episode_success",
        "dead" => "episode_dead",
        "failed_hit" => "episode_failed_hit",
        "training_complete" => "episode_training_complete",
        _ => null
    };

    internal static bool DispatchReady(long sequence, long previousSequence, DateTimeOffset now, DateTimeOffset? previousStart, int cadenceMs)
        => sequence > previousSequence && (previousStart == null || (now - previousStart.Value).TotalMilliseconds >= cadenceMs);

    internal static string? MonitorRejection(JsonObject initial, JsonObject latest, DateTimeOffset now, Options options)
    {
        if (Rejection(latest, now, options.MaxAgeMs) is string reason) return reason;
        if (initial["session_id"]?.GetValue<string>() != latest["session_id"]?.GetValue<string>()) return "session_changed";
        if (initial["scene"]?.GetValue<string>() != latest["scene"]?.GetValue<string>()) return "scene_changed";
        return latest["control_enabled"]?.GetValue<bool>() == true || TrainingControlWait(latest, now, options) ? null : "control_disabled";
    }

    internal static bool TrainingControlWait(JsonObject state, DateTimeOffset now, Options options)
        => options.Scenario == "gruz-mother"
           && state["scene"]?.GetValue<string>() == "GG_Gruz_Mother"
           && state["episode"]?["training"]?.GetValue<bool>() == true
           && state["episode"]?["status"]?.GetValue<string>() == "running"
           && state["control_enabled"]?.GetValue<bool>() == false
           && Rejection(state, now, options.MaxAgeMs) == null;

    internal static async Task<T> AwaitMonitoredAsync<T>(Task<T> work, Func<JsonObject> readState, Func<bool> stopRequested, JsonObject initial, Options options, CancellationTokenSource cancellation, Func<JsonObject, string?>? localStop = null)
    {
        try
        {
            while (!work.IsCompleted)
            {
                if (stopRequested()) throw new TrialStoppedException("stop_file");
                JsonObject latest;
                try { latest = readState(); } catch (IOException) { await Task.Delay(10, cancellation.Token); continue; }
                if (MonitorRejection(initial, latest, DateTimeOffset.UtcNow, options) is string reason) throw new TrialStoppedException(reason);
                if (localStop?.Invoke(latest) is string stopReason) throw new TrialStoppedException(stopReason);
                await Task.WhenAny(work, Task.Delay(10, cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return await work;
        }
        catch
        {
            cancellation.Cancel();
            try { await work; } catch { /* Observe cancellation/fault before releasing the HTTP task. */ }
            throw;
        }
    }

    static async Task<bool> WaitAsync(int milliseconds, CancellationToken token) { await Task.Delay(Math.Max(0, milliseconds), token); return true; }

    internal static bool CanApply(JsonObject original, JsonObject latest, DateTimeOffset now, Options options)
        => Rejection(original, now, options.MaxAgeMs) == null
           && Rejection(latest, now, options.MaxAgeMs) == null
           && latest["control_enabled"]?.GetValue<bool>() == true
           && original["scene"]?.ToJsonString() == latest["scene"]?.ToJsonString()
           && original["session_id"]?.GetValue<string>() is string session && session.Length > 0
           && session == latest["session_id"]?.GetValue<string>()
           && (latest["episode"]?["training"]?.GetValue<bool>() != true
               || JsonNode.DeepEquals(original["episode"]?["hits"], latest["episode"]?["hits"]))
           && latest["sequence"]!.GetValue<long>() >= original["sequence"]!.GetValue<long>();

    internal static JsonObject Command(string action, long sequence, int duration, DateTimeOffset now, string sessionId, int attackPressMs = 0)
    {
        if (duration is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(duration));
        if (attackPressMs is not (0 or 50)) throw new ArgumentOutOfRangeException(nameof(attackPressMs));
        if (!Actions.TryGetValue(action, out var buttons)) throw new InvalidDataException("Unknown model action.");
        var command = JsonSerializer.SerializeToNode(buttons)!.AsObject();
        command["id"] = Guid.NewGuid().ToString("N");
        command["session_id"] = sessionId;
        command["observed_sequence"] = sequence;
        command["created_utc"] = now.ToString("O");
        command["duration_ms"] = duration;
        command["attack_press_ms"] = attackPressMs;
        return command;
    }

    internal static void AtomicWrite(string path, JsonObject content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content.ToJsonString(), new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 4) throw new IOException($"Publishing local JSON file '{Path.GetFullPath(path)}' failed after 5 attempts.", error);
                    Thread.Sleep(5);
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void WriteDisplayRecord(string output, JsonObject record)
    {
        AtomicWrite(Path.Combine(output, "latest.json"), record);
        if (record["request"] != null && record["response"] != null)
            AtomicWrite(Path.Combine(output, "last-completed.json"), record);
    }

    internal static async Task RunAsync(Options options, bool live, HttpMessageHandler? testHandler = null)
    {
        if (live && testHandler == null && options.Model != "jev-1.13.0")
            throw new InvalidOperationException("Live requests require the priced, pinned model jev-1.13.0.");
        // Only one live controller may own this native command directory.
        using var liveLock = live ? new FileStream(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Command))!, "controller.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
        var paidBudget = live && testHandler == null ? PaidBudget.Open() : null;
        if (live && paidBudget == null && CandidateBudgetRejection(options) is string budgetRejection)
            throw new InvalidOperationException(budgetRejection);
        if (paidBudget != null && (options.Model != "jev-1.13.0" || options.Seconds > 600 || options.MaxCalls > 6000))
            throw new InvalidOperationException("Budgeted fight must use pinned Jev, <=600s and <=6000 calls");
        var key = live ? (testHandler == null ? Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") : "offline-test") : null;
        if (live && string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Set TYPESAFE_API_KEY locally before trial.");
        var stop = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Command))!, "STOP");
        if (live && File.Exists(stop)) throw new InvalidOperationException("STOP file is present.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var client = testHandler == null ? new HttpClient() : new HttpClient(testHandler);
        client.Timeout = TimeSpan.FromSeconds(10);
        if (live) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var path = Path.Combine(options.Output, $"{(live ? "trial" : "observe")}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
        using var log = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        Console.WriteLine($"Recording {path}. Limit {options.Seconds}s / {options.MaxCalls} calls.");
        long lastSequence = -1;
        string sessionId = "";
        int calls = 0;
        DateTimeOffset? previousRequestStart = null;
        JsonObject? previousCommand = null;
        JsonObject? lastAppliedState = null;
        JsonObject? trialAnchor = null;
        string stopReason = "limit_reached";
        string? errorType = null;
        string? ioError = null;
        var history = new List<JsonObject>();
        var stall = new StallWatch(options.StallSeconds);
        var random = new Random(options.Seed);
        paidBudget?.Start();
        try
        {
            while (!cancellation.IsCancellationRequested && (!live || calls < options.MaxCalls))
            {
                if (live && File.Exists(stop)) { stopReason = "stop_file"; break; }
                JsonObject state;
                try { state = ReadState(options.State); }
                catch (IOException) { await Task.Delay(50, cancellation.Token); continue; }
                var sequence = state["sequence"]!.GetValue<long>();
                if (live && EpisodeStop(state) is string episodeStop) { stopReason = episodeStop; break; }
                if (live && trialAnchor != null && MonitorRejection(trialAnchor, state, DateTimeOffset.UtcNow, options) is string interruption) { stopReason = interruption; break; }
                if (live && TrainingControlWait(state, DateTimeOffset.UtcNow, options))
                {
                    await Task.Delay(10, cancellation.Token);
                    continue;
                }
                if (live && trialAnchor != null && stall.Observe(state, DateTimeOffset.UtcNow) is string stalled) { stopReason = stalled; break; }
                if (!DispatchReady(sequence, lastSequence, DateTimeOffset.UtcNow, live ? previousRequestStart : null, options.CadenceMs)) { await Task.Delay(10, cancellation.Token); continue; }
                lastSequence = sequence;
                sessionId = state["session_id"]?.GetValue<string>() ?? "";
                var record = new JsonObject { ["recorded_utc"] = DateTimeOffset.UtcNow.ToString("O"), ["snapshot"] = state.DeepClone() };
                var rejection = Rejection(state, DateTimeOffset.UtcNow, options.MaxAgeMs);
                if (live && state["control_enabled"]?.GetValue<bool>() != true) rejection = "control_disabled";
                if (live && state["scene"]?.GetValue<string>() != AllowedScene(options)) rejection = "outside_trial_scene";
                if (live && sessionId.Length == 0) rejection = "missing_session";
                if (live && options.DecisionMode == "grouped") rejection ??= CandidateTelemetryRejection(state);
                if (rejection != null)
                {
                    record["status"] = rejection;
                    log.WriteLine(record.ToJsonString());
                    WriteDisplayRecord(options.Output, record);
                    if (live && calls > 0) { stopReason = rejection; break; }
                    await Task.Delay(50, cancellation.Token);
                    continue;
                }
                var request = Request(state, options, history);
                Remember(history, state);
                record["request"] = request.DeepClone();
                if (!live)
                {
                    record["status"] = "observed_no_api";
                    log.WriteLine(record.ToJsonString());
                    WriteDisplayRecord(options.Output, record);
                    await Task.Delay(100, cancellation.Token);
                    continue;
                }
                if (paidBudget != null && !paidBudget.Begin()) { stopReason = "spend_limit_reached"; break; }
                calls++;
                trialAnchor ??= state.DeepClone().AsObject();
                stall.Observe(state, DateTimeOffset.UtcNow);
                var requestStart = DateTimeOffset.UtcNow;
                record["request_interval_ms"] = previousRequestStart == null ? null : (requestStart - previousRequestStart.Value).TotalMilliseconds;
                record["previous_lease_remaining_ms_at_request"] = LeaseRemaining(previousCommand, requestStart);
                previousRequestStart = requestStart;
                record["status"] = "awaiting_api";
                log.WriteLine(record.ToJsonString());
                WriteDisplayRecord(options.Output, record);
                var timer = Stopwatch.StartNew();
                using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await AwaitMonitoredAsync(client.PostAsync(Endpoint, content, cancellation.Token), () => ReadState(options.State), () => File.Exists(stop), state, options, cancellation, current => stall.Observe(current, DateTimeOffset.UtcNow));
                var raw = await AwaitMonitoredAsync(response.Content.ReadAsStringAsync(cancellation.Token), () => ReadState(options.State), () => File.Exists(stop), state, options, cancellation, current => stall.Observe(current, DateTimeOffset.UtcNow));
                record["response_ms"] = timer.Elapsed.TotalMilliseconds;
                record["http_status"] = (int)response.StatusCode;
                // Do not retry: each request consumes the explicit trial call budget.
                if (!response.IsSuccessStatusCode)
                {
                    record["status"] = "api_error";
                    log.WriteLine(record.ToJsonString());
                    WriteDisplayRecord(options.Output, record);
                    throw new HttpRequestException($"TypeSafe returned HTTP {(int)response.StatusCode}; no retry.");
                }
                var result = JsonNode.Parse(raw)!.AsObject();
                paidBudget?.Complete(result);
                record["response"] = result.DeepClone();
                var selection = Select(result, options, random);
                var action = selection.Action;
                record["selected_action"] = action;
                if (options.DecisionMode == "grouped") record["decision_groups"] = GroupedDecision(result);
                record["model_argmax_action"] = selection.ArgmaxAction;
                record["deterministic_action_label"] = options.DecisionMode == "compositional" && options.JumpDecision == "noul" ? "Provider categorical choices plus local Noul cutoff" : "Provider categorical choices";
                record["jump_threshold"] = options.JumpThreshold;
                record["jump_decision"] = options.JumpDecision;
                record["jump_framing"] = options.JumpFraming;
                record["goal_position"] = JsonSerializer.SerializeToNode(options.GoalPosition);
                record["spatial_labels"] = options.SpatialLabels;
                record["visit_memory"] = options.VisitMemory;
                record["selection_mode"] = options.SelectionMode;
                record["sampling_seed"] = options.SelectionMode != "argmax" ? options.Seed : null;
                record["selection_draws"] = selection.Draws;
                record["decision_mode"] = options.DecisionMode;
                record["scenario"] = options.Scenario;
                record["combat_relative"] = options.Scenario == "gruz-mother" && (options.CombatRelative || options.CombatCompact);
                record["combat_compact"] = options.CombatCompact;
                record["danger_margin"] = options.CombatCompact ? options.DangerMargin : 0;
                record["attack_mode"] = options.AttackMode;
                record["terrain_format"] = options.TerrainFormat;
                // Evaluate fresh state while the preceding pulse finishes. The
                // input edge spacing constrains publication, not HTTP start.
                var spacingClock = Stopwatch.StartNew();
                while (!PulseReady(previousCommand, DateTimeOffset.UtcNow, options))
                    await AwaitMonitoredAsync(WaitAsync(5, cancellation.Token), () => ReadState(options.State), () => File.Exists(stop), state, options, cancellation, current => stall.Observe(current, DateTimeOffset.UtcNow));
                record["pulse_spacing_wait_ms"] = spacingClock.Elapsed.TotalMilliseconds;
                var latest = ReadState(options.State);
                var identityRetained = latest["sequence"]!.GetValue<long>() - sequence < 10;
                var apply = identityRetained && !cancellation.IsCancellationRequested && !File.Exists(stop) && CanApply(state, latest, DateTimeOffset.UtcNow, options);
                record["status"] = apply ? "published" : "discarded_stale_or_stopped";
                record["observation_age_ms"] = (DateTimeOffset.UtcNow - DateTimeOffset.Parse(state["observed_utc"]!.GetValue<string>())).TotalMilliseconds;
                if (apply)
                {
                    var command = Command(action, sequence, options.DurationMs, DateTimeOffset.UtcNow, sessionId, options.AttackMode == "pulse" ? 50 : 0);
                    var remainingLease = LeaseRemaining(previousCommand, DateTimeOffset.UtcNow);
                    record["previous_lease_remaining_ms_at_publish"] = remainingLease;
                    record["input_gap_ms"] = previousCommand == null ? null : Math.Max(0, -remainingLease!.Value);
                    record["same_buttons_as_previous"] = previousCommand == null ? null : SameButtons(previousCommand, command);
                    AtomicWrite(options.Command, command);
                    record["command"] = command;
                    previousCommand = command;
                    lastAppliedState = state;
                    log.WriteLine(record.ToJsonString());
                    WriteDisplayRecord(options.Output, record);
                    var ackClock = Stopwatch.StartNew();
                    while (ackClock.ElapsedMilliseconds <= options.DurationMs + 150 && !cancellation.IsCancellationRequested)
                    {
                        await Task.Delay(20, cancellation.Token);
                        JsonObject ackState;
                        try { ackState = ReadState(options.State); } catch (IOException) { continue; }
                        if (File.Exists(stop)) throw new TrialStoppedException("stop_file");
                        if (MonitorRejection(state, ackState, DateTimeOffset.UtcNow, options) is string reason) throw new TrialStoppedException(reason);
                        if (TrainingControlWait(ackState, DateTimeOffset.UtcNow, options))
                        {
                            record["status"] = "training_control_interrupted";
                            break;
                        }
                        if (stall.Observe(ackState, DateTimeOffset.UtcNow) is string stallReason) throw new TrialStoppedException(stallReason);
                        if (ackState["control"]?["last_command_id"]?.GetValue<string>() != command["id"]!.GetValue<string>()) continue;
                        record["ack"] = ackState["control"]!.DeepClone();
                        record["ack_ms"] = ackClock.Elapsed.TotalMilliseconds;
                        var bridgeStatus = ackState["control"]?["last_command_result"]?.GetValue<string>() ?? ackState["control"]?["last_status"]?.GetValue<string>();
                        if (bridgeStatus == "rejected_control_disabled" && options.Scenario == "gruz-mother" && ackState["episode"]?["training"]?.GetValue<bool>() == true)
                        {
                            record["status"] = "training_control_interrupted";
                            break;
                        }
                        if (bridgeStatus == "accepted") { record.Remove("ack"); continue; }
                        record["status"] = bridgeStatus == "applied" ? "bridge_applied" : "bridge_" + bridgeStatus;
                        break;
                    }
                    if (record["ack"] == null && record["status"]?.GetValue<string>() != "training_control_interrupted") record["status"] = "ack_timeout";
                    // Start the next evaluation while this finite lease is still active.
                    // Only a new model result can replace it; the controller never renews it itself.
                }
                log.WriteLine(record.ToJsonString());
                WriteDisplayRecord(options.Output, record);
                Console.WriteLine($"#{sequence} {action} | {record["status"]} | {timer.Elapsed.TotalMilliseconds:F0} ms");
                if (apply && record["status"]?.GetValue<string>() is not ("bridge_applied" or "training_control_interrupted"))
                {
                    stopReason = record["status"]!.GetValue<string>();
                    Console.WriteLine("Stopping trial: the bridge did not confirm application of the command.");
                    break;
                }
            }
            // The last selected action gets its bounded duration even when the call cap is reached.
            // Terminal game state / STOP / wall time can still interrupt it immediately.
            if (live && calls >= options.MaxCalls && previousCommand != null && lastAppliedState != null)
            {
                var remaining = LeaseRemaining(previousCommand, DateTimeOffset.UtcNow) ?? 0;
                if (remaining > 0) await AwaitMonitoredAsync(WaitAsync((int)Math.Ceiling(remaining), cancellation.Token), () => ReadState(options.State), () => File.Exists(stop), lastAppliedState, options, cancellation, current => stall.Observe(current, DateTimeOffset.UtcNow));
            }
        }
        catch (TrialStoppedException stopped) { stopReason = stopped.Message; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { stopReason = "cancelled_or_wall_time"; }
        catch (Exception error) { stopReason = "error"; errorType = error.GetType().Name; ioError = error is IOException or UnauthorizedAccessException ? error.ToString() : null; throw; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (live && sessionId.Length > 0) AtomicWrite(options.Command, Command("release", lastSequence, 100, DateTimeOffset.UtcNow, sessionId));
            var finished = new JsonObject { ["status"] = "finished", ["reason"] = stopReason, ["calls"] = calls, ["error_type"] = errorType, ["io_error"] = ioError, ["recorded_utc"] = DateTimeOffset.UtcNow.ToString("O") };
            log.WriteLine(finished.ToJsonString());
            AtomicWrite(Path.Combine(options.Output, "run-status.json"), finished);
        }
    }

    internal static double? LeaseRemaining(JsonObject? command, DateTimeOffset now) => command == null ? null
        : (DateTimeOffset.Parse(command["created_utc"]!.GetValue<string>()).AddMilliseconds(command["duration_ms"]!.GetValue<int>()) - now).TotalMilliseconds;

    internal static bool SameButtons(JsonObject first, JsonObject second) => new[] { "move", "look", "jump", "attack", "jump_repress", "dash" }.All(k => JsonNode.DeepEquals(first[k], second[k]));
}

internal sealed class TrialStoppedException(string reason) : Exception(reason);

internal sealed record Buttons(int move, int look, bool jump, bool attack, bool jump_repress = false, bool dash = false);
internal sealed record Selection(string Action, string ArgmaxAction, JsonObject Draws);

internal sealed class StallWatch(int seconds)
{
    DateTimeOffset? since;
    double x, y;
    internal string? Observe(JsonObject state, DateTimeOffset now)
    {
        if (seconds == 0) return null;
        var player = state["observation"]!["player"]!;
        var nextX = player["x"]!.GetValue<double>();
        var nextY = player["y"]!.GetValue<double>();
        if (since == null || Math.Sqrt(Math.Pow(nextX - x, 2) + Math.Pow(nextY - y, 2)) > .25)
        {
            since = now; x = nextX; y = nextY;
        }
        return (now - since.Value).TotalSeconds >= seconds ? "local_stall" : null;
    }
}

internal sealed record GoalPosition(double destination_x, double destination_y);
internal sealed record Options(string State, string Command, string Output, int Seconds, int MaxCalls, int DurationMs, int MaxAgeMs, string Model, string? Input, int CadenceMs, string DecisionMode, string AttackMode, string TerrainFormat, int History, int StallSeconds, string SelectionMode, int Seed, double JumpThreshold, string JumpDecision, string JumpFraming, GoalPosition? GoalPosition, bool SpatialLabels, bool VisitMemory, string Scenario, bool CombatRelative, bool CombatCompact, double DangerMargin)
{
    internal static Options Parse(string[] args, int maxSeconds = 600)
    {
        var fields = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Options require --name value.");
            if (!new[] { "--state", "--command", "--output", "--seconds", "--max-calls", "--duration-ms", "--cadence-ms", "--max-age-ms", "--model", "--input", "--decision-mode", "--attack-mode", "--terrain-format", "--history", "--stall-seconds", "--selection", "--seed", "--jump-threshold", "--jump-decision", "--jump-framing", "--goal-position", "--spatial-labels", "--visit-memory", "--scenario", "--combat-relative", "--combat-compact", "--danger-margin" }.Contains(args[i])) throw new ArgumentException("Unknown option: " + args[i]);
            fields.Add(args[i], args[i + 1]);
        }
        string Get(string key, string fallback) => fields.GetValueOrDefault(key, fallback);
        int Bounded(string key, int fallback, int maximum)
        {
            var n = int.Parse(Get(key, fallback.ToString()));
            return n >= 1 && n <= maximum ? n : throw new ArgumentOutOfRangeException(key, $"Must be 1..{maximum}.");
        }
        var scenario = Get("--scenario", "tutorial");
        if (scenario is not ("tutorial" or "gruz-mother")) throw new ArgumentException("scenario must be tutorial or gruz-mother.");
        var mode = Get("--decision-mode", scenario == "gruz-mother" ? "locomotion" : "combined");
        if (mode is not ("combined" or "compositional" or "locomotion" or "grouped")) throw new ArgumentException("decision-mode must be combined, compositional, locomotion, or grouped.");
        var attackMode = Get("--attack-mode", "hold");
        if (attackMode is not ("hold" or "pulse")) throw new ArgumentException("attack-mode must be hold or pulse.");
        var cadence = Bounded("--cadence-ms", 100, 2000);
        if (attackMode == "pulse" && cadence < 100) throw new ArgumentException("Pulse attack mode requires cadence-ms >= 100.");
        var terrainFormat = Get("--terrain-format", "grid");
        if (terrainFormat is not ("grid" or "spans" or "rectangles" or "geometry")) throw new ArgumentException("terrain-format must be grid, spans, rectangles, or geometry.");
        int OptionalBounded(string key, int maximum)
        {
            var value = int.Parse(Get(key, "0"));
            return value >= 0 && value <= maximum ? value : throw new ArgumentOutOfRangeException(key, $"Must be 0..{maximum}.");
        }
        var selection = Get("--selection", "argmax");
        if (selection is not ("argmax" or "sample" or "sample-jump" or "sample-locomotion")) throw new ArgumentException("selection must be argmax, sample, sample-jump, or sample-locomotion.");
        if (selection == "sample-jump" && mode != "compositional") throw new ArgumentException("sample-jump requires compositional decisions.");
        if (selection == "sample-locomotion" && mode != "locomotion") throw new ArgumentException("sample-locomotion requires locomotion decisions.");
        if (scenario == "gruz-mother" && selection == "sample") throw new ArgumentException("Boss scenario requires deterministic attack selection; use argmax, sample-locomotion, or sample-jump.");
        var jumpDecision = Get("--jump-decision", "noul");
        if (jumpDecision is not ("noul" or "choice")) throw new ArgumentException("jump-decision must be noul or choice.");
        var jumpFraming = Get("--jump-framing", "buttons");
        if (jumpFraming is not ("buttons" or "mechanics")) throw new ArgumentException("jump-framing must be buttons or mechanics.");
        var threshold = double.Parse(Get("--jump-threshold", "0.5"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 1) throw new ArgumentOutOfRangeException("--jump-threshold", "Must be finite and 0..1.");
        GoalPosition? goalPosition = null;
        if (fields.TryGetValue("--goal-position", out var coordinateText))
        {
            var parts = coordinateText.Split(',');
            if (parts.Length != 2) throw new ArgumentException("goal-position requires x,y.");
            var coordinates = parts.Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (coordinates.Any(p => !double.IsFinite(p))) throw new ArgumentException("goal-position requires finite coordinates.");
            goalPosition = new(coordinates[0], coordinates[1]);
        }
        var spatialLabels = bool.Parse(Get("--spatial-labels", "false"));
        var combatCompact = bool.Parse(Get("--combat-compact", "false"));
        var dangerMargin = double.Parse(Get("--danger-margin", "0"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(dangerMargin) || dangerMargin < 0 || dangerMargin > 20)
            throw new ArgumentOutOfRangeException("--danger-margin", "Must be finite and 0..20 world units.");
        if (dangerMargin > 0 && !combatCompact) throw new ArgumentException("danger-margin requires combat-compact true.");
        if (combatCompact && (scenario != "gruz-mother" || mode is not ("compositional" or "grouped") || jumpDecision != "noul"))
            throw new ArgumentException("combat-compact requires gruz-mother, compositional decisions, and jump-decision noul.");
        if (mode == "grouped" && (!combatCompact || scenario != "gruz-mother" || selection != "argmax")) throw new ArgumentException("grouped requires compact Gruz Mother and argmax.");
        return new(Get("--state", "../runtime/state.json"), Get("--command", "../runtime/command.json"), Get("--output", "artifacts"), Bounded("--seconds", 15, maxSeconds), Bounded("--max-calls", 10, 6000), Bounded("--duration-ms", 300, 300), Bounded("--max-age-ms", 500, 2000), Get("--model", "jev-1.13.0"), fields.GetValueOrDefault("--input"), cadence, mode, attackMode, terrainFormat, OptionalBounded("--history", 4), OptionalBounded("--stall-seconds", 120), selection, int.Parse(Get("--seed", "42")), threshold, jumpDecision, jumpFraming, goalPosition, spatialLabels, bool.Parse(Get("--visit-memory", "false")), scenario, bool.Parse(Get("--combat-relative", "false")), combatCompact, dangerMargin);
    }
}
