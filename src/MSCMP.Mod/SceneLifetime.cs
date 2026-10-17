using System;

namespace MSCMP
{
    // Pure bookkeeping: Unity destruction can arrive after a replacement is created.
    // Only the owner of the current generation may change readiness or end it.
    internal sealed class SceneLifetime
    {
        private int generation;
        private int activeGeneration;
        private int starts;
        private int cleanups;
        private bool ready;
        private string sceneName = String.Empty;

        internal int Generation { get { return activeGeneration; } }
        internal int Starts { get { return starts; } }
        internal int Cleanups { get { return cleanups; } }
        internal bool Ready { get { return ready; } }
        internal string SceneName { get { return sceneName; } }

        internal int Begin(string observedScene)
        {
            observedScene = observedScene ?? String.Empty;
            if (activeGeneration != 0 && sceneName == observedScene)
                return activeGeneration;

            if (activeGeneration != 0)
                End(activeGeneration);

            generation = checked(generation + 1);
            starts = checked(starts + 1);
            activeGeneration = generation;
            sceneName = observedScene;
            ready = false;
            return activeGeneration;
        }

        internal bool MarkReady(int token)
        {
            if (token == 0 || token != activeGeneration || ready)
                return false;
            ready = true;
            return true;
        }

        internal bool End(int token)
        {
            if (token == 0 || token != activeGeneration)
                return false;
            activeGeneration = 0;
            sceneName = String.Empty;
            ready = false;
            cleanups = checked(cleanups + 1);
            return true;
        }
    }
}
