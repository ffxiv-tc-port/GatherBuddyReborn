using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.SeFunctions;
using GatherBuddy.Data;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GatherBuddy.Enums;
using Aetheryte = GatherBuddy.Classes.Aetheryte;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueDismount()
        {
            TaskManager.Enqueue(StopNavigation);

            var am = ActionManager.Instance();
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) am->UseAction(ActionType.Mount, 0); }, "Dismount");

            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.InFlight] && CanAct, 1000, "Wait for not in flight");
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) am->UseAction(ActionType.Mount, 0); }, "Dismount 2");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Mounted] && CanAct, 1000, "Wait for dismount");
            TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) TaskManager.DelayNextImmediate(500); } );//Prevent "Unable to execute command while jumping."
        }

        private unsafe void EnqueueMountUp()
        {
            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
            Action doMount;

            if (IsMountUnlocked(mount) && am->GetActionStatus(ActionType.Mount, mount) == 0)
            {
                doMount = () => am->UseAction(ActionType.Mount, mount);
            }
            else
            {
                if (am->GetActionStatus(ActionType.GeneralAction, 24) != 0)
                {
                    return;
                }

                doMount = () => am->UseAction(ActionType.GeneralAction, 24);
            }

            if (!GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                TaskManager.Enqueue(StopNavigation);
            EnqueueActionWithDelay(doMount);
            TaskManager.Enqueue(() => Svc.Condition[ConditionFlag.Mounted], 2000);

            // Reset navigation to find a better path if the mount can fly
            if (GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting && !Dalamud.Conditions[ConditionFlag.Diving])
                TaskManager.Enqueue(StopNavigation);
        }

        private unsafe bool IsMountUnlocked(uint mount)
        {
            var instance = PlayerState.Instance();
            if (instance == null)
                return false;

            return instance->IsMountUnlocked(mount);
        }

        private void MoveToCloseNode(IGameObject gameObject, Gatherable targetItem, ConfigPreset config)
        {
            // We can open a node with less than 3 vertical and less than 3.5 horizontal separation
            var hSeparation = Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2());
            var vSeparation = Math.Abs(gameObject.Position.Y - Player.Position.Y);

            if (hSeparation < 3.5)
            {
                var waitGP = targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.CollectableMinGP;
                waitGP |= !targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.GatherableMinGP;

                if (Dalamud.Conditions[ConditionFlag.Mounted] && (waitGP || Dalamud.Conditions[ConditionFlag.InFlight] || GetConsumablesWithCastTime(config) > 0))
                {
                    //Try to dismount early. It would help with nodes where it is not possible to dismount at vnavmesh's provided floor point
                    EnqueueDismount();
                    TaskManager.Enqueue(() => {
                        //If early dismount failed, navigate to the nearest floor point
                        if (Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            try
                            {
                                // 🔴 提供端回的是 Vector3?(navmeshManager.Query?.FindPointOnFloor):取不到落點時是 null。
                                //    改動前宣告成 Vector3,那一次會擲 NullReferenceException,剛好被下面的
                                //    catch { } 吞掉 —— 結果碰巧正確,但靠的是例外。現在明確判斷:
                                //    取不到就整段跳過(與例外被吞掉時完全相同),留給後面的進階脫困處理。
                                var floor = VNavmesh.Query.Mesh.PointOnFloor(Player.Position, false, 3);
                                if (floor.HasValue)
                                {
                                    Navigate(floor.Value, true);
                                    TaskManager.Enqueue(() => !IsPathGenerating);
                                    TaskManager.DelayNext(50);
                                    TaskManager.Enqueue(() => !IsPathing, 1000);
                                    EnqueueDismount();
                                }
                            }
                            catch { }
                            //If even that fails, do advanced unstuck
                            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) _advancedUnstuck.Force(); });
                        }
                    });
                }
                else if (waitGP)
                {
                    StopNavigation();
                    AutoStatus = "Waiting for GP to regenerate...".Loc();
                }
                else
                {
                    // Use consumables with cast time just before gathering a node when player is surely not mounted
                    if (GetConsumablesWithCastTime(config) is var consumable and > 0)
                    {
                        if (IsPathing)
                            StopNavigation();
                        else
                            EnqueueActionWithDelay(() => UseItem(consumable));
                    }
                    else
                    {
                        // Check perception requirement before interacting with node
                        if (DiscipleOfLand.Perception < targetItem.GatheringData.PerceptionReq)
                        {
                            Communicator.PrintError($"Insufficient Perception to gather this item. Required: {targetItem.GatheringData.PerceptionReq}, current: {DiscipleOfLand.Perception}");
                            AbortAutoGather(reason: "Not enough Perception for this item.");
                            return;
                        }

                        if (vSeparation < 3)                        
                            if (targetItem.GatheringType.ToGroup() != JobAsGatheringType && targetItem.GatheringType != GatheringType.Multiple) {
                                if (ChangeGearSet(targetItem.GatheringType.ToGroup(), 0)){
                                    EnqueueNodeInteraction(gameObject, targetItem);
                                } else {
                                    AbortAutoGather(reason: "Could not change to the required gear set.");
                                }
                            }
                            else {
                                EnqueueNodeInteraction(gameObject, targetItem);
                            }

                        // The node could be behind a rock or a tree and not be interactable. This happened in the Endwalker, but seems not to be reproducible in the Dawntrail.
                        // Enqueue navigation anyway, just in case.
                        // Also move if vertical separation is too large.
                        if (!Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Gathering]) Navigate(gameObject.Position, false); });
                        }
                    }
                }
            }
            else if (hSeparation < Math.Max(GatherBuddy.Config.AutoGatherConfig.MountUpDistance, 5))
            {
                Navigate(gameObject.Position, false);
            }
            else
            {
                if (!Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    if (GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                        Navigate(gameObject.Position, false);
                    EnqueueMountUp();
                }
                else
                {
                    Navigate(gameObject.Position, ShouldFly(gameObject.Position));
                }
            }
        }

        private void StopNavigation()
        {
            // Reset navigation logic here
            // For example, reinitiate navigation to the destination
            CurrentDestination = default;
            CurrentRotation    = default;
            if (VNavmesh.Enabled)
            {
                VNavmesh.Path.Stop();
            }
        }

        private unsafe void SetRotation(Angle angle)
        {
            var playerObject = (GameObject*)Player.Object.Address;
            Svc.Log.Debug($"Setting rotation to {angle.Rad}");
            playerObject->SetRotation(angle.Rad);
        }

        private void Navigate(Vector3 destination, bool shouldFly, Angle angle = default)
        {
            if (CurrentDestination == destination && (IsPathing || IsPathGenerating))
                return;

            shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

            StopNavigation();
            CurrentDestination = destination;
            CurrentRotation    = angle;
            var correctedDestination = GetCorrectedDestination(CurrentDestination);
            GatherBuddy.Log.Debug($"Navigating to {destination} (corrected to {correctedDestination})");

            LastNavigationResult = VNavmesh.SimpleMove.PathfindAndMoveTo(correctedDestination, shouldFly);
        }

        private static Vector3 GetCorrectedDestination(Vector3 destination)
        {
            const float MaxHorizontalSeparation = 3.0f;
            const float MaxVerticalSeparation = 2.5f;

            try
            {
                float separation;
                if (WorldData.NodeOffsets.TryGetValue(destination, out var offset))
                {
                    // 🔴 提供端回 Vector3?。改動前 null 會擲 NullReferenceException,被本方法底下的
                    //    catch (Exception) 吞掉 ⇒ return destination。這裡明確走同一條路,行為不變。
                    var meshOffset = VNavmesh.Query.Mesh.NearestPoint(offset, MaxHorizontalSeparation, MaxVerticalSeparation);
                    if (!meshOffset.HasValue)
                        return destination;

                    offset = meshOffset.Value;
                    if ((separation = Vector2.Distance(offset.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                        GatherBuddy.Log.Warning($"Offset is ignored because the horizontal separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxHorizontalSeparation}.");
                    else if ((separation = Math.Abs(offset.Y - destination.Y)) > MaxVerticalSeparation)
                        GatherBuddy.Log.Warning($"Offset is ignored because the vertical separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxVerticalSeparation}.");
                    else
                        return offset;
                }

                // 🔴 同上:null ⇒ 維持改動前「例外被吞掉」的結果,直接回未修正的 destination。
                var meshDestination = VNavmesh.Query.Mesh.NearestPoint(destination, MaxHorizontalSeparation, MaxVerticalSeparation);
                if (!meshDestination.HasValue)
                    return destination;

                var correctedDestination = meshDestination.Value;
                if ((separation = Vector2.Distance(correctedDestination.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                    GatherBuddy.Log.Warning($"Query.Mesh.NearestPoint() returned a point with too large horizontal separation {separation}. Maximum allowed is {MaxHorizontalSeparation}.");
                else if ((separation = Math.Abs(correctedDestination.Y - destination.Y)) > MaxVerticalSeparation)
                    GatherBuddy.Log.Warning($"Query.Mesh.NearestPoint() returned a point with too large vertical separation {separation}. Maximum allowed is {MaxVerticalSeparation}.");
                else
                    return correctedDestination;
            }
            catch (Exception) { }

            return destination;
        }

        private void MoveToFarNode(Vector3 position)
        {
            var farNode = position;

            if (!Dalamud.Conditions[ConditionFlag.Mounted])
            {
                if (GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                    Navigate(farNode, false);
                EnqueueMountUp();
            }
            else
            {
                Navigate(farNode, ShouldFly(farNode));
            }
        }

        private void MoveToFishingSpot(Vector3 position, Angle angle)
        {
            if (!Dalamud.Conditions[ConditionFlag.Mounted])
            {
                if (GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                    Navigate(position, false, angle);
                EnqueueMountUp();
            }
            else
            {
                Navigate(position, ShouldFly(position), angle);
            }
        }

        public static Aetheryte? FindClosestAetheryte(ILocation location)
        {
            var aetheryte = location.ClosestAetheryte;

            var territory = location.Territory;
            if (ForcedAetherytes.ZonesWithoutAetherytes.FirstOrDefault(x => x.ZoneId == territory.Id).AetheryteId is var alt && alt > 0)
                territory = GatherBuddy.GameData.Aetherytes[alt].Territory;

            if (aetheryte == null || !Teleporter.IsAttuned(aetheryte.Id) || aetheryte.Territory != territory)
            {
                aetheryte = territory.Aetherytes
                    .Where(a => Teleporter.IsAttuned(a.Id))
                    .OrderBy(a => a.WorldDistance(territory.Id, location.IntegralXCoord, location.IntegralYCoord))
                    .FirstOrDefault();
            }

            return aetheryte;
        }

        private bool MoveToTerritory(ILocation location)
        {
            var aetheryte = FindClosestAetheryte(location);
            if (aetheryte == null)
            {
                Communicator.PrintError("Couldn't find an attuned aetheryte to teleport to.".Loc());
                return false;
            }

            EnqueueActionWithDelay(() => Teleporter.Teleport(aetheryte.Id));
            TaskManager.Enqueue(() => Svc.Condition[ConditionFlag.BetweenAreas]);
            TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.BetweenAreas]);
            TaskManager.DelayNext(1500);

            return true;
        }
    }
}
