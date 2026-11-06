using UnityEngine;
using Newtonsoft.Json;

[CreateAssetMenu(menuName = "Configs/TiktokConfig")]
public class TiktokConfigSO : DefaultSO
{
    public TiktokData[] tiktokDatas;

    protected override bool OnLoadContent(string json)
    {
        tiktokDatas = JsonConvert.DeserializeObject<TiktokData[]>(json);
        return true;
    }

    protected override string SaveContent()
    {
        return JsonConvert.SerializeObject(tiktokDatas, Formatting.Indented);
    }

    [System.Serializable]
    public class TiktokData
    {
        public int IdGift;
        public float PriceGift;
        public short LikeCount;
        public bool IsFollow;
        public bool IsShare;
        public string[] Comments;
        public bool IsRandomAction;
        public int[] ActiveIds;
    }
}

