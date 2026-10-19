using System.Text.Json.Nodes;
internal static class ForecastTests
{
    internal static void Run()
    {
        void Check(bool ok,string label){if(!ok)throw new Exception("Forecast: "+label);}
        var raw=JsonNode.Parse("""{"player":{"x":1,"y":1,"width":1,"height":2,"grounded":true,"vx":0,"vy":0,"blocked_left":true},"held":{"move":0,"jump":false},"abilities":{"run_speed":8,"jump_speed":16,"gravity_y":-60,"gravity_scale":0.8,"has_wall_jump":true,"has_double_jump":true},"player_motion":{},"arena":{"left":0.5,"right":20,"floor":0,"ceiling":20},"enemies":[{"x":6,"y":0,"width":2,"height":2,"vx":-26,"vy":0,"state":"Charge","motion":{"charge_angle_degrees":180}}]}""")!.AsObject();
        var before=raw.ToJsonString();
        var left=Controller.KnightPath(raw,Controller.Locomotion["left"]);
        var right=Controller.KnightPath(raw,Controller.Locomotion["right"]);
        Check(left.All(p=>p.X==0),"solid wall blocks translation");
        Check(right[^1].X>4,"free movement translates");
        var jump=Controller.KnightPath(raw,Controller.Locomotion["still_jump"]);
        Check(jump.Max(p=>p.Y)>3,"fresh jump has vertical escape");
        raw["held"]!["jump"]=true;
        Check(Controller.KnightPath(raw,Controller.Locomotion["still_jump"]).All(p=>p.Y==0),"held jump cannot relaunch from floor");
        Check(Controller.KnightPath(raw,Controller.CombatMoves()["still_rejump"]).Max(p=>p.Y)>3,"repress creates a new jump even when held");
        var cmd=Controller.Command("left_rejump_attack_up",1,300,DateTimeOffset.UtcNow,"test",50);
        Check(cmd["jump_repress"]!.GetValue<bool>() && cmd["move"]!.GetValue<int>()==-1 && cmd["look"]!.GetValue<int>()==1,"repress and simultaneous aimed attack preserved in command");
        raw["player_motion"]!["jumping"]=true;raw["player_motion"]!["jump_steps"]=0;
        var pending=Controller.KnightPath(raw,Controller.Locomotion["still_jump"]);
        Check(pending[^1].Y<pending[25].Y,"pending native jump leaves grounded state and falls after apex");
        var earlyRelease=Controller.KnightPath(raw,Controller.Locomotion["still"],0);
        Check(earlyRelease[3].Y>0.6,"native minimum jump steps survive an immediate release");
        raw["player_motion"]!.AsObject().Remove("jumping");raw["player_motion"]!.AsObject().Remove("jump_steps");
        raw["held"]!["jump"]=false;
        var charge=Controller.BossPaths(raw,raw["enemies"]![0]!);
        Check(charge.All(p=>p.Points[^1].X>=.5),"charge stays inside arena through rebound");
        Check(charge[0].Points[20].X>charge[0].Points[12].X,"charge rebounds away from wall rather than stopping there");
        var f=Controller.Forecast(raw);
        Check(f["movement_outcomes"]!["still"]!["first_contact_seconds"]!=null,"swept path detects charging body contact");
        Check(f["movement_outcomes"]!["left"]!["horizontal_direction_currently_blocked"]!.GetValue<bool>(),"wall outcome is explicit");
        Check(Controller.BodyClearance(new(0,0),new(0,0),1,2)<0,"overlap is signed negative");
        Check(raw.ToJsonString()==before,"prediction does not mutate observation");
        raw["player"]!["grounded"]=false;raw["player"]!["y"]=4;raw["player_motion"]!["can_wall_jump"]=true;
        var wj=Controller.KnightPath(raw,Controller.Locomotion["left_jump"],0);
        Check(wj[4].X>0 && wj[4].Y>0,"fresh wall jump initially pushes away from blocked direction");
        raw["player"]!["grounded"]=true;raw["player"]!["y"]=1;raw["player"]!["facing"]=1;
        raw["player_motion"]!["dash_ready"]=true;raw["player_motion"]!["shadow_dash_ready"]=true;
        raw["abilities"]!["dash_speed"]=20;raw["abilities"]!["dash_seconds"]=.15;
        var dash=Controller.KnightPath(raw,Controller.CombatMoves()["right_dash"]);
        var walk=Controller.KnightPath(raw,Controller.CombatMoves()["right"]);
        Check(dash[15].X>walk[15].X+1,"native dash travels farther than walking");
        var dc=Controller.Command("right_dash",1,300,DateTimeOffset.UtcNow,"test");
        Check(dc["dash"]!.GetValue<bool>() && !dc["jump"]!.GetValue<bool>(),"dash command does not invent a jump");
        raw["player_motion"]!["dash_ready"]=false;
        Check(Controller.Forecast(raw)["movement_outcomes"]!["right_dash"]==null,"unavailable dash is absent from model forecasts");
        var animated=raw.DeepClone().AsObject();animated["player"]!["y"]=1.0;var en=animated["enemies"]![0]!;
        en["state"]="Slam Down";en["animation"]="Slam Down";en["motion"]=JsonNode.Parse("""{"animation_remaining_seconds":0.05,"slam_up_angle_degrees":80,"body_origin":{"x":7,"y":1},"body_shapes":[{"clip":"Slam Down","frames":[{"offset_x":0,"offset_y":0,"width":2,"height":2}]},{"clip":"Fly","frames":[{"offset_x":0,"offset_y":-0.2,"width":3,"height":3}]}]}""");
        var expanded=Controller.BossPaths(animated,en)[0];
        Check(expanded.Sizes[10].X==3&&expanded.Sizes[10].Y==3,"slam launch uses larger native flight collision body");
        Check(expanded.Points[1].Y<0.1,"pause keeps native origin while animation shape changes");
        var coast=JsonNode.Parse("""{"player":{"x":1,"y":1},"arena":{"left":0,"right":100,"floor":0,"ceiling":100},"enemies":[{"x":20,"y":20,"width":2,"height":2,"vx":8,"vy":0,"state":"Slam End","animation":"Slam End"}]}""")!.AsObject();
        var coasting=Controller.BossPaths(coast,coast["enemies"]![0]!)[0];
        Check(coasting.Points[15].X>20.6&&coasting.Points[15].X<20.9,"native Slam End fixed-step damping coasts rather than stopping or keeping full velocity");
        Console.WriteLine("PASS 21 forecast geometry, wall constraint, fresh-jump, swept-contact and immutability checks");
    }
}
