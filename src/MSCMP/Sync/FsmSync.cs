using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>Added as the first action of a synced FSM state: reports when we enter it.</summary>
    internal sealed class FsmSyncHook : FsmStateAction
    {
        internal FsmSync.Hook Hook;

        public override void OnEnter()
        {
            try { FsmSync.OnHookEntered(Hook); }
            catch (Exception e) { MSCMP.Log.ExceptionOnce("FsmSync hook", e); }
            Finish();
        }
    }

    /// <summary>
    /// Mirrors world interactions. My Summer Car runs almost everything through PlayMaker
    /// state machines: doors, light switches, taps, the TV, dashboard buttons, the ignition,
    /// assembling and removing car parts, wiring, every bolt. When you click something, its
    /// state machine moves into a new state. We watch the states that are reached by a
    /// player's click or tool and replay exactly that state on every other game, which runs
    /// the same game logic there - same sounds, same animations, same saved data.
    ///
    /// The host orders all events and keeps them in the world log so friends who join later
    /// end up with the same world. Effects on the player who did it (money, hunger, the HUD,
    /// the hand) are not replayed on the other players.
    /// </summary>
    internal static class FsmSync
    {
        // ================================================================ data

        internal sealed class Entry
        {
            public uint Id;
            public PlayMakerFSM Pm;
            public string Path;
            public uint ObjectId;                   // dynamic NetObject it belongs to (0 = scene)
            public readonly List<Hook> Hooks = new List<Hook>(2);
            public readonly HashSet<FsmState> Resting = new HashSet<FsmState>();
            public Queue<Pending> Queue;
            public float QueueActiveSince = -1f;

            public bool Alive { get { return Pm != null && Pm.Fsm != null; } }
        }

        internal sealed class Hook
        {
            public Entry Entry;
            public FsmState State;
            public ushort StateIndex;
            public FsmTransition[] InputTransitions;   // null = every local entry (tool events)
            public float MaxDistance;
            public FsmStateAction[] Chain;             // actions run by this state and what follows
            public HashSet<FsmStateAction> PlayerWrites; // write the local player's globals
            public RefField[][] ChainRefs;             // per chain action: GameObject references
            public FsmGameObject[] Spawners;           // CreateObject results in the chain
        }

        internal struct RefField { public FieldInfo Field; public int Kind; } // 0 owner default, 1 game object, 2 event target

        internal struct Binding { public byte Var; public uint Object; }

        internal sealed class Pending
        {
            public ushort State;
            public byte Origin;
            public uint Serial;
            public Binding[] Bindings;
            public float Received;
        }

        sealed class Reenable { public Entry Entry; public List<FsmStateAction> Actions; public float Until; }

        sealed class LocalLink { public Hook Hook; public GameObject[] Before; public bool[] Done; public uint Serial; public int Frame; }

        static readonly Dictionary<uint, Entry> byId = new Dictionary<uint, Entry>();
        static readonly Dictionary<int, Entry> byComponent = new Dictionary<int, Entry>();
        static readonly List<Entry> queued = new List<Entry>();
        static readonly List<Reenable> reenable = new List<Reenable>();
        static readonly List<LocalLink> localLinks = new List<LocalLink>();
        static readonly HashSet<NamedVariable> playerGlobals = new HashSet<NamedVariable>();
        static readonly Dictionary<Type, FieldInfo[]> eventFieldCache = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, FieldInfo[]> varFieldCache = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, RefField[]> refFieldCache = new Dictionary<Type, RefField[]>();

        static bool applyingRemote;
        static uint localSerial;
        static int sentThisSecond;
        static float secondStart;
        static int hookedStates;
        static bool ready;

        const float RemoteWaitLimit = 5f;      // an FSM busy for longer than this gets the event anyway
        const int MaxEventsPerSecond = 120;

        // Things that belong to one player (their menus, their body, their phone, their save)
        // or that only make sense for whoever did them.
        static readonly string[] ExcludedRoots =
        {
            "PLAYER", "Systems", "GUI", "Sheets", "MSCMP", "MSCMP_Player", "NoClip", "Database",
            "TRAFFIC", "NPC_CARS", "HUMANS", "COPS", "RALLY", "DRAGRACE", "KILJUGUY", "JOBS", "INSPECTION"
        };
        static readonly string[] ExcludedPathParts =
        {
            "SAVEGAME", "Sleep", "VideoPoker", "SlotMachine", "PayMoney", "COMPUTER", "Telephone",
            "Phone", "Darts", "Lotto", "PostOrder", "CashRegister", "ShopFunctions", "ActivateStore",
            "ActivateBar", "/Store/", "PartsSalesman", "Fleetari", "Brochure", "AdvertSpawn", "Dyno"
        };
        static readonly string[] ExcludedFsmNames =
        {
            "PlayerTrigger", "Buy", "Bargain", "PlayerDrink", "Activate", "Information", "Save", "LOD"
        };
        static readonly string[] ToolStates = { "Screw", "Unscrew", "Screw 2", "Unscrew 2" };

        public static int Count { get { return byId.Count; } }
        public static int HookedStates { get { return hookedStates; } }
        public static bool ApplyingRemote { get { return applyingRemote; } }

        // ================================================================ setup

        public static void Init()
        {
            Session.On(Msg.FsmState, OnFsmState);
            Session.On(Msg.FsmSpawn, OnFsmSpawn);
            // Objects created after loading get their state machines hooked right away, so an
            // event for them that arrives in the same packet batch is not lost.
            ObjectSync.Registered += o =>
            {
                if (!ready || !o.Dynamic) return;
                try { RegisterObjectFsms(o); }
                catch (Exception e) { Log.ExceptionOnce("FsmSync object", e); }
            };
        }

        public static void OnWorldReady()
        {
            Clear();
            float t0 = Time.realtimeSinceStartup;
            CollectPlayerGlobals();

            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
            List<KeyValuePair<string, PlayMakerFSM>> keyed = new List<KeyValuePair<string, PlayMakerFSM>>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                PlayMakerFSM pm = all[i] as PlayMakerFSM;
                if (pm == null || !GameUtil.IsSceneObject(pm.gameObject)) continue;
                string path = GameUtil.PathOf(pm.transform);
                keyed.Add(new KeyValuePair<string, PlayMakerFSM>(path + "|" + pm.FsmName + "|" + GameUtil.SiblingChain(pm.transform) + "|" + ComponentIndex(pm), pm));
            }
            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            Dictionary<string, int> dup = new Dictionary<string, int>();
            HashSet<uint> used = new HashSet<uint>();
            int analysed = 0;
            for (int i = 0; i < keyed.Count; i++)
            {
                PlayMakerFSM pm = keyed[i].Value;
                string path = GameUtil.PathOf(pm.transform);
                string key = path + "|" + pm.FsmName;
                int n;
                dup.TryGetValue(key, out n);
                dup[key] = n + 1;
                if (Excluded(pm, path)) continue;
                string idKey = key + "#" + n;
                uint id = GameUtil.Fnv1a(idKey) & 0x7FFFFFFFu;
                while (id == 0 || used.Contains(id)) { idKey += "~"; id = GameUtil.Fnv1a(idKey) & 0x7FFFFFFFu; }
                used.Add(id);
                analysed++;
                try { TryRegister(pm, id, path, 0); }
                catch (Exception e) { Log.Debug("Skipped FSM " + path + " (" + pm.FsmName + "): " + e.Message); }
            }
            ready = true;
            Log.Info("World sync: " + byId.Count + " interactive state machines (" + hookedStates + " states) out of " +
                     analysed + " checked, in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms.");
        }

        public static void OnWorldLeft()
        {
            Clear();
        }

        static void Clear()
        {
            ready = false;
            foreach (Entry e in byId.Values) RemoveHooks(e);
            for (int i = 0; i < reenable.Count; i++) SetEnabled(reenable[i].Actions, true);
            byId.Clear();
            byComponent.Clear();
            queued.Clear();
            reenable.Clear();
            localLinks.Clear();
            hookedStates = 0;
            applyingRemote = false;
        }

        static int ComponentIndex(PlayMakerFSM pm)
        {
            PlayMakerFSM[] list = pm.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < list.Length; i++) if (list[i] == pm) return i;
            return 0;
        }

        static bool Excluded(PlayMakerFSM pm, string path)
        {
            if (pm.UsesTemplate) return true;
            string fsmName = pm.FsmName ?? "";
            for (int i = 0; i < ExcludedFsmNames.Length; i++) if (fsmName == ExcludedFsmNames[i]) return true;
            int slash = path.IndexOf('/');
            string root = slash < 0 ? path : path.Substring(0, slash);
            for (int i = 0; i < ExcludedRoots.Length; i++) if (root == ExcludedRoots[i]) return true;
            if (root.StartsWith("MSCMP", StringComparison.Ordinal)) return true;
            for (int i = 0; i < ExcludedPathParts.Length; i++)
                if (path.IndexOf(ExcludedPathParts[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static void CollectPlayerGlobals()
        {
            playerGlobals.Clear();
            FsmVariables g = FsmVariables.GlobalVariables;
            if (g == null) return;
            NamedVariable[] vars = g.GetAllNamedVariables();
            for (int i = 0; i < vars.Length; i++)
            {
                NamedVariable v = vars[i];
                if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                string n = v.Name;
                if (n.StartsWith("Player", StringComparison.OrdinalIgnoreCase) || n.StartsWith("GUI", StringComparison.OrdinalIgnoreCase) ||
                    n == "SavePlayer" || n == "SavePlayerCam" || n == "POV" || n == "ItemPivot" || n == "Hand" ||
                    n == "picked_object" || n == "PickedPart" || n == "PickedObject")
                    playerGlobals.Add(v);
            }
        }

        // ================================================================ analysis

        /// <summary>Finds the states of this FSM that a player's input leads to and hooks them.</summary>
        static void TryRegister(PlayMakerFSM pm, uint id, string path, uint objectId)
        {
            Fsm fsm = pm.Fsm;
            if (fsm == null) return;
            try
            {
                if (!fsm.Initialized) fsm.InitData();
            }
            catch (Exception e)
            {
                Log.Debug("Could not initialise FSM " + path + ": " + e.Message);
                return;
            }
            FsmState[] states = fsm.States;
            if (states == null || states.Length == 0 || states.Length > 4000) return;

            Entry entry = null;
            Dictionary<string, FsmState> byName = new Dictionary<string, FsmState>();
            for (int i = 0; i < states.Length; i++) if (states[i] != null && !byName.ContainsKey(states[i].Name)) byName[states[i].Name] = states[i];

            // 1. Which states wait for the player, and which of their events are input?
            Dictionary<FsmState, List<FsmTransition>> targets = new Dictionary<FsmState, List<FsmTransition>>();
            HashSet<FsmState> resting = new HashSet<FsmState>();
            for (int i = 0; i < states.Length; i++)
            {
                FsmState s = states[i];
                if (s == null) continue;
                FsmStateAction[] actions = SafeActions(s);
                bool waits;
                HashSet<string> inputEvents = InputEventsOf(actions, out waits);
                FsmTransition[] tr = s.Transitions ?? new FsmTransition[0];
                if (waits || tr.Length == 0) resting.Add(s);
                if (inputEvents.Count == 0) continue;
                for (int t = 0; t < tr.Length; t++)
                {
                    if (tr[t] == null || tr[t].EventName == null || !inputEvents.Contains(tr[t].EventName)) continue;
                    FsmState to;
                    if (!byName.TryGetValue(tr[t].ToState ?? "", out to) || to == s) continue;
                    List<FsmTransition> list;
                    if (!targets.TryGetValue(to, out list)) targets[to] = list = new List<FsmTransition>();
                    list.Add(tr[t]);
                }
            }

            // 2. Tool events (wrench / ratchet on a bolt) come from the player's tool FSM.
            List<FsmState> toolStates = null;
            if (pm.FsmName == "Screw")
            {
                for (int i = 0; i < ToolStates.Length; i++)
                {
                    FsmState s;
                    if (byName.TryGetValue(ToolStates[i], out s))
                    {
                        if (toolStates == null) toolStates = new List<FsmState>();
                        toolStates.Add(s);
                    }
                }
            }

            if (targets.Count == 0 && toolStates == null) return;

            entry = new Entry { Id = id, Pm = pm, Path = path, ObjectId = objectId };
            foreach (FsmState r in resting) entry.Resting.Add(r);

            foreach (KeyValuePair<FsmState, List<FsmTransition>> kv in targets)
                AddHook(entry, states, byName, kv.Key, kv.Value.ToArray(), 15f);
            if (toolStates != null)
                for (int i = 0; i < toolStates.Count; i++)
                    if (!targets.ContainsKey(toolStates[i])) AddHook(entry, states, byName, toolStates[i], null, 4f);

            if (entry.Hooks.Count == 0) return;
            if (byId.ContainsKey(id))
            {
                RemoveHooks(entry);
                Log.Debug("Duplicate FSM id for " + path);
                return;
            }
            byId[id] = entry;
            byComponent[pm.GetInstanceID()] = entry;
        }

        static void AddHook(Entry entry, FsmState[] states, Dictionary<string, FsmState> byName, FsmState target, FsmTransition[] inputs, float maxDistance)
        {
            int index = Array.IndexOf(states, target);
            if (index < 0 || index > ushort.MaxValue) return;
            FsmStateAction[] actions = SafeActions(target);
            if (actions == null) return;
            for (int i = 0; i < actions.Length; i++) if (actions[i] is FsmSyncHook) return; // already hooked

            Hook h = new Hook
            {
                Entry = entry,
                State = target,
                StateIndex = (ushort)index,
                InputTransitions = inputs,
                MaxDistance = maxDistance
            };

            // What runs after entering: this state and every state that follows without waiting.
            List<FsmStateAction> chain = new List<FsmStateAction>();
            List<FsmGameObject> spawners = new List<FsmGameObject>();
            HashSet<FsmState> seen = new HashSet<FsmState>();
            Queue<FsmState> open = new Queue<FsmState>();
            open.Enqueue(target);
            seen.Add(target);
            while (open.Count > 0 && seen.Count <= 32)
            {
                FsmState s = open.Dequeue();
                FsmStateAction[] acts = SafeActions(s);
                if (acts != null)
                {
                    for (int i = 0; i < acts.Length; i++)
                    {
                        FsmStateAction a = acts[i];
                        if (a == null || a is FsmSyncHook) continue;
                        chain.Add(a);
                        if (a.GetType().Name == "CreateObject")
                        {
                            FsmGameObject store = GetField(a, "storeObject") as FsmGameObject;
                            if (store != null && !store.IsNone) spawners.Add(store);
                        }
                    }
                }
                FsmTransition[] tr = s.Transitions;
                if (tr == null) continue;
                for (int t = 0; t < tr.Length; t++)
                {
                    FsmState to;
                    if (tr[t] == null || !byName.TryGetValue(tr[t].ToState ?? "", out to)) continue;
                    if (seen.Contains(to) || entry.Resting.Contains(to)) continue;
                    seen.Add(to);
                    open.Enqueue(to);
                }
            }
            h.Chain = chain.ToArray();
            h.Spawners = spawners.ToArray();
            h.ChainRefs = new RefField[h.Chain.Length][];
            h.PlayerWrites = new HashSet<FsmStateAction>();
            for (int i = 0; i < h.Chain.Length; i++)
            {
                FsmStateAction a = h.Chain[i];
                h.ChainRefs[i] = RefFieldsOf(a.GetType());
                if (WritesPlayerGlobal(a)) h.PlayerWrites.Add(a);
            }

            // Inject the hook as the very first action.
            FsmStateAction[] withHook = new FsmStateAction[actions.Length + 1];
            withHook[0] = new FsmSyncHook { Hook = h };
            Array.Copy(actions, 0, withHook, 1, actions.Length);
            target.Actions = withHook;
            entry.Hooks.Add(h);
            hookedStates++;
        }

        static void RemoveHooks(Entry e)
        {
            for (int i = 0; i < e.Hooks.Count; i++)
            {
                FsmState s = e.Hooks[i].State;
                if (s == null) continue;
                try
                {
                    FsmStateAction[] acts = s.Actions;
                    if (acts == null) continue;
                    List<FsmStateAction> keep = new List<FsmStateAction>(acts.Length);
                    for (int a = 0; a < acts.Length; a++) if (!(acts[a] is FsmSyncHook)) keep.Add(acts[a]);
                    if (keep.Count != acts.Length) s.Actions = keep.ToArray();
                }
                catch (Exception) { }
            }
        }

        static FsmStateAction[] SafeActions(FsmState s)
        {
            try { return s.Actions; }
            catch (Exception) { return null; }
        }

        /// <summary>Events that an action in this state sends because of player input.</summary>
        static HashSet<string> InputEventsOf(FsmStateAction[] actions, out bool waits)
        {
            HashSet<string> ev = new HashSet<string>();
            waits = false;
            if (actions == null) return ev;
            bool reader = false;
            for (int i = 0; i < actions.Length; i++)
            {
                FsmStateAction a = actions[i];
                if (a == null) continue;
                string tn = a.GetType().Name;
                switch (tn)
                {
                    case "GetMouseButtonDown":
                    case "GetMouseButtonUp":
                    case "GetButtonDown":
                    case "GetButtonUp":
                    case "GetKeyDown":
                    case "GetKeyUp":
                        AddEvent(ev, GetField(a, "sendEvent"));
                        waits = true;
                        break;
                    case "MousePickEvent":
                        AddEvent(ev, GetField(a, "mouseDown"));
                        AddEvent(ev, GetField(a, "mouseUp"));
                        waits = true;
                        break;
                    case "CInputGetButton":
                        AddEvent(ev, GetField(a, "buttonDownEvent"));
                        AddEvent(ev, GetField(a, "buttonUpEvent"));
                        AddEvent(ev, GetField(a, "buttonPressedEvent"));
                        waits = true;
                        break;
                    case "GetAxis":
                    case "GetAxisRaw":
                    case "CInputGetAxis":
                    case "CInputGetAxisRaw":
                    case "GetKey":
                    case "GetMouseButton":
                    case "GetButton":
                        reader = true;
                        waits = true;
                        break;
                    case "TriggerEvent":
                    case "CollisionEvent":
                    case "MousePick":
                        waits = true;
                        break;
                }
            }
            if (reader)
            {
                // Scroll wheel / held keys: the compare that follows turns input into events.
                for (int i = 0; i < actions.Length; i++)
                {
                    FsmStateAction a = actions[i];
                    if (a == null) continue;
                    string tn = a.GetType().Name;
                    if (tn.IndexOf("Compare", StringComparison.Ordinal) < 0 && tn.IndexOf("Test", StringComparison.Ordinal) < 0 &&
                        tn.IndexOf("Switch", StringComparison.Ordinal) < 0) continue;
                    FieldInfo[] fs = EventFieldsOf(a.GetType());
                    for (int f = 0; f < fs.Length; f++) AddEvent(ev, fs[f].GetValue(a));
                }
            }
            return ev;
        }

        static void AddEvent(HashSet<string> set, object fsmEvent)
        {
            FsmEvent e = fsmEvent as FsmEvent;
            if (e != null && !string.IsNullOrEmpty(e.Name)) set.Add(e.Name);
        }

        static object GetField(object o, string name)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return f != null ? f.GetValue(o) : null;
        }

        static FieldInfo[] EventFieldsOf(Type t)
        {
            FieldInfo[] r;
            if (eventFieldCache.TryGetValue(t, out r)) return r;
            List<FieldInfo> list = new List<FieldInfo>();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (f.FieldType == typeof(FsmEvent)) list.Add(f);
            return eventFieldCache[t] = list.ToArray();
        }

        static FieldInfo[] VarFieldsOf(Type t)
        {
            FieldInfo[] r;
            if (varFieldCache.TryGetValue(t, out r)) return r;
            List<FieldInfo> list = new List<FieldInfo>();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (typeof(NamedVariable).IsAssignableFrom(f.FieldType)) list.Add(f);
            return varFieldCache[t] = list.ToArray();
        }

        static RefField[] RefFieldsOf(Type t)
        {
            RefField[] r;
            if (refFieldCache.TryGetValue(t, out r)) return r;
            List<RefField> list = new List<RefField>();
            if (!IsReadOnlyAction(t.Name))
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (f.FieldType == typeof(FsmOwnerDefault)) list.Add(new RefField { Field = f, Kind = 0 });
                    else if (f.FieldType == typeof(FsmGameObject)) list.Add(new RefField { Field = f, Kind = 1 });
                    else if (f.FieldType == typeof(FsmEventTarget)) list.Add(new RefField { Field = f, Kind = 2 });
                }
            }
            return refFieldCache[t] = list.ToArray();
        }

        static bool IsReadOnlyAction(string tn)
        {
            return tn.StartsWith("Get", StringComparison.Ordinal) || tn.IndexOf("Compare", StringComparison.Ordinal) >= 0 ||
                   tn.IndexOf("Test", StringComparison.Ordinal) >= 0 || tn.IndexOf("Switch", StringComparison.Ordinal) >= 0 ||
                   tn.IndexOf("Changed", StringComparison.Ordinal) >= 0 || tn.IndexOf("IsNull", StringComparison.Ordinal) >= 0 ||
                   tn.IndexOf("AllTrue", StringComparison.Ordinal) >= 0 || tn.IndexOf("AnyTrue", StringComparison.Ordinal) >= 0 ||
                   tn.IndexOf("NoneTrue", StringComparison.Ordinal) >= 0 || tn.IndexOf("Distance", StringComparison.Ordinal) >= 0 ||
                   tn.IndexOf("Raycast", StringComparison.Ordinal) >= 0 || tn == "Wait" || tn == "MousePickEvent" || tn == "TriggerEvent";
        }

        /// <summary>True if the action writes one of the local player's own variables (money, needs, HUD, hand...).</summary>
        static bool WritesPlayerGlobal(FsmStateAction a)
        {
            if (playerGlobals.Count == 0) return false;
            string tn = a.GetType().Name;
            bool readOnly = IsReadOnlyAction(tn) && !tn.StartsWith("Get", StringComparison.Ordinal);
            FieldInfo[] fs = VarFieldsOf(a.GetType());
            for (int i = 0; i < fs.Length; i++)
            {
                NamedVariable v = fs[i].GetValue(a) as NamedVariable;
                if (v == null || !playerGlobals.Contains(v)) continue;
                string fn = fs[i].Name;
                if (fn.StartsWith("store", StringComparison.OrdinalIgnoreCase)) return true;
                if (readOnly) continue;
                if (fn.EndsWith("Variable", StringComparison.OrdinalIgnoreCase) || fn == "variable") return true;
            }
            return false;
        }

        /// <summary>True if, right now, the action would act on the local player's body, camera or hand.</summary>
        static bool TargetsPlayer(FsmStateAction a, RefField[] refs)
        {
            Transform player = LocalPlayer.Player;
            if (player == null || refs == null) return false;
            for (int i = 0; i < refs.Length; i++)
            {
                object v;
                try { v = refs[i].Field.GetValue(a); }
                catch { continue; }
                if (v == null) continue;
                GameObject go = null;
                switch (refs[i].Kind)
                {
                    case 0:
                        go = OwnerDefaultObject((FsmOwnerDefault)v);
                        break;
                    case 1:
                        go = ((FsmGameObject)v).Value;
                        break;
                    case 2:
                        FsmEventTarget et = (FsmEventTarget)v;
                        string target = et.target.ToString();
                        if (target == "BroadcastAll") return true;
                        if (target == "GameObject" || target == "GameObjectFSM") go = OwnerDefaultObject(et.gameObject);
                        else if (target == "FSMComponent" && et.fsmComponent != null) go = et.fsmComponent.gameObject;
                        break;
                }
                if (go != null && (go.transform == player || go.transform.IsChildOf(player))) return true;
            }
            return false;
        }

        static GameObject OwnerDefaultObject(FsmOwnerDefault od)
        {
            if (od == null) return null;
            if (od.OwnerOption.ToString() == "UseOwner") return null; // our FSMs never live on the player
            return od.GameObject != null ? od.GameObject.Value : null;
        }

        // ================================================================ dynamic objects

        /// <summary>Hooks the state machines of an object created after loading (a removed part, a bought item).</summary>
        static void RegisterObjectFsms(NetObject o)
        {
            if (o == null || !o.Alive) return;
            PlayMakerFSM[] list = o.Go.GetComponentsInChildren<PlayMakerFSM>(true);
            if (list.Length == 0) return;
            List<KeyValuePair<string, PlayMakerFSM>> keyed = new List<KeyValuePair<string, PlayMakerFSM>>(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                PlayMakerFSM pm = list[i];
                if (pm == null || byComponent.ContainsKey(pm.GetInstanceID())) continue;
                string rel = GameUtil.RelativePath(o.Go.transform, pm.transform);
                keyed.Add(new KeyValuePair<string, PlayMakerFSM>(rel + "|" + pm.FsmName + "|" + GameUtil.SiblingChain(pm.transform) + "|" + ComponentIndex(pm), pm));
            }
            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> dup = new Dictionary<string, int>();
            for (int i = 0; i < keyed.Count; i++)
            {
                PlayMakerFSM pm = keyed[i].Value;
                string rel = GameUtil.RelativePath(o.Go.transform, pm.transform);
                string key = rel + "|" + pm.FsmName;
                int n;
                dup.TryGetValue(key, out n);
                dup[key] = n + 1;
                string fullPath = GameUtil.PathOf(pm.transform);
                if (Excluded(pm, fullPath)) continue;
                string idKey = "obj" + o.Id + ":" + key + "#" + n;
                uint id = (GameUtil.Fnv1a(idKey) & 0x7FFFFFFFu) | 0x80000000u;
                while (byId.ContainsKey(id)) { idKey += "~"; id = (GameUtil.Fnv1a(idKey) & 0x7FFFFFFFu) | 0x80000000u; }
                try { TryRegister(pm, id, fullPath, o.Id); }
                catch (Exception e) { Log.Debug("Skipped FSM " + fullPath + ": " + e.Message); }
            }
        }

        // ================================================================ local events

        /// <summary>Called by the injected hook whenever a synced state is entered here.</summary>
        internal static void OnHookEntered(Hook h)
        {
            if (h == null || applyingRemote || !ready || !MP.InGame) return;
            if (Session.Active && !Session.Connected) return;
            if (GameSession.CatchingUp) return;
            Entry e = h.Entry;
            if (!e.Alive) return;
            Fsm fsm = e.Pm.Fsm;

            // Only entries caused by *our* player: through an input transition, near us.
            if (h.InputTransitions != null)
            {
                FsmTransition last = fsm.LastTransition;
                if (last == null || Array.IndexOf(h.InputTransitions, last) < 0) return;
            }
            Transform player = LocalPlayer.Player;
            if (player != null)
            {
                float max = h.MaxDistance;
                Vector3 d = e.Pm.transform.position - player.position;
                if (d.sqrMagnitude > max * max && !InLocalVehicle(e.Pm.transform)) return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - secondStart > 1f) { secondStart = now; sentThisSecond = 0; }
            if (++sentThisSecond > MaxEventsPerSecond)
            {
                if (Once.First("fsm-rate")) Log.Warn("Too many world events per second - some are not being shared (" + e.Path + ").");
                return;
            }

            uint serial = ++localSerial;
            byte origin = Session.LocalSlot;
            Binding[] binds = CollectBindings(fsm);

            NetWriter w = Session.Begin(Msg.FsmState);
            WriteEvent(w, e.Id, h.StateIndex, origin, serial, binds);
            if (Session.IsAuthority)
            {
                WorldLog.Append(w);
                Session.BroadcastInGame(true, Channel.Reliable);
            }
            else Session.SendToHost(true, Channel.Reliable);
            if (Log.Verbose) Log.Debug("World event: " + e.Path + " -> " + h.State.Name);
            Bots.MirrorReach(e.Pm.transform.position);

            if (h.Spawners.Length > 0)
            {
                GameObject[] before = new GameObject[h.Spawners.Length];
                for (int i = 0; i < before.Length; i++) before[i] = h.Spawners[i].Value;
                localLinks.Add(new LocalLink { Hook = h, Before = before, Done = new bool[before.Length], Serial = serial, Frame = Time.frameCount });
            }
        }

        static bool InLocalVehicle(Transform t)
        {
            NetVehicle v = LocalPlayer.PassengerOf;
            if (v == null) v = VehicleSync.FindByChild(LocalPlayer.Player);
            return v != null && v.Root != null && t.IsChildOf(v.Root);
        }

        static Binding[] CollectBindings(Fsm fsm)
        {
            FsmGameObject[] vars = fsm.Variables != null ? fsm.Variables.GameObjectVariables : null;
            if (vars == null || vars.Length == 0) return null;
            List<Binding> list = null;
            for (int i = 0; i < vars.Length && i < 255; i++)
            {
                GameObject go = vars[i] != null ? vars[i].Value : null;
                if (go == null) continue;
                NetObject o = ObjectSync.Of(go);
                if (o == null) continue;
                if (list == null) list = new List<Binding>(2);
                list.Add(new Binding { Var = (byte)i, Object = o.Id });
                if (list.Count >= 16) break;
            }
            return list != null ? list.ToArray() : null;
        }

        static void WriteEvent(NetWriter w, uint id, ushort state, byte origin, uint serial, Binding[] binds)
        {
            w.UInt(id);
            w.UShort(state);
            w.Byte(origin);
            w.UInt(serial);
            int n = binds != null ? binds.Length : 0;
            w.Byte((byte)n);
            for (int i = 0; i < n; i++)
            {
                w.Byte(binds[i].Var);
                w.UInt(binds[i].Object);
            }
        }

        /// <summary>Objects our own action created get the id every game derives from the event.</summary>
        static void ProcessLocalLinks()
        {
            if (localLinks.Count == 0) return;
            int frame = Time.frameCount;
            for (int i = localLinks.Count - 1; i >= 0; i--)
            {
                LocalLink l = localLinks[i];
                Hook h = l.Hook;
                if (!h.Entry.Alive) { localLinks.RemoveAt(i); continue; }
                bool last = frame - l.Frame >= 2;
                for (int k = 0; k < h.Spawners.Length; k++)
                {
                    if (l.Done[k]) continue;
                    GameObject now = h.Spawners[k].Value;
                    if (now == null || now == l.Before[k]) continue;
                    l.Done[k] = true;
                    uint id = SpawnId(Session.LocalSlot, l.Serial, k);
                    NetObject o = ObjectSync.Link(now, id, Session.LocalSlot);
                    if (o == null) continue;
                    // Tell everyone, in case their copy of the event did not create it.
                    NetWriter w = Session.Begin(Msg.FsmSpawn);
                    w.UInt(id);
                    w.String(o.Name);
                    w.Vector3(now.transform.position);
                    w.Quaternion(now.transform.rotation);
                    w.Byte(Session.LocalSlot);
                    if (Session.IsAuthority)
                    {
                        WorldLog.Append(w);
                        Session.BroadcastInGame(true, Channel.Reliable);
                    }
                    else Session.SendToHost(true, Channel.Reliable);
                }
                if (last) localLinks.RemoveAt(i);
            }
        }

        static uint SpawnId(byte origin, uint serial, int index)
        {
            uint h = GameUtil.Fnv1a("spawn:" + origin + ":" + serial + ":" + index);
            return 0xC0000000u | (h & 0x3FFFFFFFu);
        }

        // ================================================================ remote events

        static void OnFsmState(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            ushort state = r.UShort();
            byte origin = r.Byte();
            uint serial = r.UInt();
            int n = r.Byte();
            Binding[] binds = n > 0 ? new Binding[n] : null;
            for (int i = 0; i < n; i++) binds[i] = new Binding { Var = r.Byte(), Object = r.UInt() };
            if (from == null) return;

            if (Session.IsHost)
            {
                if (from.IsLocal) return;
                origin = from.Slot; // a client can only speak for itself
                NetWriter w = Session.Begin(Msg.FsmState);
                WriteEvent(w, id, state, origin, serial, binds);
                WorldLog.Append(w);
                Session.BroadcastInGame(true, Channel.Reliable, from);
            }
            if (!MP.InGame) return;

            Entry e;
            if (!byId.TryGetValue(id, out e) || !e.Alive)
            {
                if (Once.First("fsm-missing-" + id)) Log.Debug("World event for an unknown state machine " + id + " (ignored).");
                return;
            }
            Pending p = new Pending { State = state, Origin = origin, Serial = serial, Bindings = binds, Received = Time.realtimeSinceStartup };
            if (e.Queue != null && e.Queue.Count > 0)
            {
                Enqueue(e, p);
                return;
            }
            if (!IsReady(e)) { Enqueue(e, p); return; }
            Apply(e, p);
        }

        static void Enqueue(Entry e, Pending p)
        {
            if (e.Queue == null) e.Queue = new Queue<Pending>();
            if (e.Queue.Count >= 512) e.Queue.Dequeue();
            e.Queue.Enqueue(p);
            if (!queued.Contains(e)) queued.Add(e);
        }

        static bool IsActive(Entry e)
        {
            return e.Alive && e.Pm.enabled && e.Pm.gameObject.activeInHierarchy;
        }

        /// <summary>Is the FSM idle (waiting for a player), so we can safely move it?</summary>
        static bool IsReady(Entry e)
        {
            if (!IsActive(e)) return false;
            Fsm f = e.Pm.Fsm;
            if (!f.Started) return false;
            FsmState s = f.ActiveState;
            if (s == null) return true;
            if (e.Resting.Contains(s)) return true;
            for (int i = 0; i < e.Hooks.Count; i++) if (e.Hooks[i].State == s) return true;
            return false;
        }

        static void ProcessQueues()
        {
            if (queued.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            int budget = GameSession.CatchingUp ? 400 : 60;
            for (int i = queued.Count - 1; i >= 0 && budget > 0; i--)
            {
                Entry e = queued[i];
                if (!e.Alive || e.Queue == null || e.Queue.Count == 0)
                {
                    if (e.Queue != null) e.Queue.Clear();
                    queued.RemoveAt(i);
                    continue;
                }
                if (!IsActive(e)) { e.QueueActiveSince = -1f; continue; }
                if (e.QueueActiveSince < 0f) e.QueueActiveSince = now;
                bool force = now - e.QueueActiveSince > RemoteWaitLimit;
                while (e.Queue.Count > 0 && budget > 0 && (force || IsReady(e)))
                {
                    Apply(e, e.Queue.Dequeue());
                    budget--;
                    force = false;
                }
                if (e.Queue.Count == 0) { queued.RemoveAt(i); e.QueueActiveSince = -1f; }
            }
        }

        static void Apply(Entry e, Pending p)
        {
            PlayMakerFSM pm = e.Pm;
            Fsm fsm = pm.Fsm;
            FsmState[] states = fsm.States;
            if (states == null || p.State >= states.Length) return;
            FsmState state = states[p.State];
            Hook h = null;
            for (int i = 0; i < e.Hooks.Count; i++) if (e.Hooks[i].State == state) { h = e.Hooks[i]; break; }

            // Point the FSM at the same shared objects the other player's FSM was using.
            if (p.Bindings != null && fsm.Variables != null)
            {
                FsmGameObject[] vars = fsm.Variables.GameObjectVariables;
                for (int i = 0; i < p.Bindings.Length; i++)
                {
                    int vi = p.Bindings[i].Var;
                    if (vars == null || vi >= vars.Length || vars[vi] == null) continue;
                    NetObject o = ObjectSync.ById(p.Bindings[i].Object);
                    vars[vi].Value = o != null && o.Alive ? o.Go : null;
                }
            }

            GameObject[] before = null;
            List<FsmStateAction> disabled = null;
            if (h != null)
            {
                if (h.Spawners.Length > 0)
                {
                    before = new GameObject[h.Spawners.Length];
                    for (int i = 0; i < before.Length; i++) before[i] = h.Spawners[i].Value;
                }
                for (int i = 0; i < h.Chain.Length; i++)
                {
                    FsmStateAction a = h.Chain[i];
                    if (a == null || !a.Enabled) continue;
                    if (h.PlayerWrites.Contains(a) || TargetsPlayer(a, h.ChainRefs[i]))
                    {
                        if (disabled == null) disabled = new List<FsmStateAction>();
                        a.Enabled = false;
                        disabled.Add(a);
                    }
                }
            }

            applyingRemote = true;
            try
            {
                GameUtil.ForceState(pm, state);
            }
            finally
            {
                applyingRemote = false;
            }

            // Show the other player reaching for what they used.
            if (!GameSession.CatchingUp)
            {
                NetPlayer who = Session.BySlot(p.Origin);
                if (who != null && !who.IsLocal && who.Avatar != null && who.Avatar.Visible &&
                    (who.Avatar.HeadPosition - pm.transform.position).sqrMagnitude < 9f)
                    who.Avatar.Reach(pm.transform.position);
            }

            if (h != null && before != null)
            {
                for (int k = 0; k < h.Spawners.Length; k++)
                {
                    GameObject now = h.Spawners[k].Value;
                    if (now == null || now == before[k]) continue;
                    ObjectSync.Link(now, SpawnId(p.Origin, p.Serial, k), p.Origin);
                }
            }
            if (disabled != null)
            {
                if (IsReady(e)) SetEnabled(disabled, true); // finished in one go
                else reenable.Add(new Reenable { Entry = e, Actions = disabled, Until = Time.realtimeSinceStartup + 8f });
            }
        }

        static void SetEnabled(List<FsmStateAction> actions, bool enabled)
        {
            for (int i = 0; i < actions.Count; i++)
                if (actions[i] != null) actions[i].Enabled = enabled;
        }

        static void ProcessReenable()
        {
            if (reenable.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = reenable.Count - 1; i >= 0; i--)
            {
                Reenable r = reenable[i];
                if (!r.Entry.Alive || now > r.Until || IsReady(r.Entry))
                {
                    SetEnabled(r.Actions, true);
                    reenable.RemoveAt(i);
                }
            }
        }

        static void OnFsmSpawn(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            string name = r.String();
            Vector3 pos = r.Vector3();
            Quaternion rot = r.Quaternion();
            byte owner = r.Byte();
            if (from == null) return;
            if (Session.IsHost)
            {
                if (from.IsLocal) return;
                owner = from.Slot;
                NetWriter w = Session.Begin(Msg.FsmSpawn);
                w.UInt(id);
                w.String(name);
                w.Vector3(pos);
                w.Quaternion(rot);
                w.Byte(owner);
                WorldLog.Append(w);
                Session.BroadcastInGame(true, Channel.Reliable, from);
            }
            if (!MP.InGame) return;
            if (ObjectSync.ById(id) != null) return;
            ObjectSync.LinkOrCreate(id, name, pos, rot, owner);
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (!ready) return;
            ProcessLocalLinks();
            ProcessQueues();
            ProcessReenable();
        }

        public static void LateUpdate()
        {
            if (!ready) return;
            ProcessLocalLinks();
        }

        /// <summary>Everything a joining player needs is already in the world log.</summary>
        public static void SendCatchUp(NetPlayer p)
        {
        }
    }
}
