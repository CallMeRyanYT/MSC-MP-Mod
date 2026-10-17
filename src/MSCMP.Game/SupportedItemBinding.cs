using System;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MSCMP.Integration
{
    // Static evidence: Steam build 20171487 / level2 39e5c8f...; authored Use FSM
    // persists its owner with UniqueTagTransform=CoffeeCupPos in defaultES2File.txt.
    // This binding only reads initialized runtime state. It never injects a hook,
    // initializes an action, sends an FSM event, or changes physics/persistence.
    internal sealed class SupportedItemBinding : IDisposable
    {
        internal const string AuthoredKey = "msc-20171487:defaultES2File.txt:CoffeeCupPos";
        internal const string Archetype = "msc.coffee-cup";
        private const string SaveTag = "CoffeeCupPos";
        private const int MaximumFsms = 16000;
        private readonly int generation;
        private PlayMakerFSM itemUse;
        private PlayMakerFSM pickup;
        private Rigidbody body;
        private BoxCollider collider;
        private FixedJoint joint;
        private FsmGameObject picked;
        private FsmGameObject aimed;
        private FsmGameObject pivot;
        private FsmBool handEmpty;
        private FsmFloat coffee;
        private FsmFloat caffeine;
        private FsmString identity;

        internal GameObject ItemObject { get { return itemUse == null ? null : itemUse.gameObject; } }
        internal PlayMakerFSM PickupFsm { get { return pickup; } }
        internal PlayMakerFSM ItemUseFsm { get { return itemUse; } }

        private SupportedItemBinding(int sceneGeneration, PlayMakerFSM item, PlayMakerFSM hand, Rigidbody itemBody, BoxCollider itemCollider, FixedJoint handJoint)
        {
            generation = sceneGeneration; itemUse = item; pickup = hand; body = itemBody; collider = itemCollider; joint = handJoint;
            identity = StringVariable(item, "UniqueTagTransform"); coffee = FloatVariable(item, "Coffee"); caffeine = FloatVariable(item, "Caffeine");
            picked = ObjectVariable(hand, "PickedObject"); aimed = ObjectVariable(hand, "RaycastHitObject"); pivot = ObjectVariable(hand, "ItemPivot"); handEmpty = BoolVariable(hand, "HandEmpty");
        }

        // A bounded one-time/manual discovery pass. Names, hierarchy paths, position,
        // instance IDs and discovery order are deliberately not lookup identities.
        internal static bool TryCreate(int sceneGeneration, out SupportedItemBinding binding, out string reason)
        {
            binding = null; reason = "Supported-item diagnostics require the active GAME scene.";
            if (sceneGeneration <= 0 || Application.loadedLevelName != "GAME") return false;
            try
            {
                UnityEngine.Object[] machines = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
                if (machines.Length > MaximumFsms) { reason = "FSM discovery bound exceeded; item uniqueness cannot be proved."; return false; }
                PlayMakerFSM cup = null, hand = null; int cupClaims = 0, activeHands = 0;
                for (int i = 0; i < machines.Length; i++)
                {
                    PlayMakerFSM machine = machines[i] as PlayMakerFSM;
                    if (machine == null) continue;
                    // Include inactive loaded objects/assets in duplicate detection. A false
                    // ambiguity disables the binding instead of guessing which instance is real.
                    FsmString[] strings = machine.FsmVariables.StringVariables;
                    if (strings != null) for (int j = 0; j < strings.Length; j++)
                        if (strings[j] != null && strings[j].Value == SaveTag) { cupClaims++; cup = machine; }
                    if (machine.FsmName == "PickUp" && machine.enabled && machine.gameObject.activeInHierarchy) { activeHands++; hand = machine; }
                }
                if (cupClaims != 1) { reason = "Expected exactly one loaded persistent CoffeeCupPos claim; observed " + cupClaims + "."; return false; }
                if (activeHands != 1) { reason = "Expected exactly one active PickUp FSM; observed " + activeHands + "."; return false; }
                if (!ValidateCup(cup, out reason) || !ValidateHand(hand, out reason)) return false;
                Rigidbody[] bodies = cup.GetComponents<Rigidbody>(); BoxCollider[] colliders = cup.GetComponents<BoxCollider>(); FixedJoint[] joints = hand.GetComponents<FixedJoint>();
                if (bodies.Length != 1 || colliders.Length != 1 || joints.Length != 1) { reason = "Expected one cup Rigidbody/BoxCollider and one hand FixedJoint."; return false; }
                binding = new SupportedItemBinding(sceneGeneration, cup, hand, bodies[0], colliders[0], joints[0]);
                reason = "CoffeeCupPos and initialized FSM contract bound for observation only; no pickup or release interception is enabled.";
                return true;
            }
            catch (Exception error)
            {
                reason = "Item contract inspection failed closed: " + error.GetType().Name + ".";
                return false;
            }
        }

        internal bool TryObserve(int sceneGeneration, out string observation)
        {
            observation = "Supported item binding is absent, stale, inactive, or changed.";
            if (sceneGeneration != generation || Application.loadedLevelName != "GAME" || itemUse == null || pickup == null || body == null || collider == null || joint == null
                || !itemUse.enabled || !pickup.enabled || !itemUse.gameObject.activeInHierarchy || !pickup.gameObject.activeInHierarchy
                || identity == null || identity.Value != SaveTag || picked == null || aimed == null || pivot == null || pivot.Value == null || handEmpty == null || coffee == null || caffeine == null
                || itemUse.GetComponent<Rigidbody>() != body || itemUse.GetComponent<BoxCollider>() != collider || pickup.GetComponent<FixedJoint>() != joint) return false;
            Vector3 position = body.position, velocity = body.velocity;
            if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z) || !Finite(velocity.x) || !Finite(velocity.y) || !Finite(velocity.z) || !Finite(body.mass) || body.mass <= 0 || !Finite(coffee.Value) || !Finite(caffeine.Value))
            { observation = "Item observation rejected non-finite physics/content values."; return false; }
            bool isPicked = picked.Value == itemUse.gameObject, isAimed = aimed.Value == itemUse.gameObject;
            bool isParented = itemUse.transform.parent == pivot.Value.transform, isConnected = joint.connectedBody == body;
            bool held = isPicked && !handEmpty.Value && isParented && isConnected && itemUse.gameObject.layer == 16;
            bool free = !isPicked && !isParented && !isConnected && itemUse.gameObject.layer == 19 && !body.isKinematic;
            string relationship = held ? "held-consistent" : free ? "free-consistent" : "transition-or-mismatch";
            // Captured references survive renaming/reparenting. Text is bounded and contains no save contents.
            observation = "key=" + AuthoredKey + " relation=" + relationship + " pickup=" + Label(pickup.ActiveStateName) + " use=" + Label(itemUse.ActiveStateName)
                + " aimed=" + isAimed + " picked=" + isPicked + " handEmpty=" + handEmpty.Value + " pivotParent=" + isParented + " jointConnected=" + isConnected
                + " layer=" + itemUse.gameObject.layer + " tagPART=" + (itemUse.gameObject.tag == "PART") + " kinematic=" + body.isKinematic + " collider=" + collider.enabled
                + " mass=" + body.mass.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " coffee=" + coffee.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " caffeine=" + caffeine.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "; observation only; cup use/pour/drink and game save isolation are unsupported";
            return true;
        }

        // This reports a candidate for a future boundary probe, not an authority grant.
        // It neither consumes input nor performs a second independent raycast.
        internal bool TryGetPickupBoundaryCandidate(int sceneGeneration, out GameObject candidate, out string reason)
        {
            candidate = null;
            if (!TryObserve(sceneGeneration, out reason)) return false;
            if (!ValidateCup(itemUse, out reason) || !ValidateHand(pickup, out reason)) return false;
            if (pickup.ActiveStateName != "Look for object") { reason = "Pickup FSM is not at its pre-click Look for object boundary."; return false; }
            if (!handEmpty.Value || picked.Value != null || joint.connectedBody != null) { reason = "The native hand is already occupied or has a stale picked/joint reference."; return false; }
            if (aimed.Value != itemUse.gameObject) { reason = "The native one-metre pickup ray currently targets a different object."; return false; }
            if (itemUse.gameObject.tag != "PART" || itemUse.gameObject.layer != 19 || body.isKinematic || !collider.enabled || collider.isTrigger || itemUse.transform.parent == pivot.Value.transform)
            { reason = "Cup is not an active free PART with the inspected ground collider/layer contract."; return false; }
            if (!CupIdle(itemUse.ActiveStateName)) { reason = "Cup is loading, saving, pouring, drinking, or in another unsupported use transition."; return false; }
            FsmString vehicle = Field(State(pickup, "Check moped?").Actions[0], "stringVariable") as FsmString;
            if (vehicle == null || vehicle.Value == "Jonnez") { reason = "Moped pickup/rack behavior is outside the candidate slice."; return false; }
            // This is a guard against the game's own name-sensitive motor branch, never identity lookup.
            if (itemUse.gameObject.name == "block(Clone)") { reason = "Cup rename would enter the game's unrelated motor branch."; return false; }
            candidate = itemUse.gameObject;
            reason = "Candidate observed before State 1; PART route is Set pivot then Part picked. Runtime interception remains unverified and disabled.";
            return true;
        }

        internal bool Matches(GameObject candidate) { return itemUse != null && candidate == itemUse.gameObject; }
        public void Dispose()
        {
            itemUse = null; pickup = null; body = null; collider = null; joint = null;
            picked = null; aimed = null; pivot = null; handEmpty = null; identity = null; coffee = null; caffeine = null;
        }

        private static bool ValidateCup(PlayMakerFSM cup, out string reason)
        {
            reason = "Persistent cup owner is inactive or its expected Use/identity/content contract is missing.";
            if (cup == null || cup.FsmName != "Use" || !cup.enabled || !cup.gameObject.activeInHierarchy || cup.FsmStates == null || cup.FsmStates.Length != 14) return false;
            if (!StringValue(cup, "UniqueTagTransform", SaveTag) || !StringValue(cup, "UniqueTagCoffee", "CoffeeCupCoffee") || !StringValue(cup, "UniqueTagCaffeine", "CoffeeCupCaffeine") || FloatVariable(cup, "Coffee") == null || FloatVariable(cup, "Caffeine") == null) return false;
            FsmState save = State(cup, "Save game"), load = State(cup, "Load game"), data = State(cup, "Data"), exists = State(cup, "Check data"), wait = State(cup, "Wait button");
            if (!Contract(save, new string[] { "SaveFloat", "SaveFloat", "SaveTransform" }) || !Contract(load, new string[] { "LoadFloat", "LoadFloat", "LoadTransform", "SetScale" })
                || !Contract(data, new string[] { "FloatClamp", "FloatClamp", "FloatOperator", "SetPosition", "SetColorRGBA", "SetMaterialColor", "SetTag", "Wait" })
                || !Contract(exists, new string[] { "Exists" }) || !Contract(wait, new string[] { "MousePickEvent", "SetBoolValue", "SetStringValue", "GetButtonDown", "GetDistance", "FloatCompare" }, 2))
            { reason = "Cup save/load actions are uninitialized or differ from the inspected contract; no actions were loaded by this probe."; return false; }
            FsmStateAction saveTransform = save.Actions[2], loadTransform = load.Actions[2];
            if (!TagAction(saveTransform, "saveValue") || !TagAction(loadTransform, "loadValue") || !FieldBoolean(loadTransform, "loadFromResources", false)
                || !StringField(data.Actions[6], "tag", "PART", false) || !OwnerField(data.Actions[6], "gameObject")
                || !StringField(exists.Actions[0], "uniqueTag", "UniqueTagTransform", true) || !StringField(exists.Actions[0], "saveFile", "defaultES2File.txt", false)
                || !Transition(save, "FINISHED", "Load game") || !Transition(load, "FINISHED", "Data")
                || !Transition(wait, "USE", "Check drink") || !Transition(wait, "POUR", "Cup full?"))
            { reason = "Cup persistence actions no longer bind their owner to defaultES2File.txt/CoffeeCupPos."; return false; }
            reason = "Cup persistent identity contract matches."; return true;
        }

        private static bool ValidateHand(PlayMakerFSM hand, out string reason)
        {
            reason = "Active pickup FSM is missing required variables or its inspected 37-state layout.";
            if (hand == null || hand.FsmStates == null || hand.FsmStates.Length != 37 || ObjectVariable(hand, "PickedObject") == null || ObjectVariable(hand, "RaycastHitObject") == null || ObjectVariable(hand, "ItemPivot") == null || ObjectVariable(hand, "ItemPivot").Value == null || BoolVariable(hand, "HandEmpty") == null) return false;
            FsmState start = State(hand, "State 1"), pivotState = State(hand, "Set pivot 2"), item = State(hand, "Item picked"), drop = State(hand, "Drop part"), throwDrop = State(hand, "Drop part 2"), throwing = State(hand, "Throw part"), look = State(hand, "Look for object");
            FsmState partPivot = State(hand, "Set pivot"), part = State(hand, "Part picked"), checkPart = State(hand, "Check if Part"), checkItem = State(hand, "Check if Item"), moped = State(hand, "Check moped?"), isMoped = State(hand, "Is Jonnez");
            string[] release = new string[] { "SetIsKinematic", "SetIsKinematic", "SetParent", "SetLayer", "SetJointConnectedBody", "SetVelocity" };
            if (!Contract(start, new string[] { "ActivateGameObject", "SetGameObject" })
                || !Contract(pivotState, new string[] { "SetBoolValue", "GetPosition", "SetPosition", "SetPosition", "SetLayer", "SetParent", "SetJointConnectedBody" })
                || !Contract(item, new string[] { "SetVector3Value", "GetMouseButtonDown", "GetMouseButtonDown", "GetKeyDown", "GetButtonDown" })
                || !Contract(drop, release) || !Contract(throwDrop, release)
                || !Contract(throwing, new string[] { "GetMass", "FloatAdd", "FloatMultiply", "SetJointConnectedBody", "SetVector3Value", "Vector3Normalize", "FloatOperator", "Vector3Multiply", "AddForce" })
                || !Contract(look, new string[] { "SetVector3Value", "ActivateGameObject", "SetBoolValue", "MousePick", "GetMouseButtonDown", "GetName", "GetStringLength", "IntOperator", "IntClamp", "GetSubstring" })
                || !Contract(partPivot, new string[] { "SetBoolValue", "GetPosition", "SetPosition", "SetPosition", "GetName", "StringCompare" })
                || !Contract(part, new string[] { "SetVector3Value", "SetLayer", "SetParent", "SetJointConnectedBody", "GetMouseButtonDown", "GetMouseButtonDown", "GetKeyDown" })
                || !Contract(checkPart, new string[] { "GameObjectCompareTag" }) || !Contract(checkItem, new string[] { "GameObjectCompareTag" }) || !Contract(moped, new string[] { "StringCompare" }) || !Contract(isMoped, new string[] { "GameObjectCompare" }))
            { reason = "Pickup actions are uninitialized or changed; observation refuses to infer an injection boundary."; return false; }
            if (!Transition(look, "FINISHED", "State 1") || !Transition(start, "FINISHED", "Check moped?") || !Transition(pivotState, "FINISHED", "Item picked") || !Transition(item, "PROCEED Drop", "Drop part") || !Transition(item, "PROCEED Throw", "Drop part 2")
                || !Transition(checkItem, "LOOP", "Check if Part") || !Transition(checkPart, "PROCEED Drop", "Is Jonnez") || !Transition(isMoped, "FINISHED", "Set pivot") || !Transition(partPivot, "FINISHED", "Part picked")
                || !Transition(part, "PROCEED Drop", "Drop part") || !Transition(part, "PROCEED Throw", "Drop part 2") || !Transition(moped, "FINISHED", "Check if Item")) return false;
            if (!FsmBooleanField(start.Actions[0], "activate", false) || !ObjectField(start.Actions[1], "variable", "PickedObject") || !ObjectField(start.Actions[1], "gameObject", "RaycastHitObject")
                || !FsmBooleanField(pivotState.Actions[0], "boolValue", false) || !IntegerField(pivotState.Actions[4], "layer", 16)
                || !ObjectField(pivotState.Actions[5], "parent", "ItemPivot") || !FsmBooleanField(pivotState.Actions[5], "resetLocalPosition", false) || !FsmBooleanField(pivotState.Actions[5], "resetLocalRotation", false)
                || !OwnerField(pivotState.Actions[6], "joint") || !ObjectField(pivotState.Actions[6], "rigidBody", "PickedObject") || !IntegerField(drop.Actions[3], "layer", 19)
                || !OwnerField(drop.Actions[4], "joint") || !FsmBooleanField(drop.Actions[1], "isKinematic", false))
            { reason = "Pickup field contract changed; no game state was modified."; return false; }
            if (!StringField(checkPart.Actions[0], "tag", "PART", false) || !ObjectField(checkPart.Actions[0], "gameObject", "PickedObject")
                || !StringField(checkItem.Actions[0], "tag", "ITEM", false) || !ObjectField(checkItem.Actions[0], "gameObject", "PickedObject")
                || !StringField(moped.Actions[0], "stringVariable", "PlayerCurrentVehicle", true) || !StringField(moped.Actions[0], "compareTo", "Jonnez", false)
                || !BoolVariableField(partPivot.Actions[0], "boolVariable", "HandEmpty") || !FsmBooleanField(partPivot.Actions[0], "boolValue", false)
                || !TargetField(partPivot.Actions[1], "gameObject", "PickedObject") || !OwnerField(partPivot.Actions[2], "gameObject") || !TargetField(partPivot.Actions[3], "gameObject", "ItemPivot")
                || !EnumField(partPivot.Actions[1], "space", 0) || !EnumField(partPivot.Actions[2], "space", 0) || !EnumField(partPivot.Actions[3], "space", 0)
                || !IntegerField(part.Actions[1], "layer", 16) || !TargetField(part.Actions[1], "gameObject", "PickedObject") || !TargetField(part.Actions[2], "gameObject", "PickedObject")
                || !ObjectField(part.Actions[2], "parent", "ItemPivot") || !FsmBooleanField(part.Actions[2], "resetLocalPosition", false) || !FsmBooleanField(part.Actions[2], "resetLocalRotation", false)
                || !OwnerField(part.Actions[3], "joint") || !ObjectField(part.Actions[3], "rigidBody", "PickedObject"))
            { reason = "Coffee cup PART-route fields no longer match inspected layer/parent/joint semantics."; return false; }
            FsmFloat range = Field(look.Actions[3], "rayDistance") as FsmFloat;
            if (range == null || range.UseVariable || range.Value != 1.0f) { reason = "Inspected one-metre native pickup ray differs at runtime."; return false; }
            reason = "Pickup action contract matches; pre-mutation routing and scoped release still require runtime proof."; return true;
        }

        private static bool TagAction(FsmStateAction action, string ownerField)
        {
            FsmString tag = Field(action, "uniqueTag") as FsmString, file = Field(action, "saveFile") as FsmString;
            return tag != null && tag.UseVariable && tag.Name == "UniqueTagTransform" && tag.Value == SaveTag && file != null && !file.UseVariable && file.Value == "defaultES2File.txt" && OwnerField(action, ownerField);
        }
        private static bool Contract(FsmState state, string[] types)
        {
            return Contract(state, types, -1);
        }
        private static bool Contract(FsmState state, string[] types, int disabledAction)
        {
            // Actions getter can lazily initialize actions: do not call it before these checks.
            if (state == null || !state.IsInitialized || !state.ActionsLoaded || state.IsSequence) return false;
            FsmStateAction[] actions = state.Actions;
            if (actions == null || actions.Length != types.Length) return false;
            for (int i = 0; i < types.Length; i++)
                if (actions[i] == null || actions[i].Enabled == (i == disabledAction) || actions[i].GetType().FullName != "HutongGames.PlayMaker.Actions." + types[i] || actions[i].GetType().Assembly.GetName().Name != "Assembly-CSharp") return false;
            return true;
        }
        private static bool Transition(FsmState state, string eventName, string target)
        {
            if (state == null || state.Transitions == null) return false;
            int matches = 0;
            foreach (FsmTransition transition in state.Transitions) if (transition != null && transition.FsmEvent != null && transition.FsmEvent.Name == eventName) { if (transition.ToState != target) return false; matches++; }
            return matches == 1;
        }
        private static FsmState State(PlayMakerFSM machine, string name)
        {
            FsmState result = null;
            foreach (FsmState state in machine.FsmStates) if (state != null && state.Name == name) { if (result != null) return null; result = state; }
            return result;
        }
        private static object Field(FsmStateAction action, string name)
        {
            FieldInfo field = action.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field == null ? null : field.GetValue(action);
        }
        private static bool OwnerField(FsmStateAction action, string field) { FsmOwnerDefault owner = Field(action, field) as FsmOwnerDefault; return owner != null && (int)owner.OwnerOption == 0; }
        private static bool ObjectField(FsmStateAction action, string field, string variable) { FsmGameObject value = Field(action, field) as FsmGameObject; return value != null && value.UseVariable && value.Name == variable; }
        private static bool TargetField(FsmStateAction action, string field, string variable) { FsmOwnerDefault value = Field(action, field) as FsmOwnerDefault; return value != null && (int)value.OwnerOption == 1 && value.GameObject != null && value.GameObject.UseVariable && value.GameObject.Name == variable; }
        private static bool BoolVariableField(FsmStateAction action, string field, string variable) { FsmBool value = Field(action, field) as FsmBool; return value != null && value.UseVariable && value.Name == variable; }
        private static bool StringField(FsmStateAction action, string field, string expected, bool variable) { FsmString value = Field(action, field) as FsmString; return value != null && value.UseVariable == variable && (variable ? value.Name : value.Value) == expected; }
        private static bool EnumField(FsmStateAction action, string field, int expected) { object value = Field(action, field); return value != null && value.GetType().IsEnum && Convert.ToInt32(value) == expected; }
        private static bool FsmBooleanField(FsmStateAction action, string field, bool expected) { FsmBool value = Field(action, field) as FsmBool; return value != null && !value.UseVariable && value.Value == expected; }
        private static bool FieldBoolean(FsmStateAction action, string field, bool expected) { object value = Field(action, field); return value is bool && (bool)value == expected; }
        private static bool IntegerField(FsmStateAction action, string field, int expected) { object value = Field(action, field); return value is int && (int)value == expected; }
        private static FsmString StringVariable(PlayMakerFSM machine, string name) { FsmString found = null; foreach (FsmString value in machine.FsmVariables.StringVariables) if (value != null && value.Name == name) { if (found != null) return null; found = value; } return found; }
        private static FsmFloat FloatVariable(PlayMakerFSM machine, string name) { FsmFloat found = null; foreach (FsmFloat value in machine.FsmVariables.FloatVariables) if (value != null && value.Name == name) { if (found != null) return null; found = value; } return found; }
        private static FsmGameObject ObjectVariable(PlayMakerFSM machine, string name) { FsmGameObject found = null; foreach (FsmGameObject value in machine.FsmVariables.GameObjectVariables) if (value != null && value.Name == name) { if (found != null) return null; found = value; } return found; }
        private static FsmBool BoolVariable(PlayMakerFSM machine, string name) { FsmBool found = null; foreach (FsmBool value in machine.FsmVariables.BoolVariables) if (value != null && value.Name == name) { if (found != null) return null; found = value; } return found; }
        private static bool StringValue(PlayMakerFSM machine, string name, string expected) { FsmString value = StringVariable(machine, name); return value != null && value.Value == expected; }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static bool CupIdle(string state) { return state == "Wait player" || state == "Wait button" || state == "Delay"; }
        private static string Label(string value) { if (value == null) return ""; value = value.Replace('\r', '_').Replace('\n', '_').Replace('<', '_').Replace('>', '_'); return value.Length > 48 ? value.Substring(0, 48) : value; }
    }
}
