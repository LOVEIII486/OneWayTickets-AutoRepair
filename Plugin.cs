using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Foundations;
using Foundations.Tickers;
using HarmonyLib;
using UnityEngine;
using World;
using World.Devices;
using World.Devices.DeviceEmergencies.DeviceEmergenciesView;
using World.Player_Tool.ToolsLogic.Rags;

namespace OWT.AutoRepair
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class AutoRepairPlugin : BaseUnityPlugin
    {
        public const string Guid = "owt.autorepair";
        public const string Name = "OWT Auto Repair";
        public const string Version = "0.3.5";

        internal static ManualLogSource Log;

        private const string TickName = Guid + ".tick";

        private static Harmony _harmony;
        private static bool _errorLogged;
        private static bool _toolExceptionLogged;
        private static readonly int GuiWindowId = "OWT.AutoRepair.Gui".GetHashCode();
        private Rect _windowRect = new Rect(20f, 170f, 300f, 150f);

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Mode;
        internal static ConfigEntry<float> ServiceIntervalSeconds;
        internal static ConfigEntry<bool> WorkInSafeArea;
        internal static ConfigEntry<bool> RequireTools;
        internal static ConfigEntry<bool> LogRepairs;
        internal static ConfigEntry<bool> Diagnostics;
        internal static ConfigEntry<bool> TowelDirectMode;
        internal static ConfigEntry<float> TowelStallSeconds;
        internal static ConfigEntry<float> TowelSwapSeconds;
        internal static ConfigEntry<bool> ShowOnScreenButton;

        internal static ConfigEntry<float> ToolPickupDelaySeconds;
        internal static ConfigEntry<float> ClickIntervalSeconds;
        internal static ConfigEntry<float> ActionTimeoutSeconds;
        internal static ConfigEntry<float> ReleaseDelaySeconds;
        internal static ConfigEntry<float> ToolCooldownSeconds;
        internal static ConfigEntry<bool> ForceAutoRotate;

        internal static ConfigEntry<bool> RepairBroken;
        internal static ConfigEntry<float> RepairSeconds;
        internal static ConfigEntry<bool> RepairDamaged;
        internal static ConfigEntry<float> DamagedHpPerSecond;

        internal static ConfigEntry<bool> ExtinguishFire;
        internal static ConfigEntry<float> FireExtinguishPerSecond;
        internal static ConfigEntry<bool> DryWet;
        internal static ConfigEntry<bool> CleanAcid;
        internal static ConfigEntry<bool> CutVines;
        internal static ConfigEntry<bool> FixStupor;
        internal static ConfigEntry<bool> KickStupor;

        internal static ConfigEntry<bool> WakeComatose;
        internal static ConfigEntry<int> WakePower;

        internal static bool UseTools
        {
            get { return !string.Equals(Mode.Value, "Background", StringComparison.OrdinalIgnoreCase); }
        }

        internal static bool AllowFallback
        {
            get
            {
                return !RequireTools.Value
                    && !string.Equals(Mode.Value, "ToolsOnly", StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static int SecondsToTicks(float seconds)
        {
            return Mathf.Max(1, Mathf.RoundToInt(seconds * 120f));
        }

        private void Awake()
        {
            Log = Logger;
            BindConfig();
            Log.LogInfo(Name + " v" + Version + " loaded.");
            PatchWorldInit();
            PatchMousePosition();
            PatchToolSafety();
        }

        private void BindConfig()
        {
            Enabled = Config.Bind("1. General", "Enabled", true,
                "总开关。关闭后不执行任何自动维修。");
            Mode = Config.Bind("1. General", "Mode", "Hybrid",
                new ConfigDescription(
                    "Hybrid：有工具时用真实工具演出，没有工具时回退到后台处理。ToolsOnly：只用真实工具，没有工具就不处理。Background：旧版后台处理，不演出。",
                    new AcceptableValueList<string>("Hybrid", "ToolsOnly", "Background")));
            ServiceIntervalSeconds = Config.Bind("1. General", "ServiceIntervalSeconds", 0.1f,
                new ConfigDescription("后台模式每次处理水渍/酸液/藤蔓/故障的间隔（秒）。0.25 相当于每秒处理 4 次。",
                    new AcceptableValueRange<float>(0.05f, 2f)));
            WorkInSafeArea = Config.Bind("1. General", "WorkInSafeArea", false,
                "安全区里也自动处理。默认关闭，安全区本身会清除异常状态。");
            RequireTools = Config.Bind("1. General", "RequireTools", false,
                "为 true 时禁止后台回退，只使用工具箱里已有的真实工具；没有工具就不处理。");
            LogRepairs = Config.Bind("1. General", "LogRepairs", false,
                "在日志里记录选中的工具、开始/结束的动作和回退处理。");
            Diagnostics = Config.Bind("1. General", "Diagnostics", true,
                "记录毛巾/水滴/酸液任务的详细诊断日志，用来定位卡住问题。");
            TowelDirectMode = Config.Bind("1. General", "TowelDirectMode", false,
                "毛巾处理水滴/酸液时使用直接清理模式（稳定但不显示擦拭动作）。默认 false，使用游戏原本的擦拭动画。");
            TowelStallSeconds = Config.Bind("1. General", "TowelStallSeconds", 2f,
                new ConfigDescription("毛巾连续多久没有任何实际移除事件才触发兜底清理（秒）。",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            TowelSwapSeconds = Config.Bind("1. General", "TowelSwapSeconds", 1.2f,
                new ConfigDescription("毛巾在另一台机器也有水滴/酸液时，最多连续擦多久就换一台（秒）。",
                    new AcceptableValueRange<float>(0.5f, 5f)));
            ShowOnScreenButton = Config.Bind("1. General", "ShowOnScreenButton", true,
                "显示屏幕上的自动维修开关窗口，可以随时点击开关。");

            ToolPickupDelaySeconds = Config.Bind("1. Tool animation", "ToolPickupDelaySeconds", 0.05f,
                new ConfigDescription("自动从工具箱取工具前的停顿（秒），让取工具动作看起来更自然。",
                    new AcceptableValueRange<float>(0f, 2f)));
            ClickIntervalSeconds = Config.Bind("1. Tool animation", "ClickIntervalSeconds", 0.1f,
                new ConfigDescription("剪刀、抹布、锤子这类点击工具的点击间隔（秒）。工具自身的冷却仍会生效。",
                    new AcceptableValueRange<float>(0.05f, 2f)));
            ActionTimeoutSeconds = Config.Bind("1. Tool animation", "ActionTimeoutSeconds", 20f,
                new ConfigDescription("单个自动动作的最长时间（秒）。超时会放下工具，重新找下一个问题。",
                    new AcceptableValueRange<float>(1f, 120f)));
            ReleaseDelaySeconds = Config.Bind("1. Tool animation", "ReleaseDelaySeconds", 0.05f,
                new ConfigDescription("问题解决后保持工具动作的收尾时间（秒），让玩家看到完成反馈。",
                    new AcceptableValueRange<float>(0f, 3f)));
            ToolCooldownSeconds = Config.Bind("1. Tool animation", "ToolCooldownSeconds", 0.05f,
                new ConfigDescription("两个自动动作之间的间隔（秒）。",
                    new AcceptableValueRange<float>(0f, 5f)));
            ForceAutoRotate = Config.Bind("1. Tool animation", "ForceAutoRotate", true,
                "自动维修期间临时把当前扳手设为 auto_rotate=true，走游戏内自动扳手的同一条代码路径；结束后还原。");

            RepairBroken = Config.Bind("2. Repair", "RepairBrokenDevices", true,
                "自动修好已经损毁的装置。");
            RepairSeconds = Config.Bind("2. Repair", "RepairSeconds", 5f,
                new ConfigDescription("自动修好一台损毁装置所需时间（秒）。原版扳手需要累计 1500 点维修进度。",
                    new AcceptableValueRange<float>(0.5f, 60f)));
            RepairDamaged = Config.Bind("2. Repair", "RepairDamagedDevices", false,
                "自动回复未损毁装置的 HP。原版工具箱没有这个功能，默认关闭。");
            DamagedHpPerSecond = Config.Bind("2. Repair", "DamagedHpPercentPerSecond", 0.02f,
                new ConfigDescription("未损毁装置每秒回复的最大 HP 比例。0.02 表示每秒 2%。",
                    new AcceptableValueRange<float>(0f, 1f)));

            ExtinguishFire = Config.Bind("3. Device states", "ExtinguishFire", true,
                "自动灭火。");
            FireExtinguishPerSecond = Config.Bind("3. Device states", "FireExtinguishPerSecond", 30f,
                new ConfigDescription("每秒降低的火势。原版灭火器约为每秒 30。",
                    new AcceptableValueRange<float>(0f, 500f)));
            DryWet = Config.Bind("3. Device states", "DryWetDevices", true,
                "自动擦干装置上的水滴。");
            CleanAcid = Config.Bind("3. Device states", "CleanAcidDevices", true,
                "自动擦掉装置上的酸液。");
            CutVines = Config.Bind("3. Device states", "CutVines", true,
                "自动剪断缠绕装置的藤蔓。每剪一次会伤害附着物，和原版剪刀/锯子一致。");
            FixStupor = Config.Bind("3. Device states", "FixStupor", true,
                "自动处理装置故障（stupor）。");
            KickStupor = Config.Bind("3. Device states", "KickStupor", false,
                "故障时改用“踹一脚”。成功率高，但会伤害装置。关闭时使用无伤害的修复判定。");

            WakeComatose = Config.Bind("4. Characters", "WakeComatoseCharacters", true,
                "自动唤醒昏迷的角色。原版需要用闹钟/扩音器等工具。");
            WakePower = Config.Bind("4. Characters", "WakePowerPerAttempt", 3000,
                new ConfigDescription("每次唤醒减少的昏迷时间。原版 hammer_lv_1_effect 默认为 3000。",
                    new AcceptableValueRange<int>(1, 100000)));
        }

        private void OnGUI()
        {
            if (!ShowOnScreenButton.Value)
            {
                return;
            }

            _windowRect = GUI.Window(GuiWindowId, _windowRect, DrawWindow, "OWT Auto Repair");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Status: " + ToolAutomation.Status);
            GUILayout.Label("Mode: " + Mode.Value);

            if (GUILayout.Button(Enabled.Value ? "Auto Repair: ON" : "Auto Repair: OFF"))
            {
                ToggleAutoRepair();
            }

            bool force = ForceAutoRotate.Value;
            bool nextForce = GUILayout.Toggle(force, "Force Auto Wrench (auto_rotate)");
            if (nextForce != force)
            {
                ForceAutoRotate.Value = nextForce;
                Config.Save();
                Log.LogInfo(Name + ": ForceAutoRotate " + (nextForce ? "enabled" : "disabled") + ".");
            }

            if (GUILayout.Button("Hide window"))
            {
                ShowOnScreenButton.Value = false;
                Config.Save();
            }

            GUI.DragWindow();
        }

        private void ToggleAutoRepair()
        {
            Enabled.Value = !Enabled.Value;
            Config.Save();
            if (!Enabled.Value)
            {
                ToolAutomation.Abort("toggled off");
            }
            Log.LogInfo(Name + ": " + (Enabled.Value ? "enabled" : "disabled") + ".");
        }

        internal static void Diag(string message)
        {
            if (Diagnostics != null && Diagnostics.Value && Log != null)
            {
                Log.LogInfo("[AutoRepair:Diag] " + message);
            }
        }

        internal static void DiagWarn(string message)
        {
            if (Diagnostics != null && Diagnostics.Value && Log != null)
            {
                Log.LogWarning("[AutoRepair:Diag] " + message);
            }
        }

        private static void PatchToolSafety()
        {
            try
            {
                if (_harmony == null)
                {
                    _harmony = new Harmony(Guid);
                }

                MethodInfo finalizer = typeof(AutoRepairPlugin).GetMethod(
                    nameof(ToolExceptionFinalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (finalizer == null)
                {
                    return;
                }

                PatchFinalizer(typeof(BigRag), "tick", finalizer);
                PatchFinalizer(typeof(Rag), "OnPointerClick", finalizer);

                PatchTowelDeferral(typeof(BigRag), "tick");
                PatchTowelDeferral(typeof(Rag), "OnPointerClick");
                PatchViewReinitPrefix(typeof(DeviceWaterView), "reinit", nameof(DeferWaterReinit));
                PatchViewReinitPrefix(typeof(DeviceAcidView), "reinit", nameof(DeferAcidReinit));
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to install tool safety patches:\n" + ex);
            }
        }

        private static void PatchTowelDeferral(Type type, string methodName)
        {
            MethodInfo method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Log.LogWarning("Could not find " + type.Name + "." + methodName + " for deferral.");
                return;
            }

            MethodInfo prefix = typeof(AutoRepairPlugin).GetMethod(
                nameof(BeginTowelRaycast),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(AutoRepairPlugin).GetMethod(
                nameof(EndTowelRaycast),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix == null || postfix == null)
            {
                return;
            }

            try
            {
                _harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                Log.LogInfo("Patched " + type.Name + "." + methodName + " deferred view rebuild.");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to patch " + type.Name + "." + methodName + " deferral:\n" + ex);
            }
        }

        private static void PatchViewReinitPrefix(Type type, string methodName, string prefixName)
        {
            MethodInfo method = AccessTools.Method(type, methodName);
            MethodInfo prefix = typeof(AutoRepairPlugin).GetMethod(
                prefixName,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || prefix == null)
            {
                Log.LogWarning("Could not find " + type.Name + "." + methodName + " for reinit deferral.");
                return;
            }

            try
            {
                _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                Log.LogInfo("Patched " + type.Name + "." + methodName + " reinit deferral.");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to patch " + type.Name + "." + methodName + " reinit:\n" + ex);
            }
        }

        private static void BeginTowelRaycast()
        {
            ToolAutomation.BeginDeferredViewRebuild();
        }

        private static void EndTowelRaycast()
        {
            ToolAutomation.EndDeferredViewRebuild();
        }

        private static bool DeferWaterReinit(DeviceWaterView __instance)
        {
            if (!ToolAutomation.IsDeferringViewRebuild)
            {
                return true;
            }

            ToolAutomation.QueueWaterView(__instance);
            return false;
        }

        private static bool DeferAcidReinit(DeviceAcidView __instance)
        {
            if (!ToolAutomation.IsDeferringViewRebuild)
            {
                return true;
            }

            ToolAutomation.QueueAcidView(__instance);
            return false;
        }

        private static void PatchFinalizer(Type type, string methodName, MethodInfo finalizer)
        {
            MethodInfo method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Log.LogWarning("Could not find " + type.Name + "." + methodName + " for safety patch.");
                return;
            }

            try
            {
                _harmony.Patch(method, finalizer: new HarmonyMethod(finalizer));
                Log.LogInfo("Patched " + type.Name + "." + methodName + " exception guard.");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to patch " + type.Name + "." + methodName + ":\n" + ex);
            }
        }

        private static Exception ToolExceptionFinalizer(Exception __exception)
        {
            ToolAutomation.EndDeferredViewRebuild();

            if (__exception != null
                && (__exception is MissingReferenceException || __exception is NullReferenceException))
            {
                if (!_toolExceptionLogged)
                {
                    _toolExceptionLogged = true;
                    Log.LogWarning("Suppressed a tool exception:\n" + __exception);
                }
                return null;
            }

            return __exception;
        }

        private static void PatchWorldInit()
        {
            try
            {
                Assembly worldAssembly = Assembly.Load("World");
                Type type = worldAssembly.GetType("World.WorldSceneRoot");
                MethodInfo method = type != null ? AccessTools.Method(type, "on_init") : null;
                if (method == null)
                {
                    Log.LogError("Could not find World.WorldSceneRoot.on_init. Auto repair will not run.");
                    return;
                }

                MethodInfo postfix = typeof(AutoRepairPlugin).GetMethod(
                    nameof(OnWorldInit),
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                {
                    Log.LogError("Could not find OnWorldInit postfix.");
                    return;
                }

                _harmony = new Harmony(Guid);
                _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                Log.LogInfo("Patched World.WorldSceneRoot.on_init.");
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to patch World.WorldSceneRoot.on_init:\n" + ex);
            }
        }

        private static void PatchMousePosition()
        {
            try
            {
                Type baseType = typeof(Commons.Input.BaseInputController<World.Input.WorldInputController>);
                MethodInfo method = AccessTools.Method(baseType, "GetScreenMousePosition");
                if (method == null)
                {
                    Log.LogWarning("Could not find GetScreenMousePosition; tool automation will not move the tool.");
                    return;
                }

                MethodInfo postfix = typeof(AutoRepairPlugin).GetMethod(
                    nameof(GetScreenMousePositionPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                {
                    return;
                }

                if (_harmony == null)
                {
                    _harmony = new Harmony(Guid);
                }
                _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                Log.LogInfo("Patched BaseInputController.GetScreenMousePosition.");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to patch GetScreenMousePosition:\n" + ex);
            }
        }

        private static void GetScreenMousePositionPostfix(ref Vector2 __result)
        {
            if (ToolAutomation.OverrideMouse && Time.timeScale > 0f)
            {
                __result = ToolAutomation.MousePosition;
            }
        }

        private static void OnWorldInit()
        {
            _errorLogged = false;
            ToolAutomation.Reset();

            try
            {
                Singleton<Ticker>.instance.add_tick(100, TickName, ServiceTick);
                Log.LogInfo("Auto repair tick registered.");
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to register auto repair tick:\n" + ex);
            }
        }

        private static void ServiceTick()
        {
            if (!Enabled.Value)
            {
                ToolAutomation.Abort("disabled");
                return;
            }

            try
            {
                ServiceTickInternal();
            }
            catch (Exception ex)
            {
                ToolAutomation.Abort("tick exception");
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Log.LogError("Auto repair tick failed:\n" + ex);
                }
            }
        }

        private static void ServiceTickInternal()
        {
            WorldContext context = Singleton<WorldContext>.instance;
            if (context == null)
            {
                return;
            }

            if (!WorkInSafeArea.Value && (context.in_safe_area || context.in_safe_area2))
            {
                ToolAutomation.Abort("safe area");
                return;
            }

            if (!Singleton<Mission>.instance.try_get_mgr("DeviceMgr", out DeviceMgr deviceMgr) || deviceMgr == null)
            {
                ToolAutomation.Abort("no DeviceMgr");
                return;
            }

            ToolAutomation.Tick(context, deviceMgr);
        }
    }
}
