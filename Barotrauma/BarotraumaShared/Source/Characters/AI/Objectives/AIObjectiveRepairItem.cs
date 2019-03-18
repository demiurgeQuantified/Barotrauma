﻿using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    class AIObjectiveRepairItem : AIObjective
    {
        public override string DebugTag => "repair item";

        public override bool KeepDivingGearOn => true;

        public Item Item { get; private set; }

        private AIObjectiveGoTo goToObjective;

        private float previousCondition = -1;

        public AIObjectiveRepairItem(Character character, Item item) : base(character, "")
        {
            Item = item;
        }

        public override float GetPriority(AIObjectiveManager objectiveManager)
        {
            // TODO: priority list?
            if (Item.Repairables.None()) { return 0; }
            // Ignore items that are being repaired by someone else.
            if (Item.Repairables.Any(r => r.CurrentFixer != null && r.CurrentFixer != character)) { return 0; }
            // Vertical distance matters more than horizontal (climbing up/down is harder than moving horizontally)
            float dist = Math.Abs(character.WorldPosition.X - Item.WorldPosition.X) + Math.Abs(character.WorldPosition.Y - Item.WorldPosition.Y) * 2.0f;
            float distanceFactor = MathHelper.Lerp(1, 0.5f, MathUtils.InverseLerp(0, 10000, dist));
            float damagePriority = MathHelper.Lerp(1, 0, (Item.Condition + 10) / Item.MaxCondition);
            float successFactor = MathHelper.Lerp(0, 1, Item.Repairables.Average(r => r.DegreeOfSuccess(character)));
            float isSelected = character.SelectedConstruction == Item ? 50 : 0;
            float baseLevel = Math.Max(Priority + isSelected, 1);
            return MathHelper.Clamp(baseLevel * damagePriority * distanceFactor * successFactor, 0, 100);
        }

        public override bool CanBeCompleted => !abandon;

        public override bool IsCompleted()
        {
            bool isCompleted = Item.IsFullCondition;
            if (isCompleted)
            {
                character?.Speak(TextManager.Get("DialogItemRepaired").Replace("[itemname]", Item.Name), null, 0.0f, "itemrepaired", 10.0f);
            }
            return isCompleted;
        }

        public override bool IsDuplicate(AIObjective otherObjective)
        {
            return otherObjective is AIObjectiveRepairItem repairObjective && repairObjective.Item == Item;
        }

        protected override void Act(float deltaTime)
        {
            if (goToObjective != null && !subObjectives.Contains(goToObjective))
            {
                if (!goToObjective.IsCompleted() && !goToObjective.CanBeCompleted)
                {
                    abandon = true;
                    character?.Speak(TextManager.Get("DialogCannotRepair").Replace("[itemname]", Item.Name), null, 0.0f, "cannotrepair", 10.0f);
                }
                goToObjective = null;
            }
            foreach (Repairable repairable in Item.Repairables)
            {
                if (!repairable.HasRequiredItems(character, false))
                {
                    //make sure we have all the items required to fix the target item
                    foreach (var kvp in repairable.requiredItems)
                    {
                        foreach (RelatedItem requiredItem in kvp.Value)
                        {
                            AddSubObjective(new AIObjectiveGetItem(character, requiredItem.Identifiers, true));
                        }
                    }
                    return;
                }
            }
            if (character.CanInteractWith(Item))
            {
                OperateRepairTool(deltaTime);
                foreach (Repairable repairable in Item.Repairables)
                {
                    if (repairable.CurrentFixer != null && repairable.CurrentFixer != character)
                    {
                        // Someone else is repairing the target. Abandon the objective if the other is better at this then us.
                        abandon = repairable.DegreeOfSuccess(character) < repairable.DegreeOfSuccess(repairable.CurrentFixer);
                    }
                    if (!abandon)
                    {
                        if (character.SelectedConstruction != Item)
                        {
                            Item.TryInteract(character, true, true);
                        }
                        if (previousCondition == -1)
                        {
                            previousCondition = Item.Condition;
                        }
                        else if (Item.Condition < previousCondition)
                        {
                            // If the current condition is less than the previous condition, we can't complete the task, so let's abandon it. The item is probably deteriorating at a greater speed than we can repair it.
                            abandon = true;
                            character?.Speak(TextManager.Get("DialogRepairFailed").Replace("[itemname]", Item.Name), null, 0.0f, "repairfailed", 10.0f);
                        }
                    }
                    repairable.CurrentFixer = abandon && repairable.CurrentFixer == character ? null : character;
                    break;
                }
            }
            else if (goToObjective == null || goToObjective.Target != Item)
            {
                previousCondition = -1;
                if (goToObjective != null)
                {
                    subObjectives.Remove(goToObjective);
                }
                goToObjective = new AIObjectiveGoTo(Item, character);
                AddSubObjective(goToObjective);
            }
        }

        private void OperateRepairTool(float deltaTime)
        {
            // Operate repair tool, if required.
            foreach (Repairable repairable in Item.Repairables)
            {
                foreach (var kvp in repairable.requiredItems)
                {
                    foreach (RelatedItem requiredItem in kvp.Value)
                    {
                        foreach (var item in character.Inventory.Items)
                        {
                            if (requiredItem.MatchesItem(item))
                            {
                                var repairTool = item.GetComponent<RepairTool>();
                                if (repairTool != null)
                                {
                                    character.CursorPosition = Item.Position;
                                    character.SetInput(InputType.Aim, false, true);
                                    repairTool.Use(deltaTime, character);
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}