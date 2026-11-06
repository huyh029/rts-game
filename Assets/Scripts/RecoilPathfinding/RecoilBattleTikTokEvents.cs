using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private readonly Dictionary<TikTokArmyRule, Dictionary<string, long>> _tikTokProgress = new();
        private readonly int[,] _tikTokPendingUnits = new int[2, CharacterTypeCount];
        private readonly Dictionary<string, bool> _tikTokUserTeams = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TikEvent>> _tikTokUnassignedEvents = new(StringComparer.Ordinal);

        private void HandleTikTokBattleEvent(TikEvent message)
        {
            if (message == null || _matchFinished) return;
            if (_tikTokArmySettings == null) LoadTikTokArmySettings();
            var kind = (message.type ?? "").ToLowerInvariant();
            // The connector's final event carries the incremental gift quantity in delta.
            if (kind == "gift_final") kind = "gift";
            if (Array.IndexOf(TikTokEventTypes, kind) < 0) return;
            var user = message.user?.user_id;
            if (string.IsNullOrEmpty(user)) user = message.user?.unique_id;
            if (string.IsNullOrWhiteSpace(user)) return;
            if (kind == "comment")
            {
                var command = message.comment?.Trim();
                if (command == "1" || command == "2")
                {
                    var requestedBlue = command == "1";
                    if (_tikTokUserTeams.TryGetValue(user, out var selectedBlue))
                    {
                        if (selectedBlue != requestedBlue)
                            Debug.Log($"TikTok: {user} đã chọn phe {(selectedBlue ? "Xanh" : "Đỏ")}, không được đổi phe.", this);
                    }
                    else
                    {
                        _tikTokUserTeams.Add(user, requestedBlue);
                        Debug.Log($"TikTok: {user} gia nhập phe {(requestedBlue ? "Xanh" : "Đỏ")}.", this);
                        if (_tikTokUnassignedEvents.TryGetValue(user, out var pendingEvents))
                        {
                            _tikTokUnassignedEvents.Remove(user);
                            foreach (var pendingEvent in pendingEvents) HandleTikTokBattleEvent(pendingEvent);
                            Debug.Log($"TikTok: đã chuyển {pendingEvents.Count} sự kiện đang dồn của {user} sang phe {(requestedBlue ? "Xanh" : "Đỏ")}.", this);
                        }
                    }
                    return; // Team-selection comments are commands, not reward events.
                }
            }
            if (!_tikTokUserTeams.TryGetValue(user, out var blue))
            {
                if (!_tikTokUnassignedEvents.TryGetValue(user, out var pendingEvents))
                    _tikTokUnassignedEvents[user] = pendingEvents = new List<TikEvent>();
                pendingEvents.Add(message);
                return;
            }
            foreach (var rule in _tikTokArmySettings.Rules)
            {
                if (!rule.Enabled || !string.Equals(rule.EventType, kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (kind == "gift" && !TikTokGiftMatches(rule, message.gift)) continue;
                if (kind == "comment" && !string.Equals(rule.Comment?.Trim(), message.comment?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var amount = kind == "like" ? Math.Max(0, message.like_count) :
                    kind == "gift" ? Math.Max(0, message.delta > 0 ? message.delta : message.count) : 1;
                if (amount == 0) continue;
                if (!_tikTokProgress.TryGetValue(rule, out var users)) _tikTokProgress[rule] = users = new Dictionary<string, long>();
                users.TryGetValue(user, out var previous);
                var total = previous + amount;
                var threshold = Math.Max(1, rule.Threshold);
                var rolls = total / threshold;
                users[user] = total % threshold;
                if (rolls == 0) continue;
                if (rule.RandomRewards != null && rule.RandomRewards.Count > 0)
                {
                    // Roll once for each earned reward, preserving the configured probabilities.
                    for (long roll = 0; roll < rolls; roll++) QueueTikTokRandomReward(rule, blue);
                }
                else QueueTikTokReward(rule, blue, rolls, 1f);
            }
        }

        private bool TikTokGiftMatches(TikTokArmyRule rule, TikGift gift)
        {
            if (gift == null) return false;
            if (rule.GiftId > 0) return gift.id == rule.GiftId;
            if (!string.IsNullOrWhiteSpace(gift.name))
                return string.Equals(rule.Name?.Trim(), gift.name.Trim(), StringComparison.OrdinalIgnoreCase);
            // Missing IDs must be filled in; matching by price can call the wrong army.
            return false;
        }

        private void QueueTikTokRandomReward(TikTokArmyRule source, bool blue)
        {
            long weightTotal = 0;
            foreach (var choice in source.RandomRewards) if (choice != null) weightTotal += Math.Max(0, choice.Weight);
            if (weightTotal <= 0) return;
            var pick = UnityEngine.Random.value * weightTotal;
            foreach (var choice in source.RandomRewards)
            {
                if (choice == null || choice.Weight <= 0) continue;
                pick -= choice.Weight;
                if (pick > 0) continue;
                var reward = _tikTokArmySettings.Rules.Find(r => r.Enabled && r.EventType == "gift" && r.Name == choice.GiftName);
                if (reward != null) QueueTikTokReward(reward, blue, 1, choice.CountMultiplier);
                return;
            }
        }

        private void QueueTikTokReward(TikTokArmyRule rule, bool blue, long rolls, float multiplier)
        {
            if (rule.UnitCount <= 0) return;
            if (rule.UnitType < 0 || rule.UnitType >= CharacterTypeCount || FindTemplateForType(blue, rule.UnitType) == null)
            {
                Debug.LogWarning($"TikTok: chưa gán loại quân hợp lệ cho {rule.Name}. Không spawn thay bằng loại khác.", this);
                return;
            }
            var perRoll = Math.Max(0, (long)Math.Floor(rule.UnitCount * (double)Math.Max(0f, multiplier)));
            var team = blue ? 0 : 1;
            var available = int.MaxValue - (long)_tikTokPendingUnits[team, rule.UnitType];
            var amount = perRoll == 0 ? 0 : Math.Min(available, rolls > available / perRoll ? available : rolls * perRoll);
            _tikTokPendingUnits[team, rule.UnitType] += (int)amount;
            if (amount > 0) Debug.Log($"TikTok {rule.Name}: +{amount} {CharacterTypeLabels[rule.UnitType]} → {(blue ? "Xanh" : "Đỏ")}." +
                (!_combatStarted ? " Chờ bắt đầu trận bằng L." : ""), this);
        }

        private void FlushTikTokReinforcements()
        {
            if (!_combatStarted) return;
            for (var team = 0; team < 2; team++)
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                var pending = _tikTokPendingUnits[team, type];
                if (pending <= 0) continue;
                var total = team == 0 ? _blueSelectedSpawnTotal : _redSelectedSpawnTotal;
                var amount = Math.Min(pending, int.MaxValue - total);
                if (amount <= 0) continue;
                EnqueueSpawnType(team == 0 ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue, type, amount);
                if (team == 0) _blueSelectedSpawnTotal += amount;
                else _redSelectedSpawnTotal += amount;
                _tikTokPendingUnits[team, type] -= amount;
            }
        }
    }
}
