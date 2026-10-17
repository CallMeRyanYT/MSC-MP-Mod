using UnityEngine;

namespace MSCMP
{
    // Only this mod's object is created/destroyed. No player, physics, FSM or save state is changed.
    public sealed class DiscoveryObserver : MonoBehaviour
    {
        private DiscoveryMod owner;
        private int generation;
        private bool logSubscribed;
        internal int Generation { get { return generation; } }

        internal void Initialize(DiscoveryMod mod, int token)
        {
            owner = mod;
            generation = token;
            if (!logSubscribed) { Application.logMessageReceived += OnLog; logSubscribed = true; }
        }

        internal void Detach() { Unsubscribe(); owner = null; }

        private void OnLog(string message, string stack, LogType type)
        {
            if (owner != null) owner.ObserveUnityError(generation, message, stack, type);
        }

        private void Unsubscribe()
        {
            if (!logSubscribed) return;
            Application.logMessageReceived -= OnLog;
            logSubscribed = false;
        }

        private void Update()
        {
            if (owner != null)
                owner.Tick(generation);
        }

        private void OnGUI()
        {
            if (owner != null)
                owner.DrawOverlay(generation);
        }

        private void OnApplicationFocus(bool focused)
        {
            if (owner != null)
                owner.ObserveFocus(generation, focused);
        }

        private void OnApplicationPause(bool paused)
        {
            if (owner != null)
                owner.ObservePause(generation, paused);
        }

        private void OnApplicationQuit() { End("application-quit"); }
        private void OnDestroy() { End("observer-destroyed"); }

        private void End(string reason)
        {
            Unsubscribe();
            DiscoveryMod previous = owner;
            owner = null;
            if (previous != null)
                previous.ObserverDestroyed(generation, reason);
        }
    }
}
