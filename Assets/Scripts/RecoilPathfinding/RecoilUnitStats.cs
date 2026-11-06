using System;
using UnityEngine;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    public enum RecoilCombatRole : byte { Unknown, Militia, Infantry, Spearman, Archer, Cavalry, Elephant, Siege, Mage }
    public enum RecoilAttackType : byte { Unspecified, Melee, Ranged }
    [Serializable]
    public struct RecoilUnitCombatStats
    {
        public RecoilCombatRole CombatRole;
        public RecoilAttackType AttackType;
        [Min(1), InspectorName("HP tối đa")] public int MaxHealth;
        [Min(0), InspectorName("Sát thương")] public int Damage;
        [Min(0.01f), InspectorName("Tầm đánh")] public float Range;
        [Min(0.01f), InspectorName("Tốc độ đánh (đòn/giây)")] public float AttackSpeed;
        [Min(0.01f), InspectorName("Tốc độ di chuyển")] public float MoveSpeed;
        [Range(0f, 1f), InspectorName("Armor / giáp đánh gần (0–1)")] public float Armor;
        [Range(0f, 1f), InspectorName("P. Armor / giáp đánh xa (0–1)")] public float SecondaryArmor;
        [Min(0f), InspectorName("Bán kính sát thương lan")] public float AreaRadius;
        [Range(0f, 1f), InspectorName("Hiệu quả sát thương ở rìa nổ")] public float AreaEdgeEffectiveness;
        [HideInInspector] public bool SecondaryDamage; // Legacy serialization fallback for attack type.
        [Min(1), InspectorName("Salvo size")] public int SalvoSize;
        [Min(0f), InspectorName("Salvo delay (seconds)")] public float SalvoDelay;
        [Min(0f), InspectorName("Windup (seconds)")] public float Windup;
        [Min(0), InspectorName("Target category mask (0 = unit type)")] public int TargetCategoryMask;
        [Min(0), InspectorName("Allowed target category mask (0 = all)")] public int OnlyTargetCategoryMask;
        [Min(0), InspectorName("Lower priority target category mask")] public int AvoidTargetCategoryMask;
        [InspectorName("Target priority (lower is preferred)")] public int TargetPriority;
        public bool IsConfigured => MaxHealth > 0;
        public float AttackInterval => 1f / Mathf.Max(0.01f, AttackSpeed);
    }

    [DisallowMultipleComponent]
    public sealed class RecoilUnitStats : MonoBehaviour
    {
        [SerializeField] private RecoilUnitCombatStats stats;
        public RecoilUnitCombatStats Stats => stats;
    }

    public static class RecoilUnitDamage
    {
        public static bool UsesRangedArmor(RecoilUnitCombatStats attacker) =>
            attacker.AttackType == RecoilAttackType.Ranged ||
            (attacker.AttackType == RecoilAttackType.Unspecified && attacker.SecondaryDamage);

        public static float Bonus(RecoilCombatRole attacker, RecoilCombatRole target)
        {
            switch (attacker)
            {
                case RecoilCombatRole.Infantry:
                    return target == RecoilCombatRole.Siege ? .05f :
                        target == RecoilCombatRole.Archer || target == RecoilCombatRole.Mage ? .10f : 0f;
                case RecoilCombatRole.Spearman:
                    return target == RecoilCombatRole.Cavalry ? .15f : target == RecoilCombatRole.Elephant ? .10f : 0f;
                case RecoilCombatRole.Archer:
                    return target == RecoilCombatRole.Spearman ? .10f : target == RecoilCombatRole.Mage ? .05f : 0f;
                case RecoilCombatRole.Cavalry:
                    return target == RecoilCombatRole.Archer || target == RecoilCombatRole.Mage ? .15f :
                        target == RecoilCombatRole.Siege ? .10f : 0f;
                case RecoilCombatRole.Elephant:
                    return target == RecoilCombatRole.Infantry ? .10f : target == RecoilCombatRole.Archer ? .05f : 0f;
                case RecoilCombatRole.Siege: return target == RecoilCombatRole.Elephant ? .10f : 0f;
                case RecoilCombatRole.Mage:
                    return target == RecoilCombatRole.Spearman ? .15f : target == RecoilCombatRole.Infantry ? .10f :
                        target == RecoilCombatRole.Elephant ? .05f : 0f;
                default: return 0f;
            }
        }

        public static int Calculate(float raw, RecoilUnitCombatStats attacker, RecoilUnitCombatStats target) =>
            Calculate(raw * (1f + Bonus(attacker.CombatRole, target.CombatRole)), target, UsesRangedArmor(attacker));

        public static int Calculate(float raw, RecoilUnitCombatStats target, bool secondary)
        {
            var armor = target.IsConfigured ? (secondary ? target.SecondaryArmor : target.Armor) : 0f;
            return math.max(0, (int)math.round(math.max(0f, raw) * (1f - math.clamp(armor, 0f, 1f))));
        }
    }
}
