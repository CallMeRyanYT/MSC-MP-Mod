using System;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MSCMP
{
    internal static class TargetInspector
    {
        private const int MaxAncestors = 6;
        private const int MaxFsms = 8;
        private const int MaxStatesPerFsm = 16;
        private const int MaxInspectionEntries = 80;

        internal static string Capture(DiagnosticTrace trace)
        {
            Camera camera = Camera.main;
            if (camera == null)
                return "No active camera tagged MainCamera; no target inspected.";
            RaycastHit hit;
            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            if (!Physics.Raycast(ray, out hit, 6.0f) || hit.collider == null)
                return "No collider within 6 metres at the centre of the main camera.";

            Transform current = hit.collider.transform;
            trace.Add("inspect.begin", "Camera-centre collider observation only; names are NOT network identities.");
            int remainingEntries = MaxInspectionEntries - 2;
            int ancestors = 0;
            int fsms = 0;
            while (current != null && ancestors < MaxAncestors && fsms < MaxFsms && remainingEntries > 0)
            {
                trace.Add("inspect.object", "ancestor=" + ancestors + " name=" + Label(current.name)
                    + " active=" + current.gameObject.activeInHierarchy);
                remainingEntries--;
                // Scoped to one collider ancestry, never a world-wide discovery scan.
                PlayMakerFSM[] components = current.GetComponents<PlayMakerFSM>();
                for (int index = 0; index < components.Length && fsms < MaxFsms && remainingEntries >= 2; index++)
                {
                    PlayMakerFSM machine = components[index];
                    if (machine == null)
                        continue;
                    fsms++;
                    trace.Add("inspect.fsm", "ancestor=" + ancestors + " fsm=" + Label(machine.FsmName)
                        + " activeState=" + Label(machine.ActiveStateName));
                    // Verified local getter reads the state array directly. PlayMakerFSM.Fsm
                    // assigns Owner as a side effect, so avoid it in this observational tool.
                    FsmState[] states = machine.FsmStates;
                    int stateCount = states == null ? 0 : states.Length;
                    int shownStates = Math.Min(Math.Min(stateCount, MaxStatesPerFsm), remainingEntries - 2);
                    trace.Add("inspect.state-count", "total=" + stateCount + " shown=" + shownStates);
                    remainingEntries -= 2;
                    for (int state = 0; state < shownStates; state++)
                    {
                        trace.Add("inspect.state", states[state] == null ? "(null)" : Label(states[state].Name));
                        remainingEntries--;
                    }
                }
                ancestors++;
                current = current.parent;
            }
            trace.Add("inspect.bounds", "ancestors=" + ancestors + "/" + MaxAncestors
                + " fsms=" + fsms + "/" + MaxFsms + " states-per-fsm<=" + MaxStatesPerFsm
                + " entries<=" + MaxInspectionEntries + "; output may be truncated; no values/actions/events executed");
            return "Inspected aimed collider ancestry: " + ancestors + " objects, " + fsms + " FSMs (bounded).";
        }

        private static string Label(string value)
        {
            // Do not emit path-like object labels supplied by other mods.
            if (!String.IsNullOrEmpty(value) && (value.IndexOf('\\') >= 0 || value.IndexOf(":/") >= 0))
                return "(path-like label omitted)";
            return DiagnosticTrace.SafeText(value, 80);
        }
    }
}
