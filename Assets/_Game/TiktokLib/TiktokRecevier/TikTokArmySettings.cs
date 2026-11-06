using System;
using System.Collections.Generic;

[Serializable]
public sealed class TikTokArmySettings
{
    public string HeroRules = "Mỗi user chỉ có một tướng đang tồn tại cho mỗi loại quà. Đạt mốc trước L: đứng trong đội hình; sau L: quân tiếp viện. Intro riêng cho mỗi tướng; xử lý nhiều intro đồng thời chưa chốt. HP cao, tên và avatar user, thanh máu riêng, VFX nhận diện, kỹ năng đánh gần/xa/AOE; có thể chết và hồi sinh.";
    public List<TikTokArmyRule> Rules = new List<TikTokArmyRule>();
}

[Serializable]
public sealed class TikTokArmyRule
{
    public string Name = "";
    public int Coins;
    public string Action = "";
    public string Intro = "";
    public string TeamOneCombo = "";
    public string TeamTwoCombo = "";
    public bool Enabled = true;
    public string EventType = "like";
    public int Threshold = 100;
    public int GiftId;
    public string Comment = "";
    public int UnitType = -1;
    public int UnitCount = 1;
    public List<TikTokRandomReward> RandomRewards = new List<TikTokRandomReward>();
}

[Serializable]
public sealed class TikTokRandomReward
{
    public string GiftName;
    public int Weight;
    public float CountMultiplier = .5f;
}
