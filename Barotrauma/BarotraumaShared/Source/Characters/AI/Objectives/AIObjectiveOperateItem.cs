﻿using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    class AIObjectiveOperateItem : AIObjective
    {
        public override string DebugTag => "operate item";

        private ItemComponent component, controller;

        private Entity operateTarget;

        private bool isCompleted;

        private bool canBeCompleted;

        private bool requireEquip;

        private bool useController;

        private AIObjectiveGoTo gotoObjective;

        public override bool CanBeCompleted
        {
            get
            {
                if (gotoObjective != null && !gotoObjective.CanBeCompleted) return false;

                if (useController && controller == null) return false;

                return canBeCompleted;
            }
        }

        public Entity OperateTarget
        {
            get { return operateTarget; }
        }

        public ItemComponent Component => component;

        public override float GetPriority()
        {
            if (gotoObjective != null && !gotoObjective.CanBeCompleted) { return 0; }
            if (objectiveManager.CurrentOrder == this)
            {
                return AIObjectiveManager.OrderPriority;
            }
            float devotion = MathHelper.Min(10, Priority);
            float value = (devotion + AIObjectiveManager.OrderPriority / 2) * PriorityModifier;
            float max = MathHelper.Min((AIObjectiveManager.OrderPriority - 1), 90);
            return MathHelper.Clamp(value, 0, max);
        }

        public AIObjectiveOperateItem(ItemComponent item, Character character, AIObjectiveManager objectiveManager, string option, bool requireEquip, Entity operateTarget = null, bool useController = false, float priorityModifier = 1) 
            : base (character, objectiveManager, priorityModifier, option)
        {
            this.component = item ?? throw new System.ArgumentNullException("item", "Attempted to create an AIObjectiveOperateItem with a null target.");
            this.requireEquip = requireEquip;
            this.operateTarget = operateTarget;
            this.useController = useController;

            if (useController)
            {
                var controllers = component.Item.GetConnectedComponents<Controller>();
                if (controllers.Any()) controller = controllers[0];
            }

            canBeCompleted = true;
        }

        protected override void Act(float deltaTime)
        {
            ItemComponent target = useController ? controller : component;

            if (useController && controller == null)
            {
                character.Speak(TextManager.Get("DialogCantFindController").Replace("[item]", component.Item.Name), null, 2.0f, "cantfindcontroller", 30.0f);
                return;
            }
            

            if (target.CanBeSelected)
            { 
                if (Vector2.Distance(character.Position, target.Item.Position) < target.Item.InteractDistance
                    || target.Item.IsInsideTrigger(character.WorldPosition))
                {
                    if (character.CurrentHull == target.Item.CurrentHull)
                    {
                        if (character.SelectedConstruction != target.Item && target.CanBeSelected)
                        {
                            target.Item.TryInteract(character, false, true);
                        }
                        if (component.AIOperate(deltaTime, character, this))
                        {
                            isCompleted = true;
                        }
                        return;
                    }

                    if (component.AIOperate(deltaTime, character, this))
                    {
                        isCompleted = true;
                    }
                    return;
                }

                AddSubObjective(gotoObjective = new AIObjectiveGoTo(target.Item, character, objectiveManager));
            }
            else
            {
                if (component.Item.GetComponent<Pickable>() == null)
                {
                    //controller/target can't be selected and the item cannot be picked -> objective can't be completed
                    canBeCompleted = false;
                    return;
                }
                else if (!character.Inventory.Items.Contains(component.Item))
                {
                    AddSubObjective(new AIObjectiveGetItem(character, component.Item, objectiveManager, equip: true));
                }
                else
                {
                    if (requireEquip && !character.HasEquippedItem(component.Item))
                    {
                        //the item has to be equipped before using it if it's holdable
                        var holdable = component.Item.GetComponent<Holdable>();
                        if (holdable == null)
                        {
                            DebugConsole.ThrowError("AIObjectiveOperateItem failed - equipping item " + component.Item + " is required but the item has no Holdable component");
                            return;
                        }

                        for (int i = 0; i < character.Inventory.Capacity; i++)
                        {
                            if (character.Inventory.SlotTypes[i] == InvSlotType.Any ||
                                !holdable.AllowedSlots.Any(s => s.HasFlag(character.Inventory.SlotTypes[i])))
                            {
                                continue;
                            }

                            //equip slot already taken
                            if (character.Inventory.Items[i] != null)
                            {
                                //try to put the item in an Any slot, and drop it if that fails
                                if (!character.Inventory.Items[i].AllowedSlots.Contains(InvSlotType.Any) ||
                                    !character.Inventory.TryPutItem(character.Inventory.Items[i], character, new List<InvSlotType>() { InvSlotType.Any }))
                                {
                                    character.Inventory.Items[i].Drop(character);
                                }
                            }
                            if (character.Inventory.TryPutItem(component.Item, i, true, false, character))
                            {
                                component.Item.Equip(character);
                                break;
                            }
                        }
                        return;
                    }

                    if (component.AIOperate(deltaTime, character, this))
                    {
                        isCompleted = true;
                    }
                }
            }
        }

        public override bool IsCompleted()
        {
            return isCompleted && !IsLoop;
        }

        public override bool IsDuplicate(AIObjective otherObjective)
        {
            AIObjectiveOperateItem operateItem = otherObjective as AIObjectiveOperateItem;
            if (operateItem == null) return false;

            return (operateItem.component == component ||otherObjective.Option == Option);
        }
    }
}
