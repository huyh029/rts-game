using System;
using System.Collections.Generic;
using System.IO;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    public static class CongThanhUnitStatsSetup
    {
        internal static bool TryGetStats(string name, out RecoilUnitCombatStats stats)
        {
            var key = name.ToUpperInvariant().Replace("_", "").Replace(" ", "");
            if (key.Contains("DANBINH")) stats = Make(50, 5, 1.2f, 1f, 3.5f, 0f, 0f, 0f);
            else if (key.Contains("BOBINH") || key.Contains("DAOBINH")) stats = Make(150, 18, 1.5f, 1f, 3.5f, .2f, .1f, 0f);
            else if (key.Contains("GIAOBINH")) stats = Make(130, 22, 2.3f, .8f, 3.2f, .15f, .1f, 0f);
            else if (key.Contains("CUNGKY") || key.Contains("CUNGBINH")) stats = Make(85, 16, 14f, .8f, 3.5f, .05f, .05f, 0f);
            else if (key.Contains("KYBINH")) stats = Make(230, 28, 1.8f, .9f, 6.5f, .2f, .15f, 0f);
            else if (key.Contains("VOI")) stats = Make(1000, 65, 2.5f, .4f, 2.5f, .35f, .25f, 3f);
            else if (key.Contains("MAYBANDA") || key.Contains("MAYDA")) stats = Make(450, 110, 25f, .2f, 1.5f, .05f, .1f, 5f);
            else if (key.Contains("PHAPSU") || key.Contains("QUANSU")) stats = Make(110, 35, 12f, .5f, 3.3f, .05f, .15f, 4f);
            else { stats = default; return false; }
            stats.SecondaryDamage = key.Contains("PHAPSU") || key.Contains("QUANSU");
            return true;
        }

        private static RecoilUnitCombatStats Make(int hp, int damage, float range, float attackSpeed,
            float moveSpeed, float armor, float secondaryArmor, float area) => new RecoilUnitCombatStats
        {
            MaxHealth = hp, Damage = damage, Range = range, AttackSpeed = attackSpeed,
            MoveSpeed = moveSpeed, Armor = armor, SecondaryArmor = secondaryArmor, AreaRadius = area
        };

        [MenuItem("Game Tools/Công Thành/Áp dụng bảng thông số lính")]
        public static void ApplyAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var report = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Data/Characters/Prefabs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!TryGetStats(Path.GetFileNameWithoutExtension(path), out var stats))
                {
                    report.Add(path + ": chưa có hàng thông số tương ứng.");
                    continue;
                }
                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                    var agent = root.GetComponent<RecoilNavMeshPathAgent>();
                    var before = agent != null ? agent.MovementSettings : default;
                    var component = root.GetComponent<RecoilUnitStats>();
                    if (component == null) component = root.AddComponent<RecoilUnitStats>();
                    var serialized = new SerializedObject(component);
                    var data = serialized.FindProperty("stats");
                    data.FindPropertyRelative("MaxHealth").intValue = stats.MaxHealth;
                    data.FindPropertyRelative("Damage").intValue = stats.Damage;
                    data.FindPropertyRelative("Range").floatValue = stats.Range;
                    data.FindPropertyRelative("AttackSpeed").floatValue = stats.AttackSpeed;
                    data.FindPropertyRelative("MoveSpeed").floatValue = stats.MoveSpeed;
                    data.FindPropertyRelative("Armor").floatValue = stats.Armor;
                    data.FindPropertyRelative("SecondaryArmor").floatValue = stats.SecondaryArmor;
                    data.FindPropertyRelative("AreaRadius").floatValue = stats.AreaRadius;
                    data.FindPropertyRelative("SecondaryDamage").boolValue = stats.SecondaryDamage;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    if (agent != null && (agent.MovementSettings.BodyRadius != before.BodyRadius ||
                        agent.MovementSettings.AvoidanceRadius != before.AvoidanceRadius))
                        throw new InvalidOperationException("Bán kính đã thay đổi ngoài ý muốn.");
                    PrefabUtility.SaveAsPrefabAsset(root, path, out var success);
                    if (!success) throw new InvalidOperationException("Không lưu được prefab.");
                    report.Add(root.name + $": HP {stats.MaxHealth}, Damage {stats.Damage}, Range {stats.Range}, " +
                        $"Attack Speed {stats.AttackSpeed}, Move Speed {stats.MoveSpeed}, Armor {stats.Armor:P0}, " +
                        $"P. Armor {stats.SecondaryArmor:P0}, AoE {stats.AreaRadius}. Giữ nguyên bán kính.");
                }
                catch (Exception exception) { report.Add(path + ": LỖI " + exception.Message); Debug.LogException(exception); }
                finally { if (root != null) PrefabUtility.UnloadPrefabContents(root); }
            }
            const string reportPath = "Assets/Data/Characters/UnitStatsReport.txt";
            File.WriteAllLines(reportPath, report);
            AssetDatabase.ImportAsset(reportPath);
            Debug.Log("Thông số lính:\n" + string.Join("\n", report));
        }
    }
}
