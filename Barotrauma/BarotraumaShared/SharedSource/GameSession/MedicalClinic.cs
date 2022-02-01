#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    internal partial class MedicalClinic
    {
        public enum NetworkHeader
        {
            REQUEST_AFFLICTIONS,
            REQUEST_PENDING,
            ADD_PENDING,
            REMOVE_PENDING,
            CLEAR_PENDING,
            HEAL_PENDING
        }

        public enum AfflictionSeverity
        {
            Low,
            Medium,
            High
        }

        public enum MessageFlag
        {
            Response, // responding to your request
            Announce // responding to someone else's request
        }

        public enum HealRequestResult
        {
            Unknown, // everything is not ok
            Success, // everything ok
            InsufficientFunds, // not enough money
            Refused // the outpost has refused to provide medical assistance
        }

        [NetworkSerialize]
        public struct NetHealRequest : INetSerializableStruct
        {
            public HealRequestResult Result;
        }

        [NetworkSerialize]
        public struct NetRemovedAffliction : INetSerializableStruct
        {
            public NetCrewMember CrewMember;
            public NetAffliction Affliction;
        }

        public struct NetPendingCrew : INetSerializableStruct
        {
            [NetworkSerialize(ArrayMaxSize = CrewManager.MaxCrewSize)]
            public NetCrewMember[] CrewMembers;
        }

        public struct NetAffliction : INetSerializableStruct
        {
            [NetworkSerialize]
            public string Identifier;

            [NetworkSerialize]
            public ushort Strength;

            [NetworkSerialize]
            public ushort Price;

            public AfflictionSeverity AfflictionSeverity
            {
                get
                {
                    if (Prefab is null) { return AfflictionSeverity.Low; }

                    float normalizedStrength = Strength / Prefab.MaxStrength;

                    // lesser than 0.1
                    if (normalizedStrength <= 0.1)
                    {
                        return AfflictionSeverity.Low;
                    }

                    // between 0.1 and 0.5
                    if (normalizedStrength > 0.1f && normalizedStrength < 0.5f)
                    {
                        return AfflictionSeverity.Medium;
                    }

                    // greater than 0.5
                    return AfflictionSeverity.High;
                }
            }

            public Affliction Affliction
            {
                set
                {
                    Identifier = value.Identifier;
                    Strength = (ushort)Math.Ceiling(value.Strength);
                    Price = (ushort)(value.Prefab.BaseHealCost + Strength * value.Prefab.HealCostMultiplier);
                }
            }

            private AfflictionPrefab? cachedPrefab;

            public AfflictionPrefab? Prefab
            {
                get
                {
                    if (cachedPrefab is { } cached) { return cached; }

                    foreach (AfflictionPrefab prefab in AfflictionPrefab.List)
                    {
                        if (prefab.Identifier.Equals(Identifier, StringComparison.OrdinalIgnoreCase))
                        {
                            cachedPrefab = prefab;
                            return prefab;
                        }
                    }

                    return null;
                }
                set
                {
                    cachedPrefab = value;
                    Identifier = value?.Identifier ?? string.Empty;
                    Strength = 0;
                    Price = 0;
                }
            }

            public readonly bool AfflictionEquals(AfflictionPrefab prefab)
            {
                return prefab.Identifier.Equals(Identifier, StringComparison.OrdinalIgnoreCase);
            }

            public readonly bool AfflictionEquals(NetAffliction affliction)
            {
                return affliction.Identifier.Equals(Identifier, StringComparison.OrdinalIgnoreCase);
            }
        }

        public struct NetCrewMember : INetSerializableStruct
        {
            [NetworkSerialize]
            public int CharacterInfoID;

            [NetworkSerialize]
            public NetAffliction[] Afflictions;

            public CharacterInfo CharacterInfo
            {
                set => CharacterInfoID = value.GetIdentifierUsingOriginalName();
            }

            public readonly CharacterInfo? FindCharacterInfo(ImmutableArray<CharacterInfo> crew)
            {
                foreach (CharacterInfo info in crew)
                {
                    if (info.GetIdentifierUsingOriginalName() == CharacterInfoID)
                    {
                        return info;
                    }
                }

                return null;
            }

            public readonly bool CharacterEquals(NetCrewMember crewMember)
            {
                return crewMember.CharacterInfoID == CharacterInfoID;
            }
        }

        private readonly CampaignMode? campaign;

        public MedicalClinic(CampaignMode campaign)
        {
            this.campaign = campaign;
        }

        public readonly List<NetCrewMember> PendingHeals = new List<NetCrewMember>();

        public Action? OnUpdate;

        private static bool IsOutpostInCombat()
        {
            if (!(Level.Loaded is { Type: LevelData.LevelType.Outpost })) { return false; }

            IEnumerable<Character> crew = GetCrewCharacters().Where(c => c.Character != null).Select(c => c.Character).ToImmutableHashSet();

            foreach (Character npc in Character.CharacterList.Where(c => c.TeamID == CharacterTeamType.FriendlyNPC))
            {
                bool isInCombatWithCrew = !npc.IsInstigator && npc.AIController is HumanAIController { ObjectiveManager: { CurrentObjective: AIObjectiveCombat combatObjective } } && crew.Contains(combatObjective.Enemy);
                if (isInCombatWithCrew) { return true; }
            }

            return false;
        }

        private HealRequestResult HealAllPending(bool force = false)
        {
            int totalCost = GetTotalCost();
            if (!force)
            {
                if (GetMoney() < totalCost) { return HealRequestResult.InsufficientFunds; }

                if (IsOutpostInCombat()) { return HealRequestResult.Refused; }
            }

            ImmutableArray<CharacterInfo> crew = GetCrewCharacters();
            foreach (NetCrewMember crewMember in PendingHeals)
            {
                CharacterInfo? targetCharacter = crewMember.FindCharacterInfo(crew);
                if (!(targetCharacter?.Character is { CharacterHealth: { } health })) { continue; }

                foreach (NetAffliction affliction in crewMember.Afflictions)
                {
                    health.ReduceAffliction(null, affliction.Identifier, affliction.Prefab?.MaxStrength ?? affliction.Strength);
                }
            }

            if (campaign != null)
            {
                campaign.Money -= totalCost;
            }

            ClearPendingHeals();

            return HealRequestResult.Success;
        }

        private void ClearPendingHeals()
        {
            PendingHeals.Clear();
        }

        private void RemovePendingAffliction(NetCrewMember crewMember, NetAffliction affliction)
        {
            foreach (NetCrewMember listMember in PendingHeals.ToList())
            {
                PendingHeals.Remove(listMember);
                NetCrewMember pendingMember = listMember;

                if (pendingMember.CharacterEquals(crewMember))
                {
                    List<NetAffliction> newAfflictions = new List<NetAffliction>();
                    foreach (NetAffliction pendingAffliction in pendingMember.Afflictions)
                    {
                        if (pendingAffliction.AfflictionEquals(affliction)) { continue; }

                        newAfflictions.Add(pendingAffliction);
                    }

                    pendingMember.Afflictions = newAfflictions.ToArray();
                }

                if (!pendingMember.Afflictions.Any()) { continue; }

                PendingHeals.Add(pendingMember);
            }
        }

        private void InsertPendingCrewMember(NetCrewMember crewMember)
        {
            if (PendingHeals.FirstOrNull(m => m.CharacterEquals(crewMember)) is { } foundHeal)
            {
                PendingHeals.Remove(foundHeal);
            }

            PendingHeals.Add(crewMember);
        }

        public static bool IsHealable(Affliction affliction)
        {
            return affliction.Prefab.HealableInMedicalClinic && affliction.Strength > GetShowTreshold(affliction);
            static float GetShowTreshold(Affliction affliction) => Math.Max(0, Math.Min(affliction.Prefab.ShowIconToOthersThreshold, affliction.Prefab.ShowInHealthScannerThreshold));
        }

        private NetAffliction[] GetAllAfflictions(CharacterHealth health)
        {
            IEnumerable<Affliction> rawAfflictions = health.GetAllAfflictions().Where(a => IsHealable(a));

            List<NetAffliction> afflictions = new List<NetAffliction>();

            foreach (Affliction affliction in rawAfflictions)
            {
                NetAffliction newAffliction;
                if (afflictions.FirstOrNull(netAffliction => netAffliction.AfflictionEquals(affliction.Prefab)) is { } foundAffliction)
                {
                    afflictions.Remove(foundAffliction);
                    foundAffliction.Strength += (ushort)affliction.Strength;
                    foundAffliction.Price += (ushort)GetAdjustedPrice(GetHealPrice(affliction));
                    newAffliction = foundAffliction;
                }
                else
                {
                    newAffliction = new NetAffliction { Affliction = affliction };
                    newAffliction.Price = (ushort)GetAdjustedPrice(newAffliction.Price);
                }

                afflictions.Add(newAffliction);
            }

            return afflictions.ToArray();

            static int GetHealPrice(Affliction affliction) => (int)(affliction.Prefab.BaseHealCost + (affliction.Prefab.HealCostMultiplier * affliction.Strength));
        }

        public int GetTotalCost() => PendingHeals.SelectMany(h => h.Afflictions).Aggregate(0, (current, affliction) => current + affliction.Price);

        private int GetAdjustedPrice(int price) => campaign?.Map?.CurrentLocation is { Type: { HasOutpost: true } } currentLocation ? currentLocation.GetAdjustedHealCost(price) : int.MaxValue;

        public int GetMoney() => campaign?.Money ?? 0;

        public static ImmutableArray<CharacterInfo> GetCrewCharacters()
        {
#if DEBUG && CLIENT
            if (Screen.Selected is TestScreen)
            {
                return TestInfos.ToImmutableArray();
            }
#endif

            return Character.CharacterList.Where(c => c.Info != null && c.TeamID == CharacterTeamType.Team1).Select(c => c.Info).ToImmutableArray();
        }

#if DEBUG && CLIENT
        private static readonly CharacterInfo[] TestInfos =
        {
            new CharacterInfo("human"),
            new CharacterInfo("human"),
            new CharacterInfo("human"),
            new CharacterInfo("human"),
            new CharacterInfo("human"),
            new CharacterInfo("human"),
            new CharacterInfo("human")
        };

        private static readonly NetAffliction[] TestAfflictions =
        {
            new NetAffliction { Identifier = "internaldamage", Strength = 80, Price = 10 },
            new NetAffliction { Identifier = "blunttrauma", Strength = 50, Price = 10 },
            new NetAffliction { Identifier = "lacerations", Strength = 20, Price = 10 },
            new NetAffliction { Identifier = "burn", Strength = 10, Price = 10 }
        };
#endif
    }
}