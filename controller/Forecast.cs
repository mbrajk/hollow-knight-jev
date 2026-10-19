using System.Text.Json.Nodes;

internal static partial class Controller
{
    internal readonly record struct Point(double X, double Y);
    internal sealed record MotionPath(string Branch, Point[] Points, Point[] Sizes);
    internal readonly record struct BodyShape(double X, double Y, double Width, double Height);
    static BodyShape Shape(JsonObject raw, JsonNode enemy, string clip, double elapsed) {
        var catalog=enemy["motion"]?["body_shapes"]?.AsArray();
        var c=catalog?.FirstOrDefault(c=>c?["clip"]?.GetValue<string>()==clip);
        var frames=c?["frames"]?.AsArray();var map=c?["frame_map"]?.AsArray();
        JsonNode? frame=null;
        if(frames!=null&&map!=null&&map.Count>0) {
            int index=Math.Clamp((int)Math.Floor(Math.Max(0,elapsed)*Num(c,"fps",12)),0,map.Count-1);
            for(int j=index;j>=0&&frame==null;j--)frame=frames[map[j]!.GetValue<int>()];
        } else if(frames!=null)frame=frames.FirstOrDefault(f=>f!=null);
        if(frame!=null)return new(Num(frame,"offset_x"),Num(frame,"offset_y"),Num(frame,"width"),Num(frame,"height"));
        var origin=enemy["motion"]?["body_origin"];
        return new(origin==null?0:Num(enemy,"x")+Num(raw["player"],"x")-Num(origin,"x"),origin==null?0:Num(enemy,"y")+Num(raw["player"],"y")-Num(origin,"y"),Num(enemy,"width"),Num(enemy,"height"));
    }
    internal const double ForecastStep = .02;
    internal const int ForecastSteps = 35;
    internal static double Num(JsonNode? n, string k, double fallback = 0)
    {
        if(n?[k] is not JsonValue v)return fallback;
        if(v.TryGetValue<double>(out var d))return d;
        if(v.TryGetValue<int>(out var i))return i;
        if(v.TryGetValue<long>(out var l))return l;
        return fallback;
    }
    internal static bool Flag(JsonNode? n, string k) => n?[k]?.GetValue<bool>() == true;
    static double Round(double d) => Math.Round(d, 2);
    static Point Angle(double angle, double speed) => new(Math.Cos(angle * Math.PI / 180) * speed, Math.Sin(angle * Math.PI / 180) * speed);
    internal static double BodyClearance(Point a, Point b, double halfWidthSum, double halfHeightSum)
    {
        double dx = Math.Abs(a.X - b.X) - halfWidthSum, dy = Math.Abs(a.Y - b.Y) - halfHeightSum;
        return dx > 0 || dy > 0 ? Math.Sqrt(Math.Pow(Math.Max(0, dx), 2) + Math.Pow(Math.Max(0, dy), 2)) : Math.Max(dx, dy);
    }
    // Physical counterfactuals only. This module never selects an action.
    internal static List<MotionPath> BossPaths(JsonObject raw, JsonNode enemy)
    {
        var p = raw["player"]; var arena = raw["arena"]; var motion = enemy["motion"];
        string phase = enemy["state"]?.GetValue<string>() ?? "";
        double x = Num(enemy,"x"), y = Num(enemy,"y");
        double wallLeft=Num(arena,"left")-Num(p,"x"),wallRight=Num(arena,"right")-Num(p,"x"),wallBottom=Num(arena,"floor")-Num(p,"y"),wallTop=Num(arena,"ceiling")-Num(p,"y");
        bool hasShapes=motion?["body_shapes"] is JsonArray;
        double originX=hasShapes?Num(motion?["body_origin"],"x")-Num(p,"x"):x,originY=hasShapes?Num(motion?["body_origin"],"y")-Num(p,"y"):y;
        double remaining = Num(motion,"animation_remaining_seconds",.15);
        bool charge = phase is "Charge" or "Charge Antic";
        bool slam = phase is "Flying" or "Slam Up" or "Slam Down" or "Launch Up" or "Launch Down" or "Slam Antic";
        var branches = new List<(string label, string kind, Point velocity, double wait)>();
        if (charge)
        {
            Point velocity = motion?["charge_angle_degrees"] != null ? Angle(Num(motion,"charge_angle_degrees"),26) : new(Num(enemy,"vx"),Num(enemy,"vy"));
            double wait = phase == "Charge Antic" ? Math.Min(remaining, Math.Max(0,.75 - Num(enemy,"state_elapsed_seconds"))) : 0;
            branches.Add(("committed_charge", "charge", velocity,wait));
        }
        else if (slam)
        {
            if (phase == "Slam Antic")
            {
                double wait = Math.Min(remaining,Math.Max(0,.5 - Num(enemy,"state_elapsed_seconds")));
                // The launch direction is selected after windup. Retain both possible horizontal branches.
                branches.Add(("slam_initial_left","slam",Angle(100,50),wait));
                branches.Add(("slam_initial_right","slam",Angle(80,50),wait));
            }
            else
            {
                double angle = phase == "Slam Up" ? Num(motion,"slam_down_angle_degrees",280) :
                    phase == "Slam Down" ? Num(motion,"slam_up_angle_degrees",80) : Num(motion,"slam_angle_degrees",80);
                Point velocity = phase == "Flying" && Math.Abs(Num(enemy,"vx")) + Math.Abs(Num(enemy,"vy")) > 1 ?
                    new(Num(enemy,"vx"),Num(enemy,"vy")) : Angle(angle,50);
                branches.Add(("committed_slam","slam",velocity,phase is "Slam Up" or "Slam Down" ? remaining : 0));
            }
        }
        else if(phase.StartsWith("Charge Recover")) branches.Add(("charge_recovery_rebound","recovery",new(Num(enemy,"vx"),Num(enemy,"vy")),0));
        else branches.Add(("observed_drift_until_unknown_transition","drift",new(Num(enemy,"vx"),Num(enemy,"vy")),0));
        var result = new List<MotionPath>();
        foreach (var branch in branches)
        {
            // Two phase-clock hypotheses account for a sampled animation boundary and collider changes.
            foreach (double clockOffset in charge || slam ? new[]{-.04,.04} : new[]{0.0})
            {
                var path = new Point[ForecastSteps+1]; path[0] = new(x,y);
                var sizes = new Point[ForecastSteps+1];sizes[0]=new(Num(enemy,"width"),Num(enemy,"height"));
                double bx=originX,by=originY,vx=branch.velocity.X,vy=branch.velocity.Y,wait=Math.Max(0,branch.wait+clockOffset);
                string clip=enemy["animation"]?.GetValue<string>()??"Fly";
                double clipTime=Num(motion,"animation_time_seconds"),flightRemaining=motion?["slam_flight_limit_seconds"]!=null?Math.Max(0,Num(motion,"slam_flight_limit_seconds")-Num(motion,"slam_flight_elapsed_seconds")):double.PositiveInfinity;
                void SetClip(string value){if(clip!=value){clip=value;clipTime=0;}}
                bool stopped=false,recovering=branch.kind=="recovery",slowing=phase=="Slam End";
                double recoveryTime=recovering?Math.Max(0,.3-Num(enemy,"state_elapsed_seconds")):0;
                for(int i=1;i<=ForecastSteps;i++)
                {
                    clipTime+=ForecastStep;
                    if(branch.kind=="slam"&&flightRemaining<=0){slowing=true;SetClip("Slam End");}
                    if(recovering) { if(recoveryTime<=0)stopped=true; else recoveryTime-=ForecastStep; }
                    if(wait>0)
                    {
                        wait-=ForecastStep;
                        if(phase=="Charge Antic" && i*ForecastStep<=branch.wait) {bx+=Num(enemy,"vx")*ForecastStep;by+=Num(enemy,"vy")*ForecastStep;}
                    }
                    else if(!stopped)
                    {
                        if(branch.kind=="slam"&&!slowing){SetClip("Fly");flightRemaining-=ForecastStep;}
                        else if(branch.kind=="charge"&&!recovering)SetClip("Charge");
                        var shape=Shape(raw,enemy,clip,clipTime);
                        double xmin=wallLeft+shape.Width/2-shape.X,xmax=wallRight-shape.Width/2-shape.X,ymin=wallBottom+shape.Height/2-shape.Y,ymax=wallTop-shape.Height/2-shape.Y;
                        if(slowing){vx*=.85;vy*=.85;}
                        bx+=vx*ForecastStep;by+=vy*ForecastStep;
                        bool side=bx<xmin||bx>xmax, vertical=by<ymin||by>ymax;
                        if(side) { bx=Math.Clamp(bx,xmin,xmax); vx=recovering||slowing?0:-vx; }
                        if(vertical)
                        {
                            bool down=by<ymin; by=Math.Clamp(by,ymin,ymax);vy=recovering||slowing?0:-vy;
                            if(branch.kind=="slam"&&!slowing) {wait=Num(motion,down?"slam_down_pause_seconds":"slam_up_pause_seconds",.15);SetClip(down?"Slam Down":"Slam Up");}
                        }
                        if(branch.kind=="charge" && (side||vertical) && !recovering) { vx*=.5;vy*=.5;recovering=true;recoveryTime=.3;SetClip("Charge Recover"); }
                    }
                    var current=Shape(raw,enemy,clip,clipTime);
                    path[i]=new(bx+current.X,by+current.Y);sizes[i]=new(current.Width,current.Height);
                }
                result.Add(new(branch.label+(clockOffset<0?"_early":clockOffset>0?"_late":""),path,sizes));
            }
        }
        return result;
    }

    internal static Point[] KnightPath(JsonObject raw, Buttons action, double delay=.14)
    {
        var p=raw["player"];var a=raw["abilities"];var m=raw["player_motion"];var arena=raw["arena"];var held=raw["held"];
        double run=Num(a,"run_speed",8.3),jump=Num(a,"jump_speed",16.65),gravity=Num(a,"gravity_y",-60)*Num(a,"gravity_scale",.79);
        double xmin=Num(arena,"left")-Num(p,"x")+Num(p,"width")/2,xmax=Num(arena,"right")-Num(p,"x")-Num(p,"width")/2;
        double ymin=Num(arena,"floor")-Num(p,"y")+Num(p,"height")/2,ymax=Num(arena,"ceiling")-Num(p,"y")-Num(p,"height")/2;
        double dashTime=Flag(m,"dashing")?Math.Max(0,Num(a,"dash_seconds",.15)-Num(m,"dash_elapsed_seconds")):0;
        int dashDir=(int)Num(p,"facing",1);
        double x=0,y=0,vy=Num(p,"vy"),wallSpeed=Num(m,"wall_jump_speed"),wallTime=Flag(m,"wall_locked")?.12:0;
        int wallDir=Math.Sign(Num(p,"vx")), heldMove=(int)Num(held,"move");bool heldJump=Flag(held,"jump");
        bool grounded=Flag(p,"grounded")&&!Flag(m,"jumping")&&!Flag(m,"double_jumping"),spent=Flag(p,"double_jumped"),started=false;
        double jumpAge=Flag(m,"jumping")?Num(m,"jump_steps")*.02:10;
        double sustain=Flag(m,"jumping")? Math.Max(0,(Num(a,"jump_sustain_steps",10)-Num(m,"jump_steps"))*.02):0;
        double doubleWait=Flag(m,"double_jumping")? Math.Max(0,(4-Num(m,"double_jump_steps"))*.02):0;
        double doubleTime=Flag(m,"double_jumping")?Math.Max(0,(Num(a,"double_jump_steps",10)-Num(m,"double_jump_steps"))*.02):0;
        bool pendingJump=PendingGroundJump(raw);
        if(pendingJump){sustain=Num(a,"jump_speed_sustain_seconds",.2);jumpAge=0;grounded=false;}
        double nailTime=Num(m,"nail_recoil_remaining_seconds");
        int nailDirection=(int)Num(m,"nail_recoil_direction");
        double nailSpeed=Num(m,"nail_recoil_speed");
        var path=new Point[ForecastSteps+1];path[0]=new(0,0);
        for(int i=1;i<=ForecastSteps;i++)
        {
            double pressTime=delay+(action.jump_repress?.04:0); bool active=i*ForecastStep>=delay; int move=active?action.move:heldMove; bool j=active?(action.jump && i*ForecastStep>=pressTime):heldJump;
            if(active&&!started&&i*ForecastStep>=pressTime)
            {
                started=true;
                if(action.dash && Flag(m,"dash_ready")){dashTime=Num(a,"dash_seconds",.15);dashDir=Flag(m,"wall_sliding")?-(int)Num(p,"facing",1):action.move;sustain=0;doubleTime=0;wallTime=0;}
                if(j&&(!heldJump||action.jump_repress))
                {
                    bool atWall=!grounded&&(x<=xmin+.09||x>=xmax-.09)&&Flag(a,"has_wall_jump");
                    if(atWall){wallDir=x<=xmin+.09?1:-1;wallTime=.16;wallSpeed=Num(a,"wall_jump_kickoff_speed",12);sustain=.2;jumpAge=0;spent=false;grounded=false;}
                    else if(grounded){sustain=Num(a,"jump_speed_sustain_seconds",.2);jumpAge=0;grounded=false;}
                    else if(!spent&&Flag(a,"has_double_jump")){doubleWait=.08;doubleTime=Num(a,"double_jump_steps",10)*.02;spent=true;}
                }
            }
            if(!j&&vy>0&&doubleTime<=0&&jumpAge>=Num(a,"jump_min_steps",4)*.02){sustain=0;vy=0;} // Native release respects its minimum jump steps.
            if(doubleTime>0){if(doubleWait<=0)vy=jump*1.1+gravity*ForecastStep;doubleWait-=ForecastStep;doubleTime-=ForecastStep;}
            else if(sustain>0){vy=jump+gravity*ForecastStep;sustain-=ForecastStep;jumpAge+=ForecastStep;grounded=false;}
            else if(!grounded)vy+=gravity*ForecastStep;
            double vx=move*run;
            bool dashActive=dashTime>0;
            if(dashActive){vx=dashDir*Num(a,"dash_speed",20);vy=0;dashTime-=ForecastStep;}
            if(wallTime>0){vx=wallDir*wallSpeed;wallSpeed=Math.Max(run,wallSpeed-.5);wallTime-=ForecastStep;}
            if(!dashActive && (i-1)*ForecastStep<nailTime-1e-6)vx=NailRecoilVelocity(vx,nailDirection,nailSpeed);
            if(Flag(m,"recoiling")&&i*ForecastStep<.12){vx=Num(p,"vx");vy=Num(p,"vy");}
            x=Math.Clamp(x+vx*ForecastStep,xmin,xmax);
            if(!grounded && vy<0 && ((move<0&&x<=xmin+.02)||(move>0&&x>=xmax-.02))&&Flag(a,"has_wall_jump"))vy=Math.Max(vy,-4);
            y+=vy*ForecastStep;
            if(y<=ymin){y=ymin;vy=0;grounded=true;sustain=0;doubleTime=0;}
            if(y>=ymax){y=ymax;vy=Math.Min(vy,0);sustain=0;doubleTime=0;}
            path[i]=new(x,y);
        }
        return path;
    }
    static JsonArray SamplePath(Point[] points)
    {
        var result=new JsonArray();
        foreach(int i in new[]{0,7,15,25,35})result.Add(new JsonArray(Round(i*ForecastStep),Round(points[i].X),Round(points[i].Y)));
        return result;
    }
    internal static JsonObject Forecast(JsonObject raw)
    {
        var enemies=raw["enemies"]!.AsArray();var p=raw["player"];
        var enemyPaths=new List<(JsonNode enemy,MotionPath path)>();
        var branches=new JsonArray();
        for(int e=0;e<enemies.Count;e++)
            foreach(var path in BossPaths(raw,enemies[e]!))
            {
                enemyPaths.Add((enemies[e]!,path));
                branches.Add(new JsonObject{["enemy_index"]=e,["branch"]=path.Branch,["centers_t_x_y"]=SamplePath(path.Points)});
            }
        var outcomes=new JsonObject();
        foreach(var (name,action) in CombatMoves())
        {
            if(action.dash && !Flag(raw["player_motion"],"dash_ready")) continue;
            var path=KnightPath(raw,action);
            double min=100, first=double.PositiveInfinity,near=double.PositiveInfinity;
            double overlap=double.PositiveInfinity, preOverlap=double.PositiveInfinity, postOverlap=double.PositiveInfinity,postMargin=double.PositiveInfinity,postMin=100,postOverlapDuration=0;
            foreach(double delay in new[]{.10,.18})
            {
                var kp=KnightPath(raw,action,delay);
                foreach(var (enemy,bp) in enemyPaths)
                {
                    double branchPostDuration=0;

                    for(int i=0;i<=ForecastSteps;i++)
                    {
                        double gap=BodyClearance(kp[i],bp.Points[i],(Num(p,"width")+bp.Sizes[i].X)/2,(Num(p,"height")+bp.Sizes[i].Y)/2);
                        double t=i*ForecastStep;
                        bool existingImmunity=Flag(raw["player_motion"],"shadow_dashing") && t<Num(raw["abilities"],"dash_seconds",.15)-Num(raw["player_motion"],"dash_elapsed_seconds");
                        bool newImmunity=action.dash && Flag(raw["player_motion"],"dash_ready") && Flag(raw["player_motion"],"shadow_dash_ready") && t>=delay+.02 && t<delay+Num(raw["abilities"],"dash_seconds",.15)-.02;
                        if(existingImmunity||newImmunity)continue; // Physical overlap is non-damaging only during native Shadow Dash, with a boundary tolerance.
                        min=Math.Min(min,gap);
                        if(gap<=.15)first=Math.Min(first,i*ForecastStep);
                        if(gap<=1.5)near=Math.Min(near,i*ForecastStep);
                        if(gap<=0){overlap=Math.Min(overlap,t);if(t<delay)preOverlap=Math.Min(preOverlap,t);else{postOverlap=Math.Min(postOverlap,t);branchPostDuration+=ForecastStep;}}
                        if(t>=delay){postMin=Math.Min(postMin,gap);if(gap<=.15)postMargin=Math.Min(postMargin,t);}
                    }
                    postOverlapDuration=Math.Max(postOverlapDuration,branchPostDuration);
                }
            }
            bool blocked=action.move<0&&Flag(p,"blocked_left")||action.move>0&&Flag(p,"blocked_right");
            outcomes[name]=new JsonObject {
                ["centers_t_x_y"]=SamplePath(path),["minimum_body_clearance"]=Round(min),
                ["first_contact_seconds"]=double.IsFinite(first)?Round(first):null,
                ["first_close_seconds"]=double.IsFinite(near)?Round(near):null,
                ["first_body_overlap_seconds"]=double.IsFinite(overlap)?Round(overlap):null,
                ["pre_input_body_overlap_seconds"]=double.IsFinite(preOverlap)?Round(preOverlap):null,
                ["post_input_body_overlap_seconds"]=double.IsFinite(postOverlap)?Round(postOverlap):null,
                ["post_input_margin_breach_seconds"]=double.IsFinite(postMargin)?Round(postMargin):null,
                ["post_input_minimum_body_clearance"]=Round(postMin),
                ["post_input_overlap_duration_seconds"]=Round(postOverlapDuration),
                ["horizontal_direction_currently_blocked"]=blocked,
                ["dash"]=action.dash,
                ["fresh_jump"]=action.jump&&(action.jump_repress||!Flag(raw["held"],"jump"))
            };
        }
        return new JsonObject{["horizon_seconds"]=.7,["control_delay_seconds"]=new JsonArray(.10,.18),
            ["assumptions"]="Approximate physical branches; same intent held through horizon. Positions relative to current knight center. t_x_y is seconds,x,y. Boss bounds and center offsets follow the native animation shape catalog, at their measured animation times. Frames without a new collider retain the previous observed shape. The measured slam flight timer ends powered flight; Slam End retains momentum multiplied by0.85 each20ms physics step, rather than stopping instantly. Charge and slam transitions change shape. Knight size held constant. Clearance excludes native Shadow Dash immunity intervals, with conservative 20ms boundaries. Otherwise it is signed body-edge distance, negative means overlap; contact includes 0.15 modelling tolerance; close means gap <=1.5. Charge-wall impact reflects the impacted velocity axis and halves both components for 0.3 seconds before stopping. Body overlap is gap <=0; first_contact retains the 0.15 tolerance, so margin-only proximity is not actual overlap. pre_input and post_input separate the uncontrollable response interval from outcomes after each assumed delay; timestamps are all from the observation. Post-input duration is the worst branch/delay, not a probability. A newly observed grounded jump press with zero elapsed physics steps is propagated into the response interval. Measured ongoing nail recoil follows native horizontal velocity rules through its remaining fixed steps. New strike recoil, later unknown RNG transitions and exact wall-slide locks are not fully modelled. Re-evaluate every observation.",
            ["enemy_paths"]=branches,["movement_outcomes"]=outcomes};
    }
}
