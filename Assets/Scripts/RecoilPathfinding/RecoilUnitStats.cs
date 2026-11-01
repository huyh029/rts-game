using System;
using UnityEngine;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    [Serializable]
    public struct RecoilUnitCombatStats
    {
        [Min(1), InspectorName("HP tối đa")] public int MaxHealth;
        [Min(0), InspectorName("Sát thương")] public int Damage;
        [Min(0.01f), InspectorName("Tầm đánh")] public float Range;
        [Min(0.01f), InspectorName("Tốc độ đánh (đòn/giây)")] public float AttackSpeed;
        [Min(0.01f), InspectorName("Tốc độ di chuyển")] public float MoveSpeed;
        [Range(0f, 1f), InspectorName("Giáp vật lý (0–1)")] public float Armor;
        [Range(0f, 1f), InspectorName("Kháng phép (0–1)")] public float SecondaryArmor;
        [Min(0f), InspectorName("Bán kính sát thương lan")] public float AreaRadius;
        [InspectorName("Gây sát thương phép")] public bool SecondaryDamage;
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
        public static int Calculate(int raw, RecoilUnitCombatStats target, bool secondary)
        {
            var armor = target.IsConfigured ? (secondary ? target.SecondaryArmor : target.Armor) : 0f;
            return math.max(0, (int)math.round(math.max(0, raw) * (1f - math.clamp(armor, 0f, 1f))));
        }
    }
}
