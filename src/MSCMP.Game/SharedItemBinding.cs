using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCMP.Core;
using UnityEngine;

namespace MSCMP.Integration
{
    // Resolves the installed game's persistent save references, never names/positions/order.
    // Special use and trigger actions remain gated until their own authority adapters exist.
    internal sealed class SharedItemBinding : IDisposable
    {
        internal readonly string Key, Archetype;
        internal readonly Rigidbody Body;
        internal readonly GameObject Object;
        internal ulong EntityId, AppliedRevision;
        private readonly Collider[] colliders;
        private readonly bool[] colliderEnabled;
        private readonly PlayMakerFSM[] machines;
        private readonly bool[] machineEnabled;
        private readonly Transform parent;
        private readonly int layer;
        private readonly bool kinematic;
        private bool changed;

        internal SharedItemBinding(string tag, string archetype, GameObject target)
        {
            Key = "msc-20171487:defaultES2File.txt:" + tag; Archetype = archetype; Object = target;
            if (target == null || !target.activeInHierarchy || target.layer != 19 || (target.tag != "PART" && target.tag != "ITEM"))
                throw new InvalidOperationException("Shared item is not a loaded, free pickup: " + tag);
            Rigidbody[] bodies = target.GetComponentsInChildren<Rigidbody>(true);
            if (bodies.Length != 1 || bodies[0].gameObject != target || bodies[0].isKinematic || target.GetComponentsInChildren<Joint>(true).Length != 0)
                throw new InvalidOperationException("Shared item requires its inspected single free body: " + tag);
            Body = bodies[0];
            if (!SharedWorldSession.Vector(Body.position).Within(100000) || !WorldVector.Finite(Body.mass) || Body.mass <= 0 || Body.mass > 200)
                throw new InvalidOperationException("Invalid shared item physics: " + tag);
            parent = target.transform.parent; layer = target.layer; kinematic = Body.isKinematic;
            colliders = target.GetComponentsInChildren<Collider>(true); colliderEnabled = new bool[colliders.Length];
            if (colliders.Length == 0 || colliders.Length > 64) throw new InvalidOperationException("Shared collider bound: " + tag);
            bool solid = false;
            for (int i = 0; i < colliders.Length; i++) { colliderEnabled[i] = colliders[i].enabled; solid |= colliderEnabled[i] && !colliders[i].isTrigger; }
            if (!solid) throw new InvalidOperationException("No pickup collider: " + tag);
            machines = target.GetComponentsInChildren<PlayMakerFSM>(true); machineEnabled = new bool[machines.Length];
            if (machines.Length > 128) throw new InvalidOperationException("Shared item FSM bound: " + tag);
            for (int i = 0; i < machines.Length; i++) machineEnabled[i] = machines[i].enabled;
        }
        internal void Begin(bool host)
        {
            changed = true;
            foreach (PlayMakerFSM fsm in machines) fsm.enabled = false;
            Body.isKinematic = !host;
            for (int i = 0; i < colliders.Length; i++) if (colliders[i].isTrigger) colliders[i].enabled = false;
        }
        internal bool Hit(Collider hit)
        { for (int i = 0; i < colliders.Length; i++) if (colliders[i] == hit && colliderEnabled[i] && !hit.isTrigger) return true; return false; }
        internal void SetHeld(bool held, bool host)
        {
            if (Body == null || Object == null) throw new InvalidOperationException("Mapped item was removed by unsupported game logic.");
            Body.isKinematic = held || !host;
            Object.layer = held ? 16 : layer;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null) throw new InvalidOperationException("Mapped item collider was removed.");
                colliders[i].enabled = !held && colliderEnabled[i] && !colliders[i].isTrigger;
            }
        }
        public void Dispose()
        {
            if (!changed) return; changed = false;
            if (Object != null) { Object.transform.parent = parent; Object.layer = layer; }
            if (Body != null) Body.isKinematic = kinematic;
            for (int i = 0; i < colliders.Length; i++) if (colliders[i] != null) colliders[i].enabled = colliderEnabled[i];
            for (int i = 0; i < machines.Length; i++) if (machines[i] != null) machines[i].enabled = machineEnabled[i];
        }

        internal static List<SharedItemBinding> Discover(SupportedItemBinding cup, out PlayMakerFSM saveItems)
        {
            List<SharedItemBinding> result = new List<SharedItemBinding>();
            result.Add(new SharedItemBinding("CoffeeCupPos", SupportedItemBinding.Archetype, cup.ItemObject));
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
            if (all.Length > 16000) throw new InvalidOperationException("Item catalogue discovery bound exceeded.");
            saveItems = null;
            foreach (UnityEngine.Object obj in all)
            {
                PlayMakerFSM fsm = obj as PlayMakerFSM;
                if (fsm != null && fsm.FsmName == "SaveItems" && StringValue(fsm, "UniqueTagAx") == "AxTransform")
                { if (saveItems != null) throw new InvalidOperationException("Ambiguous persistent item registry."); saveItems = fsm; }
            }
            if (saveItems == null || !saveItems.enabled || !saveItems.gameObject.activeInHierarchy || saveItems.FsmStates.Length != 5 || saveItems.ActiveStateName != "State 1")
                throw new InvalidOperationException("Wait for the persistent item registry to finish loading.");
            // Explicit audited subset: articulated hoist/jacks and fish factories require separate adapters.
            string[] variables = { "Axe", "Axe2", "Gasoline", "Diesel", "Sledgehammer", "Toolbox", "SaunaDipper", "SaunaDipper2", "SaunaBasket", "SaunaBasket2", "Sofa", "Lantern", "Camera", "WiringMess", "GarbageBarrel", "DiggingBar", "Notepad" };
            string[] tags = { "AxTransform", "Ax2Transform", "JerryCanGasTransform", "JerryCanDieselTransform", "SledgehammerTransform", "ToolboxTransform", "SaunaDipperTransform", "SaunaDipper2Transform", "SaunaBasketTransform", "SaunaBasket2Transform", "SofaTransform", "LanternTransform", "CameraTransform", "WiringMessTransform", "GarbageBarrelTransform", "DiggingBarTransform", "NotepadTransform" };
            for (int i = 0; i < variables.Length; i++)
            {
                GameObject target = ResolvePair(saveItems, tags[i], variables[i]);
                AddUnique(result, new SharedItemBinding(tags[i], "msc.loose." + variables[i].ToLowerInvariant(), target));
            }
            string[] localTags = { "GrillPos", "CoffeePanPos", "FlashlightTransform", "PortableRadioTransform" };
            string[] archetypes = { "msc.grill", "msc.coffee-pan", "msc.flashlight", "msc.portable-radio" };
            string[] names = { "Data", "Data", "Use", "Data" };
            int[] counts = { 5, 8, 12, 6 };
            string[] idle = { "State 1", "Idle|State 2", "Wait player|Wait button", "Idle" };
            for (int i = 0; i < localTags.Length; i++)
            {
                PlayMakerFSM found = null;
                foreach (UnityEngine.Object obj in all)
                {
                    PlayMakerFSM fsm = obj as PlayMakerFSM;
                    if (fsm != null && StringValue(fsm, "UniqueTagPos") == localTags[i])
                    { if (found != null) throw new InvalidOperationException("Ambiguous item: " + localTags[i]); found = fsm; }
                }
                if (found == null || found.FsmName != names[i] || found.FsmStates.Length != counts[i] || !found.enabled
                    || Array.IndexOf(idle[i].Split('|'), found.ActiveStateName) < 0)
                    throw new InvalidOperationException("Item has not reached its supported idle state: " + localTags[i]);
                AddUnique(result, new SharedItemBinding(localTags[i], archetypes[i], ResolvePair(found, localTags[i], null)));
            }
            return result;
        }
        private static void AddUnique(List<SharedItemBinding> items, SharedItemBinding value)
        { foreach (SharedItemBinding item in items) if (item.Key == value.Key || item.Object == value.Object) throw new InvalidOperationException("Ambiguous shared item mapping."); items.Add(value); }
        private static GameObject ResolvePair(PlayMakerFSM fsm, string tag, string variable)
        {
            GameObject save = ResolveAction(fsm, "Save game", "SaveTransform", "saveValue", tag, variable);
            GameObject load = ResolveAction(fsm, "Load game", "LoadTransform", "loadValue", tag, variable);
            if (save == null || load != save) throw new InvalidOperationException("Persistent save/load target mismatch: " + tag);
            return save;
        }
        private static GameObject ResolveAction(PlayMakerFSM fsm, string stateName, string type, string valueField, string tag, string variable)
        {
            GameObject result = null; int matches = 0;
            foreach (FsmState state in fsm.FsmStates)
            {
                if (state.Name != stateName) continue;
                if (state.Actions == null) throw new InvalidOperationException("Item persistence actions are not initialized.");
                foreach (FsmStateAction action in state.Actions)
                {
                    if (action == null || action.GetType().Name != type || action.GetType().Assembly.GetName().Name != "Assembly-CSharp") continue;
                    FsmString key = Field(action, "uniqueTag") as FsmString, file = Field(action, "saveFile") as FsmString;
                    if (key == null || !key.UseVariable || StringValue(fsm, key.Name) != tag) continue;
                    if (file == null || file.UseVariable || file.Value != "defaultES2File.txt") throw new InvalidOperationException("Unexpected item save contract.");
                    FsmOwnerDefault owner = Field(action, valueField) as FsmOwnerDefault;
                    if (owner == null) throw new InvalidOperationException("Missing item target contract.");
                    if (variable == null)
                    { if ((int)owner.OwnerOption != 0) throw new InvalidOperationException("Expected owner-bound item."); result = fsm.gameObject; }
                    else
                    {
                        if ((int)owner.OwnerOption != 1 || owner.GameObject == null || !owner.GameObject.UseVariable || owner.GameObject.Name != variable)
                            throw new InvalidOperationException("Item registry target changed: " + tag);
                        result = owner.GameObject.Value;
                    }
                    if (type == "LoadTransform" && !System.Object.Equals(Field(action, "loadFromResources"), false)) throw new InvalidOperationException("Unexpected item load source.");
                    matches++;
                }
            }
            if (matches != 1) throw new InvalidOperationException("Expected one initialized persistence action: " + tag);
            return result;
        }
        private static object Field(FsmStateAction action, string name)
        { FieldInfo field = action.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance); return field == null ? null : field.GetValue(action); }
        private static string StringValue(PlayMakerFSM fsm, string name)
        { string found = null; foreach (FsmString value in fsm.FsmVariables.StringVariables) if (value.Name == name) { if (found != null) throw new InvalidOperationException("Duplicate identity variable."); found = value.Value; } return found; }
    }
}
