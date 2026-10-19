using System.Text.Json.Nodes;

internal static partial class Controller
{
    internal static bool PendingGroundJump(JsonObject raw)
    {
        var timing = raw["input_timing"];
        var m = raw["player_motion"];
        double age = Num(timing, "jump_press_age_seconds", -1);
        double step = Num(raw["abilities"], "fixed_timestep_seconds", .02);
        return Flag(raw["held"], "jump") && Flag(timing, "jump_held")
            && age >= 0 && age <= 2 * step
            && Num(timing, "physics_steps_since_jump_press", -1) == 0
            && (m?["ground_jump_ready"] == null || Flag(m, "ground_jump_ready"))
            && Flag(raw["player"], "grounded") && !Flag(m, "jumping") && !Flag(m, "double_jumping")
            && !Flag(m, "dashing") && !Flag(m, "wall_sliding") && !Flag(m, "recoiling")
            && !Flag(m, "bouncing") && !Flag(m, "shroom_bouncing");
    }

    internal static JsonObject InputPhysicsState(JsonObject raw)
    {
        var state = Fields(raw["input_timing"] ?? new JsonObject(), "jump_press_age_seconds", "physics_steps_since_jump_press");
        state["jump_phase"] = PendingGroundJump(raw) ? "fresh_ground_press_before_physics"
            : Flag(raw["player_motion"], "jumping") ? "jump_in_progress"
            : Flag(raw["player_motion"], "double_jumping") ? "double_jump_in_progress"
            : Flag(raw["held"], "jump") && raw["input_timing"] == null ? "held_press_age_unknown"
            : Flag(raw["held"], "jump") ? "held_without_new_jump" : "released";
        state["nail_recoil"] = Fields(raw["player_motion"], "nail_recoil_direction", "nail_recoil_speed", "nail_recoil_remaining_seconds");
        return state;
    }

    // Native FixedUpdate first sets movement, then enforces/adds nail recoil.
    internal static double NailRecoilVelocity(double movementVelocity, int direction, double speed)
    {
        if (direction == 0 || speed <= 0) return movementVelocity;
        return direction * movementVelocity < speed ? direction * speed : movementVelocity + direction * speed;
    }

    internal static JsonObject ForecastForModel(JsonObject raw)
    {
        var forecast = Forecast(raw);
        var outcomes = forecast["movement_outcomes"]!.AsObject();
        // All actions share the same inputs until their assumed application delay.
        double preInput = outcomes.Min(o => Num(o.Value, "pre_input_body_overlap_seconds", double.PositiveInfinity));
        forecast["pre_input_body_overlap_seconds"] = double.IsFinite(preInput) ? preInput : null;
        foreach (var (_, node) in outcomes)
        {
            var outcome = node!.AsObject();
            foreach (var key in new[] { "first_close_seconds", "pre_input_body_overlap_seconds", "post_input_margin_breach_seconds", "horizontal_direction_currently_blocked", "dash", "fresh_jump" })
                outcome.Remove(key); // Diagnostics remain in the full offline forecast; criteria already describe controls.
        }
        forecast["assumptions"] = "Approximate swept paths across boss branches and 0.10/0.18s input delays; t_x_y is seconds,x,y relative to the current knight. Measured animation shapes change boss bounds. Slam End retains momentum, decaying by 0.85 per 20ms physics step. A fresh ground press awaiting physics and measured ongoing nail recoil continue during response delay. Post-input fields summarize the worst branch/delay; durations are not probabilities. Contact metrics exclude Shadow Dash immunity with 20ms boundary tolerance. New strike recoil, later unknown RNG transitions and exact wall-slide locks remain uncertain. Beyond the input lease, paths assume renewed intent.";
        return forecast;
    }

    internal static string? CandidateTelemetryRejection(JsonObject envelope)
    {
        var raw = envelope["observation"];
        return raw?["input_timing"]?["schema_version"]?.GetValue<int>() != 1
            || raw?["player_motion"]?["nail_recoil_remaining_seconds"] == null
            ? "candidate_requires_timing_and_recoil_telemetry" : null;
    }

    internal static string? CandidateBudgetRejection(Options options) => options.MaxCalls > 24 || options.Seconds > 10
        ? "targeted-v22 validation is capped at 24 requests and 10 seconds per invocation" : null;
}
