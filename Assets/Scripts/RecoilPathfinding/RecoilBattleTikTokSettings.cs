using System;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private bool _tikTokSettingsOpen;
        private TikTokArmySettings _tikTokArmySettings;
        private Vector2 _tikTokSettingsScroll;
        private string _tikTokSettingsStatus = "";
        private static readonly string[] TikTokEventTypes = { "like", "gift", "follow", "share", "comment" };
        private string TikTokArmySettingsPath => Path.Combine(Application.streamingAssetsPath, "tiktok_army_settings.json");
        private float SetupNavigationWidth => Mathf.Min(210f, Screen.width * .17f);

        private void DrawSetupNavigation()
        {
            var style = new GUIStyle(GUI.skin.button) { fontSize = 20, wordWrap = true };
            var left = 12f;
            var width = SetupNavigationWidth;
            if (GUI.Toggle(new Rect(left, 70f, width, 64f), !_tikTokSettingsOpen, "Setup lính", style) && _tikTokSettingsOpen)
            {
                if (_tikTokArmySettings == null || SaveTikTokArmySettings()) _tikTokSettingsOpen = false;
            }
            if (GUI.Toggle(new Rect(left, 144f, width, 64f), _tikTokSettingsOpen, "Setup quà", style))
                _tikTokSettingsOpen = true;
            var previousEnabled = GUI.enabled;
            GUI.enabled = false;
                GUI.Button(new Rect(left, 218f, width, 64f), "Setup tướng\n(Sắp có)", style);
                GUI.Button(new Rect(left, 292f, width, 64f), "Quân đầu game\n(Sắp có)", style);
            GUI.enabled = previousEnabled;
            if (GUI.Button(new Rect(left, Screen.height - 74f, width, 56f), "Quay lại", style))
            {
                if (_tikTokArmySettings != null && !SaveTikTokArmySettings()) return;
                SaveLobbyUnitStats();
                _tikTokSettingsOpen = false;
                _settingsOpen = false;
            }
        }

        private void LoadTikTokArmySettings()
        {
            _tikTokArmySettings = new TikTokArmySettings();
            try
            {
                if (!File.Exists(TikTokArmySettingsPath))
                {
                    var previousPath = Path.Combine(Application.persistentDataPath, "tiktok_army_settings.json");
                    if (File.Exists(previousPath))
                    {
                        Directory.CreateDirectory(Application.streamingAssetsPath);
                        File.Copy(previousPath, TikTokArmySettingsPath, false);
                    }
                }
                if (File.Exists(TikTokArmySettingsPath))
                    _tikTokArmySettings = JsonUtility.FromJson<TikTokArmySettings>(File.ReadAllText(TikTokArmySettingsPath)) ?? new TikTokArmySettings();
                if (_tikTokArmySettings.Rules == null) _tikTokArmySettings.Rules = new System.Collections.Generic.List<TikTokArmyRule>();
                _tikTokArmySettings.Rules.RemoveAll(rule => rule == null);
                foreach (var rule in _tikTokArmySettings.Rules)
                    InitializeTikTokRandomRule(rule);
                if (!File.Exists(TikTokArmySettingsPath)) AddTikTokSampleRows();
            }
            catch (Exception e) { _tikTokSettingsStatus = "Không đọc được cấu hình: " + e.Message; }
        }

        private void AddTikTokSampleRows()
        {
            var samples = new[]
            {
                new TikTokArmyRule { Name = "50 Tym", EventType = "like", Threshold = 50, UnitCount = 2, Action = "+2 Dân Binh", Intro = "Intro quân lính nhỏ, 1/4 màn hình, tối đa 2s" },
                new TikTokArmyRule { Name = "Follow", EventType = "follow", Threshold = 1, UnitCount = 0 },
                new TikTokArmyRule { Name = "Hoa Hồng", EventType = "gift", Threshold = 1, Coins = 1, UnitType = 7, UnitCount = 50, Action = "+50 Bộ Binh", Intro = "Intro Bộ binh + số lượng" },
                new TikTokArmyRule { Name = "Rosa", EventType = "gift", Threshold = 1, Coins = 10, UnitCount = 50, Action = "+50 Cung Binh", Intro = "Intro Cung binh + số lượng", TeamOneCombo = "x10 hoặc tích 20 Rosa → Triệu Tử Long (đánh gần)", TeamTwoCombo = "x10 hoặc tích 20 Rosa → Hạ Hầu Đôn (đánh gần)" },
                new TikTokArmyRule { Name = "Perfume", EventType = "gift", Threshold = 1, Coins = 20, UnitType = 2, UnitCount = 20, Action = "+20 Giáo Binh", Intro = "Intro Giáo binh + số lượng", TeamOneCombo = "x10 hoặc tích 20 Perfume → Quan Vũ (đánh gần)", TeamTwoCombo = "x10 hoặc tích 20 Perfume → Lữ Bố (đánh gần)" },
                new TikTokArmyRule { Name = "Doughnut", EventType = "gift", Threshold = 1, Coins = 30, GiftId = 5879, UnitType = 3, UnitCount = 20, Action = "+20 Kỵ Binh", Intro = "Intro Kỵ binh + số lượng", TeamOneCombo = "x10 hoặc tích 20 Doughnut → Gia Cát Lượng (đánh xa)", TeamTwoCombo = "x10 hoặc tích 20 Doughnut → Chu Du (đánh xa)" },
                new TikTokArmyRule { Name = "Hạc Giấy", EventType = "gift", Threshold = 1, Coins = 99, GiftId = 5659, UnitType = 6, UnitCount = 10, Action = "+10 Voi Chiến", Intro = "Intro Voi chiến + số lượng", TeamOneCombo = "x5 hoặc tích 10 Hạc Giấy → Lưu Bị (đánh xa)", TeamTwoCombo = "x5 hoặc tích 10 Hạc Giấy → Tào Tháo (đánh xa)" },
                new TikTokArmyRule { Name = "Kính Râm", EventType = "gift", Threshold = 1, Coins = 199, GiftId = 5509, UnitType = 4, UnitCount = 4, Action = "+4 Máy Bắn Đá", Intro = "Intro Máy bắn đá + số lượng" },
                new TikTokArmyRule { Name = "Corgi", EventType = "gift", Threshold = 1, Coins = 299, GiftId = 6267, UnitCount = 2, Action = "+2 Pháp Sư", Intro = "Intro Pháp sư + số lượng" },
                new TikTokArmyRule { Name = "Heo", EventType = "gift", Threshold = 1, Coins = 10, GiftId = 8825, UnitCount = 0, Action = "Random 1/2 số quân quà trực tiếp: Rosa 40%; Perfume 34%; Doughnut 20%; Hạc Giấy 4%; Kính Râm 2%" }
            };
            foreach (var sample in samples)
                if (!_tikTokArmySettings.Rules.Exists(rule => rule.Name == sample.Name))
                {
                    InitializeTikTokRandomRule(sample);
                    _tikTokArmySettings.Rules.Add(sample);
                }
        }

        private static void InitializeTikTokRandomRule(TikTokArmyRule rule)
        {
            if (rule.Name != "Heo" || (rule.RandomRewards != null && rule.RandomRewards.Count > 0)) return;
            rule.RandomRewards = new System.Collections.Generic.List<TikTokRandomReward>
            {
                new TikTokRandomReward { GiftName = "Rosa", Weight = 40 },
                new TikTokRandomReward { GiftName = "Perfume", Weight = 34 },
                new TikTokRandomReward { GiftName = "Doughnut", Weight = 20 },
                new TikTokRandomReward { GiftName = "Hạc Giấy", Weight = 4 },
                new TikTokRandomReward { GiftName = "Kính Râm", Weight = 2 }
            };
        }

        private bool SaveTikTokArmySettings()
        {
            try
            {
                Directory.CreateDirectory(Application.streamingAssetsPath);
                File.WriteAllText(TikTokArmySettingsPath, JsonUtility.ToJson(_tikTokArmySettings, true));
                _tikTokSettingsStatus = "Đã lưu cấu hình.";
                return true;
            }
            catch (Exception e) { _tikTokSettingsStatus = "Không lưu được: " + e.Message; return false; }
        }

        private static int TikTokIntInput(string label, int value, int minimum)
        {
            GUILayout.Label(label);
            var text = GUILayout.TextField(value.ToString(), GUILayout.Width(90));
            return int.TryParse(text, out var parsed) ? Mathf.Max(minimum, parsed) : value;
        }

        private void DrawTikTokArmySettings()
        {
            DrawTikTokArmyTable();
        }

        private GUIStyle TikTokTableStyle(bool bold = false)
        {
            var style = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 17, alignment = TextAnchor.MiddleLeft,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                padding = new RectOffset(8, 8, 5, 5)
            };
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                         style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                state.background = Texture2D.whiteTexture;
                state.textColor = Color.black;
            }
            return style;
        }

        private int TikTokTableNumber(Rect rect, int value, int minimum, GUIStyle style)
        {
            var text = GUI.TextField(rect, value.ToString(), style);
            return int.TryParse(text, out var parsed) ? Mathf.Max(minimum, parsed) : value;
        }

        private void DrawTikTokArmyTable()
        {
            if (_tikTokArmySettings == null) LoadTikTokArmySettings();
            var area = new Rect(SetupNavigationWidth + 28f, 60f,
                Screen.width - SetupNavigationWidth - 44f, Mathf.Max(140f, Screen.height - 80f));
            var oldColor = GUI.color;
            var oldContent = GUI.contentColor;
            var oldBackground = GUI.backgroundColor;
            GUI.color = GUI.contentColor = GUI.backgroundColor = Color.white;
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            var cell = TikTokTableStyle();
            var heading = TikTokTableStyle(true);
            GUI.Label(new Rect(area.x + 10, area.y + 4, area.width - 20, 34), "SETUP QUÀ — SỰ KIỆN → QUÂN", heading);
            GUI.Label(new Rect(area.x + 10, area.y + 38, area.width - 20, 30), "Bấm ô sự kiện / loại quân để chuyển lựa chọn. Ngưỡng là số sự kiện cho mỗi lượt roll.", cell);
            var viewport = new Rect(area.x + 10, area.y + 78, area.width - 20, Mathf.Max(30, area.height - 180));
            var widths = new[] { 54f, 180f, 85f, 100f, 310f, 290f, 380f, 380f, 130f, 100f, 180f, 200f, 110f, 65f };
            var headers = new[] { "Bật", "Tên Quà, Hành Động", "Số Xu", "ID", "Sự Kiện / Kết quả", "UX/UI", "Combo đội 1", "Combo đội 2", "Loại sự kiện ↻", "Ngưỡng", "Comment", "Loại quân ↻", "Số quân", "Xóa" };
            const float rowHeight = 100f;
            cell.wordWrap = heading.wordWrap = true;
            var tableWidth = 0f;
            foreach (var columnWidth in widths) tableWidth += columnWidth;
            _tikTokSettingsScroll = GUI.BeginScrollView(viewport, _tikTokSettingsScroll,
                new Rect(0, 0, tableWidth, (_tikTokArmySettings.Rules.Count + 1) * rowHeight));
            var remove = -1;
            for (var row = -1; row < _tikTokArmySettings.Rules.Count; row++)
            {
                var x = 0f;
                var rule = row >= 0 ? _tikTokArmySettings.Rules[row] : null;
                for (var column = 0; column < widths.Length; column++)
                {
                    var rect = new Rect(x, (row + 1) * rowHeight, widths[column] - 1, rowHeight - 1);
                    x += widths[column];
                    if (row < 0) { GUI.Label(rect, headers[column], heading); continue; }
                    switch (column)
                    {
                        case 0:
                            if (GUI.Button(rect, rule.Enabled ? "✓" : "—", cell)) rule.Enabled = !rule.Enabled;
                            break;
                        case 1:
                            rule.Name = GUI.TextField(rect, rule.Name ?? "", cell); break;
                        case 2: rule.Coins = TikTokTableNumber(rect, rule.Coins, 0, cell); break;
                        case 3:
                            if (rule.EventType == "gift")
                            {
                                var idText = GUI.TextField(rect, rule.GiftId > 0 ? rule.GiftId.ToString() : "", cell);
                                if (string.IsNullOrWhiteSpace(idText)) rule.GiftId = 0;
                                else if (int.TryParse(idText, out var id)) rule.GiftId = Mathf.Max(0, id);
                            }
                            else GUI.Label(rect, "—", cell);
                            break;
                        case 4: rule.Action = GUI.TextArea(rect, rule.Action ?? "", cell); break;
                        case 5: rule.Intro = GUI.TextArea(rect, rule.Intro ?? "", cell); break;
                        case 6: rule.TeamOneCombo = GUI.TextArea(rect, rule.TeamOneCombo ?? "", cell); break;
                        case 7: rule.TeamTwoCombo = GUI.TextArea(rect, rule.TeamTwoCombo ?? "", cell); break;
                        case 8:
                            if (GUI.Button(rect, rule.EventType + " ↻", cell))
                                rule.EventType = TikTokEventTypes[(Mathf.Max(0, Array.IndexOf(TikTokEventTypes, rule.EventType)) + 1) % TikTokEventTypes.Length];
                            break;
                        case 9: rule.Threshold = TikTokTableNumber(rect, rule.Threshold, 1, cell); break;
                        case 10:
                            if (rule.EventType == "comment") rule.Comment = GUI.TextField(rect, rule.Comment ?? "", cell);
                            else GUI.Label(rect, "—", cell);
                            break;
                        case 11:
                            rule.UnitType = Mathf.Clamp(rule.UnitType, -1, CharacterTypeLabels.Length - 1);
                            var unitName = rule.UnitType < 0 ? "Chọn loại quân" : CharacterTypeLabels[rule.UnitType];
                            if (GUI.Button(rect, unitName + " ↻", cell)) rule.UnitType = (rule.UnitType + 2) % (CharacterTypeLabels.Length + 1) - 1;
                            break;
                        case 12: rule.UnitCount = TikTokTableNumber(rect, rule.UnitCount, 0, cell); break;
                        case 13: if (GUI.Button(rect, "Xóa", cell)) remove = row; break;
                    }
                }
            }
            GUI.EndScrollView();
            if (remove >= 0) _tikTokArmySettings.Rules.RemoveAt(remove);
            var footerY = area.yMax - 90;
            if (GUI.Button(new Rect(area.x + 10, footerY, 170, 38), "+ Thêm dòng", heading)) _tikTokArmySettings.Rules.Add(new TikTokArmyRule());
            if (GUI.Button(new Rect(area.x + 190, footerY, 170, 38), "Lưu cấu hình", heading)) SaveTikTokArmySettings();
            if (GUI.Button(new Rect(area.x + 370, footerY, 200, 38), "Thêm bảng mẫu", heading)) AddTikTokSampleRows();
            GUI.Label(new Rect(area.x + 580, footerY, Mathf.Max(100f, area.width - 590f), 38), "Comment 1: Xanh | 2: Đỏ. Không đổi phe.", cell);
            GUI.Label(new Rect(area.x + 10, footerY + 42, area.width - 20, 34), _tikTokSettingsStatus, cell);
            GUI.color = oldColor;
            GUI.contentColor = oldContent;
            GUI.backgroundColor = oldBackground;
        }

    }
}
