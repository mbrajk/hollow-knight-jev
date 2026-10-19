using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InControl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowKnightTrial
{
    // All Unity reads and writes happen on the game's main thread. No provider calls here.
    public sealed class Bridge : MonoBehaviour
    {
        static Bridge instance;
        string root, session, lastId = "", status = "idle", lastResult = "none", scene = "";
        bool enabledControls, stopped, wasBackground;
        long sequence;
        readonly JumpInputTiming jumpTiming = new JumpInputTiming();
        int telemetryWriteSkips;
        float nextObserve;
        int pollFrame = -1;
        DateTime leaseEnd, attackEnd, jumpReleaseEnd, dashEnd;
        Command command;
        StreamWriter recording;
        HeroController cachedHero;
        float nextHeroSearch;
        Collider2D destination;
        bool reachedDoor;
        readonly ExplorationMemory explorationMemory = new ExplorationMemory();
        readonly Queue<KeyValuePair<long, DateTime>> observations = new Queue<KeyValuePair<long, DateTime>>();
        readonly Collider2D[] hits = new Collider2D[64];
        readonly Dictionary<int, string> kinds = new Dictionary<int, string>();
        const int Width = 49, Height = 41;
        const float Cell = .5f;
        const float OriginX = -12f, OriginY = -6f;

        sealed class SensingGrid
        {
            public int width = Width, height = Height;
            public float cell = Cell, ox = OriginX, oy = OriginY;
        }

        sealed class TerrainSamples
        {
            public float origin_x, origin_y, cell_size;
            public int width, height;
            public string[] rows;
        }

        sealed class SurfacePoint
        {
            public int collider;
            public float x, y, nx, ny;
        }

        static SensingGrid GridForCamera(Vector2 center, Camera cam)
        {
            var grid = new SensingGrid();
            if (cam == null) return grid;
            float depth = cam.WorldToViewportPoint(Vector3.zero).z;
            Vector3 lower = cam.ViewportToWorldPoint(new Vector3(0, 0, depth));
            Vector3 upper = cam.ViewportToWorldPoint(new Vector3(1, 1, depth));
            float spanX = Mathf.Abs(upper.x - lower.x), spanY = Mathf.Abs(upper.y - lower.y);
            // Cover the entire viewport even when the Knight is at a camera edge.
            // Keep the telemetry bounded on unusually wide displays.
            grid.cell = Mathf.Max(Cell, Mathf.Max(spanX, spanY) / 63f);
            grid.width = Mathf.Min(64, Mathf.CeilToInt(spanX / grid.cell) + 1);
            grid.height = Mathf.Min(64, Mathf.CeilToInt(spanY / grid.cell) + 1);
            grid.ox = Mathf.Min(lower.x, upper.x) - center.x;
            grid.oy = Mathf.Min(lower.y, upper.y) - center.y;
            return grid;
        }

        public static void Install()
        {
            if (instance != null || !Environment.GetCommandLineArgs().Contains("--jev-bridge")) return;
            try
            {
                var host = new GameObject("Hollow Knight Trial Bridge");
                DontDestroyOnLoad(host);
                instance = host.AddComponent<Bridge>();
                instance.Initialize();
            }
            catch (Exception e) { Debug.LogError("[JevBridge] Install failed: " + e); }
        }

        public static JumpInputSnapshot CaptureJumpTiming(bool held)
        {
            return instance == null ? null : instance.jumpTiming.Capture(held, Time.time, Time.fixedTime, Time.fixedDeltaTime);
        }

        void Initialize()
        {
            root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.jev/runtime"));
            Directory.CreateDirectory(root);
            session = Guid.NewGuid().ToString("N");
            enabledControls = Environment.GetCommandLineArgs().Contains("--jev-control");
            wasBackground = Application.runInBackground;
            Application.runInBackground = true;
            recording = new StreamWriter(Path.Combine(root, "observations-" + session + ".jsonl"));
            recording.AutoFlush = true;
            Debug.Log("[JevBridge] Started " + session + "; controls=" + enabledControls);
            if (Environment.GetCommandLineArgs().Contains("--jev-selftest")) SelfTest();
            TrialSession.Install();
            BossTrial.Install();
        }

        bool Gameplay()
        {
            var gm = GameManager.instance;
            var hero = Hero();
            return gm != null && gm.IsGameplayScene() && hero != null && hero.gameObject.activeInHierarchy;
        }

        HeroController Hero()
        {
            if (cachedHero == null && Time.realtimeSinceStartup >= nextHeroSearch)
            {
                nextHeroSearch = Time.realtimeSinceStartup + 1;
                cachedHero = UnityEngine.Object.FindAnyObjectByType<HeroController>();
            }
            return cachedHero;
        }

        bool CanControl()
        {
            var gm = GameManager.instance;
            var hero = Hero();
            return enabledControls && !stopped && TrialSession.OwnsCurrentSlot && gm != null && gm.profileID == 4 && !TrialSession.PauseTransitionInProgress && Gameplay() && (BossTrial.Enabled ? (scene == BossTrial.Scene && BossTrial.Instance != null && BossTrial.Instance.Armed && BossTrial.Instance.Status == "running") : scene == "Tutorial_01")
                && !gm.isPaused && hero.acceptingInput && !hero.controlReqlinquished
                && !hero.cState.dead && !hero.cState.transitioning;
        }

        void Update()
        {
            try
            {
                string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (scene != currentScene)
                {
                    scene = currentScene;
                    explorationMemory.Reset();
                    jumpTiming.Reset();
                    command = null;
                    observations.Clear();
                    kinds.Clear();
                    status = "scene_changed";
                }
                Poll();
                if (Time.realtimeSinceStartup >= nextObserve)
                {
                    nextObserve = Time.realtimeSinceStartup + (BossTrial.Enabled && scene == BossTrial.Scene ? .033f : .1f);
                    Observe();
                }
            }
            catch (Exception e)
            {
                command = null;
                status = "bridge_error";
                Debug.LogError("[JevBridge] " + e);
            }
        }

        void Poll()
        {
            if (pollFrame == Time.frameCount) return;
            pollFrame = Time.frameCount;
            if (Input.GetKey(KeyCode.F8) || File.Exists(Path.Combine(root, "STOP")))
            {
                stopped = true;
                status = "emergency_stop";
                command = null;
            }
            if (command != null && (DateTime.UtcNow >= leaseEnd || !CanControl()))
            {
                command = null;
                status = "released";
            }
            string path = Path.Combine(root, "command.json");
            if (!File.Exists(path)) return;
            string json;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 4096) { command = null; status = "rejected_oversized"; return; }
                    using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
                }
            }
            catch (IOException) { return; } // Finite existing lease still expires normally.
            catch (UnauthorizedAccessException) { return; }
            if (json.Length > 4096) return;
            Command next;
            try { next = JsonConvert.DeserializeObject<Command>(json); }
            catch (JsonException) { command = null; status = "rejected_malformed"; lastResult = status; return; }
            if (next == null || string.IsNullOrEmpty(next.id) || next.id == lastId) return;
            lastId = next.id;
            DateTime created;
            var now = DateTime.UtcNow;
            bool observed = observations.Any(p => p.Key == next.observed_sequence && (now - p.Value).TotalMilliseconds <= 1000);
            if (!DateTime.TryParse(next.created_utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out created)
                || next.session_id != session || !observed || next.duration_ms < 1 || next.duration_ms > 300
                || (next.jump_repress && (!next.jump || next.duration_ms < 80))
                || (next.attack_press_ms != 0 && next.attack_press_ms != 50)
                || Math.Abs(next.move) > 1 || Math.Abs(next.look) > 1
                || (now - created.ToUniversalTime()).TotalMilliseconds < -100
                || now >= created.ToUniversalTime().AddMilliseconds(next.duration_ms))
            {
                status = "rejected_invalid_or_stale";
                lastResult = status;
                command = null;
                return;
            }
            if (!CanControl()) { status = "rejected_control_disabled"; lastResult = status; command = null; return; }
            command = next;
            dashEnd = now.AddMilliseconds(50);
            jumpReleaseEnd = next.jump_repress ? now.AddMilliseconds(40) : DateTime.MinValue;
            leaseEnd = created.ToUniversalTime().AddMilliseconds(next.duration_ms);
            attackEnd = next.attack_press_ms == 0 ? leaseEnd : created.ToUniversalTime().AddMilliseconds(next.attack_press_ms);
            status = "accepted";
            lastResult = status;
        }

        // Prefix on PlayerAction.Update: only the six base controls for the current
        // HeroActions instance are overridden. InControl retains its own edge detection.
        public static bool InputOverride(PlayerAction action, ulong tick, float delta)
        {
            try
            {
                var b = instance;
                if (b == null || b.root == null) return false;
                b.Poll();
                if (b.command == null || !b.CanControl()) return false;
                var actions = InputHandler.Instance == null ? null : InputHandler.Instance.inputActions;
                if (actions == null) return false;
                var c = b.command;
                bool down;
                if (action == actions.left) down = c.move < 0;
                else if (action == actions.right) down = c.move > 0;
                else if (action == actions.up) down = c.look > 0;
                else if (action == actions.down) down = c.look < 0;
                else if (action == actions.dash) down = c.dash && DateTime.UtcNow < b.dashEnd;
                else if (action == actions.jump) down = c.jump && DateTime.UtcNow >= b.jumpReleaseEnd;
                else if (action == actions.attack) down = c.attack && DateTime.UtcNow < b.attackEnd;
                else return false;
                action.CommitWithState(down, tick, delta);
                if (action == actions.jump) b.jumpTiming.Record(action.IsPressed, action.WasPressed, Time.time, Time.fixedTime);
                b.status = "applied";
                b.lastResult = "applied";
                return true;
            }
            catch (Exception e)
            {
                if (instance != null) { instance.command = null; instance.stopped = true; }
                Debug.LogError("[JevBridge] Input disabled: " + e);
                return false;
            }
        }

        void Observe()
        {
            DateTime now = DateTime.UtcNow;
            ++sequence;
            observations.Enqueue(new KeyValuePair<long, DateTime>(sequence, now));
            while (observations.Count > 12) observations.Dequeue();
            bool gameplay = Gameplay();
            var gm = GameManager.instance;
            var hero = Hero();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            object observation = gameplay ? (BossTrial.Enabled && scene == BossTrial.Scene ? BossTrial.Instance.Capture(hero) : Capture(hero)) : null;
            if (!gameplay) explorationMemory.Suspend();
            sw.Stop();
            var c = command;
            var state = new {
                schema_version = 1, session_id = session, sequence,
                observed_utc = now.ToString("O"), scene, gameplay,
                paused = gm != null && gm.isPaused, control_enabled = CanControl(),
                trial = new { slot = gm == null ? 0 : gm.profileID, owned_session = TrialSession.OwnsCurrentSlot },
                observation,
                episode = new { status = EpisodeStatus(hero), objective = BossTrial.Enabled ? "defeat_gruz_mother_hitless" : "reach_giant_door_alive", training = BossTrial.Training, health_restoration = BossTrial.Training, hits = BossTrial.Instance == null ? 0 : BossTrial.Instance.Hits, boss_max_hp = BossTrial.Instance == null ? 0 : BossTrial.Instance.MaxHp, boss_remaining_hp = BossTrial.Instance == null ? 0 : BossTrial.Instance.RemainingHp },
                control = new { last_command_id = lastId, last_status = status, last_command_result = lastResult, active = c != null,
                    move = c == null ? 0 : c.move, look = c == null ? 0 : c.look,
                    jump = c != null && c.jump, attack = c != null && c.attack },
                debug = new { capture_ms = sw.Elapsed.TotalMilliseconds, input_ready = hero != null && hero.acceptingInput,
                    dead = hero != null && hero.cState.dead, attacking = hero != null && hero.cState.attacking,
                    bouncing = hero != null && hero.cState.bouncing,
                    up_attacking = hero != null && hero.cState.upAttacking, down_attacking = hero != null && hero.cState.downAttacking,
                    telemetry_write_skips = telemetryWriteSkips, frame = Time.frameCount }
            };
            string json = JsonConvert.SerializeObject(state);
            try { AtomicWrite(Path.Combine(root, "state.json"), json); }
            catch (IOException) { telemetryWriteSkips++; return; }
            catch (UnauthorizedAccessException) { telemetryWriteSkips++; return; }
            recording.WriteLine(json);
        }

        object Capture(HeroController hero)
        {
            var collider = hero.GetComponent<Collider2D>();
            var bounds = collider.bounds;
            Vector2 center = bounds.center;
            var rb = hero.GetComponent<Rigidbody2D>();
            Vector2 velocity = rb == null ? Vector2.zero : rb.linearVelocity;
            var cam = Camera.main;
            var grid = GridForCamera(center, cam);
            explorationMemory.Observe(center.x, center.y, Time.realtimeSinceStartup,
                GameManager.instance != null && !GameManager.instance.isPaused && !hero.cState.dead);
            var terrain = SampleTerrain(center, hero.transform, cam, grid);
            var sensorCenter = center + new Vector2(grid.ox + (grid.width - 1) * grid.cell / 2, grid.oy + (grid.height - 1) * grid.cell / 2);
            var nearby = Physics2D.OverlapBoxAll(sensorCenter, new Vector2(grid.width * grid.cell, grid.height * grid.cell), 0);
            var enemies = new List<object>();
            var objects = new List<object>();
            var seen = new HashSet<int>();
            foreach (var hit in nearby.OrderBy(h => ((Vector2)h.bounds.center - center).sqrMagnitude))
            {
                if (!hit.enabled || hit.transform.IsChildOf(hero.transform) || !InView(cam, hit.bounds.center)) continue;
                var health = hit.GetComponentInParent<HealthManager>();
                if (health != null && !health.isDead && health.hp > 0 && enemies.Count < 8 && !seen.Contains(health.GetInstanceID()))
                {
                    // Enemy sensor/attack colliders can be much larger than its body.
                    var physical = health.GetComponentsInChildren<Collider2D>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy && c.gameObject.layer == 11);
                    if (physical == null || !InView(cam, physical.bounds.center)) continue;
                    seen.Add(health.GetInstanceID());
                    var body = physical.attachedRigidbody;
                    var v = body == null ? Vector2.zero : body.linearVelocity;
                    var b = physical.bounds;
                    enemies.Add(new { x = R(b.center.x - center.x), y = R(b.center.y - center.y), width = R(b.size.x), height = R(b.size.y), vx = R(v.x), vy = R(v.y) });
                }
                else if (objects.Count < 8)
                {
                    var breakable = BreakableComponent(hit);
                    if (breakable == null || seen.Contains(breakable.GetInstanceID())) continue;
                    // Report the actual hit target, not child reminder/sensor regions.
                    var physical = breakable.GetComponents<Collider2D>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy);
                    if (physical == null || !InView(cam, physical.bounds.center)) continue;
                    var window = new Bounds(sensorCenter, new Vector3(grid.width * grid.cell, grid.height * grid.cell, 100));
                    if (!window.Intersects(physical.bounds)) continue;
                    seen.Add(breakable.GetInstanceID());
                    var b = physical.bounds;
                    objects.Add(new { x = R(b.center.x - center.x), y = R(b.center.y - center.y), width = R(b.size.x), height = R(b.size.y), kind = "breakable" });
                }
            }
            var actions = InputHandler.Instance == null ? null : InputHandler.Instance.inputActions;
            return new {
                player = new { x = R(center.x), y = R(center.y), vx = R(velocity.x), vy = R(velocity.y), width = R(bounds.size.x), height = R(bounds.size.y), grounded = hero.cState.onGround, touching_wall_left = hero.touchingWallL, touching_wall_right = hero.touchingWallR, facing = hero.cState.facingRight ? 1 : -1, health = PlayerData.instance.health },
                terrain, enemies, objects,
                surfaces = SampleSurfaces(terrain, center, bounds.size.y, hero.transform, cam),
                exploration = explorationMemory.Snapshot(center.x, center.y,
                    center.x + grid.ox, center.x + grid.ox + (grid.width - 1) * grid.cell,
                    center.y + grid.oy, center.y + grid.oy + (grid.height - 1) * grid.cell),
                abilities = new { run_speed = R(hero.RUN_SPEED), jump_speed = R(hero.JUMP_SPEED), jump_sustain_steps = hero.JUMP_STEPS + 1, fixed_timestep_seconds = Time.fixedDeltaTime, jump_speed_sustain_seconds = (hero.JUMP_STEPS + 1) * Time.fixedDeltaTime, gravity_y = Physics2D.gravity.y, gravity_scale = hero.DEFAULT_GRAVITY, has_double_jump = PlayerData.instance.hasDoubleJump, has_dash = PlayerData.instance.hasDash, has_wall_jump = PlayerData.instance.hasWalljump },
                held = new { move = actions == null ? 0 : Math.Sign(actions.moveVector.X), look = actions == null ? 0 : Math.Sign(actions.moveVector.Y), jump = actions != null && actions.jump.IsPressed, attack = actions != null && actions.attack.IsPressed }
            };
        }

        // Local evaluation only. This identity, position and result never enter ModelState.
        string EpisodeStatus(HeroController hero)
        {
            if (BossTrial.Enabled && BossTrial.Instance != null && BossTrial.Instance.Status != "running") return BossTrial.Instance.Status;
            if (hero != null && hero.cState.dead) return "dead";
            if (BossTrial.Enabled) return "running";
            if (reachedDoor) return "success";
            if (scene != "Tutorial_01" || hero == null || !Gameplay()) return "running";
            if (destination == null)
                destination = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsSortMode.None)
                    .FirstOrDefault(c => c.name == "Door" && c.transform.parent != null && c.transform.parent.name == "Hallownest_Main_Gate");
            if (destination != null && destination.enabled)
            {
                var player = hero.GetComponent<Collider2D>().bounds;
                var door = destination.bounds;
                float gap = door.min.x - player.max.x;
                if (PlayerData.instance.health > 0 && hero.cState.onGround && gap >= -.5f && gap <= 1.5f
                    && player.center.y >= door.min.y && player.center.y <= door.max.y)
                    reachedDoor = true;
            }
            return reachedDoor ? "success" : "running";
        }

        TerrainSamples SampleTerrain(Vector2 center, Transform ignored, Camera cam, SensingGrid spec = null)
        {
            var grid = spec ?? new SensingGrid();
            var rows = new string[grid.height];
            float ox = grid.ox, oy = grid.oy, cellSize = grid.cell;
            for (int row = 0; row < grid.height; row++)
            {
                var cells = new char[grid.width];
                for (int col = 0; col < grid.width; col++)
                {
                    Vector2 point = center + new Vector2(ox + col * cellSize, oy + (grid.height - 1 - row) * cellSize);
                    if (!InView(cam, point)) { cells[col] = '?'; continue; }
                    int count = Physics2D.OverlapBoxNonAlloc(point, new Vector2(cellSize * .9f, cellSize * .9f), 0, hits);
                    char cell = count == hits.Length ? '?' : '.';
                    for (int i = 0; i < count; i++)
                    {
                        var hit = hits[i];
                        if (hit == null || !hit.enabled || (ignored != null && hit.transform.IsChildOf(ignored))) continue;
                        string kind = Kind(hit);
                        if (kind == "damage") { cell = '^'; break; }
                        if (kind == "solid") cell = '#';
                    }
                    cells[col] = cell;
                }
                rows[row] = new string(cells);
            }
            return new TerrainSamples { origin_x = ox, origin_y = oy, cell_size = cellSize, width = grid.width, height = grid.height, rows = rows };
        }

        object[] SampleSurfaces(TerrainSamples terrain, Vector2 center, float playerHeight, Transform ignored, Camera cam)
        {
            var points = new List<SurfacePoint>();
            for (int row = 1; row < terrain.height; row++)
            for (int col = 0; col < terrain.width; col++)
            {
                // A measured empty-to-solid transition identifies a place to probe,
                // not a navigational recommendation or a reachability judgment.
                if (terrain.rows[row][col] != '#' || terrain.rows[row - 1][col] != '.') continue;
                var start = center + new Vector2(terrain.origin_x + col * terrain.cell_size,
                    terrain.origin_y + (terrain.height - row) * terrain.cell_size);
                foreach (var hit in Physics2D.RaycastAll(start, Vector2.down, terrain.cell_size * 1.5f).OrderBy(h => h.distance))
                {
                    if (hit.collider == null || (ignored != null && hit.collider.transform.IsChildOf(ignored)) || Kind(hit.collider) != "solid") continue;
                    if (hit.distance > .001f && hit.normal.y >= .65f && InView(cam, hit.point))
                        points.Add(new SurfacePoint { collider = hit.collider.GetInstanceID(), x = hit.point.x - center.x,
                            y = hit.point.y - center.y, nx = hit.normal.x, ny = hit.normal.y });
                    break;
                }
            }
            var runs = new List<object>();
            foreach (var group in points.GroupBy(p => Tuple.Create(p.collider, Math.Round(p.y, 1))).OrderBy(g => g.Min(p => p.x)).ThenBy(g => g.Min(p => p.y)))
            {
                var sorted = group.OrderBy(p => p.x).ToArray();
                for (int first = 0; first < sorted.Length;)
                {
                    int last = first;
                    while (last + 1 < sorted.Length && sorted[last + 1].x - sorted[last].x <= terrain.cell_size * 1.1f) last++;
                    float y = sorted.Skip(first).Take(last - first + 1).Average(p => p.y);
                    runs.Add(new { left_x = R(sorted[first].x), right_x = R(sorted[last].x), top_y = R(y),
                        height_above_feet = R(y + playerHeight / 2), normal_x = R(sorted[first].nx), normal_y = R(sorted[first].ny) });
                    if (runs.Count == 64) return runs.ToArray();
                    first = last + 1;
                }
            }
            return runs.ToArray();
        }

        string Kind(Collider2D c)
        {
            // Damage can toggle at runtime; don't cache that state.
            var damage = c.GetComponent<DamageHero>();
            if (damage != null && damage.enabled && damage.damageDealt > 0 && c.GetComponentInParent<HealthManager>() == null) return "damage";
            string result;
            if (kinds.TryGetValue(c.GetInstanceID(), out result)) return result;
            if (!c.isTrigger && (c.gameObject.layer == 8 || c.gameObject.layer == 25)) result = "solid";
            else if (BreakableComponent(c) != null) result = "breakable";
            else result = "other";
            kinds[c.GetInstanceID()] = result;
            return result;
        }

        static MonoBehaviour BreakableComponent(Collider2D c)
        {
            return c.GetComponentsInParent<MonoBehaviour>().FirstOrDefault(m => m != null && m.GetType().Name.StartsWith("Breakable", StringComparison.Ordinal));
        }

        static bool InView(Camera cam, Vector2 point)
        {
            if (cam == null) return false;
            Vector3 p = cam.WorldToViewportPoint(new Vector3(point.x, point.y, 0));
            return p.z > 0 && p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1;
        }
        static double R(float value) { return Math.Round(value, 2); }
        static void AtomicWrite(string path, string json)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        void SelfTest()
        {
            ExplorationMemory.SelfTest();
            BossTrial.SelfTest();
            var actions = new HeroActions();
            actions.jump.CommitWithState(false, 1, .016f);
            actions.jump.CommitWithState(true, 2, .016f);
            if (!actions.jump.WasPressed || !actions.jump.IsPressed) throw new Exception("input press edge");
            actions.jump.CommitWithState(true, 3, .016f);
            if (actions.jump.WasPressed || !actions.jump.IsPressed) throw new Exception("input hold edge");
            actions.jump.CommitWithState(false, 4, .016f);
            if (!actions.jump.WasReleased || actions.jump.IsPressed) throw new Exception("input release edge");
            actions.Destroy();
            var fixture = new GameObject("Jev terrain test fixture");
            try
            {
                Vector2 center = new Vector2(-1000, -1000);
                var cameraObject = new GameObject("Fixture camera");
                cameraObject.transform.SetParent(fixture.transform);
                cameraObject.transform.position = new Vector3(center.x, center.y, -10);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.orthographic = true; camera.orthographicSize = 3; camera.aspect = 1.5f;
                var solid = new GameObject("Fixture terrain");
                solid.transform.SetParent(fixture.transform); solid.transform.position = center;
                solid.layer = 8; solid.AddComponent<BoxCollider2D>().size = new Vector2(.4f,.4f);
                var damage = new GameObject("Fixture damage");
                damage.transform.SetParent(fixture.transform); damage.transform.position = center + Vector2.right;
                damage.AddComponent<BoxCollider2D>().size = new Vector2(.4f,.4f);
                damage.AddComponent<DamageHero>();
                Physics2D.SyncTransforms();
                var sampled = SampleTerrain(center, null, camera);
                var terrain = JObject.FromObject(sampled);
                var rows = terrain["rows"].ToObject<string[]>();
                int centerRow = Height - 1 - (int)(-OriginY / Cell), centerCol = (int)(-OriginX / Cell);
                if (rows[centerRow][centerCol] != '#' || rows[centerRow][centerCol + 2] != '^' || rows[centerRow][centerCol - 2] != '.' || rows[0][0] != '?')
                    throw new Exception("terrain solid/damage/empty/out-of-view classification");
                var edgeView = GridForCamera(center + new Vector2(4, 2), camera);
                if (Math.Abs(edgeView.ox + 8.5f) > .01f || Math.Abs(edgeView.oy + 5f) > .01f
                    || edgeView.width != 19 || edgeView.height != 13)
                    throw new Exception("full viewport sensing when player is near camera edge");
                var surfaces = JArray.FromObject(SampleSurfaces(sampled, center, 1.28f, null, camera));
                if (!surfaces.Any(s => Math.Abs((double)s["top_y"] - .2) < .02
                    && Math.Abs((double)s["height_above_feet"] - .84) < .02 && (double)s["normal_y"] > .99))
                    throw new Exception("measured surface top and height above feet");
                AtomicWrite(Path.Combine(root, "selftest.json"), JsonConvert.SerializeObject(new { input_edges = "passed", terrain_samples = "passed", full_viewport = "passed", surface_heights = "passed", visit_memory = "passed", gameplay_tested = false, terrain, surfaces }));
                Debug.Log("[JevBridge] SELFTEST PASS: InControl press/hold/release and physics solid/damage/empty/unobserved cells.");
            }
            finally { Destroy(fixture); kinds.Clear(); }
        }

        void OnGUI()
        {
            if (root == null) return;
            GUI.Label(new Rect(12, 12, 650, 28), "Jev bridge | " + (CanControl() ? "CONTROL ARMED" : "observe") + " | " + status + " | F8 stops control");
        }
        void OnDestroy()
        {
            command = null;
            if (recording != null) recording.Dispose();
            Application.runInBackground = wasBackground;
            if (instance == this) instance = null;
        }

        sealed class Command
        {
            public string id, session_id, created_utc;
            public long observed_sequence;
            public int duration_ms, move, look, attack_press_ms;
            public bool jump, attack, jump_repress, dash;
        }
    }
}
