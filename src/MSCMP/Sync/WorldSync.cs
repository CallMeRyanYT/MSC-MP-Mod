using System;
using HutongGames.PlayMaker;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// The host's clock, sky and weather are the only ones: clients follow them. Money is one
    /// shared wallet - whatever anybody earns or spends changes it for everyone.
    /// </summary>
    internal static class WorldSync
    {
        static PlayMakerFSM color, clock, weather;
        static Transform clouds;
        static FsmInt colorTime, weatherType, cloudId, globalDay;
        static FsmFloat sunRotation, clockHours, clockMinutes;
        static FsmFloat wX, wZ, wOffset, wRotation, wSpeed, wPosX, wPosZ;
        static FsmBool rain;
        static FsmFloat money;
        static float lastMoney;
        static bool moneyKnown;
        static float nextSend, nextMoneySend;
        static bool found;

        const float SendInterval = 2f;

        public static void Init()
        {
            Session.On(Msg.TimeWeather, OnTimeWeather);
            Session.On(Msg.Money, OnMoney);
        }

        public static void OnWorldReady()
        {
            found = false;
            moneyKnown = false;
            try
            {
                Transform sunPivot = GameUtil.FindPath("MAP/SUN/Pivot");
                Transform sun = GameUtil.FindPath("MAP/SUN/Pivot/SUN");
                clouds = GameUtil.FindPath("MAP/CloudSystem/Clouds");
                PlayMakerFSM rotation = sunPivot != null ? GameUtil.GetFsm(sunPivot.gameObject, "Rotation") : null;
                color = sun != null ? GameUtil.GetFsm(sun.gameObject, "Color") : null;
                clock = sun != null ? GameUtil.GetFsm(sun.gameObject, "Clock") : null;
                weather = clouds != null ? GameUtil.GetFsm(clouds.gameObject, "Weather") : null;

                sunRotation = GameUtil.FsmFloat(rotation, "Rotation");
                colorTime = GameUtil.FsmInt(color, "Time");
                clockHours = GameUtil.FsmFloat(clock, "Hours");
                clockMinutes = GameUtil.FsmFloat(clock, "Minutes");
                weatherType = GameUtil.FsmInt(weather, "WeatherType");
                cloudId = GameUtil.FsmInt(weather, "WeatherCloudID");
                rain = GameUtil.FsmBool(weather, "Rain");
                wX = GameUtil.FsmFloat(weather, "X");
                wZ = GameUtil.FsmFloat(weather, "Z");
                wOffset = GameUtil.FsmFloat(weather, "Offset");
                wRotation = GameUtil.FsmFloat(weather, "Rotation");
                wSpeed = GameUtil.FsmFloat(weather, "Speed");
                wPosX = GameUtil.FsmFloat(weather, "PosX");
                wPosZ = GameUtil.FsmFloat(weather, "PosZ");
                globalDay = GameUtil.GlobalInt("GlobalDay");
                money = GameUtil.GlobalFloat("PlayerMoney");
                found = color != null && colorTime != null && sunRotation != null;
                if (!found) Log.Warn("Time of day objects not found - the clock will not be shared.");
                if (money == null) Log.Warn("PlayerMoney not found - money will not be shared.");
                else { lastMoney = money.Value; moneyKnown = true; }
            }
            catch (Exception e)
            {
                Log.Exception("Finding time/weather", e);
            }
        }

        public static void OnWorldLeft()
        {
            found = false;
            moneyKnown = false;
            color = clock = weather = null;
            clouds = null;
            money = null;
        }

        public static void Update()
        {
            if (!Session.Active || !Session.Connected) { if (money != null) lastMoney = money.Value; return; }
            float now = Time.realtimeSinceStartup;

            if (Session.IsHost && found && now >= nextSend)
            {
                nextSend = now + SendInterval;
                WriteTimeWeather(Session.Begin(Msg.TimeWeather));
                Session.BroadcastInGame(true, Channel.Reliable);
            }

            // Money: clients report what they earned or spent; the host owns the total.
            if (money != null && GameSession.CatchingUp) lastMoney = money.Value; // replayed history is not new spending
            else if (money != null && moneyKnown)
            {
                float m = money.Value;
                if (Mathf.Abs(m - lastMoney) > 0.001f)
                {
                    if (Session.IsHost)
                    {
                        if (now >= nextMoneySend)
                        {
                            nextMoneySend = now + 0.2f;
                            lastMoney = m;
                            NetWriter w = Session.Begin(Msg.Money);
                            w.Byte(0);
                            w.Float(m);
                            Session.BroadcastInGame(true, Channel.Reliable);
                        }
                    }
                    else
                    {
                        NetWriter w = Session.Begin(Msg.Money);
                        w.Byte(1);
                        w.Float(m - lastMoney);
                        Session.SendToHost(true, Channel.Reliable);
                        lastMoney = m;
                    }
                }
            }
        }

        static void WriteTimeWeather(NetWriter w)
        {
            w.Int(colorTime != null ? colorTime.Value : 0);
            w.Float(sunRotation != null ? sunRotation.Value : 0f);
            w.Float(clockHours != null ? clockHours.Value : 0f);
            w.Float(clockMinutes != null ? clockMinutes.Value : 0f);
            w.Int(globalDay != null ? globalDay.Value : -1);
            bool hasWeather = weather != null && clouds != null && weatherType != null && cloudId != null;
            w.Bool(hasWeather);
            if (!hasWeather) return;
            w.Int(weatherType.Value);
            w.Int(cloudId.Value);
            w.Bool(rain != null && rain.Value);
            w.Vector3(clouds.position);
            w.Quaternion(clouds.rotation);
            w.Float(V(wX)); w.Float(V(wZ)); w.Float(V(wOffset)); w.Float(V(wRotation));
            w.Float(V(wSpeed)); w.Float(V(wPosX)); w.Float(V(wPosZ));
        }

        static float V(FsmFloat f) { return f != null ? f.Value : 0f; }
        static void Set(FsmFloat f, float v) { if (f != null) f.Value = v; }

        static void OnTimeWeather(NetPlayer from, NetReader r)
        {
            if (Session.IsHost || !MP.InGame) return;
            int time = r.Int();
            float rot = r.Float();
            float hours = r.Float();
            float minutes = r.Float();
            int day = r.Int();
            bool hasWeather = r.Bool();
            if (!found) return;
            try
            {
                // Two-hour sky block (light, fog, colours) first, then the exact sun angle.
                if (colorTime.Value != time)
                {
                    colorTime.Value = time;
                    GameUtil.ForceState(color, "State 3");
                    Log.Debug("Clock set to the host's (" + time + ").");
                }
                if (Mathf.Abs(Mathf.DeltaAngle(sunRotation.Value, rot)) > 0.5f) sunRotation.Value = rot;
                if (clock != null && clockHours != null && hours > 0f && Mathf.Abs(clockHours.Value - hours) >= 1f)
                {
                    string hourState = ((int)hours).ToString();
                    if (GameUtil.GetState(clock, hourState) != null) GameUtil.ForceState(clock, hourState);
                }
                if (clockMinutes != null && Mathf.Abs(clockMinutes.Value - minutes) > 2f) clockMinutes.Value = minutes;
                if (globalDay != null && day >= 0 && globalDay.Value != day) globalDay.Value = day;
            }
            catch (Exception e) { Log.ExceptionOnce("Applying host time", e); }

            if (!hasWeather) return;
            int type = r.Int();
            int cloud = r.Int();
            bool raining = r.Bool();
            Vector3 pos = r.Vector3();
            Quaternion crot = r.Quaternion();
            float x = r.Float(), z = r.Float(), offset = r.Float(), wrot = r.Float(), speed = r.Float(), px = r.Float(), pz = r.Float();
            if (weather == null || clouds == null || weatherType == null || cloudId == null) return;
            try
            {
                Set(wX, x); Set(wZ, z); Set(wOffset, offset); Set(wRotation, wrot); Set(wSpeed, speed); Set(wPosX, px); Set(wPosZ, pz);
                if (weatherType.Value != type || cloudId.Value != cloud)
                {
                    weatherType.Value = type;
                    cloudId.Value = cloud;
                    clouds.position = pos;
                    clouds.rotation = crot;
                    GameUtil.ForceState(weather, "Set cloud");
                    Log.Debug("Weather set to the host's (type " + type + ").");
                }
                if (rain != null) rain.Value = raining;
                clouds.position = pos;
                clouds.rotation = crot;
            }
            catch (Exception e) { Log.ExceptionOnce("Applying host weather", e); }
        }

        static void OnMoney(NetPlayer from, NetReader r)
        {
            byte kind = r.Byte();
            float v = r.Float();
            if (money == null || !MP.InGame) return;
            if (kind == 0)
            {
                if (Session.IsHost) return;
                money.Value = v;
                lastMoney = v;
                moneyKnown = true;
            }
            else if (kind == 1 && Session.IsHost && from != null && !from.IsLocal)
            {
                if (float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v) > 1000000f) return;
                money.Value = money.Value + v; // the change is broadcast by Update
                Log.Debug(from.Name + (v >= 0 ? " earned " : " spent ") + Mathf.Abs(v).ToString("0.##") + " mk");
            }
        }

        public static void SendCatchUp(NetPlayer p)
        {
            if (found)
            {
                WriteTimeWeather(Session.Begin(Msg.TimeWeather));
                Session.SendTo(p, true, Channel.Reliable);
            }
            if (money != null)
            {
                NetWriter w = Session.Begin(Msg.Money);
                w.Byte(0);
                w.Float(money.Value);
                Session.SendTo(p, true, Channel.Reliable);
            }
        }
    }
}
