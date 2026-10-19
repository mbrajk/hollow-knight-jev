using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTrial
{
    // Explicit test-session setup, separate from gameplay inputs and model observations.
    public sealed class TrialSession : MonoBehaviour
    {
        string runtime;
        bool finishing;
        string inspected;
        bool pauseBusy;
        string proof;
        bool godseeker;
        bool enteredOwnedGame;
        public static bool OwnsCurrentSlot { get; private set; }
        public static bool PauseTransitionInProgress { get; private set; }
        float nextCapture;
        public static void Install()
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("--jev-new-slot4") && !args.Contains("--jev-resume-slot4")) return;
            if (args.Contains("--jev-new-slot4") && args.Contains("--jev-resume-slot4")) return;
            var obj = new GameObject("Jev authorized slot 4 trial");
            DontDestroyOnLoad(obj);
            obj.AddComponent<TrialSession>();
        }
        IEnumerator Start()
        {
            runtime = Path.GetFullPath(Path.Combine(Application.dataPath, "../.jev/runtime"));
            while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Menu_Title") yield return null;
            yield return new WaitForSecondsRealtime(5);
            bool? inUse = null;
            Platform.Current.IsSaveSlotInUse(4, used => inUse = used);
            float deadline = Time.realtimeSinceStartup + 15;
            while (!inUse.HasValue && Time.realtimeSinceStartup < deadline) yield return null;
            if (!inUse.HasValue) { Debug.LogError("[JevTrial] Save-slot check timed out; no save action."); yield break; }
            proof = Path.Combine(runtime, "slot4-ownership.json");
            godseeker = Environment.GetCommandLineArgs().Contains("--jev-godseeker");
            bool resume = Environment.GetCommandLineArgs().Contains("--jev-resume-slot4");
            if (resume)
            {
                if (!inUse.Value || !OwnsSavedSlot()) { Debug.LogError("[JevTrial] No matching owned slot-4 save receipt. Refusing to load or alter saves."); yield break; }
                OwnsCurrentSlot = true;
                Debug.Log("[JevTrial] Resuming the previously verified trial slot 4.");
                GameManager.instance.LoadGameFromUI(4);
                yield break;
            }
            string[] existing = Directory.GetFiles(Application.persistentDataPath, "user4*");
            if (inUse.Value || existing.Length != 0)
            {
                Debug.LogError("[JevTrial] Slot 4 is occupied or has a backup; refusing to create or replace it.");
                yield break;
            }
            File.WriteAllText(Path.Combine(runtime, "slot4-empty-check.json"), JsonConvert.SerializeObject(new {
                checked_utc = DateTime.UtcNow.ToString("O"), slot = 4,
                game_api_in_use = inUse.Value, local_user4_file_count = existing.Length,
                action = godseeker ? "create_new_godseeker_game_in_verified_empty_slot" : "create_new_normal_game_in_verified_empty_slot"
            }));
            Debug.Log("[JevTrial] VERIFIED EMPTY: game IsSaveSlotInUse(4)=false and no user4* files. Starting authorized new game.");
            GameManager.instance.profileID = 4;
            // Use the game's new-game path without forcing changes to the user's
            // overscan/brightness preferences just to pass first-run menu prompts.
            GameManager.instance.EnsureSaveSlotSpace(hasSpace => {
                if (!hasSpace) { Debug.LogError("[JevTrial] No save space; not starting."); return; }
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Menu_Title" || GameManager.instance.profileID != 4 || Directory.GetFiles(Application.persistentDataPath, "user4*").Length != 0)
                { Debug.LogError("[JevTrial] Setup state changed; refusing to create a save."); return; }
                OwnsCurrentSlot = true;
                InputHandler.Instance.StopUIInput();
                GameManager.instance.StartNewGame(false, godseeker);
            });
        }

        void Update()
        {
            if (runtime == null) return;
            var gm = GameManager.instance;
            string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            // A return to the title or another profile ends this ownership session.
            // Selecting a new/replaced slot manually must not inherit automation.
            if (enteredOwnedGame && (gm == null || gm.profileID != 4 || activeScene == "Menu_Title")) OwnsCurrentSlot = false;
            if (!OwnsCurrentSlot || gm == null || gm.profileID != 4) return;
            if (gm.IsGameplayScene() || activeScene == "GG_Entrance_Cutscene") enteredOwnedGame = true;
            if (!pauseBusy && gm != null && gm.profileID == 4 && gm.IsGameplayScene())
            {
                var pause = Path.Combine(runtime, "pause.request");
                var resume = Path.Combine(runtime, "resume.request");
                if (File.Exists(pause)) StartCoroutine(SetPause(true, pause));
                else if (File.Exists(resume)) StartCoroutine(SetPause(false, resume));
            }
            if (GameManager.instance != null && GameManager.instance.IsGameplayScene() && inspected != UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
            {
                inspected = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                var colliders = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsSortMode.None)
                    .Select(c => new { name = c.name, parent = c.transform.parent == null ? "" : c.transform.parent.name,
                        layer = c.gameObject.layer, trigger = c.isTrigger, enabled = c.enabled,
                        x = c.bounds.center.x, y = c.bounds.center.y, width = c.bounds.size.x, height = c.bounds.size.y,
                        components = c.GetComponentsInParent<MonoBehaviour>().Where(m => m != null).Select(m => m.GetType().Name).ToArray() }).ToArray();
                File.WriteAllText(Path.Combine(runtime, "scene-inspection.json"), JsonConvert.SerializeObject(colliders));
            }
            var capture = Path.Combine(runtime, "capture.request");
            if (File.Exists(capture) || (gm != null && gm.IsGameplayScene() && !gm.isPaused && Environment.GetCommandLineArgs().Contains("--jev-capture") && Time.realtimeSinceStartup >= nextCapture))
            {
                if (File.Exists(capture)) File.Delete(capture);
                nextCapture = Time.realtimeSinceStartup + 1;
                ScreenCapture.CaptureScreenshot(Path.Combine(runtime, "capture.png"));
            }
            var finish = Path.Combine(runtime, "finish.request");
            if (!finishing && File.Exists(finish) && GameManager.instance != null && GameManager.instance.profileID == 4 && (GameManager.instance.IsGameplayScene() || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GG_Entrance_Cutscene"))
            {
                File.Delete(finish);
                finishing = true;
                GameManager.instance.SaveGame(saved => {
                    bool receiptSaved = saved && SaveOwnershipReceipt();
                    File.WriteAllText(Path.Combine(runtime, "trial-save-result.json"), JsonConvert.SerializeObject(new { slot = 4, saved, ownership_receipt_saved = receiptSaved, utc = DateTime.UtcNow.ToString("O") }));
                    if (receiptSaved) Application.Quit();
                    else { finishing = false; Debug.LogError("[JevTrial] Save failed; leaving game open."); }
                });
            }
        }

        static string HashSave()
        {
            // Exact primary file only: never resolve a path from the receipt.
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Path.Combine(Application.persistentDataPath, "user4.dat")))).Replace("-", "");
        }

        bool OwnsSavedSlot()
        {
            try
            {
                var receipt = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(proof));
                return (int?)receipt["schema"] == 1 && (int?)receipt["slot"] == 4
                    && (bool?)receipt["created_from_verified_empty_slot"] == true
                    && (bool?)receipt["godseeker"] == godseeker
                    && string.Equals((string)receipt["save_sha256"], HashSave(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        bool SaveOwnershipReceipt()
        {
            try
            {
                if (!OwnsCurrentSlot || GameManager.instance.profileID != 4) return false;
                string json = JsonConvert.SerializeObject(new { schema = 1, slot = 4,
                    created_from_verified_empty_slot = true, godseeker,
                    save_sha256 = HashSave(), saved_utc = DateTime.UtcNow.ToString("O") });
                string temporary = proof + ".tmp";
                File.WriteAllText(temporary, json);
                if (File.Exists(proof)) File.Replace(temporary, proof, null);
                else File.Move(temporary, proof);
                return true;
            }
            catch (Exception) { Debug.LogError("[JevTrial] Ownership receipt failed; leaving game open."); return false; }
        }

        IEnumerator SetPause(bool desired, string request)
        {
            pauseBusy = true;
            PauseTransitionInProgress = true;
            try
            {
                // isPaused changes before GoToPauseMenu finishes assigning PAUSE_MENU.
                // Wait for that transition so resume closes the actual pause menu.
                float deadline = Time.realtimeSinceStartup + 5;
                while (UIManager.instance != null && UIManager.instance.IsAnimatingMenus)
                {
                    if (Time.realtimeSinceStartup >= deadline) { Debug.LogError("[JevTrial] Menu transition did not settle; pause request retained."); yield break; }
                    yield return null;
                }
                if (!OwnsCurrentSlot || GameManager.instance == null || GameManager.instance.profileID != 4) yield break;
                if (GameManager.instance.isPaused != desired) yield return GameManager.instance.PauseGameToggle();
                deadline = Time.realtimeSinceStartup + 5;
                while (UIManager.instance != null && UIManager.instance.IsAnimatingMenus)
                {
                    if (Time.realtimeSinceStartup >= deadline) { Debug.LogError("[JevTrial] Menu transition did not settle; pause request retained."); yield break; }
                    yield return null;
                }
                if (GameManager.instance.isPaused == desired && File.Exists(request)) File.Delete(request);
            }
            finally { pauseBusy = false; PauseTransitionInProgress = false; }
        }
    }
}
