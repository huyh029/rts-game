using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class TiktokEventTestPopup : MonoBehaviour
{
    [SerializeField] TMP_InputField userIdInput, userNameInpup, giftIdInput, giftPriceInput, likeCountInput, commentInput, countGiftInput, otherUsersInput;
    [SerializeField] Button btnSend;
    [SerializeField] Toggle shareToggle, followToggle;
    [SerializeField] GameObject SettingPopup;
    [SerializeField] InputAction keyControl;
    [SerializeField] bool isHandleKeyEvent;
    private void Awake()
    {
        btnSend.onClick.AddListener(OnSend);
        keyControl.Enable();
    }
    private void Update()
    {
        if (keyControl.WasPressedThisFrame())
        {
            SettingPopup.SetActive(!SettingPopup.activeInHierarchy);
            // Cursor.visible = SettingPopup.activeInHierarchy ? true : false;
        }
        //HandleInputActionByKey();
    }

    private void OnSend()
    {
        int.TryParse(otherUsersInput.text, out int repeat);
        repeat = Math.Max(repeat, 1);
        for (int i = 0; i < repeat; i++)
        {
            var user = new TikUser()
            {
                user_id = userIdInput.text,
                nickname = userNameInpup.text,
            };
            if (string.IsNullOrEmpty(user.user_id))
            {
                user.user_id = UnityEngine.Random.Range(0, 10000).ToString();
                user.nickname = $"User {user.user_id}";
            }
            if (!string.IsNullOrEmpty(giftPriceInput.text) || !string.IsNullOrEmpty(giftIdInput.text))
            {
                int.TryParse(giftIdInput.text, out int id);
                int.TryParse(giftPriceInput.text, out int price);
                int.TryParse(countGiftInput.text, out int delta);
                var gift = new TikGift()
                {
                    id = id,
                    price = price,
                };

                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    gift = gift,
                    type = "gift_final",
                    delta = Math.Max(delta, 1)
                });
            }
            if (!string.IsNullOrEmpty(commentInput.text))
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    comment = commentInput.text,
                    type = "comment"
                });
            }
            if (int.TryParse(likeCountInput.text, out int count))
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    like_count = count,
                    type = "like"
                });
            }
            if (shareToggle.isOn)
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    type = "share"
                });
            }
            if (followToggle.isOn)
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    type = "follow"
                });
            }
        }
    }
    // public void HandleInputActionByKey()
    // {
    //     if (!isHandleKeyEvent) return;
    //     Keyboard keyboard = Keyboard.current;
    //     if (keyboard == null || tiktokConfig == null || tiktokConfig.tiktokDatas == null)
    //     {
    //         return;
    //     }

    //     var data = tiktokConfig.tiktokDatas;
    //     int index = -1;

    //     if (keyboard.digit1Key.wasPressedThisFrame)
    //     {
    //         index = 0;
    //     }
    //     else if (keyboard.digit2Key.wasPressedThisFrame)
    //     {
    //         index = 1;
    //     }
    //     else if (keyboard.digit3Key.wasPressedThisFrame)
    //     {
    //         index = 2;
    //     }
    //     else if (keyboard.digit4Key.wasPressedThisFrame)
    //     {
    //         index = 3;
    //     }
    //     else if (keyboard.digit5Key.wasPressedThisFrame)
    //     {
    //         index = 4;
    //     }
    //     else if (keyboard.digit6Key.wasPressedThisFrame)
    //     {
    //         index = 5;
    //     }
    //     else if (keyboard.digit7Key.wasPressedThisFrame)
    //     {
    //         index = 6;
    //     }
    //     else if (keyboard.digit8Key.wasPressedThisFrame)
    //     {
    //         index = 7;
    //     }
    //     else if (keyboard.digit9Key.wasPressedThisFrame)
    //     {
    //         index = 8;
    //     }
    //     else if (keyboard.digit0Key.wasPressedThisFrame)
    //     {
    //         index = 9;
    //     }
    //     else if (keyboard.minusKey.wasPressedThisFrame)
    //     {
    //         index = 10;
    //     }
    //     else if (keyboard.equalsKey.wasPressedThisFrame)
    //     {
    //         index = 11;
    //     }
    //     else if (keyboard.backspaceKey.wasPressedThisFrame)
    //     {
    //         index = 12;
    //     }

    //     if (index < 0 || index >= data.Length)
    //     {
    //         return;
    //     }

    //     ExecuteEvent(data[index]);
    // }

    // private void ExecuteEvent(TiktokConfigSO.TiktokData tiktokData)
    // {
    //     if (tiktokData == null)
    //     {
    //         return;
    //     }

    //     var userId = UnityEngine.Random.Range(0, 10000).ToString();
    //     var user = new TikUser()
    //     {
    //         user_id = userId,
    //         nickname = $"User {userId}",
    //     };

    //     if (tiktokData.IdGift > 0 || tiktokData.PriceGift > 0)
    //     {
    //         int price = Mathf.RoundToInt(tiktokData.PriceGift);
    //         var gift = new TikGift()
    //         {
    //             id = tiktokData.IdGift,
    //             price = price,
    //         };

    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             gift = gift,
    //             type = "gift_final",
    //             count = 1,
    //             total = price,
    //             delta = 1
    //         });
    //     }

    //     if (tiktokData.LikeCount > 0)
    //     {
    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             like_count = tiktokData.LikeCount,
    //             type = "like"
    //         });
    //     }

    //     if (tiktokData.Comments != null && tiktokData.Comments.Length > 0)
    //     {
    //         foreach (var comment in tiktokData.Comments)
    //         {
    //             if (string.IsNullOrEmpty(comment))
    //             {
    //                 continue;
    //             }

    //             TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //             {
    //                 user = user,
    //                 comment = comment,
    //                 type = "comment"
    //             });
    //         }
    //     }

    //     if (tiktokData.IsShare)
    //     {
    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             type = "share"
    //         });
    //     }

    //     if (tiktokData.IsFollow)
    //     {
    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             type = "follow"
    //         });
    //     }
    // }
}