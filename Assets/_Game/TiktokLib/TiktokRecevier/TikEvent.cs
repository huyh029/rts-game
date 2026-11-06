using System;

[Serializable]
public class TikEvent 
{
    public string type;
    public long ts;

    public TikUser user;

    // comment
    public string comment;

    // like
    public int like_count;
    public int total_likes;

    // gift
    public TikGift gift;
    public int count;   // số lượng quà
    public int total;   // tổng xu = price * count
    public int delta;

    // debug/local only, không gửi qua network để tiết kiệm băng thông
    public string rawJson;
}

[Serializable]
public class TikUser
{
    public string user_id;
    public string unique_id;
    public string nickname;
    public string avatar_url;
    // TikUser uses flattened serialization via TikEvent; implement INetworkSerializable if needed later
}

[Serializable]
public class TikGift
{
    public int id;
    public string name;
    public int price;   // giá 1 món (diamond)

    // TikGift uses flattened serialization via TikEvent; implement INetworkSerializable if needed later
}
