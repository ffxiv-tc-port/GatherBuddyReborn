using ECommons.Automation.LegacyTaskManager;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text;
using Dalamud.Utility;
using ECommons;
using ECommons.ExcelServices;
using ECommons.LanguageHelpers;
using ECommons.Automation;
using ECommons.MathHelpers;
using GatherBuddy.Data;
using NodeType = GatherBuddy.Enums.NodeType;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using Lumina.Excel.Sheets;
using Fish = GatherBuddy.Classes.Fish;
using GatheringType = GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather : IDisposable
    {
        public AutoGather(GatherBuddy plugin)
        {
            // Initialize the task manager
            TaskManager                  =  new();
            TaskManager.ShowDebug        =  false;
            _plugin                      =  plugin;
            _soundHelper                 =  new SoundHelper();
            _advancedUnstuck             =  new();
            _activeItemList              =  new ActiveItemList(plugin.AutoGatherListsManager);
            ArtisanExporter              =  new Reflection.ArtisanExporter(plugin.AutoGatherListsManager);
            Svc.Chat.CheckMessageHandled += OnMessageHandled;
            //Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
            _plugin.FishRecorder.Parser.CaughtFish += OnFishCaught;
        }
        public Fish? LastCaughtFish { get; private set; }
        public Fish? PreviouslyCaughtFish { get; private set; }
        private void OnFishCaught(Fish arg1, ushort arg2, byte arg3, bool arg4, bool arg5)
        {
            PreviouslyCaughtFish = LastCaughtFish;
            LastCaughtFish       = arg1;
        }

        // Track the current gather target for robust node handling
        private GatherTarget? _currentGatherTarget;

        private void OnMessageHandled(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            try
            {
                if (type is (XivChatType)2243)
                {
                    var text = message.TextValue;
                    var id = Svc.Data.GetExcelSheet<LogMessage>()
                        ?.FirstOrDefault(x => x.Text.ToString() == text).RowId;

                    LureSuccess = GatherBuddy.GameData.Fishes.Values.FirstOrDefault(f => f.FishData?.Unknown_70_1 == text) != null;

                    if (LureSuccess)
                        return;

                    LureSuccess = id is 5565 or 5569;
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to handle message: {e}");
            }
        }

        private readonly GatherBuddy     _plugin;
        private readonly SoundHelper     _soundHelper;
        private readonly AdvancedUnstuck _advancedUnstuck;
        private readonly ActiveItemList  _activeItemList;

        public Reflection.ArtisanExporter ArtisanExporter;
        public TaskManager                TaskManager { get; }

        private           bool             _enabled { get; set; } = false;

        public bool Waiting
        {
            get;
            private set
            {
                if (GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode)
                    GatherBuddy.AutoRetainerApi.Suppressed = !value;
                field                                  = value;
            }
        } = false;

        public unsafe bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;

                if (!value)
                {
                    AutoStatus = "Idle...".Loc();
                    TaskManager.Abort();
                    YesAlready.Unlock();

                    _activeItemList.Reset();
                    Waiting                    = false;
                    ActionSequence             = null;
                    CurrentCollectableRotation = null;

                    if (VNavmesh.Enabled && IsPathGenerating)
                        VNavmesh.Nav.PathfindCancelAll();
                    StopNavigation();
                    CurrentFarNodeLocation   = null;
                    _homeWorldWarning        = false;
                    _diademQueuingInProgress = false;
                    FarNodesSeenSoFar.Clear();
                    VisitedNodes.Clear();
                }
                else
                {
                    WentHome = true; //Prevents going home right after enabling auto-gather
                    if (AutoHook.Enabled)
                        AutoHook.SetPluginState(false); //Make sure AutoHook doesn't interfere with us
                }

                _enabled = value;
                _plugin.Ipc.AutoGatherEnabledChanged(value);
            }
        }

        public bool GoHome()
        {
            StopNavigation();

            if (WentHome)
                return false;

            WentHome = true;

            if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                return false;

            if (Lifestream.Enabled && !Lifestream.IsBusy())
            {
                var command = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
                if (command.Contains("/li "))
                    command = command.Replace("/li ", "");
                Lifestream.ExecuteCommand(command);
                TaskManager.EnqueueImmediate(() => !Lifestream.IsBusy(), 120000, "Wait until Lifestream is done");
                return true;
            }
            else
            {
                GatherBuddy.Log.Warning("Lifestream not found or not ready");
                return false;
            }
        }

        private class NoGatherableItemsInNodeException : Exception
        { }

        private class NoCollectableActionsException : Exception
        { }

        private bool _diademQueuingInProgress = false;
        private bool _homeWorldWarning        = false;

        public void DoAutoGather()
        {

            // Always check these first
            if (!IsGathering)
                LuckUsed = false; //Reset the flag even if auto-gather was disabled mid-gathering

            if (!Enabled)
            {
                return;
            }

            // If we are not gathering and _currentGatherTarget is set, we just finished gathering or left the node
            if (!IsGathering && _currentGatherTarget != null)
            {
                var gatherTarget = _currentGatherTarget!;
                // Mark the node as visited if possible
                var targetNode = Svc.Targets.Target ?? Svc.Targets.PreviousTarget;
                if (targetNode != null && targetNode.ObjectKind is ObjectKind.GatheringPoint)
                {
                    _activeItemList.MarkVisited(targetNode);
                    var gatherable = gatherTarget.Value.Gatherable;
                    var node = gatherTarget.Value.Node;
                    if (gatherable != null && (gatherable.NodeType == NodeType.Regular || gatherable.NodeType == NodeType.Ephemeral)
                        && (VisitedNodes.Last?.Value != targetNode.BaseId)
                        && node != null && node.WorldPositions.ContainsKey(targetNode.BaseId))
                    {
                        FarNodesSeenSoFar.Clear();
                        VisitedNodes.AddLast(targetNode.BaseId);
                        while (VisitedNodes.Count > (node.WorldPositions.Count <= 4 ? 2 : 4))
                            VisitedNodes.RemoveFirst();
                    }
                }
                // Unset the current gather target when leaving the node
                _currentGatherTarget = null;
            }


            try
            {
                if (!NavReady)
                {
                    AutoStatus = "Waiting for Navmesh...".Loc();
                    return;
                }
            }
            catch (Exception)
            {
                //GatherBuddy.Log.Error(e.Message);
                AutoStatus = "vnavmesh communication failed. Do you have it installed??".Loc();
                return;
            }

            if (TaskManager.IsBusy)
            {
                //GatherBuddy.Log.Verbose("TaskManager has tasks, skipping DoAutoGather");
                return;
            }

            // Clean up lock that may have been left behind by cancelled or timed-out tasks.
            // Every Lock()/Unlock() pair lives inside a single task batch, so an idle task manager
            // means nothing can still be relying on the lock. This MUST stay above the CanAct gate
            // below: a task chain that timed out while the player is Occupied would otherwise never
            // reach it, and the lock is a cross-plugin flag that keeps YesAlready globally disabled.
            YesAlready.Unlock();

            if (!_homeWorldWarning && !Functions.OnHomeWorld())
            {
                _homeWorldWarning = true;
                Communicator.PrintError("You are not on your home world, some items will not be gatherable.".Loc());
            }

            if (DiscipleOfLand.NextTreasureMapAllowance == DateTime.MinValue)
            {
                //Wait for timer refresh
                AutoStatus = "Refreshing timers...".Loc();
                DiscipleOfLand.RefreshNextTreasureMapAllowance();
                return;
            }

            if (!CanAct && !_diademQueuingInProgress)
            {
                AutoStatus = Dalamud.Conditions[ConditionFlag.Gathering] ? "Gathering...".Loc() : "Player is busy...";
                return;
            }

            if (FreeInventorySlots == 0)
            {
                if (HasReducibleItems())
                {
                    if (IsGathering)
                        CloseGatheringAddons();
                    else
                        ReduceItems(false);
                }
                else
                {
                    AbortAutoGather("Inventory is full");
                }

                return;
            }

            if (_activeItemList.GetNextOrDefault(new List<uint>()).Any(g => g.Fish != null)
             && !GatherBuddy.Config.AutoGatherConfig.FishDataCollection)
            {
                Communicator.PrintError(
                    "You have fish on your auto-gather list but you have not opted in to fishing data collection. Auto-gather cannot continue. Please enable fishing data collection in your configuration options or remove fish from your auto-gather lists.".Loc());
                AbortAutoGather();
                return;
            }

            if (IsGathering)
            {

                // Set the current gather target when entering a node
                if (_currentGatherTarget == null)
                {
                    if (!_activeItemList.IsInitialized)
                        _currentGatherTarget = _activeItemList.GetNextOrDefault([Svc.Targets.Target!.BaseId]).FirstOrDefault();
                    else
                        _currentGatherTarget = _activeItemList.CurrentOrDefault;
                }

                IEnumerable<GatherTarget> gatherTarget = _currentGatherTarget != null ? new[] { (GatherTarget)_currentGatherTarget } : Array.Empty<GatherTarget>();

                if (!GatherBuddy.Config.AutoGatherConfig.DoGathering)
                    return;

                AutoStatus = "Gathering...".Loc();
                StopNavigation();

                var fish = _activeItemList.GetNextOrDefault(new List<uint>()).Where(g => g.Fish != null);
                if (fish.Any() && Player.Job == Job.FSH)
                {
                    if (GatherBuddy.Config.AutoGatherConfig.UseNavigation)
                        DoFishMovement(fish);
                    DoFishingTasks(fish);
                    return;
                }

                if (!fish.Any() && Player.Job == Job.FSH)
                {
                    QueueQuitFishingTasks();
                }

                try
                {
                    DoActionTasks(gatherTarget);
                }
                catch (NoGatherableItemsInNodeException)
                {
                    CloseGatheringAddons();
                }
                catch (NoCollectableActionsException)
                {
                    Communicator.PrintError(
                        "Unable to pick a collectability increasing action to use. Make sure that at least one of the collectable actions is enabled.".Loc());
                    AbortAutoGather();
                }


                return;
            }

            if (AutoRetainer.IsEnabled && GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode && AutoRetainer.AreAnyRetainersAvailableForCurrentChara())
            {
                Waiting = true;
                _plugin.Ipc.AutoGatherWaiting();
                return;
            }

            ActionSequence             = null;
            CurrentCollectableRotation = null;

            //Cache IPC call results
            var isPathGenerating = IsPathGenerating;
            var isPathing        = IsPathing;

            switch (_advancedUnstuck.Check(CurrentDestination, isPathGenerating, isPathing))
            {
                case AdvancedUnstuckCheckResult.Pass: break;
                case AdvancedUnstuckCheckResult.Wait: return;
                case AdvancedUnstuckCheckResult.Fail:
                    StopNavigation();
                    AutoStatus = $"Advanced unstuck in progress!";
                    return;
            }

            if (isPathGenerating)
            {
                AutoStatus = "Generating path...".Loc();
                return;
            }

            if (Player.Job is Job.BTN or Job.MIN or Job.FSH
             && !isPathing
             && !Svc.Condition[ConditionFlag.Mounted])
            {
                if (SpiritbondMax > 0)
                {
                    if (IsGathering)
                    {
                        QueueQuitFishingTasks();
                    }

                    DoMateriaExtraction();
                    return;
                }

                if (FreeInventorySlots < 20 && HasReducibleItems())
                {
                    ReduceItems(false);
                    return;
                }
            }

            var nearbyNodes = Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable).Select(o => o.BaseId);
            var next = _activeItemList.GetNextOrDefault(nearbyNodes)
                .OrderByDescending(nodes => nodes.Item.ItemId);
            if (!next.Any())
            {
                if (!_activeItemList.HasItemsToGather)
                {
                    AbortAutoGather();
                    return;
                }

                if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle)
                    if (GoHome())
                        return;

                if (HasReducibleItems())
                {
                    ReduceItems(true);
                    return;
                }

                if (!Waiting)
                {
                    Waiting = true;
                    _plugin.Ipc.AutoGatherWaiting();
                }

                AutoStatus = "No available items to gather".Loc();
                return;
            }

            Waiting = false;

            if (next.Any(n => n.Item.ItemData.IsCollectable
                 && !CheckCollectablesUnlocked(n.Fish != null ? GatheringType.Fisher : n.Gatherable!.GatheringType.ToGroup())))
            {
                AbortAutoGather();
                return;
            }

            if (RepairIfNeeded())
                return;

            if (!GatherBuddy.Config.AutoGatherConfig.UseNavigation)
            {
                AutoStatus = "Waiting for Gathering Point... (No Nav Mode)".Loc();
                return;
            }

            var territoryId = Svc.ClientState.TerritoryType;
            //Idyllshire to The Dravanian Hinterlands
            if ((territoryId == 478 && next.First().Node.Territory.Id == 399)
             || (territoryId == 418 && next.First().Node.Territory.Id is 901 or 929 or 939) && Lifestream.Enabled)
            {
                var aetheryte = Svc.Objects.Where(x => x.ObjectKind == ObjectKind.Aetheryte && x.IsTargetable)
                    .OrderBy(x => x.Position.DistanceToPlayer()).FirstOrDefault();
                if (aetheryte != null)
                {
                    if (aetheryte.Position.DistanceToPlayer() > 10)
                    {
                        AutoStatus = "Moving to aetheryte...".Loc();
                        if (!isPathing && !isPathGenerating)
                            Navigate(aetheryte.Position, false);
                    }
                    else if (!Lifestream.IsBusy())
                    {
                        AutoStatus = "Teleporting...".Loc();
                        StopNavigation();
                        string name = string.Empty;
                        switch (territoryId)
                        {
                            case 478:
                                var exit = next.First().Node.DefaultXCoord < 2000 ? 91u : 92u;
                                name = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRow(exit).AethernetName.Value.Name
                                    .ToString();
                                break;
                            case 418:
                                name = Dalamud.GameData.GetExcelSheet<TerritoryType>().GetRow(886).PlaceName.Value.Name.ToString()
                                    .Split(" ")[1];
                                break;
                        }

                        TaskManager.Enqueue(() => Lifestream.AethernetTeleport(name));
                        TaskManager.DelayNext(1000);
                        TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                    }

                    return;
                }
            }

            if (territoryId == 886 && next.First().Node.Territory.Id is 901 or 929 or 939)
            {
                var dutyNpc                    = Svc.Objects.FirstOrDefault(o => o.BaseId == 1031694);
                var selectStringAddon          = Dalamud.GameGui.GetAddonByName("SelectString");
                var talkAddon                  = Dalamud.GameGui.GetAddonByName("Talk");
                var selectYesNoAddon           = Dalamud.GameGui.GetAddonByName("SelectYesno");
                var contentsFinderConfirmAddon = Dalamud.GameGui.GetAddonByName("ContentsFinderConfirm");
                Svc.Log.Verbose($"Addons: {selectStringAddon}, {talkAddon}, {selectYesNoAddon}, {contentsFinderConfirmAddon}");
                if (dutyNpc != null && dutyNpc.Position.DistanceToPlayer() > 3)
                {
                    var point = VNavmesh.Query.Mesh.NearestPoint(dutyNpc.Position, 10, 10000);
                    VNavmesh.SimpleMove.PathfindAndMoveTo(point, false);
                    return;
                }
                else
                    switch (Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent])
                    {
                        case false when contentsFinderConfirmAddon > 0:
                        {
                            var contents = new AddonMaster.ContentsFinderConfirm(contentsFinderConfirmAddon);
                            TaskManager.Enqueue(contents.Commence);
                            TaskManager.Enqueue(() => _diademQueuingInProgress = false);
                            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BoundByDuty]);
                            TaskManager.Enqueue(YesAlready.Unlock);
                            return;
                        }
                        case false when contentsFinderConfirmAddon == nint.Zero
                         && selectStringAddon == nint.Zero
                         && selectYesNoAddon == nint.Zero:
                            unsafe
                            {
                                var targetSystem = TargetSystem.Instance();
                                if (targetSystem == null)
                                    return;

                                TaskManager.Enqueue(YesAlready.Lock);
                                TaskManager.Enqueue(StopNavigation);
                                TaskManager.Enqueue(()
                                    => targetSystem->OpenObjectInteraction(
                                        (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)dutyNpc.Address));
                                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]);
                                TaskManager.Enqueue(() => _diademQueuingInProgress = true);
                                return;
                            }
                        case true when selectStringAddon > 0:
                        {
                            var select = new AddonMaster.SelectString(selectStringAddon);
                            TaskManager.Enqueue(() => select.Entries[0].Select());
                            return;
                        }
                        case true when selectYesNoAddon > 0:
                        {
                            var yesNo = new AddonMaster.SelectYesno(selectYesNoAddon);
                            TaskManager.Enqueue(yesNo.Yes);
                            TaskManager.DelayNext(5000);
                            return;
                        }
                        case true when talkAddon > 0:
                        {
                            var talk = new AddonMaster.Talk(talkAddon);
                            TaskManager.Enqueue(talk.Click);
                            return;
                        }
                    }
            }

            var forcedAetheryte = ForcedAetherytes.ZonesWithoutAetherytes
                .FirstOrDefault(z => z.ZoneId == next.First().Location.Territory.Id);
            if (forcedAetheryte.ZoneId != 0
             && GatherBuddy.GameData.Aetherytes[forcedAetheryte.AetheryteId].Territory.Id == territoryId)
            {
                if (territoryId == 478 && !Lifestream.Enabled)
                    AutoStatus = $"Install Lifestream or teleport to {next.First().Location.Territory.Name} manually";
                else
                    AutoStatus = "Manual teleporting required".Loc();
                return;
            }

            //At this point, we are definitely going to gather something, so we may go home after that.
            if (Lifestream.Enabled)
                Lifestream.Abort();
            WentHome = false;

            if (next.First().Location.Territory.Id != territoryId)
            {
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty] && !Functions.InTheDiadem())
                {
                    AutoStatus = "Can not teleport when bound by duty".Loc();
                    return;
                }
                else if (Functions.InTheDiadem())
                {
                    LeaveTheDiadem();
                    return;
                }

                AutoStatus = "Teleporting...".Loc();
                StopNavigation();

                if (!MoveToTerritory(next.First().Location))
                    AbortAutoGather();

                // Reset target to pick up closest item after teleport
                next = default;

                return;
            }

            var config = MatchConfigPreset(next.First().Gatherable);

            if (DoUseConsumablesWithoutCastTime(config))
                return;

            if (!LocationMatchesJob(next.First().Location))
            {
                if (!ChangeGearSet(next.First().Location.GatheringType.ToGroup(), 2400))
                    AbortAutoGather();
            }

            if (next.First().Fish != null)
            {
                DoFishMovement(next);
                return;
            }

            if (next.First().Gatherable != null)
            {
                DoNodeMovement(next, config);
                return;
            }

            AutoStatus = "Fell out of control loop unexpectedly. Please report this error.".Loc();
            return;
        }

        public readonly Dictionary<GatherTarget, (Vector3 Position, Angle Rotation, DateTime Expiration)> FishingSpotData = new();

        private void DoFishMovement(IEnumerable<GatherTarget> next)
        {
            var fish = next.First(ne => ne.Fish != null);

            if (!FishingSpotData.TryGetValue(fish, out var fishingSpotData))
            {
                var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(fish!.FishingSpot);
                if (!positionData.HasValue)
                {
                    Communicator.PrintError(
                        $"No position data for fishing spot {fish.FishingSpot.Name}. Auto-Fishing cannot continue. Please, manually fish at least once at {fish.FishingSpot.Name} so GBR can know its location.");
                    AbortAutoGather();
                    return;
                }

                DateTime spotExpiration =
                    DateTime.Now.AddMinutes(GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes); //TODO: Make this configurable
                FishingSpotData.Add(fish, (positionData.Value.Position, positionData.Value.Rotation, spotExpiration));
                return;
            }

            if (fishingSpotData.Expiration < DateTime.Now)
            {
                Svc.Log.Debug("Time for a new fishing spot!");
                FishingSpotData.Remove(fish);
                if (IsGathering || IsFishing)
                {
                    QueueQuitFishingTasks();
                }

                return;
            }

            if (Vector3.Distance(fishingSpotData.Position, Player.Position) < 1)
            {
                if (Dalamud.Conditions[ConditionFlag.Mounted])
                    EnqueueDismount();

                var playerAngle = new Angle(Player.Rotation);
                if (playerAngle != fishingSpotData.Rotation)
                    TaskManager.Enqueue(() => SetRotation(fishingSpotData.Rotation));
                Svc.Log.Debug($"Fishing Spot is valid for {(fishingSpotData.Expiration - DateTime.Now).TotalSeconds} seconds");

                AutoStatus = "Fishing...".Loc();
                DoFishingTasks(next);
                return;
            }

            if (CurrentDestination != fishingSpotData.Position)
            {
                StopNavigation();
                AutoStatus = "Moving to fishing spot...".Loc();
                if (IsGathering || IsFishing)
                {
                    QueueQuitFishingTasks();
                }

                MoveToFishingSpot(fishingSpotData.Position, fishingSpotData.Rotation);
            }
        }

        private void DoNodeMovement(IEnumerable<GatherTarget> next, ConfigPreset config)
        {
            var allPositions = next.Where(n => n.Location.Territory.Id == Player.Territory)
                .SelectMany(ne => ne.Node?.WorldPositions
                        .ExceptBy(VisitedNodes, n => n.Key)
                        .SelectMany(w => w.Value)
                        .Where(v => !IsBlacklisted(v))
                 ?? []).Select(s => s)
                .ToHashSet();

            var visibleNodes = Svc.Objects
                .Where(o => allPositions.Contains(o.Position))
                .ToList();

            var closestTargetableNode = visibleNodes
                .Where(o => o.IsTargetable)
                .MinBy(o => Vector3.Distance(Player.Position, o.Position));

            if (ActivateGatheringBuffs(next.First().Gatherable.NodeType is NodeType.Unspoiled or NodeType.Legendary))
                return;

            if (closestTargetableNode != null)
            {
                AutoStatus = "Moving to node...".Loc();
                var targetItem = next.First(ti => ti.Node != null && ti.Node.WorldPositions.ContainsKey(closestTargetableNode.BaseId))
                    .Gatherable;
                MoveToCloseNode(closestTargetableNode, targetItem, config);
                return;
            }

            AutoStatus = "Moving to far node...".Loc();

            if (CurrentDestination != default)
            {
                var currentNode = visibleNodes.FirstOrDefault(o => o.Position == CurrentDestination);
                if (currentNode != null && !currentNode.IsTargetable)
                    GatherBuddy.Log.Verbose($"Far node is not targetable, distance {currentNode.Position.DistanceToPlayer()}.");

                //It takes some time (roundtrip to the server) before a node becomes targetable after it becomes visible,
                //so we need to delay excluding it. But instead of measuring time, we use distance, since character is traveling at a constant speed.
                //Value 50 was determined empirically.
                foreach (var node in allPositions.Where(o => o.DistanceToPlayer() < 50))
                    FarNodesSeenSoFar.Add(node);

                if (CurrentDestination.DistanceToPlayer() < 50)
                {
                    GatherBuddy.Log.Verbose("Far node is not targetable, choosing another");
                }
                else
                {
                    return;
                }
            }

            Vector3 selectedFarNode;

            // only Legendary and Unspoiled show marker
            var timedNode = next.FirstOrDefault(n => n.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(-8));
            if (ShouldUseFlag && timedNode != default)
            {
                var pos = TimedNodePosition;
                // marker not yet loaded on game
                if (pos == null || timedNode.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(-8))
                {
                    AutoStatus = "Waiting on flag show up".Loc();
                    return;
                }

                selectedFarNode = allPositions
                    .Where(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)) < 10)
                    .OrderBy(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)))
                    .FirstOrDefault();
                if (selectedFarNode == default)
                    selectedFarNode = VNavmesh.Query.Mesh.NearestPoint(new Vector3(pos.Value.X, 0, pos.Value.Y), 10, 10000);
            }
            else
            {
                //Select the closest node
                selectedFarNode = allPositions
                    .Where(fn => !visibleNodes.Select(vn => vn.Position).Contains(fn))
                    .OrderBy(v => Vector3.Distance(Player.Position, v))
                    .FirstOrDefault(n => !FarNodesSeenSoFar.Contains(n));

                if (selectedFarNode == default)
                {
                    FarNodesSeenSoFar.Clear();
                    GatherBuddy.Log.Verbose($"Selected node was null and far node filters have been cleared");
                    return;
                }
            }

            MoveToFarNode(selectedFarNode);
        }

        private unsafe void LeaveTheDiadem()
        {
            // AgentModule.Instance() 走 UIModule，UI 尚未建立時回 null（CS 手寫實作逐字是
            // uiModule == null ? null : uiModule->GetAgentModule()），GetAgentByInternalId 也可能回 null。
            // 取不到就不開選單直接返回——與同 repo Plugin/ContextMenu.cs HandleItemSearch 相同的失敗形式。
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return;

            var contentsFinderMenu = agentModule->GetAgentByInternalId(AgentId.ContentsFinderMenu);
            if (contentsFinderMenu == null)
                return;

            contentsFinderMenu->Show();
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContentsFinderMenu", out _))
            {
                TaskManager.Enqueue(YesAlready.Lock);
                TaskManager.Enqueue(() => FireOnAddon("ContentsFinderMenu", true,  0));
                TaskManager.Enqueue(() => FireOnAddon("ContentsFinderMenu", false, -2));
                TaskManager.DelayNext(1000);
                TaskManager.Enqueue(() => FireOnAddon("SelectYesno", true, 0));
                TaskManager.Enqueue(YesAlready.Unlock);
                return;
            }
        }

        /// <remarks>
        /// 🔴 ECommons 的 <c>Callback.Fire</c> 在送出之前先做
        /// <c>PluginLog.Verbose($"Firing callback: {Base->Name.Read()} …")</c> ——
        /// <c>Base</c> 為 null 時**第一行就解參考 null**，而且 ECommons 的 log 沒有寫入端閘門，
        /// Verbose 關著那個內插字串照樣求值。AccessViolationException 在 .NET Core 是
        /// corrupted-state exception，<c>try</c>/<c>catch</c> 與 <c>ExecuteSafe</c> 一律攔不到。
        /// <para>
        /// 原本這裡有兩種缺陷並存：
        /// ① <c>SelectYesno</c> 那一顆是 <c>GameGui.GetAddonByName(...)</c> 的回傳值**完全沒判**
        ///    就轉型成 <c>AtkUnitBase*</c>；那條路徑走 <c>RaptureAtkUnitManager</c>，找不到視窗時
        ///    **合法回 0**，而它是 <c>DelayNext(1000)</c> 之後才跑的延遲工作 —— 確認對話框沒跳出來
        ///    （網路延遲、遊戲直接省略確認）本來就是常態。
        /// ② <c>ContentsFinderMenu</c> 那兩顆把**在 enqueue 那一幀取得的原生指標**捕獲進 lambda，
        ///    等佇列跑到時已經過了好幾幀；視窗中途被 Finalize 掉就是拿已釋放的位址去用。
        /// 兩者的正解相同：**存名字、跑到的時候重新查**（艦隊硬規則：絕不跨幀保存原生指標）。
        /// </para>
        /// 這是使用者觸發的動作型路徑（自動採集離開雲冠群島），不是每幀路徑，所以取不到時
        /// 記一行 <c>Information</c>（使用者跑 LogLevel 2，Debug/Verbose 收不到）再跳過。
        /// 行為不變：視窗在的時候送出的回呼與參數逐字相同。
        /// </remarks>
        private static unsafe void FireOnAddon(string addonName, bool updateState, params object[] values)
        {
            if (GenericHelpers.TryGetAddonByName(addonName, out AtkUnitBase* addon))
            {
                Callback.Fire(addon, updateState, values);
                return;
            }

            GatherBuddy.Log.Information(
                $"LeaveTheDiadem: 視窗 \"{addonName}\" 已經不在了，略過這次回呼（不送出，避免解參考空指標）。");
        }

        private void AbortAutoGather(string? status = null)
        {
            if (Functions.InTheDiadem())
            {
                LeaveTheDiadem();
                return;
            }

            if (!string.IsNullOrEmpty(status))
                AutoStatus = status;
            if (GatherBuddy.Config.AutoGatherConfig.HonkMode)
                Task.Run(() => _soundHelper.StartHonkSoundTask(3));
            CloseGatheringAddons();
            if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone)
                EnqueueActionWithDelay(() => { GoHome(); });
            TaskManager.Enqueue(() =>
            {
                Enabled    = false;
                AutoStatus = status ?? AutoStatus;
            });
        }

        private unsafe void CloseGatheringAddons(bool closeGathering = true)
        {
            var masterpieceOpen = MasterpieceAddon != null;
            var gatheringOpen   = GatheringAddon != null;
            if (masterpieceOpen)
            {
                EnqueueActionWithDelay(() =>
                {
                    if (MasterpieceAddon is var addon and not null)
                    {
                        Callback.Fire(&addon->AtkUnitBase, true, -1);
                    }
                });
                TaskManager.Enqueue(() => MasterpieceAddon == null,                 "Wait until GatheringMasterpiece addon is closed");
                TaskManager.Enqueue(() => GatheringAddon is var addon and not null, "Wait until Gathering addon pops up");
                TaskManager.DelayNext(
                    300); //There is some delay after the moment the addon pops up (and is ready) before the callback can be used to close it. We wait some time and retry the callback.
            }

            if (closeGathering && (gatheringOpen || masterpieceOpen))
            {
                TaskManager.Enqueue(() =>
                {
                    if (GatheringAddon is var gathering and not null && gathering->IsReady)
                    {
                        Callback.Fire(&gathering->AtkUnitBase, true, -1);
                        TaskManager.DelayNextImmediate(100);
                        return false;
                    }

                    var addon = SelectYesnoAddon;
                    if (addon != null)
                    {
                        EnqueueActionWithDelay(() =>
                        {
                            if (SelectYesnoAddon is var addon and not null)
                            {
                                var master = new AddonMaster.SelectYesno(addon);
                                master.Yes();
                            }
                        }, true);
                        TaskManager.EnqueueImmediate(() => !IsGathering, "Wait until Gathering addon is closed");
                        return true;
                    }

                    return !IsGathering;
                }, "Wait until Gathering addon is closed or SelectYesno addon pops up");
            }
        }

        private bool CheckCollectablesUnlocked(GatheringType gatheringType)
        {
            var level = gatheringType switch
            {
                GatheringType.Miner    => DiscipleOfLand.MinerLevel,
                GatheringType.Botanist => DiscipleOfLand.BotanistLevel,
                GatheringType.Fisher   => DiscipleOfLand.FisherLevel,
                GatheringType.Multiple => Math.Max(DiscipleOfLand.MinerLevel, DiscipleOfLand.BotanistLevel),
                _                      => 0
            };
            if (level < Actions.Collect.MinLevel)
            {
                Communicator.PrintError("You've put a collectable on the gathering list, but your level is not high enough to gather it.".Loc());
                return false;
            }

            var questId = gatheringType switch
            {
                GatheringType.Miner    => Actions.Collect.QuestIds.Miner,
                GatheringType.Botanist => Actions.Collect.QuestIds.Botanist,
                _                      => 0u
            };

            if (questId != 0 && !QuestManager.IsQuestComplete(questId))
            {
                Communicator.PrintError("You've put a collectable on the gathering list, but you haven't unlocked the collectables.".Loc());
                var sheet      = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Quest>()!;
                var row        = sheet.GetRow(questId)!;
                var loc        = row.IssuerLocation.Value!;
                var map        = loc.Map.Value!;
                var pos        = MapUtil.WorldToMap(new Vector2(loc.X, loc.Z), map);
                var mapPayload = new MapLinkPayload(loc.Territory.RowId, loc.Map.RowId, pos.X, pos.Y);
                var text       = new SeStringBuilder();
                text.AddText("Collectables are unlocked by ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .AddQuestLink(questId)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(row.Name.ToString())
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(" quest, which can be started in ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .Add(mapPayload)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText($"{mapPayload.PlaceName} {mapPayload.CoordinateString}")
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(".");
                Communicator.Print(text.BuiltString);
                return false;
            }

            return true;
        }

        private bool ChangeGearSet(GatheringType job, int delay)
        {
            var set = job switch
            {
                GatheringType.Miner    => GatherBuddy.Config.MinerSetName,
                GatheringType.Botanist => GatherBuddy.Config.BotanistSetName,
                GatheringType.Fisher   => GatherBuddy.Config.FisherSetName,
                _                      => null,
            };
            if (string.IsNullOrEmpty(set))
            {
                Communicator.PrintError($"No gear set for {job} configured.");
                return false;
            }

            Chat.ExecuteCommand($"/gearset change \"{set}\"");
            TaskManager.DelayNext(Random.Shared.Next(delay, delay + 500)); //Add a random delay to be less suspicious
            return true;
        }

        internal void DebugClearVisited()
        {
            _activeItemList.DebugClearVisited();
        }

        internal void DebugMarkVisited(GatherTarget target)
        {
            _activeItemList.DebugMarkVisited(target);
        }

        public void Dispose()
        {
            _advancedUnstuck.Dispose();
            _activeItemList.Dispose();
            Svc.Chat.CheckMessageHandled -= OnMessageHandled;
            //Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
        }
    }
}
