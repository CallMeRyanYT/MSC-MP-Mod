using System;
using UnityEngine;

namespace MSCMP.Integration
{
    /// <summary>Read-only, walking-player binding for the inspected Unity 5 GAME scene.</summary>
    internal sealed class LocalPlayerBinding : IDisposable
    {
        private readonly int generation;
        private Transform player;
        private CharacterController controller;
        private Behaviour motor;
        private Behaviour input;
        private Camera camera;
        internal Transform Root { get { return player; } }
        internal Camera View { get { return camera; } }
        internal void PlaceAt(Vector3 position)
        {
            if (player == null || controller == null) throw new InvalidOperationException("Player binding expired.");
            bool wasEnabled = controller.enabled;
            try { controller.enabled = false; player.position = position; }
            finally { controller.enabled = wasEnabled; }
        }
        internal bool TrySampleSeated(out Vector3 position, out float yaw)
        {
            position = Vector3.zero; yaw = 0;
            if (player == null || camera == null || !camera.enabled || !camera.gameObject.activeInHierarchy) return false;
            position = player.position; yaw = player.eulerAngles.y;
            return Valid(position.x) && Valid(position.y) && Valid(position.z) && Valid(yaw);
        }

        private LocalPlayerBinding(int sceneGeneration, Transform root, CharacterController character,
            Behaviour playerMotor, Behaviour playerInput, Camera view)
        {
            generation = sceneGeneration;
            player = root;
            controller = character;
            motor = playerMotor;
            input = playerInput;
            camera = view;
        }

        internal static bool TryCreate(int sceneGeneration, out LocalPlayerBinding binding, out string reason)
        {
            binding = null;
            reason = "The inspected walking-player binding requires the GAME scene.";
            if (sceneGeneration <= 0 || Application.loadedLevelName != "GAME") return false;

            UnityEngine.Object[] controllers = UnityEngine.Object.FindObjectsOfType(typeof(CharacterController));
            CharacterController found = null;
            int matches = 0;
            for (int i = 0; i < controllers.Length; i++)
            {
                CharacterController candidate = controllers[i] as CharacterController;
                if (candidate != null && candidate.transform.parent == null && candidate.name == "PLAYER")
                {
                    found = candidate;
                    matches++;
                }
            }
            reason = "Expected exactly one active root PLAYER with CharacterController; found " + matches + ".";
            if (matches != 1 || found == null) return false;

            Behaviour playerMotor = UniqueBehaviour(found.gameObject, "CharacterMotor");
            Behaviour playerInput = UniqueBehaviour(found.gameObject, "FPSInputController");
            reason = "PLAYER requires one CharacterMotor and one FPSInputController from the inspected assembly.";
            if (playerMotor == null || playerInput == null) return false;

            string[] path = new string[] { "Pivot", "AnimPivot", "Camera", "FPSCamera", "FPSCamera" };
            Transform view = found.transform;
            for (int part = 0; part < path.Length; part++)
            {
                view = UniqueChild(view, path[part]);
                reason = "Missing or ambiguous PLAYER camera path segment: " + path[part] + ".";
                if (view == null) return false;
            }
            Camera[] cameras = view.GetComponents<Camera>();
            reason = "The inspected FPSCamera object must contain exactly one Camera.";
            if (cameras.Length != 1) return false;
            binding = new LocalPlayerBinding(sceneGeneration, found.transform, found, playerMotor, playerInput, cameras[0]);
            reason = "Read-only walking pose bound; vehicle, death and correction adapters are not enabled.";
            return true;
        }

        internal bool TrySample(int sceneGeneration, out Vector3 position, out float yaw)
        {
            position = Vector3.zero;
            yaw = 0;
            if (sceneGeneration != generation || Application.loadedLevelName != "GAME" || player == null
                || controller == null || motor == null || input == null || camera == null
                || !player.gameObject.activeInHierarchy || !controller.enabled || !motor.enabled || !input.enabled
                || !camera.enabled || !camera.gameObject.activeInHierarchy || Camera.main != camera) return false;
            position = player.position;
            yaw = player.eulerAngles.y;
            return Valid(position.x) && Valid(position.y) && Valid(position.z) && Valid(yaw)
                && Math.Abs(position.x) <= 100000 && Math.Abs(position.y) <= 100000 && Math.Abs(position.z) <= 100000;
        }

        internal bool TryInitialPose(int sceneGeneration, out Vector3 position, out float yaw)
        {
            if (TrySample(sceneGeneration, out position, out yaw)) return true;
            // Hosting uses the pause menu, where native input may be disabled.
            // Read the already resolved, unparented root only for initial placement.
            // Movement and interaction still require a live walking pose afterward.
            if (Time.timeScale != 0 || sceneGeneration != generation || Application.loadedLevelName != "GAME"
                || player == null || player.parent != null || !player.gameObject.activeInHierarchy
                || controller == null || motor == null || input == null || camera == null) return false;
            position = player.position; yaw = player.eulerAngles.y;
            return Valid(position.x) && Valid(position.y) && Valid(position.z) && Valid(yaw)
                && Math.Abs(position.x) <= 100000 && Math.Abs(position.y) <= 100000 && Math.Abs(position.z) <= 100000;
        }

        public void Dispose()
        {
            // No controller state is changed, so there is nothing to restore.
            player = null;
            controller = null;
            motor = null;
            input = null;
            camera = null;
        }

        private static Behaviour UniqueBehaviour(GameObject owner, string typeName)
        {
            Behaviour found = null;
            Component[] components = owner.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component.GetType().Name != typeName) continue;
                if (component.GetType().Assembly.GetName().Name != "Assembly-UnityScript-firstpass" || found != null) return null;
                found = component as Behaviour;
            }
            return found;
        }

        private static Transform UniqueChild(Transform parent, string name)
        {
            Transform result = null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name != name) continue;
                if (result != null) return null;
                result = child;
            }
            return result;
        }

        private static bool Valid(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }
}
