using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MSCMP
{
    /// <summary>Helpers for finding things in the game scene and poking PlayMaker.</summary>
    internal static class GameUtil
    {
        // ------------------------------------------------------------ hierarchy

        /// <summary>Finds a transform by full path, including inactive objects.</summary>
        public static Transform FindPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            string rest = slash < 0 ? null : path.Substring(slash + 1);
            Transform root = FindRoot(rootName);
            if (root == null) return null;
            return rest == null ? root : root.Find(rest);
        }

        public static Transform FindRoot(string name)
        {
            GameObject go = GameObject.Find("/" + name);
            if (go != null) return go.transform;
            // Inactive roots are invisible to GameObject.Find.
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(GameObject));
            for (int i = 0; i < all.Length; i++)
            {
                GameObject g = all[i] as GameObject;
                if (g != null && g.transform.parent == null && g.name == name && IsSceneObject(g)) return g.transform;
            }
            return null;
        }

        static HashSet<int> sceneRoots;
        static float sceneRootsTime = -100f;

        /// <summary>True if the object lives in the loaded scene (not a prefab/asset kept in memory).</summary>
        public static bool IsSceneObject(GameObject go)
        {
            if (go == null) return false;
            if (go.hideFlags != HideFlags.None) return false;
            Transform root = go.transform.root;
            if (root.gameObject.activeInHierarchy) return true;
            if (sceneRoots == null || Time.realtimeSinceStartup - sceneRootsTime > 5f) RefreshSceneRoots();
            return sceneRoots.Contains(root.GetInstanceID());
        }

        static void RefreshSceneRoots()
        {
            sceneRoots = new HashSet<int>();
            sceneRootsTime = Time.realtimeSinceStartup;
            UnityEngine.Object[] ts = UnityEngine.Object.FindObjectsOfType(typeof(Transform));
            for (int i = 0; i < ts.Length; i++)
            {
                Transform t = ts[i] as Transform;
                if (t != null) sceneRoots.Add(t.root.GetInstanceID());
            }
            // Known roots of the GAME scene that start inactive.
            string[] inactiveRoots = { "PLAYER", "COPS", "JAIL", "COMBINE(350-400psi)" };
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(GameObject));
            for (int i = 0; i < all.Length; i++)
            {
                GameObject g = all[i] as GameObject;
                if (g == null || g.transform.parent != null || g.hideFlags != HideFlags.None) continue;
                for (int k = 0; k < inactiveRoots.Length; k++)
                    if (g.name == inactiveRoots[k]) sceneRoots.Add(g.transform.GetInstanceID());
            }
        }

        public static void InvalidateSceneCache() { sceneRoots = null; }

        /// <summary>Sibling indexes from the root down, e.g. "3.0.12": tells apart objects with equal paths.</summary>
        public static string SiblingChain(Transform t)
        {
            StringBuilder sb = new StringBuilder(32);
            List<int> idx = new List<int>(8);
            for (Transform c = t; c != null; c = c.parent) idx.Add(c.GetSiblingIndex());
            for (int i = idx.Count - 1; i >= 0; i--)
            {
                sb.Append(idx[i].ToString("D4"));
                if (i > 0) sb.Append('.');
            }
            return sb.ToString();
        }

        public static string PathOf(Transform t)
        {
            if (t == null) return "";
            StringBuilder sb = new StringBuilder(t.name);
            Transform p = t.parent;
            while (p != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, p.name);
                p = p.parent;
            }
            return sb.ToString();
        }

        /// <summary>Path of t relative to ancestor (empty if t == ancestor).</summary>
        public static string RelativePath(Transform ancestor, Transform t)
        {
            if (t == ancestor) return "";
            StringBuilder sb = new StringBuilder(t.name);
            Transform p = t.parent;
            while (p != null && p != ancestor)
            {
                sb.Insert(0, '/');
                sb.Insert(0, p.name);
                p = p.parent;
            }
            return p == ancestor ? sb.ToString() : null;
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }

        public static uint Fnv1a(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619;
                }
                return h;
            }
        }

        // ------------------------------------------------------------ PlayMaker

        static MethodInfo switchState, updateStateChanges;

        public static PlayMakerFSM GetFsm(GameObject go, string fsmName)
        {
            if (go == null) return null;
            PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
                if (fsms[i] != null && fsms[i].FsmName == fsmName) return fsms[i];
            return null;
        }

        public static FsmState GetState(PlayMakerFSM fsm, string name)
        {
            if (fsm == null || fsm.Fsm == null) return null;
            FsmState[] states = fsm.FsmStates;
            for (int i = 0; i < states.Length; i++)
                if (states[i].Name == name) return states[i];
            return null;
        }

        /// <summary>Forces an FSM into a state, running that state's actions exactly as if the
        /// game had transitioned there. PlayMaker 1.7 keeps SwitchState private.</summary>
        public static bool ForceState(PlayMakerFSM fsm, string stateName)
        {
            if (fsm == null || fsm.Fsm == null) return false;
            FsmState state = GetState(fsm, stateName);
            if (state == null) return false;
            return ForceState(fsm, state);
        }

        /// <summary>Forces an FSM into a state and then lets it run every transition that follows
        /// immediately (the same frame), just like PlayMaker does after a normal transition.</summary>
        public static bool ForceState(PlayMakerFSM fsm, FsmState state)
        {
            if (fsm == null || fsm.Fsm == null || state == null) return false;
            string stateName = state.Name;
            try
            {
                if (switchState == null)
                {
                    switchState = typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.NonPublic);
                    updateStateChanges = typeof(Fsm).GetMethod("UpdateStateChanges", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                if (switchState == null)
                {
                    Log.Error("PlayMaker SwitchState not found - world sync cannot apply events.");
                    return false;
                }
                if (!fsm.Fsm.Initialized) fsm.Fsm.InitData();
                switchState.Invoke(fsm.Fsm, new object[] { state });
                // A state that finishes right away queues its next state; run those now.
                if (updateStateChanges != null) updateStateChanges.Invoke(fsm.Fsm, null);
                return true;
            }
            catch (TargetInvocationException e)
            {
                Log.ExceptionOnce("ForceState " + fsm.FsmName + "/" + stateName, e.InnerException ?? e);
                return false;
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("ForceState " + fsm.FsmName + "/" + stateName, e);
                return false;
            }
        }

        public static FsmFloat GlobalFloat(string name)
        {
            try { return FsmVariables.GlobalVariables.FindFsmFloat(name); } catch { return null; }
        }

        public static FsmInt GlobalInt(string name)
        {
            try { return FsmVariables.GlobalVariables.FindFsmInt(name); } catch { return null; }
        }

        public static FsmBool GlobalBool(string name)
        {
            try { return FsmVariables.GlobalVariables.FindFsmBool(name); } catch { return null; }
        }

        public static FsmString GlobalString(string name)
        {
            try { return FsmVariables.GlobalVariables.FindFsmString(name); } catch { return null; }
        }

        public static FsmGameObject GlobalGameObject(string name)
        {
            try { return FsmVariables.GlobalVariables.FindFsmGameObject(name); } catch { return null; }
        }

        /// <summary>Shows a line of text in the middle of the screen exactly like the game's
        /// own interaction prompts ("ENTER DRIVING MODE").</summary>
        public static void SetInteractionText(string text)
        {
            FsmString s = GlobalString("GUIinteraction");
            if (s != null) s.Value = text ?? "";
        }

        public static void SetSubtitle(string text)
        {
            FsmString s = GlobalString("GUIsubtitle");
            if (s != null) s.Value = text ?? "";
        }

        public static FsmFloat FsmFloat(PlayMakerFSM fsm, string name)
        {
            try { return fsm != null ? fsm.FsmVariables.FindFsmFloat(name) : null; } catch { return null; }
        }

        public static FsmInt FsmInt(PlayMakerFSM fsm, string name)
        {
            try { return fsm != null ? fsm.FsmVariables.FindFsmInt(name) : null; } catch { return null; }
        }

        public static FsmBool FsmBool(PlayMakerFSM fsm, string name)
        {
            try { return fsm != null ? fsm.FsmVariables.FindFsmBool(name) : null; } catch { return null; }
        }

        public static FsmString FsmString(PlayMakerFSM fsm, string name)
        {
            try { return fsm != null ? fsm.FsmVariables.FindFsmString(name) : null; } catch { return null; }
        }

        public static FsmGameObject FsmGameObject(PlayMakerFSM fsm, string name)
        {
            try { return fsm != null ? fsm.FsmVariables.FindFsmGameObject(name) : null; } catch { return null; }
        }

        // ------------------------------------------------------------ misc

        static Dictionary<string, AnimationClip> clipCache;

        /// <summary>Finds one of the game's animation clips by name (they are shared by many NPCs).</summary>
        public static AnimationClip FindClip(string name)
        {
            if (clipCache == null)
            {
                clipCache = new Dictionary<string, AnimationClip>();
                UnityEngine.Object[] clips = Resources.FindObjectsOfTypeAll(typeof(AnimationClip));
                for (int i = 0; i < clips.Length; i++)
                {
                    AnimationClip c = clips[i] as AnimationClip;
                    if (c != null && !clipCache.ContainsKey(c.name)) clipCache[c.name] = c;
                }
            }
            AnimationClip clip;
            return clipCache.TryGetValue(name, out clip) && clip != null ? clip : null;
        }

        static Dictionary<string, Material> materialCache;

        public static Material FindMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (materialCache == null)
            {
                materialCache = new Dictionary<string, Material>();
                UnityEngine.Object[] mats = Resources.FindObjectsOfTypeAll(typeof(Material));
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i] as Material;
                    if (m != null && !materialCache.ContainsKey(m.name)) materialCache[m.name] = m;
                }
            }
            Material mat;
            return materialCache.TryGetValue(name, out mat) && mat != null ? mat : null;
        }

        /// <summary>Asset caches hold scene-specific objects; clear them when the scene changes.</summary>
        public static void ClearCaches()
        {
            clipCache = null;
            materialCache = null;
            sceneRoots = null;
        }

        public static float Now { get { return Time.realtimeSinceStartup; } }
    }
}
