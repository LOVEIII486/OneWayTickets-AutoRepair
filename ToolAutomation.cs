using System;
using System.Collections.Generic;
using Foundations;
using UnityEngine;
using UnityEngine.EventSystems;
using World;
using World.Adherents;
using World.Characters;
using World.Characters.CharacterStates;
using World.Devices;
using World.Devices.DeviceEmergencies;
using World.Devices.DeviceEmergencies.DeviceEmergenciesView;
using World.Devices.DeviceUiViews;
using World.Helpers;
using World.Player_Tool;
using World.Player_Tool.ToolsLogic;
using World.Player_Tool.ToolsLogic.Rags;
using World.Player_Tool.ToolsLogic.Saws;
using World.Player_Tool.ToolsLogic.WakeUp;

namespace OWT.AutoRepair
{
    internal enum MaintenanceKind
    {
        Repair,
        Fire,
        Coma,
        Bind,
        Acid,
        Water,
        Stupor
    }

    internal enum ToolActionKind
    {
        None,
        Hold,
        Click,
        Wrench,
        Saw
    }

    internal sealed class MaintenanceJob
    {
        public MaintenanceKind Kind;
        public Device Device;
        public Character Character;
        public DeviceEmergency Emergency;
        public ToolView Tool;
        public ToolActionKind Action;
        public Func<Component> TargetProvider;
        public bool VisibleClick;
        public int StartTick;
        public int NextActionTick;
        public int FinishTick;
        public bool Picked;
        public bool Down;
        public bool Clicked;
        public bool BoxOpen;
        public bool Finishing;
        public bool AutoRotateForced;
        public bool AutoRotateOriginal;
    }

    internal static class ToolAutomation
    {
        internal static bool OverrideMouse { get; private set; }
        internal static Vector2 MousePosition { get; private set; }

        private static readonly Dictionary<Device, float> RepairProgress = new Dictionary<Device, float>();
        private static readonly HashSet<MaintenanceKind> NoToolLogged = new HashSet<MaintenanceKind>();
        private static MaintenanceJob _job;
        private static int _tick;
        private static int _cooldownUntil;
        private static int _damagedHealTimer;

        internal static void Reset()
        {
            Abort("world init");
            _tick = 0;
            _cooldownUntil = 0;
            _damagedHealTimer = 0;
            RepairProgress.Clear();
            NoToolLogged.Clear();
            LogOwnedTools();
        }

        internal static void Tick(WorldContext context, DeviceMgr deviceMgr)
        {
            _tick++;

            if (_job == null)
            {
                HealDamagedDevices(deviceMgr);

                if (_tick < _cooldownUntil)
                {
                    return;
                }

                MaintenanceJob job = FindJob(deviceMgr);
                if (job == null)
                {
                    SetMouse(false, Vector2.zero);
                    return;
                }

                StartJob(job);
                return;
            }

            UpdateJob(deviceMgr);
        }

        internal static void Abort(string reason)
        {
            if (_job != null)
            {
                try
                {
                    ReleaseTool(_job);
                }
                catch (Exception ex)
                {
                    LogWarning("Abort release failed: " + ex.Message);
                }
                _job = null;
            }

            SetMouse(false, Vector2.zero);
        }

        private static MaintenanceJob FindJob(DeviceMgr deviceMgr)
        {
            if (deviceMgr == null || deviceMgr.slots_device == null)
            {
                return null;
            }

            if (PlayerUsingTools(null))
            {
                return null;
            }

            if (AutoRepairPlugin.RepairBroken.Value || AutoRepairPlugin.ExtinguishFire.Value)
            {
                foreach (DeviceSlot slot in deviceMgr.slots_device)
                {
                    Device device = slot != null ? slot.slot_device : null;
                    if (device == null)
                    {
                        continue;
                    }

                    if (!device.is_validate && AutoRepairPlugin.RepairBroken.Value)
                    {
                        MaintenanceJob job = TryCreateJob(MaintenanceKind.Repair, device, null, device.device_emergency);
                        if (job != null)
                        {
                            return job;
                        }
                    }

                    DeviceFired fired = device.device_emergency as DeviceFired;
                    if (fired != null && fired.fire_value > 0f && AutoRepairPlugin.ExtinguishFire.Value)
                    {
                        MaintenanceJob job = TryCreateJob(MaintenanceKind.Fire, device, null, fired);
                        if (job != null)
                        {
                            return job;
                        }
                    }
                }
            }

            if (AutoRepairPlugin.WakeComatose.Value)
            {
                List<Character> characters = Character_Helper.GetAllCharacters();
                if (characters != null)
                {
                    foreach (Character character in characters)
                    {
                        if (character != null && character.state is Coma)
                        {
                            MaintenanceJob job = TryCreateJob(MaintenanceKind.Coma, null, character, null);
                            if (job != null)
                            {
                                return job;
                            }
                        }
                    }
                }
            }

            foreach (DeviceSlot slot in deviceMgr.slots_device)
            {
                Device device = slot != null ? slot.slot_device : null;
                if (device == null)
                {
                    continue;
                }

                DeviceEmergency emergency = device.device_emergency;
                if (emergency is DeviceBind && AutoRepairPlugin.CutVines.Value)
                {
                    DeviceBind bind = (DeviceBind)emergency;
                    if (bind.vine_list != null && bind.vine_list.Count > 0)
                    {
                        MaintenanceJob job = TryCreateJob(MaintenanceKind.Bind, device, null, bind);
                        if (job != null)
                        {
                            return job;
                        }
                    }
                }
                else if (emergency is DeviceAcid && AutoRepairPlugin.CleanAcid.Value)
                {
                    DeviceAcid acid = (DeviceAcid)emergency;
                    if (acid.acids != null && acid.acids.Count > 0)
                    {
                        MaintenanceJob job = TryCreateJob(MaintenanceKind.Acid, device, null, acid);
                        if (job != null)
                        {
                            return job;
                        }
                    }
                }
                else if (emergency is DeviceWet && AutoRepairPlugin.DryWet.Value)
                {
                    DeviceWet wet = (DeviceWet)emergency;
                    if (wet.waterdrops != null && wet.waterdrops.Count > 0)
                    {
                        MaintenanceJob job = TryCreateJob(MaintenanceKind.Water, device, null, wet);
                        if (job != null)
                        {
                            return job;
                        }
                    }
                }
                else if (emergency is DeviceStupor && AutoRepairPlugin.FixStupor.Value)
                {
                    MaintenanceJob job = TryCreateJob(MaintenanceKind.Stupor, device, null, emergency);
                    if (job != null)
                    {
                        return job;
                    }
                }
            }

            return null;
        }

        private static MaintenanceJob TryCreateJob(
            MaintenanceKind kind,
            Device device,
            Character character,
            DeviceEmergency emergency)
        {
            ToolView tool = null;

            if (AutoRepairPlugin.UseTools && kind != MaintenanceKind.Stupor)
            {
                tool = FindTool(kind);
                if (tool == null)
                {
                    LogNoTool(kind);
                    if (!AutoRepairPlugin.AllowFallback)
                    {
                        return null;
                    }
                }
            }

            Func<Component> provider = BuildTargetProvider(kind, device, character, tool);
            bool targetOk = false;
            Component target = SafeGet(provider);
            if (target != null)
            {
                targetOk = ScreenPos(target) != null;
            }

            if (tool != null && !targetOk)
            {
                tool = null;
                if (!AutoRepairPlugin.AllowFallback)
                {
                    return null;
                }
            }

            bool visibleClick = false;
            if (kind == MaintenanceKind.Stupor && AutoRepairPlugin.UseTools)
            {
                visibleClick = targetOk;
                if (!visibleClick && !AutoRepairPlugin.AllowFallback)
                {
                    return null;
                }
            }

            if (tool == null && kind != MaintenanceKind.Stupor && AutoRepairPlugin.UseTools
                && !AutoRepairPlugin.AllowFallback)
            {
                return null;
            }

            return new MaintenanceJob
            {
                Kind = kind,
                Device = device,
                Character = character,
                Emergency = emergency,
                Tool = tool,
                Action = GetAction(tool),
                TargetProvider = provider,
                VisibleClick = visibleClick
            };
        }

        private static ToolView FindTool(MaintenanceKind kind)
        {
            PlayerToolMgrView view = GetToolView();
            if (view == null || view.owner == null || view.tool_view_list == null)
            {
                return null;
            }

            string[] names = GetToolNames(kind);
            if (names == null)
            {
                return null;
            }

            PlayerToolMgr manager = view.owner;
            foreach (string name in names)
            {
                foreach (ToolView toolView in view.tool_view_list)
                {
                    if (toolView == null || toolView.logic == null || toolView.tool_data == null)
                    {
                        continue;
                    }

                    int currentLevel;
                    if (!manager.tools.TryGetValue(toolView.tool_data.id, out currentLevel))
                    {
                        continue;
                    }

                    if (toolView.tool_data.level != currentLevel)
                    {
                        continue;
                    }

                    if (toolView.logic.GetType().Name == name)
                    {
                        return toolView;
                    }
                }
            }

            return null;
        }

        private static string[] GetToolNames(MaintenanceKind kind)
        {
            switch (kind)
            {
                case MaintenanceKind.Repair:
                    return new[] { "FixWrench" };
                case MaintenanceKind.Fire:
                    return new[] { "Extinguisher" };
                case MaintenanceKind.Water:
                case MaintenanceKind.Acid:
                    return new[] { "Drier", "Mop", "BigRag", "Rag" };
                case MaintenanceKind.Bind:
                    return new[] { "CircleSaw", "ChainSaw", "Scissors", "Saw" };
                case MaintenanceKind.Coma:
                    return new[] { "Clock", "Tweeter", "AED", "InflatingHammer", "ScreamingChicken" };
                default:
                    return null;
            }
        }

        private static ToolActionKind GetAction(ToolView tool)
        {
            if (tool == null || tool.logic == null)
            {
                return ToolActionKind.None;
            }

            ToolLogic logic = tool.logic;
            if (logic is FixWrench)
            {
                return ToolActionKind.Wrench;
            }
            if (logic is Saw && !(logic is ChainSaw) && !(logic is CircleSaw))
            {
                return ToolActionKind.Saw;
            }
            if (logic is Rag && !(logic is BigRag))
            {
                return ToolActionKind.Click;
            }
            if (logic is Scissors || logic is InflatingHammer || logic is ScreamingChicken)
            {
                return ToolActionKind.Click;
            }
            return ToolActionKind.Hold;
        }

        private static Func<Component> BuildTargetProvider(
            MaintenanceKind kind,
            Device device,
            Character character,
            ToolView tool)
        {
            switch (kind)
            {
                case MaintenanceKind.Repair:
                    return delegate { return FindFixTarget(device); };
                case MaintenanceKind.Fire:
                    return delegate { return FindFireTarget(device); };
                case MaintenanceKind.Water:
                    if (tool != null && (tool.logic is Mop || tool.logic is Drier))
                    {
                        return delegate { return FindDeviceView(device); };
                    }
                    return delegate { return FindWaterSingle(device) ?? FindDeviceView(device); };
                case MaintenanceKind.Acid:
                    if (tool != null && (tool.logic is Mop || tool.logic is Drier))
                    {
                        return delegate { return FindDeviceView(device); };
                    }
                    return delegate { return FindAcidSingle(device) ?? FindDeviceView(device); };
                case MaintenanceKind.Bind:
                    if (tool != null && tool.logic is CircleSaw)
                    {
                        return delegate { return FindDeviceView(device); };
                    }
                    return delegate { return FindBindSingle(device) ?? FindDeviceView(device); };
                case MaintenanceKind.Stupor:
                    return delegate { return FindDeviceView(device); };
                case MaintenanceKind.Coma:
                    return delegate { return FindBuffView(character); };
                default:
                    return null;
            }
        }

        private static void ApplyForceAutoRotate(MaintenanceJob job)
        {
            if (job.Tool == null || !AutoRepairPlugin.ForceAutoRotate.Value)
            {
                return;
            }

            FixWrench wrench = job.Tool.logic as FixWrench;
            if (wrench == null || wrench.auto_rotate)
            {
                return;
            }

            job.AutoRotateOriginal = wrench.auto_rotate;
            wrench.auto_rotate = true;
            job.AutoRotateForced = true;
        }

        private static void StartJob(MaintenanceJob job)
        {
            _job = job;
            job.StartTick = _tick;
            job.NextActionTick = _tick + AutoRepairPlugin.SecondsToTicks(AutoRepairPlugin.ToolPickupDelaySeconds.Value);

            if (job.Tool != null)
            {
                PlayerToolMgrView toolView = GetToolView();
                if (toolView == null || toolView.owner == null)
                {
                    FinishJobNow(false);
                    return;
                }

                toolView.SetBox(true);
                job.BoxOpen = true;
                ApplyForceAutoRotate(job);

                if (job.Action == ToolActionKind.Wrench)
                {
                    Vector2? target = GetTargetScreen(job);
                    if (target == null)
                    {
                        FinishJobNow(false);
                        return;
                    }

                    SetMouse(true, target.Value);
                    SendClick(job.Tool.logic, PointerEventData.InputButton.Left, target.Value);
                    job.Clicked = true;
                    if (toolView.IsPicking(job.Tool))
                    {
                        job.Picked = true;
                        job.BoxOpen = false;
                    }
                    else
                    {
                        FinishJobNow(false);
                        return;
                    }
                }

                FixWrench wrench = job.Tool.logic as FixWrench;
                string wrenchInfo = wrench == null
                    ? ""
                    : " (auto_rotate=" + wrench.auto_rotate
                        + ", double_direction=" + wrench.double_direction + ")";
                LogInfo("Start " + job.Kind + " with " + job.Tool.logic.GetType().Name
                    + wrenchInfo + ".");
            }
            else if (job.VisibleClick)
            {
                Vector2? target = GetTargetScreen(job);
                if (target == null)
                {
                    FinishJobNow(false);
                    return;
                }
                SetMouse(true, target.Value);
                job.NextActionTick = _tick;
                LogInfo("Start " + job.Kind + " with the on-screen repair control.");
            }
            else
            {
                SetMouse(false, Vector2.zero);
                job.NextActionTick = _tick;
                LogInfo("Start " + job.Kind + " in background mode.");
            }
        }

        private static void UpdateJob(DeviceMgr deviceMgr)
        {
            MaintenanceJob job = _job;
            if (job == null)
            {
                return;
            }

            if (job.Finishing)
            {
                if (_tick >= job.FinishTick)
                {
                    FinishJobNow(true);
                }
                return;
            }

            if (PlayerUsingTools(job.Tool))
            {
                BeginFinish(false);
                return;
            }

            if (job.Device != null && !DeviceInSlots(deviceMgr, job.Device))
            {
                BeginFinish(false);
                return;
            }

            if (JobResolved(job))
            {
                BeginFinish(true);
                return;
            }

            int timeout = AutoRepairPlugin.SecondsToTicks(AutoRepairPlugin.ActionTimeoutSeconds.Value);
            if (_tick - job.StartTick > timeout)
            {
                BeginFinish(false);
                return;
            }

            if (job.Tool != null)
            {
                UpdateToolJob(job);
            }
            else if (job.VisibleClick)
            {
                UpdateVisibleClick(job);
            }
            else
            {
                UpdateBackgroundJob(job);
            }
        }

        private static void UpdateToolJob(MaintenanceJob job)
        {
            PlayerToolMgrView toolView = GetToolView();
            if (toolView == null || toolView.owner == null)
            {
                BeginFinish(false);
                return;
            }

            if (job.Action != ToolActionKind.Wrench)
            {
                if (!job.Picked)
                {
                    if (_tick < job.NextActionTick)
                    {
                        return;
                    }

                    if (!PickUpTool(job, toolView))
                    {
                        BeginFinish(false);
                    }
                    return;
                }

                if (!toolView.IsPicking(job.Tool))
                {
                    BeginFinish(false);
                    return;
                }
            }
            else if (job.Picked && !toolView.IsPicking(job.Tool))
            {
                BeginFinish(false);
                return;
            }

            Vector2? target = GetTargetScreen(job);
            if (target == null)
            {
                BeginFinish(false);
                return;
            }

            Vector2 toolPos = AdjustForTool(job, target.Value);
            SetMouse(true, toolPos);

            switch (job.Action)
            {
                case ToolActionKind.Wrench:
                    if (!job.Clicked)
                    {
                        SendClick(job.Tool.logic, PointerEventData.InputButton.Left, toolPos);
                        job.Clicked = true;
                    }
                    SendDown(job.Tool.logic, PointerEventData.InputButton.Left, toolPos);
                    job.Down = true;
                    break;

                case ToolActionKind.Hold:
                    if (!job.Down)
                    {
                        SendDown(job.Tool.logic, PointerEventData.InputButton.Left, toolPos);
                        job.Down = true;
                    }
                    break;

                case ToolActionKind.Click:
                    if (_tick >= job.NextActionTick)
                    {
                        SendClick(job.Tool.logic, PointerEventData.InputButton.Left, toolPos);
                        job.NextActionTick = _tick
                            + AutoRepairPlugin.SecondsToTicks(AutoRepairPlugin.ClickIntervalSeconds.Value);
                    }
                    break;

                case ToolActionKind.Saw:
                    if (!job.Clicked)
                    {
                        SendClick(job.Tool.logic, PointerEventData.InputButton.Left, toolPos);
                        job.Clicked = true;
                        return;
                    }
                    if (!job.Down)
                    {
                        SendDown(job.Tool.logic, PointerEventData.InputButton.Left, toolPos);
                        job.Down = true;
                    }
                    break;
            }
        }

        private static bool PickUpTool(MaintenanceJob job, PlayerToolMgrView toolView)
        {
            if (job.Tool == null || job.Tool.logic == null)
            {
                return false;
            }

            SendClick(job.Tool.logic, PointerEventData.InputButton.Left, new Vector2(-10000f, -10000f));
            if (!toolView.IsPicking(job.Tool))
            {
                return false;
            }

            job.Picked = true;
            job.BoxOpen = false;
            job.NextActionTick = _tick + 1;
            return true;
        }

        private static void UpdateVisibleClick(MaintenanceJob job)
        {
            Vector2? target = GetTargetScreen(job);
            if (target == null)
            {
                BeginFinish(false);
                return;
            }

            SetMouse(true, target.Value);

            if (_tick < job.NextActionTick)
            {
                return;
            }

            DeviceStuporView view = FindEmergencyView<DeviceStuporView>(job.Device);
            if (view == null || view.kick == null)
            {
                BeginFinish(false);
                return;
            }

            IPointerDownHandler down = view.kick as IPointerDownHandler;
            IPointerClickHandler click = view.kick as IPointerClickHandler;
            IPointerUpHandler up = view.kick as IPointerUpHandler;

            PointerEventData eventData = MakeEvent(PointerEventData.InputButton.Left, target.Value);
            if (down != null)
            {
                down.OnPointerDown(eventData);
            }
            if (click != null)
            {
                click.OnPointerClick(eventData);
            }
            if (up != null)
            {
                up.OnPointerUp(eventData);
            }

            job.NextActionTick = _tick
                + AutoRepairPlugin.SecondsToTicks(AutoRepairPlugin.ClickIntervalSeconds.Value);
        }

        private static void UpdateBackgroundJob(MaintenanceJob job)
        {
            SetMouse(false, Vector2.zero);

            if (_tick < job.NextActionTick)
            {
                return;
            }

            float interval = Mathf.Clamp(AutoRepairPlugin.ServiceIntervalSeconds.Value, 0.05f, 2f);
            RunBackgroundStep(job, interval);
            job.NextActionTick = _tick + AutoRepairPlugin.SecondsToTicks(interval);
        }

        private static void RunBackgroundStep(MaintenanceJob job, float interval)
        {
            switch (job.Kind)
            {
                case MaintenanceKind.Repair:
                    RunRepairStep(job, interval);
                    break;

                case MaintenanceKind.Fire:
                    if (job.Device != null && job.Device.device_emergency is DeviceFired)
                    {
                        ((DeviceFired)job.Device.device_emergency).ChangeFire(
                            -AutoRepairPlugin.FireExtinguishPerSecond.Value * interval);
                    }
                    break;

                case MaintenanceKind.Water:
                    if (job.Device != null && job.Device.device_emergency is DeviceWet)
                    {
                        DeviceWet wet = (DeviceWet)job.Device.device_emergency;
                        if (wet.waterdrops != null && wet.waterdrops.Count > 0)
                        {
                            wet.RemoveWaterDrop(wet.waterdrops[wet.waterdrops.Count - 1]);
                        }
                    }
                    break;

                case MaintenanceKind.Acid:
                    if (job.Device != null && job.Device.device_emergency is DeviceAcid)
                    {
                        DeviceAcid acid = (DeviceAcid)job.Device.device_emergency;
                        if (acid.acids != null && acid.acids.Count > 0)
                        {
                            acid.RemoveAcid(acid.acids[acid.acids.Count - 1]);
                        }
                    }
                    break;

                case MaintenanceKind.Bind:
                    if (job.Device != null && job.Device.device_emergency is DeviceBind)
                    {
                        CutOneVine((DeviceBind)job.Device.device_emergency);
                    }
                    break;

                case MaintenanceKind.Stupor:
                    if (job.Device != null && job.Device.device_emergency is DeviceStupor)
                    {
                        DeviceStupor stupor = (DeviceStupor)job.Device.device_emergency;
                        if (AutoRepairPlugin.KickStupor.Value)
                        {
                            stupor.Kick();
                        }
                        else
                        {
                            stupor.Fix();
                        }
                    }
                    break;

                case MaintenanceKind.Coma:
                    if (job.Character != null && job.Character.state is Coma)
                    {
                        job.Character.state.trigger(Mathf.Max(1, AutoRepairPlugin.WakePower.Value));
                    }
                    break;
            }
        }

        private static void HealDamagedDevices(DeviceMgr deviceMgr)
        {
            if (deviceMgr == null || deviceMgr.slots_device == null)
            {
                return;
            }

            if (!AutoRepairPlugin.RepairDamaged.Value)
            {
                return;
            }

            _damagedHealTimer++;
            if (_damagedHealTimer < 120)
            {
                return;
            }
            _damagedHealTimer = 0;

            float fraction = Mathf.Clamp(AutoRepairPlugin.DamagedHpPerSecond.Value, 0f, 1f);
            if (fraction <= 0f)
            {
                return;
            }

            foreach (DeviceSlot slot in deviceMgr.slots_device)
            {
                Device device = slot != null ? slot.slot_device : null;
                if (device == null
                    || !device.is_validate
                    || device.max_hp <= 0
                    || device.current_hp >= device.max_hp)
                {
                    continue;
                }

                int amount = Mathf.Max(1, Mathf.RoundToInt(device.max_hp * fraction));
                device.Fix(amount);
            }
        }

        private static void RunRepairStep(MaintenanceJob job, float interval)
        {
            Device device = job.Device;
            if (device == null || device.is_validate)
            {
                RepairProgress.Remove(device);
                return;
            }

            float seconds = Mathf.Max(0.5f, AutoRepairPlugin.RepairSeconds.Value);
            float progress;
            RepairProgress.TryGetValue(device, out progress);
            progress += 1500f * interval / seconds;

            if (progress >= 1500f)
            {
                progress = 0f;
                device.FixDisable();
                LogInfo("Background repair completed.");
            }

            RepairProgress[device] = progress;
        }

        private static void CutOneVine(DeviceBind bind)
        {
            if (bind == null || bind.adherents_vines_dic == null)
            {
                return;
            }

            Device owner = bind.owner;
            if (owner == null)
            {
                return;
            }

            List<Adherent> adherents = Adherent_Helper.GetTargetAdherent(owner);
            if (adherents == null)
            {
                return;
            }

            foreach (Adherent adherent in adherents)
            {
                if (adherent == null || adherent.desc == null)
                {
                    continue;
                }

                List<vine> vines;
                if (!bind.adherents_vines_dic.TryGetValue(adherent, out vines)
                    || vines == null
                    || vines.Count == 0)
                {
                    continue;
                }

                int ropeNum = Mathf.Max(1, adherent.desc.DP_rope_num);
                int damage = Mathf.CeilToInt((float)adherent.desc.hp_max / ropeNum);
                BattleUtility.do_real_hurt(adherent, damage);

                vine target = vines[vines.Count - 1];
                if (target != null)
                {
                    bind.RemoveVine(adherent, target);
                }
                bind.UpdateVine();
                return;
            }
        }

        private static void BeginFinish(bool resolved)
        {
            MaintenanceJob job = _job;
            if (job == null || job.Finishing)
            {
                return;
            }

            if (job.Tool == null)
            {
                FinishJobNow(resolved);
                return;
            }

            job.Finishing = true;
            job.FinishTick = _tick + AutoRepairPlugin.SecondsToTicks(AutoRepairPlugin.ReleaseDelaySeconds.Value);

            if (job.Down && job.Tool.logic != null)
            {
                SendUp(job.Tool.logic, PointerEventData.InputButton.Left, MousePosition);
                job.Down = false;
            }

            LogInfo((resolved ? "Finished " : "Aborted ") + job.Kind + ".");
        }

        private static void FinishJobNow(bool resolved)
        {
            MaintenanceJob job = _job;
            if (job != null)
            {
                ReleaseTool(job);
                _job = null;
            }

            SetMouse(false, Vector2.zero);
            _cooldownUntil = _tick + AutoRepairPlugin.SecondsToTicks(AutoRepairPlugin.ToolCooldownSeconds.Value);
        }

        private static void ReleaseTool(MaintenanceJob job)
        {
            if (job == null)
            {
                return;
            }

            PlayerToolMgrView toolView = GetToolView();

            if (job.Tool != null && job.Tool.logic != null)
            {
                if (job.Down)
                {
                    SendUp(job.Tool.logic, PointerEventData.InputButton.Left, MousePosition);
                    job.Down = false;
                }

                SendClick(job.Tool.logic, PointerEventData.InputButton.Right, MousePosition);
                job.Picked = false;
            }

            if (job.BoxOpen)
            {
                if (toolView != null)
                {
                    toolView.SetBox(false);
                }
                job.BoxOpen = false;
            }

            if (job.AutoRotateForced && job.Tool != null)
            {
                FixWrench wrench = job.Tool.logic as FixWrench;
                if (wrench != null)
                {
                    wrench.auto_rotate = job.AutoRotateOriginal;
                }
                job.AutoRotateForced = false;
            }
        }

        private static bool JobResolved(MaintenanceJob job)
        {
            switch (job.Kind)
            {
                case MaintenanceKind.Repair:
                    return job.Device != null && job.Device.is_validate;

                case MaintenanceKind.Fire:
                    return job.Device == null
                        || !(job.Device.device_emergency is DeviceFired)
                        || job.Device.state != Device.DeviceState.fired;

                case MaintenanceKind.Water:
                    if (job.Device == null)
                    {
                        return true;
                    }
                    DeviceWet wet = job.Device.device_emergency as DeviceWet;
                    return wet == null || wet.waterdrops == null || wet.waterdrops.Count == 0;

                case MaintenanceKind.Acid:
                    if (job.Device == null)
                    {
                        return true;
                    }
                    DeviceAcid acid = job.Device.device_emergency as DeviceAcid;
                    return acid == null || acid.acids == null || acid.acids.Count == 0;

                case MaintenanceKind.Bind:
                    if (job.Device == null)
                    {
                        return true;
                    }
                    DeviceBind bind = job.Device.device_emergency as DeviceBind;
                    return bind == null || bind.vine_list == null || bind.vine_list.Count == 0;

                case MaintenanceKind.Stupor:
                    return job.Device == null || !(job.Device.device_emergency is DeviceStupor);

                case MaintenanceKind.Coma:
                    return job.Character == null || !(job.Character.state is Coma);

                default:
                    return true;
            }
        }

        private static bool DeviceInSlots(DeviceMgr deviceMgr, Device device)
        {
            if (deviceMgr == null || deviceMgr.slots_device == null)
            {
                return false;
            }

            foreach (DeviceSlot slot in deviceMgr.slots_device)
            {
                if (slot != null && slot.slot_device == device)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool PlayerUsingTools(ToolView except)
        {
            PlayerToolMgrView view = GetToolView();
            if (view == null || view.tool_view_list == null)
            {
                return false;
            }

            foreach (ToolView toolView in view.tool_view_list)
            {
                if (toolView == null || toolView == except)
                {
                    continue;
                }

                if (view.IsPicking(toolView))
                {
                    return true;
                }
            }

            return false;
        }

        private static PlayerToolMgrView GetToolView()
        {
            PlayerToolMgr manager;
            Mission mission = Singleton<Mission>.instance;
            if (mission == null || !mission.try_get_mgr("Tool", out manager) || manager == null || manager.views == null)
            {
                return null;
            }

            foreach (IPlayerToolMgrView view in manager.views)
            {
                PlayerToolMgrView toolView = view as PlayerToolMgrView;
                if (toolView != null)
                {
                    if (toolView.owner == null)
                    {
                        toolView.owner = manager;
                    }
                    return toolView;
                }
            }

            return null;
        }

        private static DeviceUiView FindDeviceUiViewRaw(Device device)
        {
            if (device == null || device.views == null)
            {
                return null;
            }

            foreach (IDeviceView view in device.views)
            {
                DeviceUiView uiView = view as DeviceUiView;
                if (uiView != null)
                {
                    return uiView;
                }
            }

            return null;
        }

        private static Component FindDeviceView(Device device)
        {
            DeviceUiView view = FindDeviceUiViewRaw(device);
            if (view == null || !view.gameObject.activeInHierarchy)
            {
                return null;
            }
            return view;
        }

        private static Component FindFixTarget(Device device)
        {
            DeviceUiView view = FindDeviceUiViewRaw(device);
            if (view == null || view.mask == null)
            {
                return null;
            }

            DeviceFixMask mask = view.mask.GetComponentInChildren<DeviceFixMask>(true);
            if (mask == null)
            {
                return null;
            }

            return mask.fix_position != null ? (Component)mask.fix_position : mask.transform;
        }

        private static Component FindFireTarget(Device device)
        {
            DeviceFiredView view = FindEmergencyView<DeviceFiredView>(device);
            if (view != null)
            {
                if (view.fire_list != null && view.fire_list.Count > 0 && view.fire_list[0] != null)
                {
                    return view.fire_list[0];
                }
                return view;
            }
            return FindDeviceView(device);
        }

        private static Component FindWaterSingle(Device device)
        {
            DeviceWaterView view = FindEmergencyView<DeviceWaterView>(device);
            if (view != null && view.water_list != null)
            {
                foreach (SingleWaterView single in view.water_list)
                {
                    if (single != null)
                    {
                        return single;
                    }
                }
            }
            return null;
        }

        private static Component FindAcidSingle(Device device)
        {
            DeviceAcidView view = FindEmergencyView<DeviceAcidView>(device);
            if (view != null && view.acid_list != null)
            {
                foreach (SingleAcidView single in view.acid_list)
                {
                    if (single != null)
                    {
                        return single;
                    }
                }
            }
            return null;
        }

        private static Component FindBindSingle(Device device)
        {
            DeviceBindView view = FindEmergencyView<DeviceBindView>(device);
            if (view != null && view.vine_list != null)
            {
                foreach (SingleBindView single in view.vine_list)
                {
                    if (single != null)
                    {
                        return single;
                    }
                }
            }
            return null;
        }

        private static T FindEmergencyView<T>(Device device) where T : class, IDeviceEmergencyView
        {
            if (device == null || device.device_emergency == null || device.device_emergency.views == null)
            {
                return null;
            }

            foreach (IDeviceEmergencyView view in device.device_emergency.views)
            {
                T result = view as T;
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static Component FindBuffView(Character character)
        {
            if (character == null)
            {
                return null;
            }

            CharacterMgr manager;
            Mission mission = Singleton<Mission>.instance;
            if (mission == null || !mission.try_get_mgr("CharacterMgr", out manager) || manager == null || manager.views == null)
            {
                return null;
            }

            foreach (ICharacterMgrView view in manager.views)
            {
                CharacterMgrView mgrView = view as CharacterMgrView;
                if (mgrView == null || mgrView.characters_view == null)
                {
                    continue;
                }

                foreach (CharacterView characterView in mgrView.characters_view)
                {
                    if (characterView == null
                        || characterView.character != character
                        || characterView.buffview == null
                        || !characterView.buffview.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    return characterView.buffview;
                }
            }

            return null;
        }

        private static Vector2? GetTargetScreen(MaintenanceJob job)
        {
            if (job == null)
            {
                return null;
            }

            Component target = SafeGet(job.TargetProvider);
            if (target == null)
            {
                return null;
            }

            return ScreenPos(target);
        }

        private static Component SafeGet(Func<Component> provider)
        {
            if (provider == null)
            {
                return null;
            }

            try
            {
                return provider();
            }
            catch
            {
                return null;
            }
        }

        private static Vector2? ScreenPos(Component target)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                return null;
            }

            WorldSceneRoot root = MonoBehaviourSingleton<WorldSceneRoot>.instance;
            if (root == null)
            {
                return null;
            }

            RectTransform rect = target.transform as RectTransform;
            Vector3 world = rect != null
                ? rect.TransformPoint(rect.rect.center)
                : target.transform.position;

            Vector2 screen = RectTransformUtility.WorldToScreenPoint(root.uiCamera, world);
            if (screen.x < 0f || screen.y < 0f || screen.x > Screen.width || screen.y > Screen.height)
            {
                return null;
            }

            return screen;
        }

        private static Vector2 AdjustForTool(MaintenanceJob job, Vector2 target)
        {
            if (job.Tool != null)
            {
                Extinguisher extinguisher = job.Tool.logic as Extinguisher;
                if (extinguisher != null && extinguisher.bubble_position != null)
                {
                    Vector2? toolPos = ScreenPos(extinguisher);
                    Vector2? bubblePos = ScreenPos(extinguisher.bubble_position.transform);
                    if (toolPos.HasValue && bubblePos.HasValue)
                    {
                        target -= bubblePos.Value - toolPos.Value;
                    }
                }
            }

            if (job.Action == ToolActionKind.Wrench)
            {
                FixWrench wrench = job.Tool != null ? job.Tool.logic as FixWrench : null;
                if (wrench != null && !wrench.auto_rotate)
                {
                    int elapsed = _tick - job.StartTick;
                    if (elapsed > 12)
                    {
                        float amplitude = Mathf.Clamp(Screen.width * 0.25f, 200f, 800f);
                        bool positive = ((elapsed / 2) % 2) == 0;
                        target.x = Mathf.Clamp(
                            target.x + (positive ? amplitude : -amplitude),
                            0f,
                            Screen.width);
                    }
                }
            }
            else if (job.Action == ToolActionKind.Saw)
            {
                target.y += Mathf.Sin(_tick * 0.18f) * 55f;
            }

            target.x = Mathf.Clamp(target.x, 0f, Screen.width);
            target.y = Mathf.Clamp(target.y, 0f, Screen.height);
            return target;
        }

        private static void SetMouse(bool active, Vector2 position)
        {
            OverrideMouse = active;
            MousePosition = position;
        }

        private static PointerEventData MakeEvent(PointerEventData.InputButton button, Vector2 position)
        {
            return new PointerEventData(EventSystem.current)
            {
                button = button,
                position = position
            };
        }

        private static void SendClick(ToolLogic logic, PointerEventData.InputButton button, Vector2 position)
        {
            if (logic == null)
            {
                return;
            }

            IPointerClickHandler handler = logic as IPointerClickHandler;
            if (handler != null)
            {
                handler.OnPointerClick(MakeEvent(button, position));
            }
        }

        private static void SendDown(ToolLogic logic, PointerEventData.InputButton button, Vector2 position)
        {
            if (logic == null)
            {
                return;
            }

            IPointerDownHandler handler = logic as IPointerDownHandler;
            if (handler != null)
            {
                handler.OnPointerDown(MakeEvent(button, position));
            }
        }

        private static void SendUp(ToolLogic logic, PointerEventData.InputButton button, Vector2 position)
        {
            if (logic == null)
            {
                return;
            }

            IPointerUpHandler handler = logic as IPointerUpHandler;
            if (handler != null)
            {
                handler.OnPointerUp(MakeEvent(button, position));
            }
        }

        private static void LogInfo(string message)
        {
            if (AutoRepairPlugin.LogRepairs.Value && AutoRepairPlugin.Log != null)
            {
                AutoRepairPlugin.Log.LogInfo("[AutoRepair] " + message);
            }
        }

        private static void LogNoTool(MaintenanceKind kind)
        {
            if (NoToolLogged.Add(kind))
            {
                LogInfo("No tool available for " + kind + "; " +
                    (AutoRepairPlugin.AllowFallback ? "using background fallback." : "waiting.")
                    + " Owned tools: " + DescribeTools());
            }
        }

        private static string DescribeTools()
        {
            PlayerToolMgrView view = GetToolView();
            if (view == null || view.owner == null || view.tool_view_list == null)
            {
                return "none (no PlayerToolMgrView)";
            }

            List<string> names = new List<string>();
            foreach (ToolView toolView in view.tool_view_list)
            {
                if (toolView == null || toolView.logic == null || toolView.tool_data == null)
                {
                    continue;
                }

                int level;
                if (!view.owner.tools.TryGetValue(toolView.tool_data.id, out level)
                    || toolView.tool_data.level != level)
                {
                    continue;
                }

                names.Add(toolView.tool_data.id + ":" + level + ":" + toolView.logic.GetType().Name);
            }

            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "none";
        }

        private static void LogOwnedTools()
        {
            if (!AutoRepairPlugin.LogRepairs.Value)
            {
                return;
            }

            LogInfo("Owned tools: " + DescribeTools());

            Commons.Config config = Commons.Config.current;
            if (config != null)
            {
                LogInfo("Wrench config: velocity=" + config.wrench_velocity
                    + ", efficiency=" + config.wrench_efficiency_init
                    + ", pos=" + config.wrench_pos_min + ".." + config.wrench_pos_max
                    + ", fix_ratio=" + config.fix_hp_ratio
                    + ", fix_value=" + config.fix_hp_value);
            }
        }

        private static void LogWarning(string message)
        {
            if (AutoRepairPlugin.Log != null)
            {
                AutoRepairPlugin.Log.LogWarning("[AutoRepair] " + message);
            }
        }
    }
}
