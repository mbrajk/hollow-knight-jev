using System;
using System.Collections;
using System.IO;
using System.Linq;
using GlobalEnums;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowKnightTrial
{
    // Arena setup and scoring are local harness responsibilities, never model advice.
    public sealed class BossTrial : MonoBehaviour
    {
        public static BossTrial Instance;
        public static bool Enabled { get { return Environment.GetCommandLineArgs().Contains("--jev-gruz-mother"); } }
        public static bool Training { get { return Enabled && Environment.GetCommandLineArgs().Contains("--jev-boss-training"); } }
        public const string Scene = "GG_Gruz_Mother";
        public string Status = "running";
        public int Hits, MaxHp, RemainingHp;
        public bool Armed;
        string runtime;
        HeroController subscribedHero;
        BossSceneController subscribedBoss;
        HealthManager boss;
        bool entering;
        bool inspected;
        static readonly System.Reflection.MethodInfo canAttack = typeof(HeroController).GetMethod("CanAttack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        static readonly System.Reflection.FieldInfo doubleJumped = typeof(HeroController).GetField("doubleJumped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        float? left, right, floor, ceiling;

        public static void Install()
        {
            if (!Enabled || Instance != null) return;
            var obj = new GameObject("Jev boss trial evaluator");
            DontDestroyOnLoad(obj);
            Instance = obj.AddComponent<BossTrial>();
            Instance.runtime = Path.GetFullPath(Path.Combine(Application.dataPath, "../.jev/runtime"));
        }

        void Update()
        {
            var gm = GameManager.instance;
            if (!TrialSession.OwnsCurrentSlot || gm == null || gm.profileID != 4) return;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (!gm.IsGameplayScene() && scene != "GG_Entrance_Cutscene") return;
            var hero = HeroController.instance;
            if (hero == null) return;
            var enter = Path.Combine(runtime, "enter-gruz.request");
            if (!entering && File.Exists(enter) && !gm.IsInSceneTransition && (hero.acceptingInput || gm.isPaused))
            {
                File.Delete(enter);
                StartCoroutine(EnterArena());
                return;
            }
            if (subscribedHero != hero)
            {
                if (subscribedHero != null) subscribedHero.OnTakenDamage -= OnHit;
                subscribedHero = hero;
                hero.OnTakenDamage += OnHit;
            }
            if (scene != Scene) { Armed = false; return; }
            if (entering) return;
            var controller = BossSceneController.Instance;
            if (controller != null && subscribedBoss != controller)
            {
                subscribedBoss = controller;
                controller.OnBossesDead += OnVictory;
            }
            if (boss == null)
                boss = UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None)
                    .FirstOrDefault(h => h.name == "Giant Fly");
            if (boss != null)
            {
                MaxHp = Math.Max(MaxHp, boss.hp);
                RemainingHp = Math.Max(0, boss.hp);
                if (gm.isPaused && !inspected)
                {
                    inspected = true;
                    DumpBoss();
                }
            }
            if (!Armed && !entering && hero.acceptingInput && !hero.controlReqlinquished && !hero.cState.transitioning && controller != null && controller.HasTransitionedIn)
            {
                MeasureArena(hero);
                if (boss != null && MaxHp > 0 && left.HasValue && right.HasValue && floor.HasValue && ceiling.HasValue)
                {
                    Armed = true;
                    File.WriteAllText(Path.Combine(runtime, "pause.request"), "boss arena ready for bounded trial");
                }
            }
        }

        IEnumerator EnterArena()
        {
            entering = true;
            var gm = GameManager.instance;
            if (gm.isPaused) yield return gm.PauseGameToggle();
            if (!TrialSession.OwnsCurrentSlot || gm.profileID != 4) { entering = false; yield break; }
            Armed = false; Status = "running"; Hits = MaxHp = RemainingHp = 0;
            boss = null; subscribedBoss = null; inspected = false;
            left = right = floor = ceiling = null;
            // Same arena level and return event as the native Attuned statue selection.
            BossSceneController.SetupEvent = self => {
                self.BossLevel = 0;
                self.DreamReturnEvent = "DREAM RETURN";
                self.OnBossSceneComplete += () => self.DoDreamReturn();
            };
            gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = Scene, EntryGateName = "door_dreamEnter", EntryDelay = 0,
                Visualization = GameManager.SceneLoadVisualizations.GodsAndGlory,
                // The dream gate supplies its own Godhome transition. A regular
                // scene fade has no matching fade-in on this customFade gate.
                PreventCameraFadeOut = true,
                WaitForSceneTransitionCameraFade = false
            });
            while (gm.IsInSceneTransition || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != Scene) yield return null;
            entering = false;
        }

        void OnHit()
        {
            if (!TrialSession.OwnsCurrentSlot || GameManager.instance == null || GameManager.instance.profileID != 4 || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != Scene) return;
            Hits++;
            if (Training)
            {
                // The native damage event runs after health subtraction and before
                // the death check. Preserve recoil and normal invulnerability frames.
                HeroController.instance.AddHealth(PlayerData.instance.CurrentMaxHealth);
                Debug.Log("[JevBridge] TRAINING HIT " + Hits + ": health restored; continuing.");
                return;
            }
            Status = "failed_hit";
            File.WriteAllText(Path.Combine(runtime, "pause.request"), "hitless attempt failed on damage event");
        }
        void OnVictory()
        {
            if (!TrialSession.OwnsCurrentSlot || GameManager.instance == null || GameManager.instance.profileID != 4 || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != Scene) return;
            RemainingHp = 0;
            Status = Training ? "training_complete" : Hits == 0 ? "success" : "failed_hit";
            File.WriteAllText(Path.Combine(runtime, "pause.request"), "boss defeated; attempt complete");
        }
        void OnDestroy()
        {
            if (subscribedHero != null) subscribedHero.OnTakenDamage -= OnHit;
            if (subscribedBoss != null) subscribedBoss.OnBossesDead -= OnVictory;
            if (Instance == this) Instance = null;
        }
        static readonly System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        static object HeroField(HeroController h, string name) { var f = typeof(HeroController).GetField(name, Private); return f == null ? null : f.GetValue(h); }
        static bool HeroCan(HeroController h, string name) { var m = typeof(HeroController).GetMethod(name, Private); return m != null && (bool)m.Invoke(h, null); }
        static double? FsmFloat(PlayMakerFSM f, string name) { var v = f == null ? null : f.FsmVariables.FindFsmFloat(name); return v == null ? (double?)null : R(v.Value); }
        static double? ClipDuration(tk2dSpriteAnimator a, string name) { var c = a == null ? null : a.GetClipByName(name); return c == null || c.fps <= 0 ? (double?)null : (double)c.frames.Length / c.fps; }
        static object BodyShapes(tk2dSpriteAnimator a) {
            if (a == null || a.Library == null) return null;
            var sprite = a.GetComponent<tk2dBaseSprite>();
            if (sprite == null) return null;
            return a.Library.clips.Where(c => c != null && c.frames != null && new [] { "Fly", "Charge", "Charge Antic", "Charge Recover", "Slam Up", "Slam Down", "Slam End" }.Contains(c.name)).Select(c => {
                var all = c.frames.Select(frame => {
                    var d = frame.spriteCollection.spriteDefinitions[frame.spriteId];
                    if (d.colliderVertices == null || d.colliderVertices.Length < 2) return null;
                    var offset = a.transform.TransformVector(Vector3.Scale(d.colliderVertices[0], sprite.scale));
                    var vx = a.transform.TransformVector(new Vector3(d.colliderVertices[1].x * sprite.scale.x * 2, 0, 0));
                    var vy = a.transform.TransformVector(new Vector3(0, d.colliderVertices[1].y * sprite.scale.y * 2, 0));
                    return new { offset_x = R(offset.x), offset_y = R(offset.y), width = R(Math.Abs(vx.x) + Math.Abs(vy.x)), height = R(Math.Abs(vx.y) + Math.Abs(vy.y)) };
                }).ToArray();
                var unique = all.Distinct().ToArray();
                return new { clip = c.name, seconds = c.fps > 0 ? (double)c.frames.Length / c.fps : 0, fps = c.fps, frames = unique, frame_map = all.Select(v => Array.IndexOf(unique, v)).ToArray() };
            }).ToArray();
        }

        static object Motion(PlayMakerFSM f, tk2dSpriteAnimator a)
        {
            if (f == null) return null;
            var phase = f.ActiveStateName;
            bool charge = phase == "Charge" || phase == "Charge Antic";
            bool slam = phase == "Flying" || phase == "Slam Up" || phase == "Slam Down" || phase.StartsWith("Launch ");
            var c = a == null ? null : a.CurrentClip;
            bool once = c != null && c.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once && a.ClipFps > 0;
            return new {
                charge_angle_degrees = charge ? FsmFloat(f, "Charge Angle") : null,
                slam_angle_degrees = slam ? FsmFloat(f, "Slamming Angle") : null,
                slam_up_angle_degrees = slam ? FsmFloat(f, "Up Angle") : null,
                slam_down_angle_degrees = slam ? FsmFloat(f, "Down Angle") : null,
                body_shapes = BodyShapes(a),
                animation_frame = a == null ? 0 : a.CurrentFrame,
                animation_time_seconds = a == null ? 0 : a.ClipTimeSeconds,
                slam_flight_elapsed_seconds = slam ? FsmFloat(f,"Timer") : null,
                slam_flight_limit_seconds = slam ? FsmFloat(f,"Slam Time") : null,
                body_origin = a == null ? null : (object)new { x = R(a.transform.position.x), y = R(a.transform.position.y) },
                animation_remaining_seconds = once ? (double?)Math.Max(0, c.frames.Length / a.ClipFps - a.ClipTimeSeconds) : null,
                slam_up_pause_seconds = ClipDuration(a, "Slam Up"),
                slam_down_pause_seconds = ClipDuration(a, "Slam Down")
            };
        }
        static float? Wall(Vector2 start, Vector2 direction, bool vertical)
        {
            foreach (var hit in Physics2D.RaycastAll(start, direction, 100, (1 << 8) | (1 << 25)).OrderBy(h => h.distance))
                if (!hit.collider.isTrigger && hit.distance > .01f) return vertical ? hit.point.y : hit.point.x;
            return null;
        }
        void MeasureArena(HeroController hero)
        {
            Vector2 p = hero.GetComponent<Collider2D>().bounds.center;
            floor = Wall(p, Vector2.down, true);
            ceiling = Wall(p, Vector2.up, true);
            Vector2 q = new Vector2(p.x, (floor ?? p.y) + 2);
            left = Wall(q, Vector2.left, false);
            right = Wall(q, Vector2.right, false);
        }
        static double R(float x) { return Math.Round(x, 2); }
        static object InspectValue(object value)
        {
            if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return value;
            if (value is Vector2) { var v = (Vector2)value; return new { x = v.x, y = v.y }; }
            if (value is Vector3) { var v = (Vector3)value; return new { x = v.x, y = v.y, z = v.z }; }
            if (value is UnityEngine.Object) return (UnityEngine.Object)value == null ? null : ((UnityEngine.Object)value).name;
            if (value.GetType().Namespace == "HutongGames.PlayMaker")
            {
                var property = value.GetType().GetProperty("Value");
                if (property != null) { var name = value.GetType().GetProperty("Name"); return new { variable = name == null ? null : name.GetValue(value, null), value = InspectValue(property.GetValue(value, null)) }; }
            }
            return value.GetType().Name;
        }
        void DumpBoss()
        {
            // Engineering evidence only: this file is never part of observations
            // or the controller's model-state whitelist.
            var fsms = boss.GetComponents<PlayMakerFSM>().Select(f => new {
                name = f.FsmName, current_state = f.ActiveStateName,
                floats = f.FsmVariables.FloatVariables.Select(v => new { name = v.Name, value = v.Value }).ToArray(),
                ints = f.FsmVariables.IntVariables.Select(v => new { name = v.Name, value = v.Value }).ToArray(),
                states = f.Fsm.States.Select(s => new {
                    name = s.Name,
                    transitions = s.Transitions.Select(t => new { event_name = t.EventName, to = t.ToState }).ToArray(),
                    actions = s.Actions.Select(a => new {
                        type = a.GetType().FullName,
                        fields = a.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .ToDictionary(field => field.Name, field => InspectValue(field.GetValue(a)))
                    }).ToArray()
                }).ToArray()
            }).ToArray();
            File.WriteAllText(Path.Combine(runtime, "boss-fsm-inspection.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new { local_inspection_only = true, fsms, body_shapes = BodyShapes(boss.GetComponent<tk2dSpriteAnimator>()) }));
        }
        static bool Blocked(Bounds bounds, Vector2 direction)
        {
            // A small physical probe identifies current wall contact only. It
            // neither predicts enemy motion nor chooses an evasive direction.
            Vector2 size = new Vector2(bounds.size.x - .02f, bounds.size.y - .04f);
            return Physics2D.BoxCastAll(bounds.center, size, 0, direction, .075f, (1 << 8) | (1 << 25))
                .Any(h => h.collider != null && !h.collider.isTrigger && Vector2.Dot(h.normal, direction) < -.5f);
        }
        public static void SelfTest()
        {
            var wall = new GameObject("Jev measured wall fixture");
            var floorObject = new GameObject("Jev floor fixture");
            try
            {
                Vector2 p = new Vector2(-1500, -1500);
                var bounds = new Bounds(p, new Vector3(.5f, 1.28f, 1));
                wall.layer = 8; wall.transform.position = p + Vector2.left * .8f;
                var collider = wall.AddComponent<BoxCollider2D>(); collider.size = new Vector2(1, 3);
                floorObject.layer = 8; floorObject.transform.position = p + Vector2.down * 1.14f;
                floorObject.AddComponent<BoxCollider2D>().size = new Vector2(8, 1);
                Physics2D.SyncTransforms();
                if (!Blocked(bounds, Vector2.left) || Blocked(bounds, Vector2.right)) throw new Exception("Boss wall probe direction/floor fixture failed");
                collider.isTrigger = true;
                Physics2D.SyncTransforms();
                if (Blocked(bounds, Vector2.left)) throw new Exception("Boss wall probe trigger fixture failed");
                Debug.Log("[JevBridge] BOSS SELFTEST PASS: measured left wall, free right, floor and trigger exclusion.");
            }
            finally { DestroyImmediate(wall); DestroyImmediate(floorObject); }
        }
        // Only the grounded, read-only part of CanJump. Calling native CanJump during
        // observation can consume its ledge buffer, so never invoke it here.
        static bool GroundJumpReady(HeroController hero)
        {
            return hero.cState.onGround && hero.hero_state != ActorStates.no_input
                && hero.hero_state != ActorStates.hard_landing && hero.hero_state != ActorStates.dash_landing
                && !hero.cState.wallSliding && !hero.cState.dashing && !hero.cState.backDashing
                && !hero.cState.jumping && !hero.cState.bouncing && !hero.cState.shroomBouncing;
        }

        public object Capture(HeroController hero)
        {
            var bounds = hero.GetComponent<Collider2D>().bounds;
            Vector2 p = bounds.center;
            var v = hero.GetComponent<Rigidbody2D>().linearVelocity;
            var actions = InputHandler.Instance.inputActions;
            var cam = Camera.main;
            if (cam == null) return null;
            var enemies = UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None)
                .Where(h => !h.isDead && h.hp > 0).Select(h => {
                    var body = h.GetComponentsInChildren<Collider2D>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy && c.gameObject.layer == 11);
                    if (body == null) return null;
                    var b = body.bounds;
                    var screen = cam.WorldToViewportPoint(b.center);
                    if (screen.z <= 0 || screen.x < 0 || screen.x > 1 || screen.y < 0 || screen.y > 1) return null;
                    var ev = body.attachedRigidbody == null ? Vector2.zero : body.attachedRigidbody.linearVelocity;
                    var animator = h.GetComponent<tk2dSpriteAnimator>();
                    var fsm = h.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Big Fly Control");
                    return (object)new { x = R(b.center.x - p.x), y = R(b.center.y - p.y), width = R(b.size.x), height = R(b.size.y), vx = R(ev.x), vy = R(ev.y), hp = h.hp, max_hp = h == boss ? (int?)MaxHp : null, boss = h == boss,
                        animation = animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : null,
                        motion = Motion(fsm, animator),
                        state = fsm == null ? null : fsm.ActiveStateName,
                        state_elapsed_seconds = fsm == null || fsm.Fsm.ActiveState == null ? (double?)null : R(fsm.Fsm.ActiveState.StateTime) };
                }).Where(e => e != null).Take(32).ToArray();
            return new {
                player = new { x = R(p.x), y = R(p.y), vx = R(v.x), vy = R(v.y), width = R(bounds.size.x), height = R(bounds.size.y), grounded = hero.cState.onGround, touching_wall_left = hero.touchingWallL, touching_wall_right = hero.touchingWallR, blocked_left = Blocked(bounds, Vector2.left), blocked_right = Blocked(bounds, Vector2.right), attacking = hero.cState.attacking, attack_ready = canAttack != null && (bool)canAttack.Invoke(hero, null), double_jumped = doubleJumped != null && (bool)doubleJumped.GetValue(hero), facing = hero.cState.facingRight ? 1 : -1, health = PlayerData.instance.health },
                game_clock = new { game_seconds = Time.time, time_scale = Time.timeScale },
                input_timing = Bridge.CaptureJumpTiming(actions.jump.IsPressed),
                player_motion = new { dash_ready = HeroCan(hero, "CanDash"), shadow_dash_ready = PlayerData.instance.hasShadowDash && Convert.ToSingle(HeroField(hero, "shadowDashTimer")) <= 0, dashing = hero.cState.dashing, shadow_dashing = hero.cState.shadowDashing, dash_elapsed_seconds = HeroField(hero, "dash_timer"), dash_cooldown_seconds = HeroField(hero, "dashCooldownTimer"), shadow_dash_cooldown_seconds = HeroField(hero, "shadowDashTimer"), jumping = hero.cState.jumping, ground_jump_ready = GroundJumpReady(hero), double_jumping = hero.cState.doubleJumping, wall_sliding = hero.cState.wallSliding, recoiling = hero.cState.recoiling, bouncing = hero.cState.bouncing, shroom_bouncing = hero.cState.shroomBouncing, nail_recoil_direction = hero.cState.recoilingRight ? 1 : hero.cState.recoilingLeft ? -1 : 0, nail_recoil_speed = R(Convert.ToBoolean(HeroField(hero, "recoilLarge")) ? hero.RECOIL_HOR_VELOCITY_LONG : hero.RECOIL_HOR_VELOCITY), nail_recoil_remaining_seconds = R((hero.cState.recoilingLeft || hero.cState.recoilingRight) ? Math.Max(0, hero.RECOIL_HOR_STEPS - Convert.ToSingle(HeroField(hero, "recoilSteps")) + 1) * Time.fixedDeltaTime : 0), can_wall_jump = HeroCan(hero, "CanWallJump"), can_double_jump = HeroCan(hero, "CanDoubleJump"), jump_steps = HeroField(hero, "jump_steps"), double_jump_steps = HeroField(hero, "doubleJump_steps"), wall_locked = HeroField(hero, "wallLocked"), wall_lock_steps = HeroField(hero, "wallLockSteps"), wall_jump_speed = HeroField(hero, "currentWalljumpSpeed") },
                enemies, arena = new { left, right, floor, ceiling },
                held = new { move = Math.Sign(actions.moveVector.X), look = Math.Sign(actions.moveVector.Y), jump = actions.jump.IsPressed, attack = actions.attack.IsPressed },
                abilities = new { dash_speed = R(hero.DASH_SPEED), dash_seconds = R(hero.DASH_TIME), has_shadow_dash = PlayerData.instance.hasShadowDash, wall_jump_kickoff_speed = R(hero.WJ_KICKOFF_SPEED), jump_min_steps = hero.JUMP_STEPS_MIN, double_jump_steps = hero.DOUBLE_JUMP_STEPS, run_speed = R(hero.RUN_SPEED), jump_speed = R(hero.JUMP_SPEED), jump_sustain_steps = hero.JUMP_STEPS + 1, fixed_timestep_seconds = Time.fixedDeltaTime, jump_speed_sustain_seconds = (hero.JUMP_STEPS + 1) * Time.fixedDeltaTime, gravity_y = Physics2D.gravity.y, gravity_scale = hero.DEFAULT_GRAVITY, has_double_jump = PlayerData.instance.hasDoubleJump, has_dash = PlayerData.instance.hasDash, has_wall_jump = PlayerData.instance.hasWalljump }
            };
        }
    }
}
