using System.Text.Json.Nodes;

internal static class Tests
{
    internal static JsonObject Fixture(DateTimeOffset now) => JsonNode.Parse("""
    {"schema_version":1,"session_id":"test-session","sequence":10,"scene":"Tutorial_01","observed_utc":"2000-01-01T00:00:00Z","gameplay":true,"paused":false,"control_enabled":true,"debug":{"jump_now":true},"observation":{"player":{"x":1,"y":2,"vx":0,"vy":0,"width":0.6,"height":1.1,"grounded":true,"facing":1,"health":5,"recommended_action":"jump"},"terrain":{"origin_x":-1,"origin_y":-1,"cell_size":0.5,"width":4,"height":4,"rows":["....","....","####","####"],"jump_now":true},"enemies":[],"objects":[],"held":{"move":0,"look":0,"jump":false,"attack":false},"advice":"jump now"}}
    """)!.AsObject().WithTimestamp(now);

    static JsonObject WithTimestamp(this JsonObject obj, DateTimeOffset now) { obj["observed_utc"] = now.ToString("O"); return obj; }

    internal static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); count++; Console.WriteLine("PASS " + name); }
        void Throws(Action action, string name) { try { action(); } catch (Exception) { count++; Console.WriteLine("PASS " + name); return; } throw new Exception("FAILED: " + name); }
        var now = DateTimeOffset.UtcNow;
        var state = Fixture(now);
        var options = Options.Parse([]);
        var combatOptions = Options.Parse(["--scenario", "gruz-mother"]);
        var combatState = state.DeepClone().AsObject();
        combatState["scene"] = "GG_Gruz_Mother";
        combatState["observation"]!["arena"] = JsonNode.Parse("""{"left":1,"right":30,"floor":4,"ceiling":18,"safe_corner":"left"}""");
        combatState["observation"]!["enemies"] = JsonNode.Parse("""[{"x":3.25,"y":2.5,"width":2.1,"height":2.7,"vx":-4,"vy":1,"hp":400,"max_hp":450,"boss":true,"animation":"Fly","state":"Idle","dodge_now":true}]""");
        combatState["observation"]!["boss_state"] = "Idle";
        combatState["episode"] = JsonNode.Parse("""{"status":"running","hits_taken":0,"target_x":123}""");
        var combatRequest = Controller.Request(combatState, combatOptions with { VisitMemory = true, SpatialLabels = true, History = 4, GoalPosition = new(999, 999), TerrainFormat = "geometry" });
        var combatModel = combatRequest["state"]!.AsObject();
        Check(combatOptions.DecisionMode == "locomotion" && combatOptions.SelectionMode == "argmax" && Controller.AllowedScene(combatOptions) == "GG_Gruz_Mother" && Controller.AllowedScene(options) == "Tutorial_01", "boss profile defaults deterministic locomotion and exact catalog scene while tutorial defaults persist");
        Check(combatModel.Count == 5 && combatModel["player"]!.AsObject().Count == 9 && combatModel["arena"]!.AsObject().Count == 4 && combatModel["enemies"]![0]!.AsObject().Count == 11 && combatModel["enemies"]![0]!["hp"]!.GetValue<int>() == 400 && combatModel["boss_state"]!.GetValue<string>() == "Idle", "combat whitelist preserves exact bounds/health/arena/raw labels without injected fields");
        var beforeRelative = combatState.ToJsonString();
        var relativeCombatOptions = combatOptions with { CombatRelative = true };
        var relativeCombatRequest = Controller.Request(combatState, relativeCombatOptions);
        var relativeCombat = relativeCombatRequest["state"]!;
        Check(relativeCombat["player"]!["x"]!.GetValue<double>() == 0 && relativeCombat["player"]!["y"]!.GetValue<double>() == 0 && relativeCombat["arena"]!["left"]!.GetValue<double>() == 0 && relativeCombat["arena"]!["right"]!.GetValue<double>() == 29 && relativeCombat["arena"]!["floor"]!.GetValue<double>() == 2 && relativeCombat["arena"]!["ceiling"]!.GetValue<double>() == 16, "relative combat shifts player and arena around current player center exactly");
        Check(combatState.ToJsonString() == beforeRelative && JsonNode.DeepEquals(relativeCombat["enemies"], combatModel["enemies"]) && JsonNode.DeepEquals(relativeCombat["held"], combatModel["held"]) && JsonNode.DeepEquals(relativeCombat["player"]!["vx"], combatModel["player"]!["vx"]), "relative combat never mutates snapshot, enemy coordinates, held inputs or velocity");
        var translatedCombat = combatState.DeepClone().AsObject();
        translatedCombat["observation"]!["player"]!["x"] = 101.0; translatedCombat["observation"]!["player"]!["y"] = 52.0;
        translatedCombat["observation"]!["arena"] = JsonNode.Parse("""{"left":101,"right":130,"floor":54,"ceiling":68}""");
        Check(JsonNode.DeepEquals(Controller.Request(translatedCombat, relativeCombatOptions), relativeCombatRequest), "relative combat request is invariant under equal player/arena world translation");
        Check(relativeCombatRequest["questions"]!["locomotion"]!["instructions"]!.GetValue<string>().Contains("All positions are relative") && !relativeCombatRequest["questions"]!["locomotion"]!["instructions"]!.GetValue<string>().Contains("absolute world coordinates") && !Options.Parse([]).CombatRelative, "relative combat legend is explicit and option remains disabled by default");
        Throws(() => Options.Parse(["--combat-relative", "yes"]), "combat-relative option requires true or false");
        Check(new[] { "terrain", "surfaces", "objects", "exploration", "objective", "recent_player_states", "episode" }.All(k => combatModel[k] == null) && !combatRequest.ToJsonString().Contains("giant door") && !combatRequest.ToJsonString().Contains("deprioritize"), "combat profile excludes tutorial perception, route metadata, visit policy and goal instructions even when requested");
        Check(combatRequest["questions"]!["attack"]!["instructions"]!.GetValue<string>().Contains("without taking any hits") && combatRequest["questions"]!["attack"]!["instructions"]!.GetValue<string>().Contains("Attack when there is a safe opening"), "combat goal prioritizes avoiding contact with opportunistic attacks");
        Check(combatRequest["questions"]!["locomotion"]!["instructions"]!.GetValue<string>().Contains("Dash, spells, and healing inputs are unavailable") && combatRequest["questions"]!["attack"]!["instructions"]!.GetValue<string>().Contains("upgrade flags report inventory, not additional inputs"), "combat instructions distinguish owned upgrades from available controller buttons");
        var combatFlags = combatState.DeepClone().AsObject();
        foreach (var flag in new[] { "touching_wall_left", "touching_wall_right", "attacking", "attack_ready", "double_jumped" }) combatFlags["observation"]!["player"]![flag] = true;
        var projectedFlags = Controller.CombatState(combatFlags)["player"]!.AsObject();
        Check(projectedFlags.Count == 14 && new[] { "touching_wall_left", "touching_wall_right", "attacking", "attack_ready", "double_jumped" }.All(flag => projectedFlags[flag]!.GetValue<bool>()), "combat preserves supplied raw wall/attack/double-jump flags without deriving actions");
        var compactOptions = Options.Parse(["--scenario", "gruz-mother", "--decision-mode", "compositional", "--combat-compact", "true", "--attack-mode", "pulse"]);
        var compactSource = combatFlags.DeepClone().AsObject();
        compactSource["observation"]!["abilities"] = JsonNode.Parse("""{"run_speed":8.3,"jump_speed":16.65,"has_dash":true,"gravity_y":-60,"dodge_now":true}""");
        var compactBefore = compactSource.ToJsonString();
        var compactRequest = Controller.Request(compactSource, compactOptions);
        var compactModel = compactRequest["state"]!;
        Check(compactModel["player"]!["left"]!.GetValue<double>() == -.3 && compactModel["player"]!["right"]!.GetValue<double>() == .3 && compactModel["player"]!["bottom"]!.GetValue<double>() == -.55 && compactModel["player"]!["top"]!.GetValue<double>() == .55 && compactModel["player"]!["x"] == null && compactModel["player"]!["width"] == null, "compact player edges replace center and size around player origin");
        Check(compactModel["enemies"]![0]!["left"]!.GetValue<double>() == 2.2 && compactModel["enemies"]![0]!["right"]!.GetValue<double>() == 4.3 && compactModel["enemies"]![0]!["bottom"]!.GetValue<double>() == 1.15 && compactModel["enemies"]![0]!["top"]!.GetValue<double>() == 3.85 && compactModel["arena"]!["right"]!.GetValue<double>() == 29, "compact enemy and arena edges preserve measured relative geometry");
        Check(compactModel.AsObject().Count == 6 && compactModel["run_speed"]!.GetValue<double>() == 8.3 && compactModel["jump_speed"]!.GetValue<double>() == 16.65 && compactModel["abilities"] == null && compactModel["boss_state"] == null && !compactModel.ToJsonString().Contains("dodge_now") && !compactModel.ToJsonString().Contains("recommended_action"), "compact state keeps only two speed constants and strict raw combat whitelist");
        Check(compactSource.ToJsonString() == compactBefore && JsonNode.DeepEquals(compactModel["held"], compactSource["observation"]!["held"]) && compactModel["player"]!["attack_ready"]!.GetValue<bool>() && compactModel["enemies"]![0]!["hp"]!.GetValue<int>() == 400 && compactModel["enemies"]![0]!["state"]!.GetValue<string>() == "Idle", "compact transform preserves original snapshot, raw flags, held inputs, health and current labels");
        Check(JsonNode.DeepEquals(Controller.CompactCombatState(translatedCombat), Controller.CompactCombatState(combatState)), "compact edge representation is invariant under player and arena world translation");
        var compactQuestions = compactRequest["questions"]!;
        Check(compactQuestions.AsObject().Count == 3 && compactQuestions["contact"] == null && compactQuestions["jump"]!["type"]!.GetValue<string>() == "noul" && compactQuestions["move"]!["criteria"]!["still"]!.GetValue<string>() == "Remaining still avoids contact; no horizontal evasion is needed.", "compact request uses calibrated movement criteria and independent jump without contact gating");
        Check(!compactRequest.ToJsonString().Contains("Default to staying still") && !compactRequest.ToJsonString().Contains("jump_sustain_steps") && compactQuestions["attack"]!["instructions"]!.GetValue<string>().Contains("50ms") && compactQuestions["jump"]!["instructions"]!.GetValue<string>().Contains("cannot start another jump"), "compact questions omit repeated long policy while retaining finite attack pulse and jump edge semantics");
        Check(!Options.Parse([]).CombatCompact && JsonNode.DeepEquals(Controller.Request(combatState, combatOptions), Controller.Request(combatState, combatOptions with { CombatCompact = false })), "compact option remains disabled and baseline request is unchanged");
        var blockedSource = compactSource.DeepClone().AsObject();
        blockedSource["observation"]!["player"]!["blocked_left"] = true;
        blockedSource["observation"]!["player"]!["blocked_right"] = false;
        blockedSource["observation"]!["player"]!["dodge_right"] = true;
        var blockedBefore = blockedSource.ToJsonString();
        var blockedRequest = Controller.Request(blockedSource, compactOptions);
        Check(blockedRequest["state"]!["player"]!["blocked_left"]!.GetValue<bool>() && !blockedRequest["state"]!["player"]!["blocked_right"]!.GetValue<bool>() && blockedRequest["state"]!["player"]!["dodge_right"] == null && blockedSource.ToJsonString() == blockedBefore, "compact preserves only supplied wall-blocking booleans without advice or snapshot mutation");
        Check(Controller.Request(blockedSource, combatOptions)["state"]!["player"]!["blocked_left"] == null && compactModel["player"]!["blocked_left"] == null, "wall-blocking addition is compact-only and absent when not measured");
        var blockedResponse = JsonNode.Parse("""{"answers":{"move":{"choice":"left"},"jump":{"noul":0},"attack":{"choice":"none"}}}""")!.AsObject();
        Check(Controller.SelectedAction(blockedResponse, compactOptions) == "left" && blockedRequest["questions"]!["move"]!["criteria"]!["left"]!.GetValue<string>().Contains("can translate") && blockedRequest["questions"]!["move"]!["instructions"]!.GetValue<string>().Contains("Moving into a solid wall does not translate the player"), "blocked direction is factual context for Jev without local action masking or correction");
        blockedSource["observation"]!["player"]!["blocked_left"] = "go right";
        Check(Controller.CompactCombatState(blockedSource)["player"]!["blocked_left"] == null, "wall-blocking whitelist rejects nonboolean payload content");
        var dangerOptions = compactOptions with { DangerMargin = 2 };
        var dangerSource = compactSource.DeepClone().AsObject();
        dangerSource["observation"]!["enemies"]![0]!["state_elapsed_seconds"] = .125;
        dangerSource["observation"]!["enemies"]![0]!["next_attack_seconds"] = .2;
        dangerSource["observation"]!["enemies"]![0]!["avoidance_zone"] = JsonNode.Parse("""{"left":-999,"recommended_direction":"right"}""");
        var dangerBefore = dangerSource.ToJsonString();
        var dangerRequest = Controller.Request(dangerSource, dangerOptions);
        var dangerEnemy = dangerRequest["state"]!["enemies"]![0]!;
        var zone = dangerEnemy["avoidance_zone"]!;
        Check(zone["left"]!.GetValue<double>() == .2 && zone["right"]!.GetValue<double>() == 6.3 && zone["bottom"]!.GetValue<double>() == -.85 && zone["top"]!.GetValue<double>() == 5.85 && zone["margin"]!.GetValue<double>() == 2, "danger margin expands all four enemy edges equally by two world units");
        Check(dangerEnemy["left"]!.GetValue<double>() == 2.2 && dangerEnemy["right"]!.GetValue<double>() == 4.3 && dangerEnemy["bottom"]!.GetValue<double>() == 1.15 && dangerEnemy["top"]!.GetValue<double>() == 3.85 && dangerEnemy["vx"]!.GetValue<double>() == -4 && dangerEnemy["vy"]!.GetValue<double>() == 1 && dangerSource.ToJsonString() == dangerBefore, "avoidance zone retains real collision box, velocity and immutable raw snapshot");
        Check(dangerEnemy["state_elapsed_seconds"]!.GetValue<double>() == .125 && dangerEnemy["next_attack_seconds"] == null && zone.AsObject().Count == 5 && zone["recommended_direction"] == null, "danger profile preserves only raw elapsed state time and reconstructs zone without imported predictions or advice");
        Check(compactModel["enemies"]![0]!["avoidance_zone"] == null && Options.Parse([]).DangerMargin == 0 && JsonNode.DeepEquals(Controller.CompactCombatState(translatedCombat, 2), Controller.CompactCombatState(combatState, 2)), "zero margin omits zones and expanded geometry remains translation invariant");
        Check(dangerRequest["questions"]!["move"]!["instructions"]!.GetValue<string>().Contains("configured clearance region") && dangerRequest["questions"]!["move"]!["criteria"]!["still"]!.GetValue<string>().Contains("outside avoidance zones") && dangerRequest["questions"]!["attack"]!["instructions"]!.GetValue<string>().Contains("not a prohibition on attacking"), "expanded-zone policy distinguishes clearance from actual hitbox and retains stationary-safe and opportunistic attack semantics");
        Check(Controller.SelectedAction(blockedResponse, dangerOptions) == "left", "danger zone never masks or substitutes provider selected movement");
        foreach (var invalidElapsed in new JsonNode[] { JsonValue.Create(-.1)!, JsonValue.Create("dodge now")! })
        {
            dangerSource["observation"]!["enemies"]![0]!["state_elapsed_seconds"] = invalidElapsed;
            Check(Controller.CompactCombatState(dangerSource, 2)["enemies"]![0]!["state_elapsed_seconds"] == null, "raw elapsed state time excludes negative or nonnumeric content");
        }
        Check(Options.Parse(["--scenario", "gruz-mother", "--decision-mode", "compositional", "--combat-compact", "true", "--danger-margin", "2"]).DangerMargin == 2, "danger-margin CLI accepts explicit finite world-unit padding");
        foreach (var invalidMargin in new[] { "NaN", "Infinity", "-1", "20.1" })
            Throws(() => Options.Parse(["--danger-margin", invalidMargin]), "invalid danger margin rejected: " + invalidMargin);
        Throws(() => Options.Parse(["--danger-margin", "2"]), "noncompact profiles reject positive danger margin");
        Throws(() => Controller.CompactCombatState(compactSource, double.NaN), "projection independently rejects nonfinite danger margin");
        foreach (var invalidCompact in new[] { new[] { "--combat-compact", "true" }, new[] { "--scenario", "gruz-mother", "--combat-compact", "true" }, new[] { "--scenario", "gruz-mother", "--decision-mode", "compositional", "--combat-compact", "true", "--jump-decision", "choice" }, new[] { "--combat-compact", "yes" } })
            Throws(() => Options.Parse(invalidCompact), "compact rejects unsupported scenario/decision/primitive or invalid boolean");
        Check(combatRequest["questions"]!["locomotion"]!["instructions"]!.GetValue<string>().Contains("A new ground, double, or wall jump input requires releasing jump and then pressing it again"), "combat prompt describes verified fresh jump press semantics");
        foreach (var mode in new[] { "combined", "compositional", "locomotion" }) Check(JsonNode.DeepEquals(Controller.Request(combatState, combatOptions with { DecisionMode = mode })["state"], combatModel), "combat state identical across " + mode + " decisions");
        Throws(() => Options.Parse(["--scenario", "gruz-mother", "--selection", "sample"]), "combat rejects sampling attack choices");
        Throws(() => Options.Parse(["--scenario", "other"]), "unknown scenario rejected");
        var failedHit = combatState.DeepClone().AsObject(); failedHit["episode"]!["status"] = "failed_hit";
        Check(Controller.Rejection(failedHit, now, 500) == "episode_failed_hit" && !Controller.CanApply(combatState, failedHit, now, combatOptions), "first-hit episode blocks command application regardless of remaining health");
        var training = combatState.DeepClone().AsObject();
        training["episode"] = JsonNode.Parse("""{"status":"running","training":true,"hits":1}""");
        training["control_enabled"] = false;
        Check(Controller.TrainingControlWait(training, now, combatOptions) && Controller.MonitorRejection(combatState, training, now, combatOptions) == null && !Controller.CanApply(combatState, training, now, combatOptions), "training recoil waits while still refusing commands during unavailable controls");
        var recoveredTraining = training.DeepClone().AsObject(); recoveredTraining["control_enabled"] = true;
        Check(!Controller.TrainingControlWait(recoveredTraining, now, combatOptions) && !Controller.CanApply(combatState, recoveredTraining, now, combatOptions) && Controller.CanApply(recoveredTraining, recoveredTraining, now, combatOptions), "recovered training requires a fresh decision after changed hit count");
        var strictRecoil = training.DeepClone().AsObject(); strictRecoil["episode"]!["training"] = false;
        Check(Controller.MonitorRejection(combatState, strictRecoil, now, combatOptions) == "control_disabled", "ordinary hitless control loss still stops rather than entering training wait");
        training["observed_utc"] = now.AddSeconds(-3).ToString("O");
        Check(!Controller.TrainingControlWait(training, now, combatOptions) && Controller.MonitorRejection(combatState, training, now, combatOptions) == "stale_observation", "training never waives observation freshness");
        training["observed_utc"] = now.ToString("O");
        using (var trainingCancellation = new CancellationTokenSource())
        {
            var completed = await Controller.AwaitMonitoredAsync(Task.Run(async () => { await Task.Delay(25); return 7; }), () => training.WithTimestamp(DateTimeOffset.UtcNow), () => false, combatState, combatOptions, trainingCancellation);
            Check(completed == 7 && !trainingCancellation.IsCancellationRequested, "an already pending evaluation can finish during training recoil without cancelling the bounded run");
        }
        var trainingComplete = recoveredTraining.DeepClone().AsObject(); trainingComplete["episode"]!["status"] = "training_complete";
        Check(Controller.EpisodeStop(trainingComplete) == "episode_training_complete" && !Controller.CanApply(recoveredTraining, trainingComplete, now, combatOptions), "training completion has a distinct terminal reason and never claims hitless success");
        var waitDirectory = Directory.CreateTempSubdirectory("jev-training-wait-test-");
        try
        {
            var waitState = Path.Combine(waitDirectory.FullName, "state.json");
            var waitCommand = Path.Combine(waitDirectory.FullName, "command.json");
            Controller.AtomicWrite(waitState, training.WithTimestamp(DateTimeOffset.UtcNow));
            var noCalls = new FakeTypeSafe();
            await Controller.RunAsync(combatOptions with { State = waitState, Command = waitCommand, Output = waitDirectory.FullName, Seconds = 1, MaxAgeMs = 2000 }, true, noCalls);
            Check(noCalls.Calls == 0 && !File.Exists(waitCommand) && Controller.ReadState(Path.Combine(waitDirectory.FullName, "run-status.json"))["reason"]!.GetValue<string>() == "cancelled_or_wall_time", "training waits without HTTP or command publication and remains bounded by wall time");
        }
        finally { foreach (var file in waitDirectory.GetFiles()) file.Delete(); waitDirectory.Delete(); }
        using (var hitCancellation = new CancellationTokenSource())
        {
            var pendingHit = new TaskCompletionSource<bool>();
            using var registration = hitCancellation.Token.Register(() => pendingHit.TrySetCanceled(hitCancellation.Token));
            bool stopped = false;
            try { await Controller.AwaitMonitoredAsync(pendingHit.Task, () => failedHit, () => false, combatState, combatOptions, hitCancellation); }
            catch (TrialStoppedException e) { stopped = e.Message == "episode_failed_hit"; }
            Check(stopped && pendingHit.Task.IsCanceled, "failed_hit cancels pending evaluation immediately");
        }
        var previousDisplay = new JsonObject { ["snapshot"] = new JsonObject { ["session_id"] = "s", ["sequence"] = 10 }, ["request"] = new JsonObject { ["id"] = "old" }, ["response"] = new JsonObject { ["id"] = "old-response" } };
        var pendingDisplay = new JsonObject { ["snapshot"] = new JsonObject { ["session_id"] = "s", ["sequence"] = 11 }, ["request"] = new JsonObject { ["id"] = "new" }, ["status"] = "awaiting_api" };
        var matchedDisplay = Dashboard.CompletedDecision(pendingDisplay, previousDisplay, "s")!;
        Check(matchedDisplay["request"]!["id"]!.GetValue<string>() == "old" && matchedDisplay["response"]!["id"]!.GetValue<string>() == "old-response" && matchedDisplay["snapshot"]!["sequence"]!.GetValue<int>() == 10, "pending dashboard evaluation retains matching completed request/response pair");
        Check(Dashboard.CompletedDecision(pendingDisplay, previousDisplay, "different-session") == null && Dashboard.CompletedDecision(pendingDisplay, null, "s") == null, "dashboard never pairs pending request with prior response or stale game session");
        state["episode"] = new JsonObject { ["status"] = "running", ["target_x"] = 900, ["instructions"] = "go right" };
        state["observation"]!["player"]!["touching_wall_left"] = false;
        state["observation"]!["player"]!["touching_wall_right"] = true;
        var modelState = Controller.ModelState(state).ToJsonString();
        Check(!modelState.Contains("jump_now") && !modelState.Contains("recommended_action") && !modelState.Contains("advice") && !modelState.Contains("session") && !modelState.Contains("scene"), "model state excludes metadata and injected advice at every level");
        Check(!modelState.Contains("episode") && !modelState.Contains("target_x") && !modelState.Contains("go right"), "local episode goal detector is excluded from model state");
        Check(Controller.ModelState(state)["player"]!["touching_wall_right"]!.GetValue<bool>(), "raw engine wall-contact flags pass through current player whitelist");
        Check(Controller.Request(state, options)["questions"]!.AsObject().Count == 1, "only action question; no leading secondary judgments");
        var destinationOptions = Options.Parse(["--goal-position", "190.147552,68.90694"]);
        Check(Controller.Request(state, options)["state"]!["objective"] == null && Controller.Request(state, destinationOptions)["state"]!["objective"]!["destination_x"]!.GetValue<double>() == 190.147552 && Controller.Request(state, destinationOptions)["state"]!["objective"]!["destination_y"]!.GetValue<double>() == 68.90694, "measured destination is absent by default and preserves exact opt-in coordinates");
        Check(Controller.ModelState(state)["objective"] == null && Controller.Request(state, destinationOptions)["state"]!["objective"]!.AsObject().Count == 2, "destination adds only two explicit objective coordinates without changing telemetry whitelist");
        foreach (var invalid in new[] { "NaN,1", "1,Infinity", "1,2,3", "1" }) Throws(() => Options.Parse(["--goal-position", invalid]), "invalid destination rejected: " + invalid);
        var compositional = options with { DecisionMode = "compositional" };
        var locomotionOptions = options with { DecisionMode = "locomotion" };
        var locomotionRequest = Controller.Request(state, locomotionOptions);
        Check(locomotionRequest["questions"]!.AsObject().Count == 2 && locomotionRequest["questions"]!["locomotion"]!["criteria"]!.AsObject().Count == 6 && locomotionRequest["questions"]!["jump"] == null, "locomotion mode asks one six-option movement/jump Choice plus separate attack Choice");
        Check(JsonNode.DeepEquals(locomotionRequest["state"], Controller.Request(state, compositional)["state"]) && JsonNode.DeepEquals(locomotionRequest["questions"]!["attack"], Controller.Request(state, compositional)["questions"]!["attack"]), "locomotion mode preserves exact raw state and separate attack question");
        foreach (var movement in Controller.Locomotion)
            foreach (var attack in new[] { "none", "forward", "up", "down" })
            {
                var projected = Controller.Actions[Controller.ComposeLocomotion(movement.Key, attack)];
                Check(projected.move == movement.Value.move && projected.jump == movement.Value.jump && projected.attack == (attack != "none") && projected.look == (attack == "up" ? 1 : attack == "down" ? -1 : 0), "locomotion projection preserves " + movement.Key + "/" + attack);
            }
        var locomotionResponse = JsonNode.Parse("""{"answers":{"locomotion":{"choice":"left_jump","probabilities":{"left_jump":0.5,"right":0.5}},"attack":{"choice":"none","probabilities":{"none":1}}}}""")!.AsObject();
        Check(Controller.SelectedAction(locomotionResponse, locomotionOptions) == "left_jump", "deterministic locomotion directly follows provider joint choice");
        var locomotionRandom = new Random(42);
        var locomotionSamples = Enumerable.Range(0, 100).Select(_ => Controller.Select(locomotionResponse, locomotionOptions with { SelectionMode = "sample" }, locomotionRandom)).ToArray();
        Check(locomotionSamples.All(s => s.Action is "left_jump" or "right") && locomotionSamples.Select(s => s.Action).Distinct().Count() == 2 && locomotionSamples.All(s => s.Draws.Count == 2 && s.Draws.ContainsKey("locomotion") && !s.Draws.ContainsKey("jump")), "sampling preserves joint locomotion correlations and logs exact two draws");
        var locomotionOnlyResponse = locomotionResponse.DeepClone().AsObject();
        locomotionOnlyResponse["answers"]!["attack"] = JsonNode.Parse("""{"choice":"forward","probabilities":{"forward":0.6,"none":0.4}}""");
        var locomotionOnlyRandom = new Random(42);
        var locomotionOnlySamples = Enumerable.Range(0, 100).Select(_ => Controller.Select(locomotionOnlyResponse, locomotionOptions with { SelectionMode = "sample-locomotion" }, locomotionOnlyRandom)).ToArray();
        Check(locomotionOnlySamples.All(s => Controller.Actions[s.Action].attack && Controller.Actions[s.Action].look == 0 && s.Draws.Count == 1 && s.Draws.ContainsKey("locomotion")) && locomotionOnlySamples.Select(s => s.Action).Distinct().Count() == 2, "sample-locomotion preserves provider attack choice while drawing only joint movement/jump");
        Throws(() => Options.Parse(["--decision-mode", "compositional", "--selection", "sample-locomotion"]), "sample-locomotion rejects other decision modes");
        Throws(() => Options.Parse(["--decision-mode", "locomotion", "--selection", "sample-jump"]), "locomotion mode rejects independent jump sampling");
        Throws(() => Controller.ComposeLocomotion("invented", "none"), "unknown locomotion rejected");
        var compositeRequest = Controller.Request(state, compositional);
        Check(compositeRequest["questions"]!.AsObject().Count == 3 && compositeRequest["questions"]!["jump"]!["type"]!.GetValue<string>() == "noul", "compositional mode asks only movement, jump-button, and attack questions");
        Check(JsonNode.DeepEquals(compositeRequest["state"], Controller.Request(state, options)["state"]), "compositional mode receives exactly the same raw observations");
        var compositeResponse = JsonNode.Parse("""{"answers":{"move":{"choice":"right"},"jump":{"noul":0.8},"attack":{"choice":"down"}}}""")!.AsObject();
        Check(Controller.SelectedAction(compositeResponse, compositional) == "right_jump_attack_down", "independent answers compose directly into legal combined input");
        compositeResponse["answers"]!["jump"]!["noul"] = .5;
        compositeResponse["answers"]!["attack"]!["choice"] = "none";
        Check(Controller.SelectedAction(compositeResponse, compositional) == "right", "jump tie releases button with explicit fixed threshold");
        var lowerCutoff = compositional with { JumpThreshold = .2 };
        Check(Controller.SelectedAction(compositeResponse, lowerCutoff) == "right_jump", "local cutoff changes only deterministic Noul decoding");
        Check(JsonNode.DeepEquals(Controller.Request(state, compositional), Controller.Request(state, lowerCutoff)), "jump threshold does not change raw model request or instructions");
        compositeResponse["answers"]!["jump"]!["noul"] = .2;
        Check(Controller.SelectedAction(compositeResponse, lowerCutoff) == "right", "custom jump threshold uses strict greater-than comparison");
        Check(Options.Parse([]).JumpThreshold == .5 && Options.Parse(["--jump-threshold", "0"]).JumpThreshold == 0 && Options.Parse(["--jump-threshold", "1"]).JumpThreshold == 1, "jump cutoff defaults and inclusive endpoints parse");
        foreach (var invalid in new[] { "NaN", "Infinity", "-0.1", "1.1" }) Throws(() => Options.Parse(["--jump-threshold", invalid]), "invalid jump cutoff rejected: " + invalid);
        compositeResponse["answers"]!["jump"]!["noul"] = 1.1;
        Throws(() => Controller.SelectedAction(compositeResponse, compositional), "invalid jump probability is rejected");
        var pulse = Options.Parse(["--attack-mode", "pulse", "--decision-mode", "compositional"]);
        var pulseRequest = Controller.Request(state, pulse);
        Check(JsonNode.DeepEquals(pulseRequest["state"], compositeRequest["state"]), "pulse action mode preserves identical raw model observations");
        Check(pulseRequest["questions"]!["attack"]!["instructions"]!.GetValue<string>().Contains("50 ms") && pulseRequest["questions"]!["attack"]!["criteria"]!["forward"]!.GetValue<string>().Contains("then release"), "pulse request describes literal press duration and release");
        var pulsedCommand = Controller.Command("right_jump_attack_forward", 10, 300, now, "test-session", 50);
        Check(pulsedCommand["attack_press_ms"]!.GetValue<int>() == 50 && pulsedCommand["jump"]!.GetValue<bool>() && pulsedCommand["duration_ms"]!.GetValue<int>() == 300, "pulse command carries separate attack duration without shortening movement or jump lease");
        Check(Controller.Command("right_attack_forward", 10, 300, now, "test-session")["attack_press_ms"]!.GetValue<int>() == 0, "hold remains default command semantics");
        Check(!Controller.PulseReady(pulsedCommand, now.AddMilliseconds(99), pulse) && Controller.PulseReady(pulsedCommand, now.AddMilliseconds(100), pulse), "pulse dispatch waits 100ms from prior publication despite response-latency variation");
        Throws(() => Options.Parse(["--attack-mode", "pulse", "--cadence-ms", "50"]), "pulse mode rejects cadence shorter than release interval");
        var spanOptions = options with { TerrainFormat = "spans" };
        var spanRequest = Controller.Request(state, spanOptions);
        Check(spanRequest["state"]!.AsObject().Count == 5, "span format adds no model state groups");
        Check(ReconstructRows(spanRequest["state"]!["terrain"]!.AsObject()).SequenceEqual(state["observation"]!["terrain"]!["rows"]!.AsArray().Select(r => r!.GetValue<string>())), "spans reconstruct original fixture samples exactly");
        var irregularGrid = JsonNode.Parse("""{"origin_x":-3.25,"origin_y":-2.5,"cell_size":0.25,"width":8,"height":5,"rows":["?#..^^?#","........","####....","..^..?.?",".##..###"]}""")!.AsObject();
        var irregularSpans = Controller.TerrainSpans(irregularGrid);
        Check(ReconstructRows(irregularSpans).SequenceEqual(irregularGrid["rows"]!.AsArray().Select(r => r!.GetValue<string>())), "mixed single/multi-cell runs, unknowns, empty rows and nonzero origin round-trip losslessly");
        Check(ReconstructRows(Controller.TerrainRectangles(irregularGrid)).SequenceEqual(irregularGrid["rows"]!.AsArray().Select(r => r!.GetValue<string>())), "rectangle encoding preserves mixed kinds, singleton cells and missing rows exactly");
        var lShape = JsonNode.Parse("""{"origin_x":-1,"origin_y":-1,"cell_size":0.5,"width":4,"height":4,"rows":["##..","##..","#...","##.."]}""")!.AsObject();
        var lRectangles = Controller.TerrainRectangles(lShape);
        Check(lRectangles["solid"]!.AsArray().Count == 3 && ReconstructRows(lRectangles).SequenceEqual(new[] { "##..", "##..", "#...", "##.." }), "rectangles merge identical adjacent spans only, preserving concavities and separated spans");
        var namedGeometry = Controller.TerrainGeometry(lShape);
        Check(ReconstructRows(namedGeometry).SequenceEqual(ReconstructRows(lRectangles)) && namedGeometry["solid"]![0]!.AsObject().Select(p => p.Key).SequenceEqual(new[] { "left_x", "right_x", "bottom_y", "top_y" }), "named geometry preserves every rectangle sample with only explicit coordinate field names");
        Check(ReconstructRows(Controller.TerrainGeometry(irregularGrid)).SequenceEqual(ReconstructRows(irregularSpans)), "named geometry round-trips mixed solid, damage, unknown and empty samples");
        var wideGrid = new JsonObject { ["origin_x"] = -12.0, ["origin_y"] = -6.0, ["cell_size"] = .5, ["width"] = 49, ["height"] = 41, ["rows"] = new JsonArray(Enumerable.Range(0, 41).Select(i => JsonValue.Create(i < 10 ? new string('?', 49) : i > 30 ? new string('#', 49) : new string('.', 49))).ToArray()) };
        var wideRectangles = Controller.TerrainRectangles(wideGrid);
        Check(wideRectangles["solid"]!.AsArray().Count == 1 && wideRectangles["unknown"]!.AsArray().Count == 1 && ReconstructRows(wideRectangles).SequenceEqual(wideGrid["rows"]!.AsArray().Select(r => r!.GetValue<string>())), "wider 49x41 view round-trips and collapses repeated horizontal spans vertically");
        var abilityState = state.DeepClone().AsObject();
        abilityState["observation"]!["abilities"] = JsonNode.Parse("""{"run_speed":8.3,"jump_speed":16.4,"jump_sustain_steps":10,"fixed_timestep_seconds":0.02,"gravity_y":-9.81,"gravity_scale":1,"has_double_jump":false,"has_dash":false,"has_wall_jump":false,"jump_speed_sustain_seconds":0.2,"reachable":true,"jump_now":true}""");
        var abilities = Controller.ModelState(abilityState)["abilities"]!.AsObject();
        Check(abilities.Count == 10 && !abilities.ContainsKey("reachable") && !abilities.ContainsKey("jump_now") && abilities["jump_sustain_steps"]!.GetValue<int>() == 10, "optional raw abilities are re-whitelisted without reachability or advice");
        Check(Controller.ModelState(state)["abilities"] == null, "abilities remain absent for earlier recordings without them");
        var surfaceState = state.DeepClone().AsObject();
        surfaceState["observation"]!["surfaces"] = JsonNode.Parse("""[{"left_x":-2.125,"right_x":3.875,"top_y":1.25,"height_above_feet":1.8,"normal_x":0.125,"normal_y":0.992,"reachable":true,"headroom":true,"jump_now":true}]""");
        var surfaces = Controller.ModelState(surfaceState)["surfaces"]!.AsArray();
        Check(surfaces.Count == 1 && surfaces[0]!.AsObject().Count == 6 && surfaces[0]!["left_x"]!.GetValue<double>() == -2.125 && surfaces[0]!["right_x"]!.GetValue<double>() == 3.875 && surfaces[0]!["top_y"]!.GetValue<double>() == 1.25 && surfaces[0]!["height_above_feet"]!.GetValue<double>() == 1.8 && surfaces[0]!["normal_x"]!.GetValue<double>() == .125 && surfaces[0]!["normal_y"]!.GetValue<double>() == .992, "surface whitelist preserves six exact measured values and excludes reachability/headroom/advice");
        Check(Controller.ModelState(state)["surfaces"] == null, "surface observations remain omitted when bridge does not emit them");
        var visitState = state.DeepClone().AsObject();
        visitState["observation"]!["exploration"] = JsonNode.Parse("""{"cell_size":2,"seconds_since_new_cell":4.75,"current_cell_seconds":9.25,"current_cell_entries":3,"go_left":true,"nearby_visited_cells":[{"x":-1.125,"y":2.25,"entries":2,"seconds":6.5,"preferred":true}]}""");
        Check(Controller.Request(visitState, options)["state"]!["exploration"] == null && !Options.Parse([]).VisitMemory, "visit memory omitted by default even when bridge emits it");
        var visitRequest = Controller.Request(visitState, locomotionOptions with { VisitMemory = true });
        var visits = visitRequest["state"]!["exploration"]!;
        Check(visits.AsObject().Count == 5 && visits["current_cell_entries"]!.GetValue<int>() == 3 && visits["seconds_since_new_cell"]!.GetValue<double>() == 4.75 && visits["current_cell_seconds"]!.GetValue<double>() == 9.25 && visits["nearby_visited_cells"]![0]!.AsObject().Count == 4 && visits["nearby_visited_cells"]![0]!["x"]!.GetValue<double>() == -1.125 && visits["nearby_visited_cells"]![0]!["seconds"]!.GetValue<double>() == 6.5, "visit memory strict nested whitelist preserves observed counts/times/relative centers only");
        Check(visitRequest["questions"]!["locomotion"]!["instructions"]!.GetValue<string>().Contains("deprioritize directions") && !visitRequest["questions"]!["attack"]!["instructions"]!.GetValue<string>().Contains("deprioritize directions"), "user-authorized visit policy applies only to navigation, never attack question");
        var manyVisits = visitState.DeepClone().AsObject();
        manyVisits["observation"]!["exploration"]!["nearby_visited_cells"] = new JsonArray(Enumerable.Range(0, 65).Select(_ => visits["nearby_visited_cells"]![0]!.DeepClone()).ToArray());
        Throws(() => Controller.Request(manyVisits, options with { VisitMemory = true }), "visit memory over64 cells rejected");
        Throws(() => Options.Parse(["--visit-memory", "yes"]), "visit-memory requires true or false");
        Check(!Options.Parse([]).SpatialLabels && !Controller.Request(surfaceState, options)["state"]!["surfaces"]![0]!.AsObject().ContainsKey("horizontal_relation"), "spatial labels remain absent by default");
        Throws(() => Options.Parse(["--spatial-labels", "yes"]), "spatial label option requires true or false");
        foreach (var (left, right, height, horizontal, vertical) in new[]
        {
            (-1.0, -.31, .11, "left", "above_feet"),
            (-1.0, -.3, .1, "overlapping_player_x", "at_feet"),
            (.3, 1.0, -.1, "overlapping_player_x", "at_feet"),
            (.31, 1.0, -.11, "right", "below_feet"),
            (-1.0, 1.0, 0.0, "overlapping_player_x", "at_feet")
        })
        {
            var measured = surfaceState.DeepClone().AsObject();
            var surface = measured["observation"]!["surfaces"]![0]!;
            surface["left_x"] = left; surface["right_x"] = right; surface["height_above_feet"] = height;
            var labeled = Controller.Request(measured, options with { SpatialLabels = true })["state"]!["surfaces"]![0]!;
            Check(labeled["horizontal_relation"]!.GetValue<string>() == horizontal && labeled["vertical_relation"]!.GetValue<string>() == vertical && labeled["left_x"]!.GetValue<double>() == left, "surface coordinate boundaries: " + horizontal + "/" + vertical);
        }
        foreach (var (x, y, horizontal, vertical) in new[] { (1.1, 1.9, "aligned", "level"), (1.11, 2.11, "right", "above"), (.89, 1.89, "left", "below") })
        {
            var destination = Controller.Request(state, options with { SpatialLabels = true, GoalPosition = new(x, y) })["state"]!["objective"]!;
            Check(destination["horizontal_relation"]!.GetValue<string>() == horizontal && destination["vertical_relation"]!.GetValue<string>() == vertical && Math.Abs(destination["relative_x"]!.GetValue<double>() - (x - 1)) < 1e-9 && Math.Abs(destination["relative_y"]!.GetValue<double>() - (y - 2)) < 1e-9, "destination deltas and tolerance boundaries: " + horizontal + "/" + vertical);
        }
        Check(Controller.Request(state, options with { SpatialLabels = true })["state"]!["objective"] == null && Controller.ModelState(surfaceState)["surfaces"]![0]!.AsObject().Count == 6, "spatial labels do not invent destination or alter raw whitelist");
        Check(JsonNode.DeepEquals(Controller.Request(surfaceState, options)["state"], Controller.Request(surfaceState, compositional)["state"]) && JsonNode.DeepEquals(Controller.Request(surfaceState, options)["state"], Controller.Request(surfaceState, locomotionOptions)["state"]), "all decision modes receive identical sampled surface payload");
        var oversizedSurfaces = surfaceState.DeepClone().AsObject();
        oversizedSurfaces["observation"]!["surfaces"] = new JsonArray(Enumerable.Range(0, 65).Select(_ => surfaces[0]!.DeepClone()).ToArray());
        Throws(() => Controller.ModelState(oversizedSurfaces), "surface observations above64 sampled runs rejected");
        Check(irregularSpans["rows"]!.AsArray().Count == 4 && irregularSpans["rows"]![0]!["y"]!.GetValue<double>() > irregularSpans["rows"]![1]!["y"]!.GetValue<double>(), "spans omit only empty rows and preserve top-to-bottom sample order");
        var prior = Fixture(now.AddMilliseconds(-300)); prior["sequence"] = 7L;
        prior["observation"]!["player"]!["touching_wall_left"] = true;
        prior["observation"]!["held"]!["recommended_action"] = "attack now";
        var recent = Fixture(now.AddMilliseconds(-100)); recent["sequence"] = 9L;
        var otherSession = Fixture(now.AddMilliseconds(-200)); otherSession["session_id"] = "other"; otherSession["sequence"] = 8L;
        var future = Fixture(now.AddMilliseconds(100)); future["sequence"] = 8L;
        var remembered = Controller.Request(state, options with { History = 4 }, [recent, otherSession, prior, future]);
        var history = remembered["state"]!["recent_player_states"]!.AsArray();
        Check(history.Count == 2 && history[0]!["age_ms"]!.GetValue<double>() == 300 && history[1]!["age_ms"]!.GetValue<double>() == 100, "history is chronological, bounded to prior observations and excludes other sessions/future samples");
        Check(history[0]!.AsObject().Count == 3 && !history.ToJsonString().Contains("recommended_action") && !history.ToJsonString().Contains("debug") && !history.ToJsonString().Contains("episode") && history[0]!["player"]!["touching_wall_left"]!.GetValue<bool>(), "history re-whitelists only raw player/held/age including engine wall contacts");
        Check(Controller.Request(state, options, [prior])["state"]?["recent_player_states"] == null, "raw history remains absent by default");
        Check(Controller.RecentPlayerStates(state, [prior, recent], 1).Count == 1 && Controller.RecentPlayerStates(state, [prior, recent], 1)[0]!["age_ms"]!.GetValue<double>() == 100, "history count retains most recent earlier request");
        var stalled = new StallWatch(5);
        Check(stalled.Observe(state, now) == null && stalled.Observe(state, now.AddSeconds(4.9)) == null && stalled.Observe(state, now.AddSeconds(5)) == "local_stall", "local stall stops only after configured stationary interval");
        var moved = state.DeepClone().AsObject(); moved["observation"]!["player"]!["x"] = 1.26;
        Check(stalled.Observe(moved, now.AddSeconds(5.1)) == null && stalled.Observe(moved, now.AddSeconds(10)) == null, "measured displacement above 0.25 resets local stall clock");
        Check(new StallWatch(0).Observe(state, now.AddDays(1)) == null && !remembered["state"]!.ToJsonString().Contains("stall"), "stall detection is disabled by default and excluded from model inputs");
        Throws(() => Options.Parse(["--history", "5"]), "history larger than four observations rejected");
        var sampleOptions = compositional with { SelectionMode = "sample", Seed = 42 };
        Check(JsonNode.DeepEquals(Controller.Request(state, sampleOptions), Controller.Request(state, compositional)), "sampling mode and seed do not change the model request");
        var sampleResponse = JsonNode.Parse("""{"answers":{"move":{"choice":"right","probabilities":{"left":0.2,"still":0.05,"right":0.75}},"jump":{"noul":0.25},"attack":{"choice":"none","probabilities":{"none":0.7,"forward":0.2,"up":0.1,"down":0}}}}""")!.AsObject();
        var jumpOnly = sampleOptions with { SelectionMode = "sample-jump" };
        var jumpOnlyRandom = new Random(42);
        var jumpOnlySamples = Enumerable.Range(0, 1000).Select(_ => Controller.Select(sampleResponse, jumpOnly, jumpOnlyRandom)).ToArray();
        Check(jumpOnlySamples.All(s => Controller.Actions[s.Action].move == 1 && !Controller.Actions[s.Action].attack && s.Draws.Count == 1 && s.Draws.ContainsKey("jump")), "jump-only sampling preserves deterministic movement/attack and records only jump draw");
        Check(jumpOnlySamples.Count(s => Controller.Actions[s.Action].jump) is > 210 and < 290, "jump-only Noul sampling follows unmodified returned probability");
        Check(JsonNode.DeepEquals(Controller.Request(state, jumpOnly), Controller.Request(state, compositional)), "jump-only selection leaves exact request unchanged");
        Throws(() => Options.Parse(["--selection", "sample-jump"]), "jump-only sampling rejects combined actions");
        var mechanicsRequest = Controller.Request(state, compositional with { JumpFraming = "mechanics" });
        Check(JsonNode.DeepEquals(mechanicsRequest["state"], compositeRequest["state"]) && JsonNode.DeepEquals(mechanicsRequest["questions"]!["move"], compositeRequest["questions"]!["move"]) && JsonNode.DeepEquals(mechanicsRequest["questions"]!["attack"], compositeRequest["questions"]!["attack"]), "mechanics framing changes only jump question, never state or other questions");
        Check(mechanicsRequest["questions"]!["jump"]!["instructions"]!.GetValue<string>().Contains("does not create another press") && mechanicsRequest["questions"]!["jump"]!["criteria"]!["true"]!.GetValue<string>() == "Press or keep holding the jump button.", "mechanics framing states generic physical press/hold/release semantics");
        Throws(() => Options.Parse(["--jump-framing", "invented"]), "unknown jump framing rejected");
        var choiceOptions = compositional with { JumpDecision = "choice" };
        var choiceRequest = Controller.Request(state, choiceOptions);
        Check(JsonNode.DeepEquals(choiceRequest["state"], compositeRequest["state"]) && JsonNode.DeepEquals(choiceRequest["questions"]!["move"], compositeRequest["questions"]!["move"]) && JsonNode.DeepEquals(choiceRequest["questions"]!["attack"], compositeRequest["questions"]!["attack"]), "jump Choice preserves raw state and all other questions");
        Check(choiceRequest["questions"]!["jump"]!["type"]!.GetValue<string>() == "choice" && choiceRequest["questions"]!["jump"]!["criteria"]!.AsObject().Count == 2, "jump Choice contains only literal jump and release alternatives");
        var choiceResponse = sampleResponse.DeepClone().AsObject();
        choiceResponse["answers"]!["jump"] = JsonNode.Parse("""{"choice":"jump","probabilities":{"jump":1,"release":0}}""");
        Check(Controller.SelectedAction(choiceResponse, choiceOptions with { JumpThreshold = 1 }) == "right_jump", "jump Choice follows provider answer independent of Noul cutoff");
        Check(Controller.Actions[Controller.Select(choiceResponse, choiceOptions with { SelectionMode = "sample" }, new Random(42)).Action].jump, "jump Choice sampling uses categorical distribution");
        var choiceJumpOnly = Controller.Select(choiceResponse, choiceOptions with { SelectionMode = "sample-jump" }, new Random(42));
        Check(choiceJumpOnly.Action == "right_jump" && choiceJumpOnly.Draws.Count == 1, "jump-only Choice sampling keeps other controls deterministic");
        choiceResponse["answers"]!["jump"]!["choice"] = "release";
        Check(Controller.SelectedAction(choiceResponse, choiceOptions) == "right", "jump Choice release maps directly to released button");
        choiceResponse["answers"]!["jump"]!["choice"] = "invented";
        Throws(() => Controller.SelectedAction(choiceResponse, choiceOptions), "invalid jump Choice rejected");
        Throws(() => Options.Parse(["--jump-decision", "invented"]), "invalid jump decision mode rejected");
        var seededA = new Random(42); var seededB = new Random(42);
        var samplesA = Enumerable.Range(0, 20).Select(_ => Controller.Select(sampleResponse, sampleOptions, seededA)).ToArray();
        var samplesB = Enumerable.Range(0, 20).Select(_ => Controller.Select(sampleResponse, sampleOptions, seededB)).ToArray();
        Check(samplesA.Select(s => s.Action + s.Draws.ToJsonString()).SequenceEqual(samplesB.Select(s => s.Action + s.Draws.ToJsonString())), "same seed and returned distributions reproduce exact sampled actions and draws");
        Check(samplesA.All(s => s.ArgmaxAction == "right") && samplesA.Any(s => s.Action != s.ArgmaxAction) && samplesA.All(s => s.Draws.Count == 3), "sampled actions remain distinct from provider argmax and log each independent draw");
        var populationRandom = new Random(42);
        var population = Enumerable.Range(0, 2000).Select(_ => Controller.Select(sampleResponse, sampleOptions, populationRandom)).ToArray();
        var stillRate = population.Count(s => Controller.Actions[s.Action].move == 0) / 2000.0;
        var jumpRate = population.Count(s => Controller.Actions[s.Action].jump) / 2000.0;
        Check(stillRate > .03 && stillRate < .07 && jumpRate > .22 && jumpRate < .28 && population.All(s => Controller.Actions[s.Action].look != -1), "sampling respects rare still probability, Bernoulli jump rate and zero-probability exclusion without masks");
        Check(Controller.Request(state, compositional)["questions"]!["move"]!["instructions"]!.GetValue<string>().Contains("Avoid unnecessary waiting") && Controller.Request(state, compositional)["questions"]!["move"]!["criteria"]!["still"]!.GetValue<string>().StartsWith("Briefly"), "user progress goal and brief release semantics remain explicit");
        var negative = JsonNode.Parse("""{"left":-0.1,"right":1.1}""")!.AsObject();
        Throws(() => Controller.DrawCategorical(negative, ["left", "right"], new Random(1), "move", new JsonObject()), "negative probability weights rejected");
        Throws(() => Controller.DrawCategorical(JsonNode.Parse("""{"left":1.1}""")!.AsObject(), ["left"], new Random(1), "move", new JsonObject()), "probability weights above one rejected");
        var infinite = new JsonObject { ["left"] = double.PositiveInfinity };
        Throws(() => Controller.DrawCategorical(infinite, ["left"], new Random(1), "move", new JsonObject()), "nonfinite probability weights rejected");
        Throws(() => Controller.DrawCategorical(JsonNode.Parse("""{"left":0}""")!.AsObject(), ["left"], new Random(1), "move", new JsonObject()), "zero-mass probability distribution rejected");
        var normalizedDraw = new JsonObject();
        Controller.DrawCategorical(JsonNode.Parse("""{"left":0.1,"right":0.3}""")!.AsObject(), ["left", "right"], new Random(1), "move", normalizedDraw);
        Check(Math.Abs(normalizedDraw["move"]!["normalized_probabilities"]!["right"]!.GetValue<double>() - .75) < 1e-10, "positive categorical probabilities normalized and recorded");
        Check(Controller.Rejection(state, now, 500) == null, "fresh gameplay accepted");
        Check(Controller.Rejection(state, now.AddSeconds(2), 500) == "stale_observation", "old input rejected");
        Check(Controller.Rejection(state, now.AddSeconds(-2), 500) == "stale_observation", "future input rejected");
        var paused = state.DeepClone().AsObject(); paused["paused"] = true;
        Check(!Controller.CanApply(state, paused, now, options), "paused latest state cancels action");
        var scene = state.DeepClone().AsObject(); scene["scene"] = "Other";
        Check(!Controller.CanApply(state, scene, now, options), "scene transition cancels action");
        var session = state.DeepClone().AsObject(); session["session_id"] = "next-session";
        Check(!Controller.CanApply(state, session, now, options), "game restart cancels action");
        var disabled = state.DeepClone().AsObject(); disabled["control_enabled"] = false;
        Check(!Controller.CanApply(state, disabled, now, options), "disabled bridge cancels action");
        var success = state.DeepClone().AsObject(); success["episode"]!["status"] = "success";
        Check(!Controller.CanApply(state, success, now, options), "successful episode blocks further model actions");
        var episodeDeath = state.DeepClone().AsObject(); episodeDeath["episode"]!["status"] = "dead";
        Check(Controller.Rejection(episodeDeath, now, 500) == "episode_dead", "local episode death stops independently of health field");
        Check(!Controller.CanApply(state, state, now.AddSeconds(2), options), "late response rejected even with previously valid input");
        var dead = state.DeepClone().AsObject(); dead["observation"]!["player"]!["health"] = 0;
        Check(Controller.Rejection(dead, now, 500) != null, "dead state rejected");
        Throws(() => Controller.Command("right", 1, 301, now, "s"), "oversized input lease rejected");
        Throws(() => Options.Parse(["--duration-ms", "301"]), "controller duration option remains capped at300ms");
        Throws(() => Options.Parse(["--execution-mode", "discrete"]), "unvalidated discrete mode is unavailable");
        Throws(() => Controller.Command("right", 1, 0, now, "s"), "zero input lease rejected");
        Throws(() => Controller.Command("invented", 1, 100, now, "s"), "unknown action rejected");
        Throws(() => Options.Parse(["--seconds", "601"]), "unbounded wall time rejected");
        Check(Options.Parse(["--seconds", "600", "--max-calls", "1000"]).MaxCalls == 1000 && Options.Parse([]).Seconds == 15 && Options.Parse([]).MaxCalls == 10, "extended explicit trial caps preserve conservative defaults");
        Check(Options.Parse(["--seconds", "3600"], 3600).Seconds == 3600, "watch-only parsing permits a bounded one-hour monitor");
        Throws(() => Options.Parse(["--max-calls", "6001"]), "unbounded API calls rejected");
        var command = Controller.Command("right_jump_attack_down", 10, 150, now, "test-session");
        Check(command["move"]!.GetValue<int>() == 1 && command["jump"]!.GetValue<bool>() && command["attack"]!.GetValue<bool>() && command["look"]!.GetValue<int>() == -1, "compound controls preserve player choice");
        Check(command["session_id"]!.GetValue<string>() == "test-session" && command["duration_ms"]!.GetValue<int>() == 150, "commands carry session and bounded lease");
        Check(Controller.DispatchReady(11, 10, now.AddMilliseconds(100), now, 100) && Controller.LeaseRemaining(command, now.AddMilliseconds(100)) > 0, "fresh request can start before previous lease expires");
        Check(!Controller.DispatchReady(10, 10, now.AddSeconds(1), now, 100), "new requests require a newer observation");
        Check(!Controller.DispatchReady(11, 10, now.AddMilliseconds(99), now, 100), "request cadence minimum enforced");
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = new TaskCompletionSource<bool>();
            using var registration = cancelled.Token.Register(() => pending.TrySetCanceled(cancelled.Token));
            var caught = false;
            try { await Controller.AwaitMonitoredAsync(pending.Task, () => success, () => false, state, options, cancelled); }
            catch (TrialStoppedException e) { caught = e.Message == "episode_success"; }
            Check(caught && pending.Task.IsCanceled, "episode completion cancels an in-flight evaluation");
        }
        var path = Path.Combine(Path.GetTempPath(), "jev-controller-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Controller.AtomicWrite(path, command);
            Controller.AtomicWrite(path, Controller.Command("release", 10, 1, now, "test-session"));
            Check(Controller.ReadState(path)["move"]!.GetValue<int>() == 0, "atomic replacement publishes release");
            using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var unlock = Task.Run(() => { Thread.Sleep(20); heldFile.Dispose(); });
                var afterRetry = Controller.ReadState(path);
                await unlock;
                Check(afterRetry["move"]!.GetValue<int>() == 0, "temporary exclusive telemetry lock succeeds after bounded retry");
            }
            using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Throws(() => Controller.ReadState(path), "persistent exclusive telemetry lock fails after bounded retries");
            var preservedCommand = Controller.Command("right_attack_forward", 10, 300, now, "test-session", 50);
            using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var unlock = Task.Run(() => { Thread.Sleep(8); heldFile.Dispose(); });
                Controller.AtomicWrite(path, preservedCommand);
                await unlock;
            }
            var afterPublishRetry = Controller.ReadState(path);
            Check(afterPublishRetry["id"]!.GetValue<string>() == preservedCommand["id"]!.GetValue<string>() && afterPublishRetry["created_utc"]!.GetValue<string>() == preservedCommand["created_utc"]!.GetValue<string>(), "atomic publication retries a brief destination lock without renewing identity or creation time");
            using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Throws(() => Controller.AtomicWrite(path, preservedCommand), "persistent destination lock fails after bounded publication retries");
                Check(timer.ElapsedMilliseconds < 250, "publication lock retries remain bounded");
            }
        }
        finally { File.Delete(path); }
        await CheckOverlappingLoopAsync(Check);
        Console.WriteLine($"{count} offline checks passed. No network requests or game inputs.");
        return 0;
    }

    static string[] ReconstructRows(JsonObject terrain)
    {
        var width = terrain["width"]!.GetValue<int>();
        var height = terrain["height"]!.GetValue<int>();
        var ox = terrain["origin_x"]!.GetValue<double>();
        var oy = terrain["origin_y"]!.GetValue<double>();
        var cell = terrain["cell_size"]!.GetValue<double>();
        var rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat('.', width).ToArray()).ToArray();
        if (terrain["rows"] == null)
        {
            foreach (var (name, value) in new[] { ("solid", '#'), ("damage", '^'), ("unknown", '?') })
            {
                if (terrain[name] is not JsonArray rectangles) continue;
                foreach (var rectangle in rectangles)
                {
                    double Coordinate(int index, string name) => (rectangle is JsonObject ? rectangle[name] : rectangle![index])!.GetValue<double>();
                    var left = (int)Math.Round((Coordinate(0, "left_x") - ox) / cell);
                    var right = (int)Math.Round((Coordinate(1, "right_x") - ox) / cell);
                    var bottom = (int)Math.Round((Coordinate(2, "bottom_y") - oy) / cell);
                    var top = (int)Math.Round((Coordinate(3, "top_y") - oy) / cell);
                    for (int y = bottom; y <= top; y++)
                        for (int x = left; x <= right; x++) rows[height - 1 - y][x] = value;
                }
            }
            return rows.Select(r => new string(r)).ToArray();
        }
        foreach (var row in terrain["rows"]!.AsArray())
        {
            var r = height - 1 - (int)Math.Round((row!["y"]!.GetValue<double>() - oy) / cell);
            foreach (var (name, value) in new[] { ("solid", '#'), ("damage", '^'), ("unknown", '?') })
            {
                if (row[name] is not JsonArray spans) continue;
                foreach (var span in spans)
                {
                    var first = (int)Math.Round((span![0]!.GetValue<double>() - ox) / cell);
                    var last = (int)Math.Round((span[1]!.GetValue<double>() - ox) / cell);
                    for (int c = first; c <= last; c++) rows[r][c] = value;
                }
            }
        }
        return rows.Select(r => new string(r)).ToArray();
    }

    static async Task CheckOverlappingLoopAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("jev-controller-loop-test-");
        var statePath = Path.Combine(directory.FullName, "state.json");
        var commandPath = Path.Combine(directory.FullName, "command.json");
        var options = Options.Parse(["--state", statePath, "--command", commandPath, "--output", directory.FullName, "--seconds", "3", "--max-calls", "2", "--duration-ms", "300", "--cadence-ms", "20"]);
        using var bridgeStop = new CancellationTokenSource();
        var handler = new FakeTypeSafe();
        var state = Fixture(DateTimeOffset.UtcNow);
        Controller.AtomicWrite(statePath, state);
        using (var competing = new FileStream(Path.Combine(directory.FullName, "controller.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            bool refused = false;
            try { await Controller.RunAsync(options, true, handler); }
            catch (IOException) { refused = true; }
            check(refused && handler.Calls == 0, "concurrent live owner is rejected before any HTTP or command publication");
        }
        var bridge = Task.Run(async () =>
        {
            long sequence = 10;
            try
            {
                while (!bridgeStop.IsCancellationRequested)
                {
                    // Match native Observe: a transient local sharing failure skips
                    // one telemetry tick. Command failures and freshness/ack checks
                    // remain strict; persistent missing telemetry still fails tests.
                    try
                    {
                        var snapshot = Fixture(DateTimeOffset.UtcNow);
                        snapshot["sequence"] = ++sequence;
                        if (File.Exists(commandPath))
                        {
                            var command = Controller.ReadState(commandPath);
                            snapshot["control"] = new JsonObject
                            {
                                ["last_command_id"] = command["id"]!.DeepClone(),
                                ["last_command_result"] = "applied",
                                ["last_status"] = Controller.LeaseRemaining(command, DateTimeOffset.UtcNow) > 0 ? "applied" : "released"
                            };
                        }
                        Controller.AtomicWrite(statePath, snapshot);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    await Task.Delay(10, bridgeStop.Token);
                }
            }
            catch (OperationCanceledException) when (bridgeStop.IsCancellationRequested) { }
        });
        try
        {
            await Controller.RunAsync(options, true, handler);
            var records = File.ReadLines(Directory.GetFiles(directory.FullName, "trial-*.jsonl").Single()).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
            var requests = records.Where(r => r["status"]?.GetValue<string>() == "awaiting_api").ToArray();
            var applied = records.Where(r => r["status"]?.GetValue<string>() == "bridge_applied").ToArray();
            check(handler.Calls == 2 && requests.Length == 2, "loop obeys exact call cap with mocked HTTP only");
            check(applied.Length == 2, "loop waits for immutable bridge application acknowledgements");
            check(requests[1]["previous_lease_remaining_ms_at_request"]!.GetValue<double>() > 0, "second API request overlaps first finite lease");
            check(applied[1]["previous_lease_remaining_ms_at_publish"]!.GetValue<double>() > 0 && applied[1]["input_gap_ms"]!.GetValue<double>() == 0, "timely next model response preserves continuous inputs");
            check(applied[1]["same_buttons_as_previous"]!.GetValue<bool>(), "same model-chosen buttons are recorded without scripted renewal");
            var final = Controller.ReadState(commandPath);
            check(!final["jump"]!.GetValue<bool>() && final["move"]!.GetValue<int>() == 0 && final["duration_ms"]!.GetValue<int>() == 100, "bounded loop always publishes finite shutdown release");
            var priorLogs = Directory.GetFiles(directory.FullName, "trial-*.jsonl");
            await Controller.RunAsync(options with { AttackMode = "pulse", CadenceMs = 100 }, true, new FakeTypeSafe(60));
            var pulseLog = Directory.GetFiles(directory.FullName, "trial-*.jsonl").Except(priorLogs).Single();
            var pulseRows = File.ReadLines(pulseLog).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
            var pulseRequests = pulseRows.Where(r => r["status"]?.GetValue<string>() == "awaiting_api").ToArray();
            var pulseApplied = pulseRows.Where(r => r["status"]?.GetValue<string>() == "bridge_applied").ToArray();
            check(pulseRequests.Length == 2 && pulseRequests[1]["previous_lease_remaining_ms_at_request"]!.GetValue<double>() > 200,
                "next evaluation can start before prior pulse publication spacing elapses");
            check(pulseApplied.Length == 2 && (DateTimeOffset.Parse(pulseApplied[1]["command"]!["created_utc"]!.GetValue<string>()) - DateTimeOffset.Parse(pulseApplied[0]["command"]!["created_utc"]!.GetValue<string>())).TotalMilliseconds >= 100,
                "overlapped evaluation preserves minimum 100ms command publication spacing");
            var slow = new FakeTypeSafe(5000);
            var wallClock = System.Diagnostics.Stopwatch.StartNew();
            await Controller.RunAsync(options with { Seconds = 1 }, true, slow);
            check(slow.Calls == 1 && slow.Cancelled && wallClock.Elapsed.TotalSeconds < 2, "wall-time limit cancels slow in-flight request without another call");
            check(Controller.ReadState(Path.Combine(directory.FullName, "run-status.json"))["reason"]!.GetValue<string>() == "cancelled_or_wall_time", "wall-time stop reason is recorded");
            var lastPending = Controller.ReadState(Path.Combine(directory.FullName, "latest.json"));
            var lastCompleted = Controller.ReadState(Path.Combine(directory.FullName, "last-completed.json"));
            check(lastPending["status"]!.GetValue<string>() == "awaiting_api" && lastCompleted["request"] != null && lastCompleted["response"] != null && Dashboard.CompletedDecision(lastPending, lastCompleted, "test-session")!["snapshot"]!["sequence"]!.GetValue<long>() == lastCompleted["snapshot"]!["sequence"]!.GetValue<long>(), "persisted dashboard completed pair survives next cancelled pending request");
        }
        finally
        {
            bridgeStop.Cancel();
            await bridge;
            foreach (var file in directory.GetFiles())
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Delete(file.FullName); break; }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(25); }
                    catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(25); }
                }
            }
            directory.Delete();
        }
    }

    sealed class FakeTypeSafe(int delayMilliseconds = 40) : HttpMessageHandler
    {
        internal int Calls;
        internal bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            try { await Task.Delay(delayMilliseconds, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""
            {"model":"offline-test","answers":{"action":{"type":"choice","choice":"right_jump","confidence":1,"probabilities":{"right_jump":1}}},"usage":{"input_tokens":0,"output_tokens":0}}
            """) };
        }
    }
}
