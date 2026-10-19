using System.Text.Json.Nodes;

internal static partial class Controller
{
    internal static Dictionary<string, Buttons> CombatMoves() => new(Locomotion) {
        ["left_dash"] = new(-1,0,false,false,false,true), ["right_dash"] = new(1,0,false,false,false,true),
        ["left_rejump"] = new(-1,0,true,false,true), ["still_rejump"] = new(0,0,true,false,true), ["right_rejump"] = new(1,0,true,false,true)
    };
    internal static string ComposeCombatMove(string movement, string attack) {
        if (!movement.EndsWith("_rejump") && !movement.EndsWith("_dash")) return ComposeLocomotion(movement,attack);
        var key=movement+(attack=="none"?"":"_attack_"+attack);
        return Actions.ContainsKey(key)?key:throw new InvalidDataException("Invalid repress composition.");
    }
    internal static JsonArray AttackWindow(JsonObject raw) {
        var held=raw["held"];var knight=KnightPath(raw,new Buttons((int)Num(held,"move"),0,Flag(held,"jump"),false),1);
        var output=new JsonArray();int index=0;
        foreach(var enemy in raw["enemies"]!.AsArray()) {
            foreach(var branch in BossPaths(raw,enemy!)) foreach(int step in new[]{7,11}) {
                double x=branch.Points[step].X-knight[step].X,y=branch.Points[step].Y-knight[step].Y,w=branch.Sizes[step].X/2,h=branch.Sizes[step].Y/2;
                output.Add(new JsonObject{["enemy_index"]=index,["branch"]=branch.Branch,["at_seconds"]=Round(step*ForecastStep),["left"]=Round(x-w),["right"]=Round(x+w),["bottom"]=Round(y-h),["top"]=Round(y+h)});
            }
            index++;
        }
        return output;
    }
    // Geometry is observation, not an action recommendation. Gap uses actual bodies.
    internal static JsonObject? NearestEnemy(JsonObject state)
    {
        var player = state["player"]!;
        double Get(JsonNode body, string key) => body[key]!.GetValue<double>();
        JsonObject? nearest = null;
        double best = double.PositiveInfinity;
        var enemies = state["enemies"]!.AsArray();
        for (int i = 0; i < enemies.Count; i++)
        {
            var enemy = enemies[i]!;
            double dx = Math.Max(0, Math.Max(Get(enemy, "left") - Get(player, "right"), Get(player, "left") - Get(enemy, "right")));
            double dy = Math.Max(0, Math.Max(Get(enemy, "bottom") - Get(player, "top"), Get(player, "bottom") - Get(enemy, "top")));
            double gap = Math.Sqrt(dx * dx + dy * dy);
            if (gap >= best) continue;
            best = gap;
            nearest = new JsonObject { ["enemy_index"] = i, ["body_gap"] = Math.Round(gap, 3), ["horizontal_gap"] = Math.Round(dx, 3), ["vertical_gap"] = Math.Round(dy, 3) };
        }
        return nearest;
    }

    internal static JsonObject GroupedCombatRequest(JsonObject envelope, Options options)
    {
        var baseline = CompactCombatRequest(envelope, options with { DecisionMode = "compositional" });
        var state = baseline["state"]!.DeepClone().AsObject();
        state["nearest_enemy"] = NearestEnemy(state);
        state["input_physics"] = InputPhysicsState(envelope["observation"]!.AsObject());
        state["attack_window"] = AttackWindow(envelope["observation"]!.AsObject());
        state["forecast"] = ForecastForModel(envelope["observation"]!.AsObject());
        state["dash_capabilities"] = Fields(envelope["observation"]!["player_motion"], "dash_ready", "shadow_dash_ready", "dashing", "shadow_dashing", "dash_cooldown_seconds", "shadow_dash_cooldown_seconds");
        state["jump_capabilities"] = Fields(envelope["observation"]!["player_motion"], "can_wall_jump", "can_double_jump", "wall_sliding", "wall_locked");
        state["available_attack_controls"] = new JsonArray("nail");
        // Retain fixed motion/windup semantics from the baseline, not a tactical oracle.
        var moveInstructions = baseline["questions"]!["move"]!["instructions"]!.GetValue<string>();
        var legend = moveInstructions[..moveInstructions.IndexOf("Choose horizontal movement", StringComparison.Ordinal)];
        string jump = "Jump ON with held.jump=false is a fresh press. ON with held.jump=true continues the hold and cannot start another jump. input_physics distinguishes a fresh ground press awaiting physics from a held button after landing. A pending press may still report grounded; its ascent is already included in the response-delay forecast. After a completed landing with jump still held, OFF re-arms the next press. While rising ON sustains ascent and OFF shortens it. While falling, another jump requires a fresh press after OFF. ";
        var movement = new JsonObject();
        var heldJump = state["held"]!["jump"]!.GetValue<bool>();
        var grounded = state["player"]!["grounded"]!.GetValue<bool>();
        bool pendingJump = PendingGroundJump(envelope["observation"]!.AsObject());
        var jumpOn = pendingJump ? "continue the fresh ground jump press awaiting its first physics step; this sustains that pending ascent" : heldJump ? (grounded ? "continue the already held jump button; this does NOT start a jump from the ground" : "continue holding jump; sustain ascent if rising, but do NOT start another jump") : "press jump freshly; start a jump if the native game allows it";
        var jumpOff = heldJump ? "release jump; re-arm a later fresh press and shorten ascent if rising" : "leave jump released";
        foreach (var (name, buttons) in CombatMoves())
        {
            if(buttons.dash && !Flag(envelope["observation"]!["player_motion"],"dash_ready")) continue;
            var movementText = buttons.move switch { -1 => "Move left", 1 => "Move right", _ => "Stay horizontally still" };
            bool blocked = buttons.move < 0 && Flag(state["player"], "blocked_left") || buttons.move > 0 && Flag(state["player"], "blocked_right");
            if(buttons.dash) movementText += "; press dash for 50ms: native dash moves faster for its measured duration; Shadow Dash can pass through enemy contact only while its invulnerability is active. Wall slides flip facing before dashing";
            if (blocked) movementText += " against the current solid wall: horizontal travel is blocked, so this is NOT horizontal evasion";
            movement[name] = movementText + "; " + (buttons.jump_repress ? "release jump for 40ms then press freshly: request a new ground jump, wall jump or available double jump; this is NOT sustaining current ascent" : buttons.jump ? jumpOn : jumpOff) + ". Inspect forecast.movement_outcomes." + name + "; a fresh airborne wall jump can push AWAY from the wall regardless of the held direction.";
        }
        var questions = new JsonObject
        {
            ["movement"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "Defeat Gruz Mother without being hit. Choose the movement whose predicted swept path avoids the boss across the possible branches. forecast compares each control with the same boss branches and realistic input delay. First avoid predicted body overlap. first_body_overlap_seconds describes actual body intersection; first_contact_seconds also includes a 0.15 modelling margin and does not by itself mean a damaging overlap. If every option has early contact during the response delay, compare post_input_body_overlap_seconds, post_input_minimum_body_clearance and post_input_overlap_duration_seconds for the controllable escape; the shared pre-input exposure does not make the later paths equivalent. Null means no event within this horizon. Treat a margin breach as uncertain proximity, not proof of safety or damage. When walking, standing or jumping already avoids contact, prefer those ordinary controls so nail attacks remain available. Reserve dash for threats that ordinary movement cannot evade or for a clearly necessary escape; do not spend it just to maximize distance. Dashing prevents nail attacks until it finishes. Between safe ordinary options, keep useful clearance while avoiding unnecessary displacement. An earlier contact time is worse; negative minimum_body_clearance is overlap. Account for forecast uncertainty and the observed windup. Stay still with jump OFF when its path remains clear; do not move merely to attack. A blocked direction cannot provide horizontal escape. Only use a blocked direction intentionally for its wall-slide/jump effect when the predicted vertical path helps. Repeatedly holding jump does not initiate a fresh jump. The _rejump alternatives explicitly release for 40ms and press again; use them when a new jump is needed while jump is held. Use _jump to sustain current ascent. Repressing can consume an available double jump; use the measured jump_capabilities and projected paths. Dash options exist only when the native dash action is available. Their forecasts include native dash travel and, when ready, its brief Shadow Dash contact immunity; landing or being inside an enemy when it ends is still dangerous. Dashing interrupts an attack. The strike decision is independent of movement and includes whether to strike and its direction. " + jump + $"Inputs last at most {options.DurationMs}ms; projections beyond that assume renewed intent.",
                ["criteria"] = movement
            }
        };
        var facing = state["player"]!["facing"]!.GetValue<int>();
        string horizontal = facing < 0 ? "left" : "right";
        state["facing_direction"]=horizontal;
        state["available_nail_directions"]=grounded?new JsonArray(horizontal,"up"):new JsonArray(horizontal,"up","down");
        var strikes=new JsonObject {
            ["none"]="Do not strike: attack is unavailable or none of the available strike directions can reach an enemy in attack_window.",
            [horizontal]=$"Strike with the nail toward {horizontal}, the observed facing direction, ONLY if an enemy is within that horizontal strike's reach at arrival. {(facing<0?"Left is negative x: an enemy wholly on the positive-x side is not in front of this strike.":"Right is positive x: an enemy wholly on the negative-x side is not in front of this strike.")}",
            ["up"]="Strike upward with the nail ONLY if the enemy's projected body is above and sufficiently aligned horizontally for an upward strike to connect."
        };
        if(!grounded)strikes["down"]="Strike downward with the nail ONLY if the enemy's projected body is below and sufficiently aligned horizontally for a downward strike to connect; a hit bounces the airborne knight upward.";
        questions["strike"]=new JsonObject {
            ["type"]="choice",
            ["instructions"]="Choose a feasible nail strike and its direction together, or none. Deal damage when a strike can connect and attack_ready is true. attack_window gives projected enemy collision edges at input arrival, relative to the knight center: negative x is left, positive x right, positive y up. Use those actual edges, not avoidance_zone. The knight's body edges are player.left/right/bottom/top. Horizontal strikes use the observed facing_direction; the opposite direction is unavailable without first turning. Movement is decided independently, so do not assume its answer will turn the knight before this strike. A strike aimed away from the enemy cannot hit it. A strike into a solid wall can recoil the knight away from the wall; down strikes on an enemy produce a bounce. Choose none if no available direction can connect; do not force an aim merely because an enemy is nearby. Nail is currently the only connected weapon. Strike is a 50ms button press followed by native cooldown.",
            ["criteria"]=strikes
        };
        return new JsonObject { ["model"] = options.Model, ["state"] = state, ["questions"] = questions };
    }

    internal static string SelectedGroupedAction(JsonObject response)
    {
        var answers = response["answers"]?.AsObject() ?? throw new InvalidDataException("Missing grouped answers.");
        var movement = answers["movement"]?["choice"]?.GetValue<string>();
        if (movement == null || !CombatMoves().TryGetValue(movement, out var buttons)) throw new InvalidDataException("Invalid grouped movement.");
        if(answers["strike"] is JsonNode strikeAnswer) {
            var strike=strikeAnswer["choice"]?.GetValue<string>();
            return ComposeCombatMove(movement,strike switch { "none"=>"none","left" or "right"=>"forward","up"=>"up","down"=>"down",_=>throw new InvalidDataException("Invalid grouped strike.") });
        }
        var weapon = answers["weapon"]?["choice"]?.GetValue<string>();
        if (weapon == "none") return ComposeCombatMove(movement, "none");
        if (weapon != "nail") throw new InvalidDataException("Unavailable grouped weapon.");
        var aim = answers["aim"]?["choice"]?.GetValue<string>();
        var attack = aim switch
        {
            "left" or "right" => "forward",
            "up" => "up", "down" => "down",
            _ => throw new InvalidDataException("Invalid grouped aim.")
        };
        return ComposeCombatMove(movement, attack);
    }

    internal static JsonObject GroupedDecision(JsonObject response)
    {
        var action = SelectedGroupedAction(response);
        var buttons = Actions[action];
        var evading = buttons.move != 0 || buttons.jump;
        return new JsonObject
        {
            ["mode"] = evading ? (buttons.attack ? "evade_and_attack" : "evade") : (buttons.attack ? "attack" : "observe"),
            ["mode_source"] = "Summary of selected controls, not a separate model answer",
            ["movement"] = response["answers"]!["movement"]!["choice"]!.DeepClone(),
            ["weapon"] = buttons.attack?"nail":"none",
            ["active_aim_question"] = buttons.attack ? (response["answers"]?["strike"]!=null?"strike":"aim") : null,
            ["aim"] = buttons.attack ? (response["answers"]!["strike"]??response["answers"]!["aim"])!["choice"]!.DeepClone() : null
        };
    }
}
