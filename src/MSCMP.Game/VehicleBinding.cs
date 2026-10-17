using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using MSCMP.Core;
using UnityEngine;

namespace MSCMP.Integration
{
    // Version-specific resolver: VanTransform Save/Load owner plus inspected entry FSM.
    // The hierarchy/name is never used as cross-machine identity.
    internal sealed class VehicleBinding : IDisposable
    {
        internal const string Key = "msc-20171487:defaultES2File.txt:VanTransform";
        internal const string Archetype = "msc.hayosiko";
        private readonly Dictionary<Behaviour, bool> enabled = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<Rigidbody, bool> childBodies = new Dictionary<Rigidbody, bool>();
        private PlayMakerFSM persistence, entry;
        private Behaviour axis, drivetrain, dynamics;
        private MethodInfo getInput, fixedUpdate, shift;
        private FieldInfo[] inputFields;
        private FieldInfo gear;
        private FsmTransition enterTransition, exitTransition;
        private string enterTarget, exitTarget;
        private FsmState[] originalStates;
        private FsmEvent[] originalEvents;
        private FsmTransition[] originalGlobal;
        private VehicleFixedStep step;
        private Transform driverHead, passengerMass;
        internal Func<bool> CanEnterDriver;
        internal Vector3 SeatHead(int seat)
        {
            if (seat == 0) return driverHead.position;
            Vector3 local = Body.transform.InverseTransformPoint(passengerMass.position);
            local.y = Body.transform.InverseTransformPoint(driverHead.position).y;
            return Body.transform.TransformPoint(local);
        }
        internal Vector3 SeatFeet(int seat) { return SeatHead(seat) - Body.transform.up * 1.55f; }
        internal float SeatYaw { get { return Body.rotation.eulerAngles.y; } }
        private bool originalKinematic, activated, disposed, locallySeated;
        private RigidbodyConstraints originalConstraints;
        internal Rigidbody Body { get; private set; }
        internal int GearCount { get; private set; }
        internal int CurrentGear { get { return (int)gear.GetValue(drivetrain); } }
        internal void SetReplicaGear(int value) { if (value >= 0 && value < GearCount) gear.SetValue(drivetrain, value); }
        internal Action<int> SeatRequested;
        internal Func<VehicleInput> ApprovedInput;
        internal Action<Exception> Failure;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static VehicleBinding Create()
        {
            VehicleBinding binding = new VehicleBinding();
            try { binding.Resolve(); return binding; } catch { binding.Dispose(); throw; }
        }
        private void Resolve()
        {
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
            if (all.Length > 16000) throw new InvalidOperationException("Vehicle FSM discovery capacity exceeded.");
            int claims = 0;
            foreach (UnityEngine.Object value in all)
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f == null) continue;
                foreach (FsmString s in f.FsmVariables.StringVariables)
                    if (s != null && s.Name == "UniqueTagPosition" && s.Value == "VanTransform") { persistence = f; claims++; }
            }
            if (claims != 1 || persistence.FsmName != "LOD" || !persistence.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Expected one active VanTransform persistence owner.");
            VerifyPersistence("Save game", "SaveTransform", "saveValue"); VerifyPersistence("Load game", "LoadTransform", "loadValue");
            Body = persistence.GetComponent<Rigidbody>();
            if (Body == null || Body.mass < 1000 || Body.mass > 2000) throw new InvalidOperationException("Van body differs from the inspected contract.");
            axis = Unique(persistence.gameObject, "AxisCarController"); drivetrain = Unique(persistence.gameObject, "Drivetrain"); dynamics = Unique(persistence.gameObject, "CarDynamics");
            Type baseType = axis.GetType().BaseType;
            getInput = axis.GetType().GetMethod("GetInput", Fields); fixedUpdate = baseType.GetMethod("FixedUpdate", Fields);
            shift = drivetrain.GetType().GetMethod("Shift", Fields, null, new Type[] { typeof(int) }, null);
            gear = drivetrain.GetType().GetField("gear", Fields);
            float[] ratios = drivetrain.GetType().GetField("gearRatios", Fields).GetValue(drivetrain) as float[];
            if (getInput == null || fixedUpdate == null || shift == null || gear == null || ratios == null || ratios.Length < 2 || ratios.Length > 16)
                throw new InvalidOperationException("Van controller API differs from the inspected build.");
            GearCount = ratios.Length;
            string[] names = { "throttleInput", "brakeInput", "steerInput", "handbrakeInput", "clutchInput", "startEngineInput", "brakeKey", "accelKey" };
            inputFields = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++) { inputFields[i] = baseType.GetField(names[i], Fields); if (inputFields[i] == null) throw new InvalidOperationException("Missing vehicle input field."); }
            int entries = 0;
            foreach (PlayMakerFSM f in persistence.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName != "PlayerTrigger") continue;
                foreach (FsmGameObject v in f.FsmVariables.GameObjectVariables)
                    if (v.Name == "DriverHeadPivot" && v.Value != null) { entry = f; driverHead = v.Value.transform; entries++; }
            }
            if (entries != 1 || entry.FsmStates.Length != 9 || (entry.ActiveStateName != "Wait for player" && entry.ActiveStateName != "Press return"))
                throw new InvalidOperationException("Van driver entry is missing, ambiguous or already occupied.");
            // Authored front passenger mass locates the seat horizontally. The
            // original driver camera supplies its eye height; no joint is enabled.
            Transform root = persistence.transform;
            for (int i = 0; i < root.childCount; i++)
                if (root.GetChild(i).name == "MassPassenger")
                { if (passengerMass != null) throw new InvalidOperationException("Ambiguous passenger anchor."); passengerMass = root.GetChild(i); }
            if (passengerMass == null || (passengerMass.localPosition - new Vector3(0.381f, 0.741f, 1.064f)).sqrMagnitude > 0.01f)
                throw new InvalidOperationException("Passenger anchor differs from the inspected van.");
            enterTransition = Transition(entry, "Press return", "Key DOWN", "Reset view");
            exitTransition = Transition(entry, "Check speed", "FINISHED", "Create player");
            originalStates = entry.Fsm.States; originalEvents = entry.Fsm.Events; originalGlobal = entry.Fsm.GlobalTransitions;
            originalKinematic = Body.isKinematic; originalConstraints = Body.constraints;
        }
        private void VerifyPersistence(string stateName, string actionName, string ownerName)
        {
            FsmState state = FindState(persistence, stateName);
            if (state == null || state.Actions.Length < 1 || state.Actions[0].GetType().Name != actionName) throw new InvalidOperationException("Van persistence actions differ.");
            FsmStateAction action = state.Actions[0];
            FsmString tag = Field(action, "uniqueTag") as FsmString, file = Field(action, "saveFile") as FsmString;
            FsmOwnerDefault owner = Field(action, ownerName) as FsmOwnerDefault;
            if (tag == null || tag.Name != "UniqueTagPosition" || !tag.UseVariable || file == null || file.UseVariable || file.Value != "defaultES2File.txt" || owner == null || (int)owner.OwnerOption != 0)
                throw new InvalidOperationException("Van persistence identity is not owner-bound.");
        }
        internal void Activate(bool host)
        {
            activated = true;
            foreach (Rigidbody body in persistence.GetComponentsInChildren<Rigidbody>(true))
                if (body != Body) { childBodies.Add(body, body.isKinematic); if (!host) body.isKinematic = true; }
            // Keep the actual CarController's fixed-step logic; replace only its source of input.
            // Start only assigns its inspected cached component references. Awake already built Axles.
            axis.GetType().BaseType.GetMethod("Start", Fields).Invoke(axis, null);
            Array wheels = axis.GetType().BaseType.GetField("allWheels", Fields).GetValue(axis) as Array;
            if (wheels == null || wheels.Length != 4) throw new InvalidOperationException("Van wheel bindings are not initialized.");
            foreach (Behaviour behaviour in persistence.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour is PlayMakerFSM || behaviour.GetType().Name == "AxisCarController" || behaviour.GetType().Name == "MouseCarController"
                    || behaviour.GetType().Name == "MobileCarController" || behaviour.GetType().Name == "CarDynamics" || behaviour.GetType().Name == "Drivetrain" || behaviour.GetType().Name == "Wheel")
                {
                    enabled.Add(behaviour, behaviour.enabled);
                    if (behaviour != entry) behaviour.enabled = host && (behaviour == dynamics || behaviour == drivetrain || behaviour.GetType().Name == "Wheel");
                }
            }
            enterTarget = enterTransition.ToState; exitTarget = exitTransition.ToState;
            AddPending("MSCMP enter", 0, "Reset view", "Press return");
            AddPending("MSCMP exit", -1, "Create player", "Player in car");
            enterTransition.ToState = "MSCMP enter"; exitTransition.ToState = "MSCMP exit";
            entry.AddGlobalTransition("MSCMP cleanup", "Create player");
            entry.AddGlobalTransition("MSCMP request entry", "MSCMP enter");
            entry.AddGlobalTransition("MSCMP request exit", "MSCMP exit");
            step = persistence.gameObject.AddComponent<VehicleFixedStep>(); step.Binding = this; step.Host = host;
            Enforce(host);
        }
        private void AddPending(string name, int seat, string granted, string denied)
        {
            FsmState state = entry.AddState(name);
            state.Actions = new FsmStateAction[] { new SeatAction { Binding = this, Seat = seat } };
            state.AddTransition("MSCMP granted", granted); state.AddTransition("MSCMP denied", denied);
        }
        private sealed class SeatAction : FsmStateAction
        {
            internal VehicleBinding Binding; internal int Seat;
            public override void OnEnter()
            {
                if (Seat == 0 && Binding.CanEnterDriver != null && !Binding.CanEnterDriver()) { Binding.CompleteSeat(Seat, false); return; }
                if (Binding.SeatRequested != null) Binding.SeatRequested(Seat);
            }
        }
        internal void CompleteSeat(int seat, bool accepted)
        {
            if (entry == null || (entry.ActiveStateName != "MSCMP enter" && entry.ActiveStateName != "MSCMP exit")) return;
            if (accepted) locallySeated = seat == 0;
            entry.Fsm.Event(accepted ? "MSCMP granted" : "MSCMP denied");
        }
        internal void ReleaseLocal()
        {
            if (!locallySeated || entry == null) return;
            entry.Fsm.Event("MSCMP cleanup"); entry.Fsm.UpdateStateChanges(); locallySeated = false;
        }
        internal void RequestSeat(bool exit)
        {
            if (entry == null || disposed || entry.ActiveStateName.StartsWith("MSCMP", StringComparison.Ordinal)) return;
            entry.Fsm.Event(exit ? "MSCMP request exit" : "MSCMP request entry");
        }
        internal VehicleInput ReadLocalInput()
        {
            object[] values = { 0f, 0f, 0f, 0f, 0f, false, 1 };
            getInput.Invoke(axis, values);
            // Explicit starter key remains available while native dashboard-use FSMs are gated.
            values[5] = (bool)values[5] || Input.GetKey(KeyCode.I);
            return new VehicleInput { Throttle = Mathf.Clamp01((float)values[0]), Brake = Mathf.Clamp01((float)values[1]), Steering = Mathf.Clamp((float)values[2], -1f, 1f), Handbrake = Mathf.Clamp01((float)values[3]), Clutch = Mathf.Clamp01((float)values[4]), StartEngine = (bool)values[5], Gear = Math.Max(0, Math.Min(GearCount - 1, (int)values[6])) };
        }
        internal void Tick(bool host)
        {
            if (disposed || Body == null) return;
            try
            {
                Enforce(host);
                if (!host) return;
                VehicleInput input = ApprovedInput == null ? VehicleInput.Stopped : ApprovedInput();
                object[] values = { input.Throttle, input.Brake, input.Steering, input.Handbrake, input.Clutch, input.StartEngine, input.Brake > 0, input.Throttle > 0 };
                for (int i = 0; i < inputFields.Length; i++) inputFields[i].SetValue(axis, values[i]);
                if ((int)gear.GetValue(drivetrain) != input.Gear && !(bool)drivetrain.GetType().GetField("changingGear", Fields).GetValue(drivetrain)) shift.Invoke(drivetrain, new object[] { input.Gear });
                fixedUpdate.Invoke(axis, null);
            }
            catch (Exception error) { if (step != null) step.enabled = false; if (Failure != null) Failure(error); }
        }
        private void Enforce(bool host)
        {
            foreach (KeyValuePair<Behaviour, bool> pair in enabled)
            {
                Behaviour b = pair.Key;
                if (b == null || b == entry) continue;
                b.enabled = host && (b == dynamics || b == drivetrain || b.GetType().Name == "Wheel");
            }
            Body.isKinematic = !host;
            Body.constraints = host ? originalConstraints : RigidbodyConstraints.FreezeAll;
            if (!host) foreach (Rigidbody body in childBodies.Keys) if (body != null) body.isKinematic = true;
        }
        private static Behaviour Unique(GameObject owner, string name)
        {
            Behaviour result = null;
            foreach (Behaviour b in owner.GetComponents<Behaviour>()) if (b != null && b.GetType().Name == name && b.GetType().Assembly.GetName().Name == "Assembly-CSharp")
            { if (result != null) throw new InvalidOperationException("Ambiguous vehicle component."); result = b; }
            if (result == null) throw new InvalidOperationException("Missing vehicle component: " + name); return result;
        }
        private static object Field(object owner, string name) { FieldInfo field = owner.GetType().GetField(name, Fields); return field == null ? null : field.GetValue(owner); }
        private static FsmState FindState(PlayMakerFSM fsm, string name) { foreach (FsmState state in fsm.FsmStates) if (state.Name == name) return state; return null; }
        private static FsmTransition Transition(PlayMakerFSM fsm, string stateName, string eventName, string target)
        {
            FsmState state = FindState(fsm, stateName);
            if (state != null) foreach (FsmTransition transition in state.Transitions) if (transition.EventName == eventName && transition.ToState == target) return transition;
            throw new InvalidOperationException("Van seat transition differs from the inspected game.");
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (step != null) { step.Binding = null; step.enabled = false; UnityEngine.Object.Destroy(step); }
            if (activated && entry != null)
            {
                if (locallySeated) { entry.Fsm.Event("MSCMP cleanup"); entry.Fsm.UpdateStateChanges(); }
                else if (entry.ActiveStateName == "MSCMP enter" || entry.ActiveStateName == "MSCMP exit") { entry.Fsm.Event("MSCMP denied"); entry.Fsm.UpdateStateChanges(); }
                if (enterTarget != null) enterTransition.ToState = enterTarget;
                if (exitTarget != null) exitTransition.ToState = exitTarget;
                entry.Fsm.States = originalStates; entry.Fsm.Events = originalEvents; entry.Fsm.GlobalTransitions = originalGlobal;
            }
            foreach (KeyValuePair<Behaviour, bool> pair in enabled) if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (KeyValuePair<Rigidbody, bool> pair in childBodies) if (pair.Key != null) pair.Key.isKinematic = pair.Value;
            if (activated && Body != null) { Body.isKinematic = originalKinematic; Body.constraints = originalConstraints; }
        }
    }
    public sealed class VehicleFixedStep : MonoBehaviour
    {
        internal VehicleBinding Binding; internal bool Host;
        private void FixedUpdate() { if (Binding != null) Binding.Tick(Host); }
    }
}
