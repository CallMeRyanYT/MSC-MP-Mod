using System;
using System.Globalization;
using MSCMP.Network;
using MSCMP.Integration;
using UnityEngine;

namespace MSCMP
{
    // Main-thread diagnostic transport UI. An accepted connection is NOT permission
    // to mutate the game or evidence that a world baseline has been installed.
    internal sealed class NetworkPanel : IDisposable
    {
        private readonly Action<string, string> record;
        private readonly Action<string> reportProblem;
        private DiagnosticSession session;
        private AvatarPreview preview;
        private CharacterPreview characterPreview;
        private bool characterPreviewFailed;
        private int sceneGeneration;
        private bool sceneReady;
        private Vector2 scroll;
        private string address = "127.0.0.1";
        private string portText = "20577";
        private string displayName = "Player";
        private string reportNote = String.Empty;
        private GUIStyle wrappedLabel;
        private CharacterStyle characterStyle = CharacterStyle.Male;
        private string notice = "Private LAN / tailnet diagnostics. Gameplay is not shared.";

        internal NetworkPanel(Action<string, string> recordEvent, Action<string> reportProblem)
        {
            record = recordEvent;
            this.reportProblem = reportProblem;
        }

        internal string DiagnosticSummary
        {
            get
            {
                if (session == null)
                    return "state=Disconnected role=none peers=0 port=none sent=0 received=0 queue=0 style=" + characterStyle + " avatarsActive=False";
                // Status text can contain a remote rejection/disconnect reason. Use
                // controlled state labels so reports never inherit an address/name.
                string state = !session.IsRunning ? "Stopped" : session.IsHost ? "Listening" : session.LocalPlayerId == 0 ? "Connecting" : "Accepted";
                return "state=" + state + " role=" + (session.IsHost ? "host" : "client")
                    + " peers=" + session.AcceptedPeerCount + " port=" + session.LocalPort + " sent=" + session.SentBytes
                    + " received=" + session.ReceivedBytes + " queue=" + session.QueueDepth + " style=" + characterStyle
                    + " avatarsActive=" + (preview != null && preview.IsActive);
            }
        }

        internal void SetSceneContext(int generation, bool ready)
        {
            if (generation != sceneGeneration || !ready) StopPreview();
            if (generation != sceneGeneration) StopCharacterPreview();
            sceneGeneration = generation; sceneReady = ready;
        }

        internal void Poll()
        {
            if (session == null) return;
            try { session.Poll(); if (preview != null) { preview.Poll(); if (!preview.IsActive) { preview.Dispose(); preview = null; } } }
            catch (Exception error) { Fail("poll", error); }
        }

        internal void Draw()
        {
            bool previousEnabled = GUI.enabled;
            if (wrappedLabel == null) { wrappedLabel = new GUIStyle(GUI.skin.label); wrappedLabel.wordWrap = true; }
            float width = Math.Min(405, Screen.width - 24);
            float x = Math.Max(12, Screen.width - width - 12);
            float y = Screen.width < 1150 ? 246 : 12;
            GUILayout.BeginArea(new Rect(x, y, width, Math.Max(100, Math.Min(465, Screen.height - y - 12))), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);
            try
            {
                GUILayout.Label("MSC-MP | Network diagnostics");
                GUILayout.Label("Use the game's menu or pause menu to click these controls.", wrappedLabel);
                GUILayout.Label("Network tests and optional visual avatars. Shared items and save transfer are unavailable.", wrappedLabel);
                bool running = session != null && session.IsRunning;
                GUI.enabled = !running;
                GUILayout.Label("Display name");
                displayName = GUILayout.TextField(displayName, 24);
                GUI.enabled = true;
                GUILayout.Label("Character");
                int characterChoice = GUILayout.SelectionGrid(characterStyle == CharacterStyle.Male ? 0 : 1, new string[] { "Male", "Female" }, 2);
                CharacterStyle selectedStyle = characterChoice == 0 ? CharacterStyle.Male : CharacterStyle.Female;
                if (selectedStyle != characterStyle)
                {
                    characterStyle = selectedStyle;
                    if (preview != null && !preview.SetCharacterStyle(characterStyle)) notice = "Character update is waiting for the connection.";
                }
                DrawCharacterPreview();
                // Keep this control in every Layout/Repaint pass, including success.
                GUI.enabled = characterPreviewFailed;
                if (GUILayout.Button("Retry character preview")) StopCharacterPreview();
                GUI.enabled = !running;
                GUILayout.Label("Host IP address / UDP port");
                GUILayout.BeginHorizontal();
                address = GUILayout.TextField(address, 64);
                portText = GUILayout.TextField(portText, 5, GUILayout.Width(70));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host diagnostics")) Start(true);
                if (GUILayout.Button("Join diagnostics")) Start(false);
                GUILayout.EndHorizontal();
                running = session != null && session.IsRunning;
                GUI.enabled = running;
                if (GUILayout.Button("Leave")) Leave("user-leave");
                running = session != null && session.IsRunning;
                GUI.enabled = running && session != null && session.AcceptedPeerCount > 0;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Ping")) Perform(false);
                if (GUILayout.Button("Reliable probe")) Perform(true);
                GUILayout.EndHorizontal();
                GUI.enabled = true;
                bool canPreview = running && session != null && session.LocalPlayerId > 0 && sceneReady;
                GUI.enabled = canPreview;
                bool desiredPreview = GUILayout.Toggle(preview != null, "Experimental walking avatars (visual only)");
                if (desiredPreview && preview == null)
                {
                    string reason;
                    if (AvatarPreview.TryCreate(session, sceneGeneration, characterStyle, out preview, out reason))
                        record("network.avatars-enabled", "original geometry; read-only local pose; runtime unverified");
                    notice = reason;
                }
                else if (!desiredPreview && preview != null) StopPreview();
                GUI.enabled = sceneReady;
                if (GUILayout.Button("Check player and cup bindings")) CheckBindings();
                GUI.enabled = true;
                // Actions above can replace session during an input pass. Always
                // allocate four status rows to keep the GUILayout tree consistent.
                GUILayout.Label("State: " + (session == null ? "Disconnected" : DiagnosticTrace.SafeText(session.Status, 100)), wrappedLabel);
                GUILayout.Label(session == null ? "Peers: 0 | Port: -- | RTT: --"
                    : "Peers: " + session.AcceptedPeerCount + " | Port: " + session.LocalPort + " | RTT: " + session.LastPingMilliseconds + " ms", wrappedLabel);
                GUILayout.Label(session == null ? "Sent: 0 B | Received: 0 B | Queue: 0"
                    : "Sent: " + session.SentBytes + " B | Received: " + session.ReceivedBytes + " B | Queue: " + session.QueueDepth, wrappedLabel);
                GUILayout.Label(session == null ? "Pongs: 0 | Reliable probes: 0"
                    : "Pongs: " + session.PongsReceived + " | Reliable probes: " + session.ProbesReceived, wrappedLabel);
                GUILayout.Label(DiagnosticTrace.SafeText(notice, 180), wrappedLabel);
                GUILayout.Label("Problem note (up to 240 characters)");
                reportNote = GUILayout.TextField(reportNote, 240);
                GUI.enabled = reportProblem != null;
                if (GUILayout.Button("Report a problem")) ReportProblem();
            }
            finally { GUI.enabled = previousEnabled; GUILayout.EndScrollView(); GUILayout.EndArea(); }
        }

        private void Start(bool host)
        {
            try
            {
                int port;
                if (!Int32.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1024 || port > 65535)
                { notice = "Use a UDP port between 1024 and 65535."; return; }
                if (String.IsNullOrEmpty(displayName) || System.Text.Encoding.UTF8.GetByteCount(displayName) > 32)
                { notice = "Enter a display name of 1-32 UTF-8 bytes."; return; }
                Leave("new-session");
                session = new DiagnosticSession(RuntimeCompatibility.Create(), displayName);
                session.Event += OnEvent;
                if (host) session.Host(port); else session.Join(address, port);
                notice = "Transport active. World gameplay remains unavailable.";
                record("network.started", host ? "host diagnostic endpoint" : "client diagnostic endpoint");
            }
            catch (Exception error) { Fail("start", error); }
        }

        private void Perform(bool reliable)
        {
            try { if (!(reliable ? session.SendProbe("MSCMP reliable probe") : session.SendPing())) notice = "No peer accepted the send; retry after connection or queue recovery."; }
            catch (Exception error) { Fail("send", error); }
        }

        internal void CheckBindings()
        {
            if (!sceneReady) { notice = "Binding checks wait for the loaded game scene."; return; }
            LocalPlayerBinding player = null;
            SupportedItemBinding item = null;
            try
            {
                string playerReason, itemReason;
                bool playerBound = LocalPlayerBinding.TryCreate(sceneGeneration, out player, out playerReason);
                Vector3 position; float yaw;
                bool sampleValid = playerBound && player.TrySample(sceneGeneration, out position, out yaw);
                record("bindings.player", "bound=" + playerBound + " walkingPose=" + sampleValid + "; " + playerReason);
                bool itemBound = SupportedItemBinding.TryCreate(sceneGeneration, out item, out itemReason);
                record("bindings.cup", "bound=" + itemBound + "; " + itemReason);
                string observation = "Supported cup observation unavailable.";
                bool observed = itemBound && item.TryObserve(sceneGeneration, out observation);
                if (itemBound) record("bindings.cup-observation", observation);
                notice = "Player: " + (!playerBound ? "not bound" : sampleValid ? "bound, walking pose OK" : "bound, pose paused/unavailable")
                    + " | Cup: " + (!itemBound ? "not bound" : observed ? "bound, observed" : "bound, observation unavailable") + ". Details in the diagnostic log.";
            }
            catch (Exception error)
            {
                notice = "Binding check failed: " + error.GetType().Name + ". Details saved in the diagnostic log.";
                record("bindings.failed", error.ToString());
            }
            finally
            {
                if (player != null) player.Dispose();
                if (item != null) item.Dispose();
            }
        }

        private void ReportProblem()
        {
            if (reportProblem == null) return;
            try
            {
                reportProblem(reportNote);
                notice = "Problem report requested with your note and the current diagnostics.";
            }
            catch (Exception error)
            {
                notice = "Problem report failed: " + error.GetType().Name + ". Your note is still here.";
                record("diagnostic.problem-report-error", error.GetType().Name);
            }
        }

        private void OnEvent(SessionEvent value)
        {
            notice = value.Kind + ": " + value.Detail;
            record("network." + value.Kind, value.Detail + " player=" + value.PlayerId
                + " connection=" + value.ConnectionGeneration + " sequence=" + value.Sequence);
        }

        private void Fail(string operation, Exception error)
        {
            Leave("error");
            notice = operation + " failed: " + error.GetType().Name + ". Check the diagnostic log.";
            record("network.error", "operation=" + operation + " " + error.ToString());
        }

        internal void Leave(string reason)
        {
            StopPreview();
            if (session == null) return;
            DiagnosticSession previous = session;
            session = null;
            previous.Event -= OnEvent;
            try { previous.Dispose(); }
            catch (Exception error) { record("network.dispose-error", error.ToString()); }
            notice = "Disconnected: " + reason;
            record("network.stopped", reason);
        }

        private void StopPreview()
        {
            if (preview == null) return;
            AvatarPreview previous = preview; preview = null; previous.Dispose();
        }

        private void DrawCharacterPreview()
        {
            // Portrait cameras and textures belong to this panel's scene lifetime.
            // Drawing a selected character must never require starting a network session.
            Rect area = GUILayoutUtility.GetRect(88, 132, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            if (characterPreviewFailed)
            {
                GUI.Label(area, "Character preview unavailable. Use Retry character preview; details are in the diagnostic log.", wrappedLabel);
                return;
            }
            try
            {
                if (characterPreview == null) characterPreview = new CharacterPreview();
                GUI.DrawTexture(area, characterPreview.GetPreview(characterStyle), ScaleMode.ScaleToFit);
            }
            catch (Exception error)
            {
                StopCharacterPreview();
                characterPreviewFailed = true;
                record("characters.preview-unavailable", error.ToString());
                GUI.Label(area, "Character preview unavailable. Use Retry character preview; details are in the diagnostic log.", wrappedLabel);
            }
        }

        private void StopCharacterPreview()
        {
            if (characterPreview != null) { characterPreview.Dispose(); characterPreview = null; }
            characterPreviewFailed = false;
        }

        public void Dispose() { Leave("scene-or-mod-teardown"); StopCharacterPreview(); }
    }
}
