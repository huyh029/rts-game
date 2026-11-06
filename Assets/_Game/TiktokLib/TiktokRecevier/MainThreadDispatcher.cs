using System;
using System.Collections.Generic;
using UnityEngine;

public class MainThreadDispatcher : MonoBehaviour
{
    static readonly Queue<Action> queue = new Queue<Action>();

    public static void Run(Action action)
    {
        lock (queue)
        {
            queue.Enqueue(action);
        }
    }

    void Update()
    {
        lock (queue)
        {
            while (queue.Count > 0)
            {
                queue.Dequeue()?.Invoke();
            }
        }
    }
}