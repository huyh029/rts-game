using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private static readonly string[] CombatRoleLabels =
            { "Chưa chọn", "Dân binh", "Bộ binh", "Giáo binh", "Cung binh", "Kỵ binh", "Voi chiến", "Máy bắn đá", "Pháp sư" };
        private static readonly RecoilCombatRole[] DefaultCombatRoles =
        {
            RecoilCombatRole.Archer, RecoilCombatRole.Militia, RecoilCombatRole.Spearman,
            RecoilCombatRole.Cavalry, RecoilCombatRole.Siege, RecoilCombatRole.Mage,
            RecoilCombatRole.Elephant, RecoilCombatRole.Infantry
        };

        private void InitializeCombatRoles()
        {
            for (var type = 0; type < lobbyUnitStats.Length; type++)
            {
                var stats = lobbyUnitStats[type];
                if (stats.CombatRole == RecoilCombatRole.Unknown && type < DefaultCombatRoles.Length)
                    stats.CombatRole = DefaultCombatRoles[type];
                if (stats.AttackType == RecoilAttackType.Unspecified)
                    stats.AttackType = stats.CombatRole == RecoilCombatRole.Archer || stats.CombatRole == RecoilCombatRole.Siege ||
                        stats.CombatRole == RecoilCombatRole.Mage ? RecoilAttackType.Ranged : RecoilAttackType.Melee;
                stats.SecondaryDamage = stats.AttackType == RecoilAttackType.Ranged;
                // This ruleset applies the same raw damage throughout the AoE.
                stats.AreaEdgeEffectiveness = 1f;
                lobbyUnitStats[type] = stats;
            }
        }

        private void DrawCombatRoleControls(Rect rect, ref RecoilUnitCombatStats stats)
        {
            var role = Mathf.Clamp((int)stats.CombatRole, 0, CombatRoleLabels.Length - 1);
            if (GUI.Button(new Rect(rect.x, rect.y, rect.width * .58f, rect.height), "Vai trò: " + CombatRoleLabels[role]))
                stats.CombatRole = (RecoilCombatRole)(role % (CombatRoleLabels.Length - 1) + 1);
            if (GUI.Button(new Rect(rect.x + rect.width * .60f, rect.y, rect.width * .40f, rect.height),
                stats.AttackType == RecoilAttackType.Ranged ? "Đánh xa" : "Đánh gần"))
                stats.AttackType = stats.AttackType == RecoilAttackType.Ranged ? RecoilAttackType.Melee : RecoilAttackType.Ranged;
            stats.SecondaryDamage = stats.AttackType == RecoilAttackType.Ranged;
        }
    }
}
