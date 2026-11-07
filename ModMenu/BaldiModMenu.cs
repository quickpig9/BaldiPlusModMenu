using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace BaldiModMenu
{
    [BepInPlugin("local.baldi.modmenu", "Baldi Mod Menu", ModMenuPlugin.MenuVersion)]
    public class ModMenuPlugin : BaseUnityPlugin
    {
        public const string MenuVersion = "1.4.2";

        private class Target
        {
            public Transform t;
            public MonoBehaviour mb;
            public string label;
            public Color color;
            public bool npc;
        }

        private const BindingFlags AllInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        // flaga czytana przez patch Harmony
        private static bool s_infiniteItems = false;

        // --- stan menu ---
        private bool showMenu = false;
        private bool wasMenu = false;
        private Rect window = new Rect(30, 30, 450, 660);
        private Vector2 scroll = Vector2.zero;
        private Vector2 itemScroll = Vector2.zero;
        private int tab = 0;
        private readonly string[] tabNames = { "Gracz", "NPC", "Swiat", "Wizualne", "Wersja", "Debug" };

        // --- opcje: gracz ---
        private bool infiniteStamina = false;
        private bool noclip = false;
        private bool speedHack = false;
        private float speedMultiplier = 2f;
        private bool infiniteYtps = false;
        private bool infiniteItems = false;
        private string itemFilter = "";

        // --- opcje: npc ---
        private bool freezeBaldi = false;
        private bool lastFreezeBaldi = false;
        private bool npcIgnore = false;
        private bool lastNpcIgnore = false;
        private float npcSpeedMul = 1f;

        // --- opcje: swiat ---
        private float gameSpeed = 1f;
        private string seedText = "0";
        private float sizeW = 30f;
        private float sizeH = 30f;

        // --- opcje: wizualne ---
        private bool espNpcs = false;
        private bool espItems = false;
        private bool rainbowEsp = false;
        private bool showFps = false;
        private bool hideHud = false;
        private bool lastHideHud = false;
        private bool entityGps = false;
        private bool customFov = false;
        private bool lastCustomFov = false;
        private float fov = 90f;

        private string status = "";
        private float fps = 60f;

        // --- kamera 3D ---
        private int camMode = 0; // 0 normalna, 1 zza plecow, 2 z gory
        private readonly string[] camModeNames = { "Normalna", "3D zza plecow", "Z gory", "Gora/dol" };
        private float camDist = 20f;
        private float camHeight = 0f;
        private bool showBody = true;
        private Camera camTarget;
        private float camPitch = 0f;
        private float pitchSens = 3f;
        private Camera worldCam;
        private float nextCamPick = 0f;
        private bool mouseAxisBroken = false;
        private GameObject bodyMarker;

        // --- niebo ---
        private bool customSky = false;
        private bool lastCustomSky = false;
        private bool rainbowSky = false;
        private float skyR = 0.4f, skyG = 0.7f, skyB = 1f;
        private bool hideCeilings = false;
        private float nextCeilScan = 0f;
        private readonly List<Renderer> hiddenCeilings = new List<Renderer>();
        private readonly Dictionary<int, CameraClearFlags> origSkyFlags = new Dictionary<int, CameraClearFlags>();
        private readonly Dictionary<int, Color> origSkyColor = new Dictionary<int, Color>();

        // --- aktualizacje ---
        private ConfigEntry<string> cfgUpdateUrl;
        private ConfigEntry<bool> cfgAutoCheck;
        private int updState = 0; // 0 nie sprawdzano, 1 sprawdzanie, 2 ok, 3 blad
        private bool updAvailable = false;
        private string updRemoteVersion = "";
        private string updDownloadUrl = "";
        private string updChangelog = "";
        private string updError = "";
        private Vector2 changelogScroll = Vector2.zero;

        // cache
        private float nextScan = 0f;
        private bool loggedMapMethods = false;
        private readonly List<MonoBehaviour> players = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> playerMgrs = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> baldis = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> maps = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> loopers = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> navigators = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> gameMgrs = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> events = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> hudMgrs = new List<MonoBehaviour>();
        private readonly List<Canvas> hudCanvases = new List<Canvas>();
        private readonly HashSet<MonoBehaviour> npcRefs = new HashSet<MonoBehaviour>();
        private readonly List<Target> targets = new List<Target>();
        private readonly Dictionary<int, float[]> originalSpeeds = new Dictionary<int, float[]>();
        private readonly Dictionary<int, float[]> originalNavSpeeds = new Dictionary<int, float[]>();
        private readonly Dictionary<int, float> originalFov = new Dictionary<int, float>();
        private readonly List<UnityEngine.Object> itemAssets = new List<UnityEngine.Object>();
        private readonly HashSet<Collider> ignored = new HashSet<Collider>();
        private List<Collider> myCols = new List<Collider>();

        private GUIStyle espStyle;
        private GUIStyle fpsStyle;

        // ===================================================================
        // start
        // ===================================================================
        private void Awake()
        {
            Logger.LogInfo("Baldi Mod Menu " + MenuVersion + " zaladowany. F1 = menu.");

            cfgUpdateUrl = Config.Bind("Update", "UpdateUrl",
                "https://raw.githubusercontent.com/quickpig9/BaldiPlusModMenu/refs/heads/main/version.txt",
                "Adres pliku version.txt (1 linia: wersja, 2 linia: link do pobrania, dalej: lista zmian).");
            cfgAutoCheck = Config.Bind("Update", "AutoCheck", true,
                "Sprawdzaj aktualizacje automatycznie przy starcie gry.");

            Camera.onPreCull += OnCamPreCull;
            Camera.onPostRender += OnCamPostRender;

            if (cfgAutoCheck.Value && !string.IsNullOrEmpty(cfgUpdateUrl.Value)) StartCheck();

            try
            {
                Type im = FindType("ItemManager");
                if (im != null)
                {
                    Harmony h = new Harmony("local.baldi.modmenu");
                    MethodInfo prefix = typeof(ModMenuPlugin).GetMethod("RemoveItemPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    int count = 0;
                    foreach (MethodInfo m in im.GetMethods(AllInst | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name == "RemoveItem")
                        {
                            h.Patch(m, new HarmonyMethod(prefix));
                            count++;
                        }
                    }
                    Logger.LogInfo("Patch RemoveItem: " + count);
                }
                else
                {
                    Logger.LogWarning("Nie znaleziono typu ItemManager - nieskonczone przedmioty nie beda dzialac.");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Harmony: " + e);
            }
        }

        private static bool RemoveItemPrefix()
        {
            return !s_infiniteItems;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                showMenu = !showMenu;
            }

            if (showMenu)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                wasMenu = true;
            }
            else if (wasMenu)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                wasMenu = false;
            }

            if (Time.unscaledDeltaTime > 0f)
            {
                fps = Mathf.Lerp(fps, 1f / Time.unscaledDeltaTime, 0.1f);
            }

            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 1f;
                Rescan();
                UpdateNoclip();
                UpdateYtps();
            }

            ApplyOptions();

            if (Time.unscaledTime >= nextCamPick)
            {
                nextCamPick = Time.unscaledTime + 0.5f;
                worldCam = PickWorldCamera();
            }
            camTarget = camMode != 0 ? worldCam : null;
            UpdatePitchInput();
            UpdateBodyMarker();
            UpdateCeilings();
        }

        private void LateUpdate()
        {
            ApplySky();

            if (customFov)
            {
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null || !c.enabled || c.orthographic) continue;
                    int id = c.GetInstanceID();
                    if (!originalFov.ContainsKey(id)) originalFov[id] = c.fieldOfView;
                    c.fieldOfView = fov;
                }
                lastCustomFov = true;
            }
            else if (lastCustomFov)
            {
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null) continue;
                    float orig;
                    if (originalFov.TryGetValue(c.GetInstanceID(), out orig)) c.fieldOfView = orig;
                }
                lastCustomFov = false;
            }
        }

        // ===================================================================
        // skanowanie obiektow
        // ===================================================================
        private static bool IsOrDerivedFrom(Type t, string name)
        {
            while (t != null)
            {
                if (t.Name == name) return true;
                t = t.BaseType;
            }
            return false;
        }

        private void Rescan()
        {
            players.Clear();
            playerMgrs.Clear();
            baldis.Clear();
            maps.Clear();
            loopers.Clear();
            navigators.Clear();
            gameMgrs.Clear();
            events.Clear();
            hudMgrs.Clear();
            targets.Clear();

            foreach (MonoBehaviour mb in FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                Type type = mb.GetType();
                string n = type.Name;

                if (n == "PlayerMovement") players.Add(mb);
                else if (n == "PlayerManager") playerMgrs.Add(mb);
                else if (n == "Baldi") baldis.Add(mb);
                else if (n == "Map") maps.Add(mb);
                else if (n == "Looker") loopers.Add(mb);
                else if (n == "Navigator") navigators.Add(mb);
                else if (n == "HudManager") hudMgrs.Add(mb);

                if (IsOrDerivedFrom(type, "BaseGameManager")) gameMgrs.Add(mb);
                if (IsOrDerivedFrom(type, "RandomEvent")) events.Add(mb);

                bool isNpc = IsOrDerivedFrom(type, "NPC");
                if (isNpc) npcRefs.Add(mb);

                if (!mb.gameObject.activeInHierarchy) continue;

                if (isNpc)
                {
                    Target tg = new Target();
                    tg.t = mb.transform;
                    tg.mb = mb;
                    tg.label = n;
                    tg.npc = true;
                    tg.color = (n == "Baldi") ? Color.red : new Color(1f, 0.6f, 0.1f);
                    targets.Add(tg);
                }
                else if (n == "Pickup")
                {
                    Target tg = new Target();
                    tg.t = mb.transform;
                    tg.mb = mb;
                    tg.label = GetItemName(mb);
                    tg.color = Color.green;
                    targets.Add(tg);
                }
                else if (n == "Notebook")
                {
                    Target tg = new Target();
                    tg.t = mb.transform;
                    tg.mb = mb;
                    tg.label = "Notebook";
                    tg.color = Color.cyan;
                    targets.Add(tg);
                }
            }

            npcRefs.RemoveWhere(x => x == null);

            hudCanvases.Clear();
            foreach (Canvas c in FindObjectsOfType<Canvas>())
            {
                if (c == null) continue;
                bool isHud = c.gameObject.name.ToLowerInvariant().Contains("hud");
                if (!isHud)
                {
                    foreach (MonoBehaviour hm in hudMgrs)
                    {
                        if (hm != null && c.transform.IsChildOf(hm.transform)) { isHud = true; break; }
                    }
                }
                if (isHud) hudCanvases.Add(c);
            }
        }

        private static string GetItemName(object pickup)
        {
            UnityEngine.Object o = GetMember(pickup, "item") as UnityEngine.Object;
            return o != null ? o.name : "Item";
        }

        // ===================================================================
        // opcje dzialajace co klatke
        // ===================================================================
        private void ApplyOptions()
        {
            s_infiniteItems = infiniteItems;

            if (Time.timeScale > 0f && Mathf.Abs(Time.timeScale - gameSpeed) > 0.01f)
            {
                Time.timeScale = gameSpeed;
            }

            foreach (MonoBehaviour p in players)
            {
                if (p == null) continue;

                if (infiniteStamina)
                {
                    float max = GetFloat(p, "staminaMax");
                    if (max > 0f) SetMember(p, "stamina", max);
                }

                int id = p.GetInstanceID();
                if (!originalSpeeds.ContainsKey(id))
                {
                    originalSpeeds[id] = new float[] { GetFloat(p, "walkSpeed"), GetFloat(p, "runSpeed") };
                }
                float[] orig = originalSpeeds[id];
                float mul = speedHack ? speedMultiplier : 1f;
                SetMember(p, "walkSpeed", orig[0] * mul);
                SetMember(p, "runSpeed", orig[1] * mul);
            }

            foreach (MonoBehaviour nav in navigators)
            {
                if (nav == null) continue;
                int id = nav.GetInstanceID();
                if (!originalNavSpeeds.ContainsKey(id))
                {
                    originalNavSpeeds[id] = new float[] { GetFloat(nav, "maxSpeed"), GetFloat(nav, "speed") };
                }
                float[] o = originalNavSpeeds[id];
                if (o[0] > 0f) SetMember(nav, "maxSpeed", o[0] * npcSpeedMul);
                if (o[1] > 0f) SetMember(nav, "speed", o[1] * npcSpeedMul);
            }

            if (freezeBaldi || lastFreezeBaldi)
            {
                foreach (MonoBehaviour b in baldis)
                {
                    if (b != null) b.enabled = !freezeBaldi;
                }
                lastFreezeBaldi = freezeBaldi;
            }

            if (npcIgnore || lastNpcIgnore)
            {
                foreach (MonoBehaviour l in loopers)
                {
                    if (l != null) l.enabled = !npcIgnore;
                }
                lastNpcIgnore = npcIgnore;
            }

            if (hideHud || lastHideHud)
            {
                foreach (Canvas c in hudCanvases)
                {
                    if (c != null) c.enabled = !hideHud;
                }
                lastHideHud = hideHud;
            }
        }

        // ===================================================================
        // YTP
        // ===================================================================
        private void UpdateYtps()
        {
            if (!infiniteYtps) return;
            object cgm = GetCoreGameManager();
            if (cgm == null) return;
            object result;
            int pts = 0;
            if (InvokeByName(cgm, "GetPoints", out result, 0) && result is int) pts = (int)result;
            if (pts < 500)
            {
                InvokeByName(cgm, "AddPoints", out result, 1000, 0, false);
            }
        }

        private object GetCoreGameManager()
        {
            Type t = FindType("CoreGameManager");
            return t != null ? GetStatic(t, "Instance") : null;
        }

        private void AddYtps(int amount)
        {
            object cgm = GetCoreGameManager();
            object result;
            if (cgm != null && InvokeByName(cgm, "AddPoints", out result, amount, 0, false))
                status = "Dodano YTP: " + amount;
            else
                status = "Nie udalo sie dodac YTP (wejdz do poziomu / zrzut do logu).";
        }

        // ===================================================================
        // notebook
        // ===================================================================
        private void AddNotebook()
        {
            MonoBehaviour nb = null;
            foreach (Target tg in targets)
            {
                if (tg.label == "Notebook" && tg.mb != null) { nb = tg.mb; break; }
            }
            if (nb == null)
            {
                status = "Brak notebooka na poziomie (wszystkie zebrane?).";
                return;
            }

            object r;
            bool ok = InvokeByName(nb, "Clicked", out r, 0);
            if (!ok)
            {
                foreach (MonoBehaviour gm in gameMgrs)
                {
                    if (gm != null && InvokeByName(gm, "CollectNotebook", out r, nb)) { ok = true; break; }
                }
            }
            status = ok ? "Dodano notebook." : "Nie udalo sie dodac notebooka (zrzut do logu).";
        }

        // ===================================================================
        // noclip
        // ===================================================================
        private void UpdateNoclip()
        {
            if (noclip)
            {
                HashSet<Collider> mine = new HashSet<Collider>();
                foreach (MonoBehaviour p in playerMgrs)
                    if (p != null) foreach (Collider c in p.GetComponentsInChildren<Collider>(true)) mine.Add(c);
                foreach (MonoBehaviour p in players)
                    if (p != null) foreach (Collider c in p.GetComponentsInChildren<Collider>(true)) mine.Add(c);
                if (mine.Count == 0) return;

                myCols = new List<Collider>(mine);
                foreach (Collider c in FindObjectsOfType<Collider>())
                {
                    if (c == null || c.isTrigger || mine.Contains(c) || ignored.Contains(c)) continue;
                    foreach (Collider pc in myCols)
                    {
                        if (pc != null) Physics.IgnoreCollision(pc, c, true);
                    }
                    ignored.Add(c);
                }
            }
            else if (ignored.Count > 0)
            {
                foreach (Collider c in ignored)
                {
                    if (c == null) continue;
                    foreach (Collider pc in myCols)
                    {
                        if (pc != null) Physics.IgnoreCollision(pc, c, false);
                    }
                }
                ignored.Clear();
            }
        }

        // ===================================================================
        // przedmioty
        // ===================================================================
        private void RefreshItems()
        {
            itemAssets.Clear();
            Type it = FindType("ItemObject");
            if (it == null)
            {
                status = "Brak typu ItemObject - uzyj 'Zrzut do logu'.";
                return;
            }
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(it);
            HashSet<string> seen = new HashSet<string>();
            foreach (UnityEngine.Object o in all)
            {
                if (o == null) continue;
                if (seen.Add(o.name)) itemAssets.Add(o);
            }
            itemAssets.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            status = "Znaleziono przedmiotow: " + itemAssets.Count;
        }

        private void GiveItem(UnityEngine.Object item)
        {
            int given = 0;
            foreach (MonoBehaviour pm in playerMgrs)
            {
                if (pm == null) continue;
                object itm = GetMember(pm, "itm");
                object r;
                if (itm != null && InvokeByName(itm, "AddItem", out r, item)) given++;
            }
            status = given > 0 ? "Dano: " + item.name : "Nie udalo sie (AddItem). Wejdz do poziomu / zrzut do logu.";
        }

        // ===================================================================
        // NPC
        // ===================================================================
        private void TeleportNpcToMe(MonoBehaviour npc)
        {
            Camera cam = GetCamera();
            if (cam == null || npc == null)
            {
                status = "Brak kamery gracza.";
                return;
            }
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            Vector3 pos = cam.transform.position + fwd.normalized * 4f;
            pos.y = npc.transform.position.y;

            Component agent = npc.GetComponent("NavMeshAgent");
            object r;
            if (agent != null && InvokeByName(agent, "Warp", out r, pos))
            {
                // agent przeniesiony
            }
            else
            {
                npc.transform.position = pos;
            }
            status = "Przeniesiono: " + npc.GetType().Name;
        }

        // ===================================================================
        // eventy
        // ===================================================================
        private void StartEvent(MonoBehaviour ev)
        {
            object r;
            bool ok = InvokeByName(ev, "Begin", out r);
            status = ok ? "Event wystartowal: " + ev.GetType().Name : "Nie udalo sie wystartowac eventu (zrzut do logu).";
        }

        // eventy, ktorych nie wolno zatrzymywac z menu (przycisk Stop zablokowany)
        private static bool IsLockedEvent(MonoBehaviour ev)
        {
            if (ev == null) return false;
            string n = ev.GetType().Name.ToLowerInvariant().Replace(" ", "").Replace("_", "");
            return n.Contains("timeout");
        }

        private void EndEvent(MonoBehaviour ev)
        {
            if (IsLockedEvent(ev))
            {
                status = "Stop zablokowany dla: " + ev.GetType().Name;
                return;
            }
            object r;
            bool ok = InvokeByName(ev, "End", out r);
            status = ok ? "Event zakonczony: " + ev.GetType().Name : "Nie udalo sie zakonczyc eventu.";
        }

        // ===================================================================
        // seed i rozmiar
        // ===================================================================
        private void ApplySeed()
        {
            int seed;
            if (!int.TryParse(seedText, out seed))
            {
                status = "Seed musi byc liczba calkowita.";
                return;
            }
            object cgm = GetCoreGameManager();
            if (cgm == null)
            {
                status = "Brak CoreGameManager (wejdz do gry).";
                return;
            }
            object r;
            bool ok = InvokeByName(cgm, "SetSeed", out r, seed);
            if (!ok) ok = SetMember(cgm, "seed", seed);
            status = ok
                ? "Seed ustawiony: " + seed + " (dziala od nastepnego wygenerowanego poziomu)"
                : "Nie znaleziono seeda - zrzut do logu.";
        }

        private void ApplySize()
        {
            Type lt = FindType("LevelObject");
            if (lt == null)
            {
                status = "Brak typu LevelObject - zrzut do logu.";
                return;
            }
            int w = Mathf.RoundToInt(sizeW);
            int h = Mathf.RoundToInt(sizeH);
            int n = 0;
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(lt))
            {
                if (o == null) continue;
                bool a = SetIntVector(o, "minSize", w, h);
                bool b = SetIntVector(o, "maxSize", w, h);
                if (a || b) n++;
            }
            status = n > 0
                ? "Rozmiar " + w + "x" + h + " ustawiony w " + n + " poziomach (od nastepnego generowania)"
                : "Nie znaleziono minSize/maxSize - zrzut do logu.";
        }

        private static bool SetIntVector(object o, string name, int x, int z)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(name, AllInst | BindingFlags.DeclaredOnly);
                if (fi == null) continue;
                object boxed = fi.GetValue(o);
                if (boxed == null) return false;
                Type vt = fi.FieldType;
                FieldInfo fx = vt.GetField("x", AllInst);
                FieldInfo fz = vt.GetField("z", AllInst);
                if (fx == null || fz == null) return false;
                fx.SetValue(boxed, x);
                fz.SetValue(boxed, z);
                fi.SetValue(o, boxed);
                return true;
            }
            return false;
        }

        // ===================================================================
        // mapa
        // ===================================================================
        private void RevealMap()
        {
            if (maps.Count == 0)
            {
                status = "Nie znaleziono mapy (wejdz do poziomu).";
                return;
            }

            int called = 0;
            foreach (MonoBehaviour map in maps)
            {
                if (map == null) continue;
                Type type = map.GetType();

                if (!loggedMapMethods)
                {
                    foreach (MethodInfo mi in type.GetMethods(AllInst | BindingFlags.DeclaredOnly))
                    {
                        Logger.LogInfo("Map." + mi.Name + "(" + mi.GetParameters().Length + " arg)");
                    }
                }

                MethodInfo m = type.GetMethod("CompleteMap", AllInst, null, Type.EmptyTypes, null);
                if (m == null)
                {
                    foreach (MethodInfo mi in type.GetMethods(AllInst))
                    {
                        if (mi.GetParameters().Length != 0) continue;
                        string nm = mi.Name.ToLowerInvariant();
                        if (nm.Contains("complete") || nm.Contains("reveal") || nm.Contains("showall") || nm.Contains("fullmap"))
                        {
                            m = mi;
                            break;
                        }
                    }
                }

                if (m != null)
                {
                    try
                    {
                        m.Invoke(map, null);
                        called++;
                        Logger.LogInfo("Wywolano Map." + m.Name);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError("Blad Map." + m.Name + ": " + e.Message);
                    }
                }
            }
            loggedMapMethods = true;

            status = called > 0
                ? "Mapa odkryta (wywolano metode)."
                : "Brak pasujacej metody. Zobacz liste metod Map w BepInEx/LogOutput.log.";
        }

        // ===================================================================
        // zrzut struktury gry do logu (pomoc w diagnozie)
        // ===================================================================
        private void DumpInfo()
        {
            string[] names = {
                "CoreGameManager", "BaseGameManager", "PlayerManager", "PlayerMovement", "ItemManager", "ItemObject",
                "NPC", "Baldi", "Looker", "Navigator", "Map", "Notebook", "HudManager", "RandomEvent",
                "LevelObject", "SceneObject", "EnvironmentController", "LevelGenerator"
            };
            foreach (string name in names)
            {
                Type t = FindType(name);
                if (t == null)
                {
                    Logger.LogInfo("[Dump] brak typu " + name);
                    continue;
                }
                Logger.LogInfo("[Dump] === " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.Name : "-"));
                BindingFlags f = AllInst | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (FieldInfo fi in t.GetFields(f))
                    Logger.LogInfo("[Dump]   field " + fi.FieldType.Name + " " + fi.Name);
                foreach (PropertyInfo pi in t.GetProperties(f))
                    Logger.LogInfo("[Dump]   prop  " + pi.PropertyType.Name + " " + pi.Name);
                foreach (MethodInfo mi in t.GetMethods(f))
                {
                    if (mi.IsSpecialName) continue;
                    string args = "";
                    foreach (ParameterInfo p in mi.GetParameters()) args += p.ParameterType.Name + " " + p.Name + ", ";
                    Logger.LogInfo("[Dump]   method " + mi.ReturnType.Name + " " + mi.Name + "(" + args + ")");
                }
            }
            status = "Zrzut zapisany w BepInEx/LogOutput.log";
        }

        // ===================================================================
        // ESP / GPS / FPS
        // ===================================================================
        private Camera GetCamera()
        {
            if (worldCam != null) return worldCam;
            return Camera.main;
        }

        private void DrawEsp()
        {
            if (!espNpcs && !espItems) return;
            if (Event.current.type != EventType.Repaint) return;
            Camera cam = GetCamera();
            if (cam == null) return;

            if (espStyle == null)
            {
                espStyle = new GUIStyle(GUI.skin.label);
                espStyle.fontSize = 13;
                espStyle.fontStyle = FontStyle.Bold;
                espStyle.alignment = TextAnchor.UpperCenter;
            }

            Color old = GUI.color;
            int idx = 0;
            foreach (Target tg in targets)
            {
                idx++;
                if (tg.t == null) continue;
                if (tg.npc && !espNpcs) continue;
                if (!tg.npc && !espItems) continue;

                Vector3 sp = ProjectPoint(cam, tg.t.position + Vector3.up * 1f);
                if (sp.z <= 0f) continue;
                float x = sp.x;
                float y = Screen.height - sp.y;
                float dist = Vector3.Distance(cam.transform.position, tg.t.position);

                Color col = tg.color;
                if (rainbowEsp) col = Color.HSVToRGB(Mathf.Repeat(Time.time * 0.4f + idx * 0.13f, 1f), 1f, 1f);

                GUI.color = col;
                GUI.DrawTexture(new Rect(x - 5f, y - 5f, 10f, 10f), Texture2D.whiteTexture);

                string text = tg.label + " [" + dist.ToString("0") + "m]";
                GUI.color = Color.black;
                GUI.Label(new Rect(x - 99f, y + 9f, 200f, 24f), text, espStyle);
                GUI.color = col;
                GUI.Label(new Rect(x - 100f, y + 8f, 200f, 24f), text, espStyle);
            }
            GUI.color = old;
        }

        private void DrawFps()
        {
            if (fpsStyle == null)
            {
                fpsStyle = new GUIStyle(GUI.skin.label);
                fpsStyle.fontSize = 18;
                fpsStyle.fontStyle = FontStyle.Bold;
            }
            string text = "FPS: " + fps.ToString("0");
            Color old = GUI.color;
            GUI.color = Color.black;
            GUI.Label(new Rect(11f, 6f, 200f, 28f), text, fpsStyle);
            GUI.color = Color.white;
            GUI.Label(new Rect(10f, 5f, 200f, 28f), text, fpsStyle);
            GUI.color = old;
        }

        private static readonly string[] dirNames = {
            "przod", "przod-prawo", "prawo", "tyl-prawo", "tyl", "tyl-lewo", "lewo", "przod-lewo"
        };

        private void DrawGps()
        {
            Camera cam = GetCamera();
            if (cam == null) return;

            List<Target> list = new List<Target>();
            foreach (Target tg in targets)
            {
                if (tg.t != null && (tg.npc || tg.label == "Notebook")) list.Add(tg);
            }
            Vector3 cp = cam.transform.position;
            list.Sort((a, b) => Vector3.Distance(cp, a.t.position).CompareTo(Vector3.Distance(cp, b.t.position)));

            GUILayout.BeginArea(new Rect(Screen.width - 280f, 40f, 270f, 340f));
            GUILayout.Label("Entity GPS", GUI.skin.box);
            Color old = GUI.contentColor;
            int shown = 0;
            foreach (Target tg in list)
            {
                if (shown++ >= 10) break;
                Vector3 to = tg.t.position - cp;
                to.y = 0f;
                Vector3 fwd = cam.transform.forward;
                fwd.y = 0f;
                float ang = Vector3.SignedAngle(fwd, to, Vector3.up);
                int di = ((Mathf.RoundToInt(ang / 45f) % 8) + 8) % 8;
                GUI.contentColor = tg.color;
                GUILayout.Label(tg.label + ": " + Vector3.Distance(cp, tg.t.position).ToString("0") + "m  (" + dirNames[di] + ")");
            }
            GUI.contentColor = old;
            GUILayout.EndArea();
        }

        // ===================================================================
        // GUI
        // ===================================================================
        private void OnGUI()
        {
            DrawEsp();
            if (showFps) DrawFps();
            if (entityGps) DrawGps();

            if (!showMenu) return;
            window = GUILayout.Window(7331, window, DrawWindow, "Baldi Mod Menu  [F1]");
        }

        private void DrawWindow(int id)
        {
            tab = GUILayout.Toolbar(tab, tabNames);

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(window.height - 110f));
            switch (tab)
            {
                case 0: DrawTabPlayer(); break;
                case 1: DrawTabNpc(); break;
                case 2: DrawTabWorld(); break;
                case 3: DrawTabVisual(); break;
                case 4: DrawTabVersion(); break;
                default: DrawTabDebug(); break;
            }
            GUILayout.EndScrollView();

            GUILayout.Label("Status: " + status);
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void DrawTabPlayer()
        {
            infiniteStamina = GUILayout.Toggle(infiniteStamina, " Nieskonczona stamina");
            noclip = GUILayout.Toggle(noclip, " Noclip (przechodzenie przez sciany)");
            speedHack = GUILayout.Toggle(speedHack, " Szybszy ruch");
            GUILayout.Label("Mnoznik ruchu: " + speedMultiplier.ToString("0.0") + "x");
            speedMultiplier = GUILayout.HorizontalSlider(speedMultiplier, 1f, 5f);

            GUILayout.Space(8);
            GUILayout.Label("YTP", GUI.skin.box);
            infiniteYtps = GUILayout.Toggle(infiniteYtps, " Nieskonczone YTP");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+100 YTP")) AddYtps(100);
            if (GUILayout.Button("+1000 YTP")) AddYtps(1000);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Notebooki", GUI.skin.box);
            if (GUILayout.Button("Dodaj jeden notebook")) AddNotebook();

            GUILayout.Space(8);
            GUILayout.Label("Przedmioty", GUI.skin.box);
            infiniteItems = GUILayout.Toggle(infiniteItems, " Nieskonczone przedmioty");
            if (GUILayout.Button("Odswiez liste przedmiotow")) RefreshItems();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Szukaj:", GUILayout.Width(50f));
            itemFilter = GUILayout.TextField(itemFilter);
            GUILayout.EndHorizontal();
            itemScroll = GUILayout.BeginScrollView(itemScroll, GUILayout.Height(170f));
            string flt = itemFilter.ToLowerInvariant();
            foreach (UnityEngine.Object it in itemAssets)
            {
                if (it == null) continue;
                if (flt.Length > 0 && !it.name.ToLowerInvariant().Contains(flt)) continue;
                if (GUILayout.Button(it.name)) GiveItem(it);
            }
            GUILayout.EndScrollView();
        }

        private void DrawTabNpc()
        {
            freezeBaldi = GUILayout.Toggle(freezeBaldi, " Zamroz Baldiego");
            npcIgnore = GUILayout.Toggle(npcIgnore, " NPC ignoruja gracza (wylacza ich wzrok)");
            GUILayout.Label("Predkosc NPC: " + npcSpeedMul.ToString("0.0") + "x");
            npcSpeedMul = GUILayout.HorizontalSlider(npcSpeedMul, 0f, 3f);

            GUILayout.Space(8);
            GUILayout.Label("Lista NPC (" + npcRefs.Count + ")", GUI.skin.box);
            List<MonoBehaviour> snapshot = new List<MonoBehaviour>(npcRefs);
            foreach (MonoBehaviour npc in snapshot)
            {
                if (npc == null) continue;
                GUILayout.BeginHorizontal();
                bool active = npc.gameObject.activeSelf;
                GUILayout.Label(npc.GetType().Name + (active ? "" : " (wylaczony)"), GUILayout.Width(130f));
                if (GUILayout.Button(active ? "Wylacz" : "Wlacz", GUILayout.Width(60f)))
                    npc.gameObject.SetActive(!active);
                if (GUILayout.Button(npc.enabled ? "Zamroz" : "Odmroz", GUILayout.Width(65f)))
                    npc.enabled = !npc.enabled;
                if (GUILayout.Button("Do mnie", GUILayout.Width(65f)))
                    TeleportNpcToMe(npc);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawTabWorld()
        {
            GUILayout.Label("Mapa", GUI.skin.box);
            if (GUILayout.Button("Odkryj cala mape")) RevealMap();

            GUILayout.Space(8);
            GUILayout.Label("Eventy (" + events.Count + ")", GUI.skin.box);
            foreach (MonoBehaviour ev in events)
            {
                if (ev == null) continue;
                object act = GetMember(ev, "active");
                bool isActive = act is bool && (bool)act;
                GUILayout.BeginHorizontal();
                GUILayout.Label(ev.GetType().Name + (isActive ? " [aktywny]" : ""), GUILayout.Width(190f));
                if (GUILayout.Button("Start", GUILayout.Width(60f))) StartEvent(ev);
                bool locked = IsLockedEvent(ev);
                bool prevEnabled = GUI.enabled;
                GUI.enabled = prevEnabled && !locked;
                if (GUILayout.Button(locked ? "Stop (lock)" : "Stop", GUILayout.Width(85f))) EndEvent(ev);
                GUI.enabled = prevEnabled;
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label("Generowanie (od nastepnego poziomu)", GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed:", GUILayout.Width(50f));
            seedText = GUILayout.TextField(seedText);
            if (GUILayout.Button("Ustaw", GUILayout.Width(60f))) ApplySeed();
            GUILayout.EndHorizontal();
            GUILayout.Label("Rozmiar: " + Mathf.RoundToInt(sizeW) + " x " + Mathf.RoundToInt(sizeH));
            sizeW = GUILayout.HorizontalSlider(sizeW, 10f, 100f);
            sizeH = GUILayout.HorizontalSlider(sizeH, 10f, 100f);
            if (GUILayout.Button("Ustaw rozmiar")) ApplySize();

            GUILayout.Space(8);
            GUILayout.Label("Szybkosc gry: " + gameSpeed.ToString("0.00") + "x", GUI.skin.box);
            gameSpeed = GUILayout.HorizontalSlider(gameSpeed, 0.25f, 3f);
            if (GUILayout.Button("Reset szybkosci gry"))
            {
                gameSpeed = 1f;
                Time.timeScale = 1f;
            }
        }

        private void DrawTabVisual()
        {
            espNpcs = GUILayout.Toggle(espNpcs, " ESP postacie (Baldi, NPC)");
            espItems = GUILayout.Toggle(espItems, " ESP przedmioty i notebooki");
            rainbowEsp = GUILayout.Toggle(rainbowEsp, " Rainbow ESP");
            entityGps = GUILayout.Toggle(entityGps, " Entity GPS (lista odleglosci i kierunkow)");

            GUILayout.Space(8);
            showFps = GUILayout.Toggle(showFps, " Pokaz FPS");
            hideHud = GUILayout.Toggle(hideHud, " Ukryj HUD");

            GUILayout.Space(8);
            customFov = GUILayout.Toggle(customFov, " Wlasny Field of View");
            GUILayout.Label("FOV: " + Mathf.RoundToInt(fov));
            fov = GUILayout.HorizontalSlider(fov, 40f, 140f);

            GUILayout.Space(10);
            GUILayout.Label("Tryb kamery", GUI.skin.box);
            camMode = GUILayout.Toolbar(camMode, camModeNames);
            if (camMode == 1 || camMode == 2)
            {
                GUILayout.Label((camMode == 1 ? "Odleglosc za graczem: " : "Wysokosc nad graczem: ") + Mathf.RoundToInt(camDist));
                camDist = GUILayout.HorizontalSlider(camDist, 5f, 60f);
            }
            if (camMode == 1)
            {
                GUILayout.Label("Przesuniecie kamery w pionie: " + Mathf.RoundToInt(camHeight));
                camHeight = GUILayout.HorizontalSlider(camHeight, -10f, 20f);
            }
            if (camMode == 1 || camMode == 3)
            {
                GUILayout.Label("Patrzenie gora/dol: " + Mathf.RoundToInt(camPitch) + " st.");
                GUILayout.Label("Mysz w gore/dol, PageUp/PageDown, Home = reset");
                camPitch = GUILayout.HorizontalSlider(camPitch, -80f, 80f);
                GUILayout.Label("Czulosc myszy gora/dol: " + pitchSens.ToString("0.0"));
                pitchSens = GUILayout.HorizontalSlider(pitchSens, 0.5f, 10f);
                if (GUILayout.Button("Reset patrzenia")) camPitch = 0f;
            }
            if (camMode == 1 || camMode == 2)
            {
                showBody = GUILayout.Toggle(showBody, " Pokaz znacznik postaci gracza");
            }

            GUILayout.Space(10);
            GUILayout.Label("Niebo", GUI.skin.box);
            customSky = GUILayout.Toggle(customSky, " Wlasne niebo (kolor tla kamery)");
            if (customSky)
            {
                rainbowSky = GUILayout.Toggle(rainbowSky, " Tecza (animowane)");
                if (!rainbowSky)
                {
                    GUILayout.Label("R " + skyR.ToString("0.00"));
                    skyR = GUILayout.HorizontalSlider(skyR, 0f, 1f);
                    GUILayout.Label("G " + skyG.ToString("0.00"));
                    skyG = GUILayout.HorizontalSlider(skyG, 0f, 1f);
                    GUILayout.Label("B " + skyB.ToString("0.00"));
                    skyB = GUILayout.HorizontalSlider(skyB, 0f, 1f);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Dzien")) { skyR = 0.4f; skyG = 0.7f; skyB = 1f; }
                    if (GUILayout.Button("Zachod")) { skyR = 1f; skyG = 0.45f; skyB = 0.25f; }
                    if (GUILayout.Button("Noc")) { skyR = 0.02f; skyG = 0.03f; skyB = 0.12f; }
                    if (GUILayout.Button("Rozowe")) { skyR = 1f; skyG = 0.5f; skyB = 0.8f; }
                    GUILayout.EndHorizontal();
                }
            }
            hideCeilings = GUILayout.Toggle(hideCeilings, " Ukryj sufity (zeby bylo widac niebo)");
        }

        // ===================================================================
        // kamera 3D: zmieniamy pozycje tylko na czas renderowania,
        // wiec gra nie widzi zmian i jej sterowanie dziala normalnie
        // ===================================================================
        private struct SavedPose
        {
            public Vector3 pos;
            public Quaternion rot;
        }

        private readonly Dictionary<Camera, SavedPose> savedPoses = new Dictionary<Camera, SavedPose>();

        private static int PopCount(int v)
        {
            uint u = (uint)v;
            int n = 0;
            while (u != 0u)
            {
                n += (int)(u & 1u);
                u >>= 1;
            }
            return n;
        }

        // wybiera kamere, ktora renderuje swiat 3D (najwiecej warstw, nie nakladkowa)
        private Camera PickWorldCamera()
        {
            Camera best = null;
            int bestScore = -1;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null || !c.enabled || c.orthographic || c.targetTexture != null) continue;
                int score = PopCount(c.cullingMask) * 2;
                if (c.clearFlags == CameraClearFlags.Skybox || c.clearFlags == CameraClearFlags.SolidColor) score += 100;
                if (c == Camera.main) score += 1;
                if (score > bestScore)
                {
                    best = c;
                    bestScore = score;
                }
            }
            return best;
        }

        // patrzenie gora/dol (gra Baldi nie ma pionowego rozgladania sie)
        private void UpdatePitchInput()
        {
            if (camMode != 1 && camMode != 3) return;
            if (Input.GetKeyDown(KeyCode.Home)) camPitch = 0f;
            if (showMenu || Time.timeScale <= 0f) return;

            float dy = 0f;
            if (!mouseAxisBroken)
            {
                try { dy = Input.GetAxisRaw("Mouse Y"); }
                catch (Exception) { mouseAxisBroken = true; }
            }
            camPitch -= dy * pitchSens;
            if (Input.GetKey(KeyCode.PageUp)) camPitch -= 60f * Time.unscaledDeltaTime;
            if (Input.GetKey(KeyCode.PageDown)) camPitch += 60f * Time.unscaledDeltaTime;
            camPitch = Mathf.Clamp(camPitch, -80f, 80f);
        }

        // oblicza docelowa pozycje kamery dla wybranego trybu
        private void ComputeRig(Vector3 origPos, Quaternion origRot, out Vector3 pos, out Quaternion rot)
        {
            Vector3 flat = origRot * Vector3.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
            flat.Normalize();
            Quaternion baseRot = Quaternion.LookRotation(flat, Vector3.up);

            pos = origPos;
            rot = origRot;

            if (camMode == 1)
            {
                Quaternion orbit = baseRot * Quaternion.Euler(camPitch, 0f, 0f);
                Vector3 pivot = origPos + Vector3.up * camHeight;
                Vector3 look = orbit * Vector3.forward;
                Vector3 want = pivot - look * camDist;
                Vector3 dir = want - pivot;
                float len = dir.magnitude;
                RaycastHit hit;
                if (len > 0.01f && Physics.Raycast(pivot, dir / len, out hit, len, ~0, QueryTriggerInteraction.Ignore))
                {
                    want = pivot + dir / len * Mathf.Max(0.5f, hit.distance - 0.5f);
                }
                pos = want;
                rot = orbit;
            }
            else if (camMode == 2)
            {
                pos = origPos + Vector3.up * camDist * 2f;
                rot = Quaternion.LookRotation(Vector3.down, flat);
            }
            else if (camMode == 3)
            {
                rot = baseRot * Quaternion.Euler(camPitch, 0f, 0f);
            }
        }

        private void OnCamPreCull(Camera c)
        {
            if (camMode == 0 || c == null) return;
            Camera p = camTarget;
            if (p == null) return;

            Transform pt = p.transform;
            Transform ct = c.transform;
            if (c != p)
            {
                // kamery stojace w tym samym miejscu co glowna (np. od sprite'ow) ruszamy razem z nia
                if (c.orthographic) return;
                if (Vector3.Distance(ct.position, pt.position) > 3f) return;
                if (Quaternion.Angle(ct.rotation, pt.rotation) > 10f) return;
            }

            Vector3 np;
            Quaternion nr;
            ComputeRig(pt.position, pt.rotation, out np, out nr);
            Quaternion d = nr * Quaternion.Inverse(pt.rotation);

            SavedPose sp;
            sp.pos = ct.position;
            sp.rot = ct.rotation;
            savedPoses[c] = sp;

            ct.SetPositionAndRotation(np + d * (ct.position - pt.position), d * ct.rotation);
        }

        private void OnCamPostRender(Camera c)
        {
            SavedPose sp;
            if (c != null && savedPoses.TryGetValue(c, out sp))
            {
                c.transform.SetPositionAndRotation(sp.pos, sp.rot);
                savedPoses.Remove(c);
            }
        }

        // rzutowanie punktu na ekran z uwzglednieniem trybu kamery (dla ESP)
        private Vector3 ProjectPoint(Camera cam, Vector3 world)
        {
            if (camMode == 0 || cam != camTarget) return cam.WorldToScreenPoint(world);
            Transform t = cam.transform;
            Vector3 op = t.position;
            Quaternion orot = t.rotation;
            Vector3 np;
            Quaternion nr;
            ComputeRig(op, orot, out np, out nr);
            t.SetPositionAndRotation(np, nr);
            Vector3 r = cam.WorldToScreenPoint(world);
            t.SetPositionAndRotation(op, orot);
            return r;
        }

        private void UpdateBodyMarker()
        {
            bool want = (camMode == 1 || camMode == 2) && showBody && camTarget != null;
            if (want)
            {
                if (bodyMarker == null) CreateMarker();
                if (bodyMarker != null)
                {
                    bodyMarker.SetActive(true);
                    bodyMarker.transform.position = camTarget.transform.position + Vector3.down * 2.5f;
                }
            }
            else if (bodyMarker != null)
            {
                bodyMarker.SetActive(false);
            }
        }

        private void CreateMarker()
        {
            try
            {
                bodyMarker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                bodyMarker.name = "BaldiModMenu_Body";
                Collider col = bodyMarker.GetComponent<Collider>();
                if (col != null) Destroy(col);
                bodyMarker.transform.localScale = new Vector3(2f, 2.5f, 2f);

                Renderer r = bodyMarker.GetComponent<Renderer>();
                string[] shaders = { "Unlit/Color", "Sprites/Default", "Standard" };
                foreach (string s in shaders)
                {
                    Shader sh = Shader.Find(s);
                    if (sh == null) continue;
                    Material m = new Material(sh);
                    m.color = new Color(0.2f, 0.6f, 1f);
                    r.material = m;
                    break;
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Marker: " + e.Message);
            }
        }

        // ===================================================================
        // niebo
        // ===================================================================
        private Material origSkybox;
        private Material skyMat;
        private bool skyApplied = false;
        private Cubemap whiteCube;

        private static readonly string[] skyShaderNames =
            { "Skybox/Procedural", "Skybox/Cubemap", "Skybox/6 Sided", "Skybox/Panoramic" };

        private void ApplySky()
        {
            if (customSky)
            {
                Color col = rainbowSky
                    ? Color.HSVToRGB(Mathf.Repeat(Time.time * 0.1f, 1f), 0.6f, 1f)
                    : new Color(skyR, skyG, skyB);

                if (!skyApplied)
                {
                    origSkybox = RenderSettings.skybox;
                    BuildSkyMaterial();
                    skyApplied = true;
                }

                bool haveSky = skyMat != null;
                if (haveSky)
                {
                    TintSkyMaterial(skyMat, col);
                    if (RenderSettings.skybox != skyMat) RenderSettings.skybox = skyMat;
                }

                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null || !c.enabled) continue;
                    // kamery nakladkowe (HUD, przedmioty) maja DepthOnly - ich nie ruszamy
                    if (c.clearFlags != CameraClearFlags.Skybox && c.clearFlags != CameraClearFlags.SolidColor) continue;
                    int id = c.GetInstanceID();
                    if (!origSkyFlags.ContainsKey(id))
                    {
                        origSkyFlags[id] = c.clearFlags;
                        origSkyColor[id] = c.backgroundColor;
                    }
                    c.clearFlags = haveSky ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                    c.backgroundColor = col;
                }
            }
            else if (skyApplied)
            {
                RenderSettings.skybox = origSkybox;
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null) continue;
                    int id = c.GetInstanceID();
                    CameraClearFlags f;
                    if (origSkyFlags.TryGetValue(id, out f))
                    {
                        c.clearFlags = f;
                        c.backgroundColor = origSkyColor[id];
                    }
                }
                origSkyFlags.Clear();
                origSkyColor.Clear();
                if (skyMat != null) Destroy(skyMat);
                skyMat = null;
                skyApplied = false;
            }
        }

        // kopia skyboxa z gry (zeby zmieniac prawdziwy skybox) albo nowy, jesli gra zadnego nie ma
        private void BuildSkyMaterial()
        {
            skyMat = null;
            try
            {
                if (origSkybox != null)
                {
                    skyMat = new Material(origSkybox);
                }
                else
                {
                    foreach (string n in skyShaderNames)
                    {
                        Shader sh = Shader.Find(n);
                        if (sh != null)
                        {
                            skyMat = new Material(sh);
                            break;
                        }
                    }
                }
                if (skyMat != null) NeutralizeSkyTextures(skyMat);
            }
            catch (Exception e)
            {
                Logger.LogError("Niebo: " + e.Message);
                skyMat = null;
            }
        }

        // podmienia tekstury skyboxa na biale, zeby kolor zastapil oryginalne niebo
        private void NeutralizeSkyTextures(Material m)
        {
            if (whiteCube == null)
            {
                whiteCube = new Cubemap(8, TextureFormat.RGBA32, false);
                Color[] px = new Color[64];
                for (int i = 0; i < px.Length; i++) px[i] = Color.white;
                CubemapFace[] faces =
                {
                    CubemapFace.PositiveX, CubemapFace.NegativeX,
                    CubemapFace.PositiveY, CubemapFace.NegativeY,
                    CubemapFace.PositiveZ, CubemapFace.NegativeZ
                };
                foreach (CubemapFace f in faces) whiteCube.SetPixels(px, f);
                whiteCube.Apply();
            }

            if (m.HasProperty("_Tex")) m.SetTexture("_Tex", whiteCube);
            string[] flatProps = { "_MainTex", "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };
            foreach (string p in flatProps)
            {
                if (m.HasProperty(p)) m.SetTexture(p, Texture2D.whiteTexture);
            }
        }

        private static void TintSkyMaterial(Material m, Color col)
        {
            if (m.HasProperty("_SkyTint")) m.SetColor("_SkyTint", col);
            if (m.HasProperty("_GroundColor")) m.SetColor("_GroundColor", new Color(col.r * 0.7f, col.g * 0.7f, col.b * 0.7f, 1f));
            if (m.HasProperty("_Exposure")) m.SetFloat("_Exposure", 1.3f);
            // "Tint" w skyboxach Unity jest mnozony x2, wiec polowa koloru daje prawidlowy kolor
            if (m.HasProperty("_Tint")) m.SetColor("_Tint", new Color(col.r * 0.5f, col.g * 0.5f, col.b * 0.5f, 1f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", col);
        }

        private void UpdateCeilings()
        {
            if (hideCeilings)
            {
                if (Time.unscaledTime < nextCeilScan) return;
                nextCeilScan = Time.unscaledTime + 3f;
                foreach (Renderer r in FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled) continue;
                    string n = r.gameObject.name.ToLowerInvariant();
                    if (n.Contains("ceil") || n.Contains("sufit"))
                    {
                        r.enabled = false;
                        hiddenCeilings.Add(r);
                    }
                }
            }
            else if (hiddenCeilings.Count > 0)
            {
                foreach (Renderer r in hiddenCeilings)
                {
                    if (r != null) r.enabled = true;
                }
                hiddenCeilings.Clear();
            }
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCamPreCull;
            Camera.onPostRender -= OnCamPostRender;
            if (bodyMarker != null) Destroy(bodyMarker);
        }

        // ===================================================================
        // zakladka Wersja + aktualizacje
        // ===================================================================
        private void DrawTabVersion()
        {
            GUILayout.Label("Wersja menu: " + MenuVersion, GUI.skin.box);
            GUILayout.Label("Wersja Baldiego: " + Application.version, GUI.skin.box);
            GUILayout.Label("Gra: " + Application.productName + "   |   Unity " + Application.unityVersion);

            GUILayout.Space(10);
            GUILayout.Label("Aktualizacje", GUI.skin.box);

            if (updState == 1)
            {
                GUILayout.Label("Sprawdzanie aktualizacji...");
            }
            else if (updAvailable)
            {
                GUILayout.Label("Dostepna nowa wersja menu: " + updRemoteVersion + "  (masz " + MenuVersion + ")");
                if (GUILayout.Button("Update"))
                {
                    if (updDownloadUrl.StartsWith("https://") || updDownloadUrl.StartsWith("http://"))
                    {
                        Application.OpenURL(updDownloadUrl);
                        status = "Otwarto strone pobierania.";
                    }
                    else
                    {
                        status = "Brak poprawnego linku do pobrania w pliku wersji.";
                    }
                }
                GUILayout.Label("Co dodano:");
                changelogScroll = GUILayout.BeginScrollView(changelogScroll, GUILayout.Height(190f));
                GUILayout.Label(updChangelog.Length > 0 ? updChangelog : "(brak opisu)");
                GUILayout.EndScrollView();
            }
            else if (updState == 2)
            {
                GUILayout.Label("Masz najnowsza wersje (" + MenuVersion + ").");
            }
            else if (updState == 3)
            {
                GUILayout.Label("Blad: " + updError);
            }
            else
            {
                GUILayout.Label("Jeszcze nie sprawdzono.");
            }

            if (GUILayout.Button("Sprawdz aktualizacje")) StartCheck();

            GUILayout.Space(6);
            GUILayout.Label("Adres version.txt ustawiasz w pliku BepInEx/config/local.baldi.modmenu.cfg (UpdateUrl).");
        }

        private void StartCheck()
        {
            if (updState == 1) return;
            StartCoroutine(CheckUpdateRoutine());
        }

        private System.Collections.IEnumerator CheckUpdateRoutine()
        {
            string url = cfgUpdateUrl != null ? cfgUpdateUrl.Value : "";
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("https://") || url.StartsWith("http://")))
            {
                updState = 3;
                updError = "brak adresu aktualizacji. Wpisz UpdateUrl w BepInEx/config/local.baldi.modmenu.cfg i zrestartuj gre.";
                yield break;
            }

            updState = 1;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (!string.IsNullOrEmpty(req.error) || req.responseCode >= 400)
                {
                    updState = 3;
                    updError = string.IsNullOrEmpty(req.error) ? ("HTTP " + req.responseCode) : req.error;
                    yield break;
                }
                ParseUpdate(req.downloadHandler.text);
            }
        }

        private void ParseUpdate(string text)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim().Length == 0)
            {
                updState = 3;
                updError = "plik wersji jest pusty.";
                return;
            }

            string rv = lines[0].Trim().TrimStart('v', 'V');
            updRemoteVersion = rv;
            updDownloadUrl = lines.Length > 1 ? lines[1].Trim() : "";
            updChangelog = lines.Length > 2 ? string.Join("\n", lines, 2, lines.Length - 2).Trim() : "";

            System.Version a, b;
            bool newer;
            if (System.Version.TryParse(rv, out a) && System.Version.TryParse(MenuVersion, out b)) newer = a > b;
            else newer = rv != MenuVersion;

            updAvailable = newer;
            updState = 2;
            if (newer) Logger.LogInfo("Dostepna aktualizacja menu: " + rv);
        }

        private void DrawTabDebug()
        {
            if (GUILayout.Button("Zrzut struktury gry do logu")) DumpInfo();
            GUILayout.Space(8);
            GUILayout.Label("Znaleziono:");
            GUILayout.Label("graczy " + players.Count + ", PlayerManager " + playerMgrs.Count + ", Baldich " + baldis.Count);
            GUILayout.Label("NPC " + npcRefs.Count + ", celow ESP " + targets.Count + ", map " + maps.Count);
            GUILayout.Label("eventow " + events.Count + ", Navigator " + navigators.Count + ", Looker " + loopers.Count);
            GUILayout.Label("GameManager " + gameMgrs.Count + ", HudManager " + hudMgrs.Count + ", canvasow HUD " + hudCanvases.Count);
            GUILayout.Label("przedmiotow na liscie " + itemAssets.Count);
        }

        // ===================================================================
        // refleksja - zeby nie zalezec od wersji Assembly-CSharp
        // ===================================================================
        private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        private static Type FindType(string name)
        {
            Type found;
            if (typeCache.TryGetValue(name, out found)) return found;

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.StartsWith("Assembly-CSharp")) continue;
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (Type t in types)
                {
                    if (t != null && t.Name == name)
                    {
                        typeCache[name] = t;
                        return t;
                    }
                }
            }
            typeCache[name] = null;
            return null;
        }

        private static object GetStatic(Type t, string name)
        {
            PropertyInfo pi = t.GetProperty(name, AllStatic);
            if (pi != null && pi.CanRead) return pi.GetValue(null, null);
            FieldInfo fi = t.GetField(name, AllStatic);
            if (fi != null) return fi.GetValue(null);
            return null;
        }

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(name, AllInst | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
                PropertyInfo pi = t.GetProperty(name, AllInst | BindingFlags.DeclaredOnly);
                if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) return pi.GetValue(o, null);
            }
            return null;
        }

        private static bool SetMember(object o, string name, object value)
        {
            if (o == null) return false;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(name, AllInst | BindingFlags.DeclaredOnly);
                if (fi != null)
                {
                    fi.SetValue(o, ConvertTo(value, fi.FieldType));
                    return true;
                }
                PropertyInfo pi = t.GetProperty(name, AllInst | BindingFlags.DeclaredOnly);
                if (pi != null && pi.CanWrite && pi.GetIndexParameters().Length == 0)
                {
                    pi.SetValue(o, ConvertTo(value, pi.PropertyType), null);
                    return true;
                }
            }
            return false;
        }

        private static object ConvertTo(object value, Type target)
        {
            if (value == null || target.IsInstanceOfType(value)) return value;
            try { return Convert.ChangeType(value, target); }
            catch (Exception) { return value; }
        }

        private static float GetFloat(object o, string name)
        {
            object v = GetMember(o, name);
            if (v == null) return 0f;
            try { return Convert.ToSingle(v); }
            catch (Exception) { return 0f; }
        }

        private bool InvokeByName(object o, string name, out object result, params object[] args)
        {
            result = null;
            if (o == null) return false;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(AllInst | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != name) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != args.Length) continue;
                    bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (args[i] == null) { if (ps[i].ParameterType.IsValueType) ok = false; }
                        else if (!ps[i].ParameterType.IsInstanceOfType(args[i])) ok = false;
                    }
                    if (!ok) continue;
                    try
                    {
                        result = m.Invoke(o, args);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Logger.LogError("Blad wywolania " + name + ": " + e.Message);
                        return false;
                    }
                }
            }
            return false;
        }
    }
}
