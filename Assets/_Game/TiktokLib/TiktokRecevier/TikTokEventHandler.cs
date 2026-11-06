using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public abstract class TikTokEventHandler : MonoBehaviour
{
    public TiktokConfigSO tiktokConfigSO;
    [SerializeField] private float followCooldownSeconds = 3600f;
    [SerializeField] private bool cacheUserAvatars = true;
    [SerializeField] private bool logGiftEvents = true;
    protected Dictionary<string, int> UserLikeCountLookup = new();
    private readonly Dictionary<string, float> _lastFollowTimeByUserId = new();

    public void OnEnable()
    {
        TikTokUdpReceiver.OnEvent += HandleEventLogic;
        Debug.Log("Subcribe Event Tiktok");
    }

    public void OnDisable()
    {
        TikTokUdpReceiver.OnEvent -= HandleEventLogic;
        Debug.Log("Unsubscribe Event Tiktok");
    }

    public void HandleEventLogic(TikEvent ev)
    {
        CacheUserAvatar(ev);

        switch (ev.type)
        {
            case "comment":
                HandleComment(ev);
                break;
            case "gift_final":
            case "gift_tick":
                HandleGift(ev);
                break;
            case "like":
                HandleLike(ev);
                break;
            case "share":
                HandleShare(ev);
                break;
            case "follow":
                HandleFollow(ev);
                break;
        }
    }

    private void CacheUserAvatar(TikEvent ev)
    {
        if (!cacheUserAvatars || ev?.user == null || string.IsNullOrWhiteSpace(ev.user.avatar_url))
        {
            return;
        }

        TikTokAvatarCache.RequestAvatar(this, ev.user.avatar_url);
    }

    private void HandleFollow(TikEvent ev)
    {
        string userId = ev.user?.user_id;

        if (string.IsNullOrEmpty(userId))
        {
            OnHandleFollow(ev.user);
            return;
        }

        float now = Time.realtimeSinceStartup;

        if (_lastFollowTimeByUserId.TryGetValue(userId, out float lastFollowTime) &&
            now - lastFollowTime < followCooldownSeconds)
        {
            return;
        }

        _lastFollowTimeByUserId[userId] = now;
        if (!string.IsNullOrEmpty(ev.user.unique_id)) EventBus.Publish(new TikUserFollow());
        OnHandleFollow(ev.user);
    }

    protected virtual void OnHandleFollow(TikUser user)
    {
        foreach (var item in tiktokConfigSO.tiktokDatas.Where(x => x.IsFollow).ToArray())
        {
            ExecutesAction(user, item.IsRandomAction, 1, false, item.ActiveIds);
        }
    }
    protected abstract void ExecuteAction(TikUser tikUser, int id, int count = 1, bool joinGame = false);

    private void HandleShare(TikEvent ev)
    {
        if (!string.IsNullOrEmpty(ev.user.unique_id)) EventBus.Publish(new TikUserShare());
        OnHandleShare(ev.user);
    }

    protected virtual void OnHandleShare(TikUser user)
    {
        foreach (var item in tiktokConfigSO.tiktokDatas.Where(x => x.IsShare).ToArray())
        {
            ExecutesAction(user, item.IsRandomAction, 1, false, item.ActiveIds);
        }
    }
    protected void ExecutesAction(TikUser tikUser, bool isRandom, int count, bool joinGame, params int[] activeIds)
    {
        if (isRandom)
        {
            ExecuteAction(tikUser, activeIds.OrderBy(x => UnityEngine.Random.value).First(), count);
        }
        else
        {
            foreach (var id in activeIds)
            {
                ExecuteAction(tikUser, id);
            }
        }
    }
    private void HandleLike(TikEvent ev)
    {
        string nickname = ev.user.nickname;
        int likeCount = ev.like_count;
        if (!string.IsNullOrEmpty(ev.user.unique_id))
            EventBus.Publish(new TikUserLike()
            {
                likeCount = likeCount
            });
        OnHandleLike(ev.user, likeCount);
    }

    protected virtual void OnHandleLike(TikUser user, int likeCount)
    {
        if (!UserLikeCountLookup.TryAdd(user.user_id, likeCount))
        {
            UserLikeCountLookup[user.user_id] += likeCount;
        }
        foreach (var item in tiktokConfigSO.tiktokDatas.Where(x => x.LikeCount > 0 && x.LikeCount <= UserLikeCountLookup[user.user_id]).ToArray())
        {
            ExecutesAction(user, item.IsRandomAction, 1, false, item.ActiveIds);
            UserLikeCountLookup[user.user_id] -= item.LikeCount;
        }
    }

    private void HandleGift(TikEvent ev)
    {
        int delta = ev.delta;
        int giftPrice = ev.gift.price;
        int giftId = ev.gift.id;
        if (logGiftEvents) Debug.Log($"[Gift] {ev.user.nickname} id={giftId}  price={giftPrice}  delta={delta}");
        if (!string.IsNullOrEmpty(ev.user.unique_id))
        {
            EventBus.Publish(new TikUserGiftEvent()
            {
                giftID = giftId,
                giftPrice = giftPrice,
                userUniqueId = ev.user.unique_id,
                giftDelta = delta,
            });
        }
        OnHandleGift(ev.user, delta, giftPrice, giftId);
    }

    public virtual void OnHandleGift(TikUser user, int delta, int giftPrice, int giftId)
    {
        foreach (var item in tiktokConfigSO.tiktokDatas.Where(x => x.IdGift == giftId).ToArray())
        {
            for (int i = 0; i < Mathf.Max(1, delta); i++)
            {
                ExecutesAction(user, item.IsRandomAction, 1, true, item.ActiveIds);
            }
        }
    }

    private void HandleComment(TikEvent ev)
    {
        string comment = ev.comment?.Trim();
        if (string.IsNullOrEmpty(comment)) return;
        string commentLower = comment.ToLower();
        if (!string.IsNullOrEmpty(ev.user.unique_id)) EventBus.Publish(new TikUserComment());
        OnHandleComment(ev.user, commentLower);
    }

    protected virtual void OnHandleComment(TikUser user, string comment)
    {
        foreach (var item in tiktokConfigSO.tiktokDatas.Where(x => x.Comments.Any(x => x.Trim().ToLower() == comment.Trim().ToLower())).ToArray())
        {
            ExecutesAction(user, item.IsRandomAction, 1, false, item.ActiveIds);
        }
    }
}

internal class TikUserGiftEvent
{
    public TikUserGiftEvent()
    {
    }

    public int giftID { get; set; }
    public int giftPrice { get; set; }
    public string userUniqueId { get; set; }
    public int giftDelta { get; set; }
}

internal class TikUserLike
{
    public TikUserLike()
    {
    }

    public int likeCount { get; set; }
}

internal class TikUserFollow
{
    public TikUserFollow()
    {
    }
}

internal class TikUserShare
{
    public TikUserShare()
    {
    }
}

internal class TikUserComment
{
    public TikUserComment()
    {
    }
}