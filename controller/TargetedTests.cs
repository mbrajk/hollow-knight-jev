using System.Text.Json.Nodes;
using HollowKnightTrial;

internal static class TargetedTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Targeted: " + message); }
        var clock = new JumpInputTiming();
        Check(clock.Capture(true, 10, 10, .02).jump_press_age_seconds == null, "unknown prior hold does not invent a press");
        clock.Record(true, true, 10, 10);
        var edge = clock.Capture(true, 10.01, 10, .02);
        Check(edge.jump_press_age_seconds == .01 && edge.physics_steps_since_jump_press == 0, "fresh edge precedes physics");
        clock.Record(true, false, 10.02, 10.02);
        Check(clock.Capture(true, 10.02, 10.02, .02).physics_steps_since_jump_press == 1, "continued hold does not reset edge age");
        Check(clock.Capture(true, 11, 11, .02).jump_press_age_seconds == 1, "landing with held button keeps old age");
        clock.Record(false, false, 11, 11);
        Check(clock.Capture(false, 11, 11, .02).jump_press_age_seconds == null, "release invalidates pending edge");
        clock.Record(true, true, 11.04, 11.04);
        Check(clock.Capture(true, 11.04, 11.04, .02).physics_steps_since_jump_press == 0, "repress creates a new edge");
        clock.Reset();
        Check(clock.Capture(true, 0, 0, .02).jump_press_age_seconds == null, "scene reset cannot carry previous scene input age");

        var raw = JsonNode.Parse("""
        {"player":{"x":10,"y":1,"width":1,"height":2,"grounded":true,"vx":0,"vy":0,"facing":1,"attack_ready":true},
         "held":{"move":0,"jump":true,"attack":false},
         "abilities":{"run_speed":8,"jump_speed":16,"jump_speed_sustain_seconds":0.2,"jump_min_steps":4,"gravity_y":-60,"gravity_scale":0.8,"fixed_timestep_seconds":0.02,"has_wall_jump":true,"has_double_jump":true},
         "player_motion":{},"arena":{"left":0,"right":40,"floor":0,"ceiling":30},
         "enemies":[{"x":2.1,"y":0,"width":3,"height":2,"vx":0,"vy":0,"state":"Slam End"}]}
        """)!.AsObject();
        var hold = Controller.CombatMoves()["still_jump"];
        var release = Controller.CombatMoves()["still"];
        Check(!Controller.PendingGroundJump(raw), "missing age must not relaunch every grounded hold");
        Check(Controller.KnightPath(raw, hold).All(p => p.Y == 0), "legacy post-landing held button stays grounded");
        raw["input_timing"] = JsonNode.Parse("""{"schema_version":1,"jump_held":true,"jump_press_age_seconds":0.01,"physics_steps_since_jump_press":0} """);
        Check(Controller.PendingGroundJump(raw), "recent grounded press awaits physics");
        var rising = Controller.KnightPath(raw, hold);
        var cut = Controller.KnightPath(raw, release);
        Check(rising[4].Y > 1 && rising.Max(p => p.Y) > cut.Max(p => p.Y), "pending jump rises before new control arrives; release shortens ascent");
        Check(Controller.KnightPath(raw, release, 0)[3].Y > .6, "pending jump respects native minimum ascent even with immediate release");
        raw["input_timing"]!["jump_press_age_seconds"] = 1;
        raw["input_timing"]!["physics_steps_since_jump_press"] = 50;
        Check(!Controller.PendingGroundJump(raw) && Controller.KnightPath(raw, hold).All(p => p.Y == 0), "aged hold after landing does not become a pending jump");
        raw["input_timing"]!["jump_press_age_seconds"] = .01;
        raw["input_timing"]!["physics_steps_since_jump_press"] = 1;
        Check(!Controller.PendingGroundJump(raw), "one physics update rules out this narrow pre-physics case");
        raw["input_timing"]!["physics_steps_since_jump_press"] = 0;
        raw["player_motion"]!["ground_jump_ready"] = false;
        Check(!Controller.PendingGroundJump(raw), "hard landing or other native ground-jump restriction blocks pending ascent");
        raw["player_motion"]!.AsObject().Remove("ground_jump_ready");
        raw["player_motion"]!["dashing"] = true;
        Check(!Controller.PendingGroundJump(raw), "fresh press during dash must not invent a ground jump");
        raw["player_motion"]!["dashing"] = false;
        raw["player_motion"]!["jumping"] = true;
        Check(!Controller.PendingGroundJump(raw), "jump already started must not be started twice");
        raw["player_motion"]!["jumping"] = false;
        raw["held"]!["jump"] = false;
        Check(!Controller.PendingGroundJump(raw), "released button cannot be pending");

        raw["player_motion"]!["nail_recoil_direction"] = 1;
        raw["player_motion"]!["nail_recoil_speed"] = 3.75;
        raw["player_motion"]!["nail_recoil_remaining_seconds"] = .08;
        var recoil = Controller.KnightPath(raw, release);
        Check(Math.Abs(recoil[4].X - .3) < 1e-8 && recoil[5].X == recoil[4].X, "four native recoil steps move .3 units then stop");
        Check(Controller.KnightPath(raw, Controller.CombatMoves()["left"])[5].X > 0, "opposing command cannot erase recoil before expiry");
        Check(Controller.NailRecoilVelocity(8, 1, 3.75) == 11.75, "same-direction motion adds native recoil");
        Check(Controller.NailRecoilVelocity(-8, -1, 3.75) == -11.75, "recoil rule mirrors correctly");
        Check(Controller.NailRecoilVelocity(-8, 1, 3.75) == 3.75, "opposing motion is overridden by native recoil");
        var mirror = raw.DeepClone().AsObject();
        mirror["player_motion"]!["nail_recoil_direction"] = -1;
        Check(Math.Abs(Controller.KnightPath(mirror, release)[4].X + .3) < 1e-8, "left/right recoil paths mirror");
        raw["player_motion"]!["nail_recoil_remaining_seconds"] = 0;
        Check(Controller.KnightPath(raw, release).All(p => p.X == 0), "expired recoil cannot persist into future predictions");

        var forecast = Controller.Forecast(raw);
        var still = forecast["movement_outcomes"]!["still"]!;
        Check(Controller.Num(still, "first_contact_seconds", -1) == 0 && still["first_body_overlap_seconds"] == null, "margin-only proximity is not physical overlap");
        var left = forecast["movement_outcomes"]!["left"]!;
        var right = forecast["movement_outcomes"]!["right"]!;
        Check(left["post_input_body_overlap_seconds"] == null && right["post_input_body_overlap_seconds"] != null, "shared zero-time margin retains different controllable outcomes");
        Check(Controller.Num(right, "post_input_overlap_duration_seconds") > 0, "post-input overlap duration is measurable");
        Check(left["pre_input_body_overlap_seconds"] == null, "margin during response is not reported as damaging overlap");
        raw["enemies"]![0]!["x"] = 1.9;
        var overlapping = Controller.Forecast(raw)["movement_outcomes"]!["left"]!;
        Check(Controller.Num(overlapping, "pre_input_body_overlap_seconds", -1) == 0, "real pre-input overlap is explicitly retained");

        var envelope = new JsonObject { ["schema_version"] = 1, ["observation"] = raw.DeepClone() };
        Check(Controller.CandidateTelemetryRejection(envelope) == null, "new native telemetry permits candidate dispatch");
        envelope["observation"]!.AsObject().Remove("input_timing");
        Check(Controller.CandidateTelemetryRejection(envelope) != null, "old installed telemetry blocks paid live candidate trials");
        var options = Options.Parse(["--scenario","gruz-mother","--combat-compact","true","--decision-mode","grouped","--attack-mode","pulse"]);
        raw["enemies"]![0]!["x"] = -3.0;
        envelope["observation"] = raw.DeepClone();
        var original = envelope.ToJsonString();
        var request = Controller.Request(envelope, options);
        Check(request["questions"]!.AsObject().Select(q => q.Key).Order().SequenceEqual(new[]{"movement","strike"}), "attack feasibility and direction share one question; movement remains separate");
        Check(request["questions"]!["strike"]!["criteria"]!["left"] == null && request["questions"]!["strike"]!["criteria"]!["right"] != null && request["questions"]!["strike"]!["criteria"]!["none"] != null, "cannot turn a horizontal nail by inventing an aim control; none remains available");
        Check(request["questions"]!["strike"]!["criteria"]!["down"] == null, "grounded downward strike is unavailable");
        Check(request["state"]!["forecast"]!.AsObject().ContainsKey("pre_input_body_overlap_seconds") && request["state"]!["forecast"]!["movement_outcomes"]!["still"]!["pre_input_body_overlap_seconds"] == null, "shared pre-input risk is factored out of per-option model payload");
        Check(envelope.ToJsonString() == original, "request generation never mutates evidence");
        raw["player"]!["grounded"] = false;
        envelope["observation"] = raw.DeepClone();
        Check(Controller.Request(envelope, options)["questions"]!["strike"]!["criteria"]!["down"] != null, "airborne downward strike remains available");
        envelope["observation"]!.AsObject().Remove("input_timing");
        Check(Controller.Request(envelope, options)["state"]!["input_physics"]!["jump_phase"] != null, "legacy offline requests tolerate missing timing without inventing a fresh press");
        Check(Controller.CandidateBudgetRejection(Options.Parse(["--max-calls","24","--seconds","10"])) == null, "bounded candidate validation allowed");
        Check(Controller.CandidateBudgetRejection(Options.Parse(["--max-calls","25","--seconds","10"])) != null, "candidate refuses more than 24 paid requests per invocation");
        Check(Controller.CandidateBudgetRejection(Options.Parse(["--max-calls","24","--seconds","11"])) != null, "candidate refuses validation longer than ten seconds");
        Console.WriteLine($"PASS {checks} targeted input timing, recoil, overlap, grouped-strike and telemetry-gate checks");
    }
}
