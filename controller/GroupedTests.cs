using System.Text.Json.Nodes;

internal static class GroupedTests
{
    internal static void Run()
    {
        void Check(bool condition) { if (!condition) throw new Exception("Grouped regression check failed."); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid grouped response accepted."); }
        var state = JsonNode.Parse("""{"player":{"left":-1,"right":1,"bottom":-1,"top":1},"enemies":[{"left":4,"right":6,"bottom":5,"top":7},{"left":-3,"right":-2,"bottom":-1,"top":1}]}""")!.AsObject();
        var before = state.ToJsonString();
        Check(Controller.NearestEnemy(state)!["body_gap"]!.GetValue<double>() == 1);
        Check(Controller.NearestEnemy(state)!["enemy_index"]!.GetValue<int>() == 1 && before == state.ToJsonString());
        state["enemies"]!.AsArray().RemoveAt(1);
        Check(Controller.NearestEnemy(state)!["body_gap"]!.GetValue<double>() == 5);
        state["enemies"]!.AsArray().Clear();
        Check(Controller.NearestEnemy(state) == null);
        var response = JsonNode.Parse("""{"answers":{"movement":{"choice":"left_jump"},"weapon":{"choice":"nail"},"aim":{"choice":"left"}}}""")!.AsObject();
        Check(Controller.SelectedGroupedAction(response) == "left_jump_attack_forward");
        Check(Controller.GroupedDecision(response)["mode"]!.GetValue<string>() == "evade_and_attack");
        response["answers"]!["aim"]!["choice"] = "teleport";
        Reject(() => Controller.SelectedGroupedAction(response));
        response["answers"]!["weapon"]!["choice"] = "none";
        Check(Controller.SelectedGroupedAction(response) == "left_jump"); // unused branches do not gate evasion
        response["answers"]!["movement"]!["choice"] = "teleport";
        Reject(() => Controller.SelectedGroupedAction(response));
        response["answers"]!["movement"]!["choice"] = "still";
        response["answers"]!["weapon"]!["choice"] = "spell";
        Reject(() => Controller.SelectedGroupedAction(response));
        var combined=JsonNode.Parse("""{"answers":{"movement":{"choice":"still_jump"},"strike":{"choice":"none"}}}""")!.AsObject();
        Check(Controller.SelectedGroupedAction(combined)=="still_jump");
        combined["answers"]!["strike"]!["choice"]="up";
        Check(Controller.SelectedGroupedAction(combined)=="still_jump_attack_up"&&Controller.GroupedDecision(combined)["aim"]!.GetValue<string>()=="up");
        combined["answers"]!["strike"]!["choice"]="spell";Reject(()=>Controller.SelectedGroupedAction(combined));
        Console.WriteLine("PASS grouped geometry, branch composition, simultaneous attack/evasion and unavailable-control rejection");
    }
}
