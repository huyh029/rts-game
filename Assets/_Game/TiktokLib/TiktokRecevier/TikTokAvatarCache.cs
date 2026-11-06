using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using WebP;

public static class TikTokAvatarCache
{
    private static readonly Dictionary<string, Texture2D> AvatarByUrl = new();
    private static readonly HashSet<string> LoadingUrls = new();
    private static readonly Dictionary<string, List<Action<Texture2D>>> PendingCallbacksByUrl = new();

    public static bool TryGetAvatar(TikUser user, out Texture2D texture)
    {
        texture = null;
        return user != null && TryGetAvatar(user.avatar_url, out texture);
    }

    public static bool TryGetAvatar(string avatarUrl, out Texture2D texture)
    {
        texture = null;

        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return false;
        }

        return AvatarByUrl.TryGetValue(avatarUrl, out texture) && texture != null;
    }

    public static void RequestAvatar(MonoBehaviour runner, TikUser user, Action<TikUser, Texture2D> onLoaded = null)
    {
        if (runner == null || user == null)
        {
            return;
        }

        RequestAvatar(runner, user.avatar_url, texture => onLoaded?.Invoke(user, texture));
    }

    public static void RequestAvatar(MonoBehaviour runner, string avatarUrl, Action<Texture2D> onLoaded = null)
    {
        if (runner == null || string.IsNullOrWhiteSpace(avatarUrl))
        {
            return;
        }

        if (TryGetAvatar(avatarUrl, out Texture2D cachedTexture))
        {
            onLoaded?.Invoke(cachedTexture);
            return;
        }

        if (onLoaded != null)
        {
            if (!PendingCallbacksByUrl.TryGetValue(avatarUrl, out var callbacks))
            {
                callbacks = new List<Action<Texture2D>>();
                PendingCallbacksByUrl[avatarUrl] = callbacks;
            }

            callbacks.Add(onLoaded);
        }

        if (LoadingUrls.Contains(avatarUrl))
        {
            return;
        }

        runner.StartCoroutine(DownloadAndCacheAvatar(avatarUrl));
    }

    public static bool Remove(string avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return false;
        }

        return AvatarByUrl.Remove(avatarUrl);
    }

    public static bool Remove(TikUser user)
    {
        return user != null && Remove(user.avatar_url);
    }

    public static void Clear()
    {
        AvatarByUrl.Clear();
        LoadingUrls.Clear();
        PendingCallbacksByUrl.Clear();
    }

    private static IEnumerator DownloadAndCacheAvatar(string avatarUrl)
    {
        LoadingUrls.Add(avatarUrl);

        using UnityWebRequest request = UnityWebRequest.Get(avatarUrl);
        yield return request.SendWebRequest();

        LoadingUrls.Remove(avatarUrl);

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[TikTokAvatarCache] Download avatar failed: {avatarUrl} | {request.error}");
            PendingCallbacksByUrl.Remove(avatarUrl);
            yield break;
        }

        byte[] bytes = request.downloadHandler?.data;
        if (bytes == null || bytes.Length == 0)
        {
            Debug.LogWarning($"[TikTokAvatarCache] Download avatar returned empty bytes: {avatarUrl}");
            PendingCallbacksByUrl.Remove(avatarUrl);
            yield break;
        }

        Texture2D texture = DecodeTexture(bytes, avatarUrl);
        if (texture == null)
        {
            PendingCallbacksByUrl.Remove(avatarUrl);
            yield break;
        }

        texture.name = $"TikTokAvatar_{AvatarByUrl.Count}";
        AvatarByUrl[avatarUrl] = texture;

        if (PendingCallbacksByUrl.TryGetValue(avatarUrl, out var callbacks))
        {
            PendingCallbacksByUrl.Remove(avatarUrl);

            foreach (var callback in callbacks)
            {
                callback?.Invoke(texture);
            }
        }
    }

    private static Texture2D DecodeTexture(byte[] bytes, string avatarUrl)
    {
        Texture2D texture = DecodeWebP(bytes, avatarUrl);
        if (texture != null)
        {
            return texture;
        }

        return DecodeWithUnityLoadImage(bytes, avatarUrl);
    }

    private static Texture2D DecodeWebP(byte[] bytes, string avatarUrl)
    {
        try
        {
            Texture2D texture = Texture2DExt.CreateTexture2DFromWebP(
                bytes,
                lMipmaps: false,
                lLinear: false,
                lError: out Error error,
                makeNoLongerReadable: false);

            if (error == Error.Success && texture != null)
            {
                return texture;
            }

            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }

            Debug.LogWarning($"[TikTokAvatarCache] WebP decode failed, fallback LoadImage: {avatarUrl} | {error}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TikTokAvatarCache] WebP decode exception, fallback LoadImage: {avatarUrl} | {ex.Message}");
        }

        return null;
    }

    private static Texture2D DecodeWithUnityLoadImage(byte[] bytes, string avatarUrl)
    {
        Texture2D texture = new(2, 2, TextureFormat.RGBA32, mipChain: false);

        try
        {
            if (texture.LoadImage(bytes, markNonReadable: false))
            {
                return texture;
            }

            UnityEngine.Object.Destroy(texture);
            Debug.LogWarning($"[TikTokAvatarCache] Unity LoadImage fallback failed: {avatarUrl}");
        }
        catch (Exception ex)
        {
            UnityEngine.Object.Destroy(texture);
            Debug.LogWarning($"[TikTokAvatarCache] Unity LoadImage fallback exception: {avatarUrl} | {ex.Message}");
        }

        return null;
    }
}