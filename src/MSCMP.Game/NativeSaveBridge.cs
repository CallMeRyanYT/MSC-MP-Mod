using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCMP.Integration
{
    // Inspected seven toilet Save & Quit FSMs. Capture before their first save
    // action, then release native saving. The loader's after-save callback confirms
    // that ITEMS reached Save game. Offline capture waits until the process exits.
    internal sealed class NativeSaveBridge : IDisposable
    {
        private readonly Dictionary<FsmState, FsmStateAction[]> originals = new Dictionary<FsmState, FsmStateAction[]>();
        private bool fired;
        internal NativeSaveBridge(Action beforeSave)
        {
            List<PlayMakerFSM> targets = new List<PlayMakerFSM>();
            foreach (UnityEngine.Object value in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
            {
                PlayMakerFSM fsm = value as PlayMakerFSM;
                if (fsm == null || fsm.gameObject.name != "SAVEGAME" || fsm.FsmName != "Button") continue;
                FsmState all = fsm.GetState("Save all"), mute = fsm.GetState("Mute audio"), menu = fsm.GetState("Load menu");
                if (all == null || mute == null || menu == null || all.Actions.Length != 2
                    || all.Actions[0].GetType().Name != "SetStringValue" || all.Actions[1].GetType().Name != "SendEvent"
                    || mute.Actions.Length != 7 || mute.Actions[0].GetType().Name != "SendEvent"
                    || menu.Actions.Length != 2 || menu.Actions[1].GetType().Name != "LoadLevel")
                    throw new InvalidOperationException("Native Save & Quit contract differs from the inspected game.");
                targets.Add(fsm);
            }
            if (targets.Count != 7) throw new InvalidOperationException("Expected seven inspected native Save & Quit points.");
            try
            {
                foreach (PlayMakerFSM fsm in targets)
                {
                    FsmState state = fsm.GetState("Mute audio"); originals.Add(state, state.Actions);
                    if (!fsm.FsmInject("Mute audio", delegate { if (fired) return; fired = true; beforeSave(); }, false, 0, false))
                        throw new InvalidOperationException("Native save hook could not be installed.");
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        { foreach (KeyValuePair<FsmState, FsmStateAction[]> original in originals) original.Key.Actions = original.Value; originals.Clear(); }
    }
}
