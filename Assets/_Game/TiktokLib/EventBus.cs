using System;
using System.Collections.Generic;

public static class EventBus
{
    // A dictionary holding lists of delegates mapped by their Event Type
    private static readonly Dictionary<Type, Delegate> Events = new Dictionary<Type, Delegate>();

    /// <summary>
    /// Subscribe to a specific event type.
    /// </summary>
    public static void Subscribe<T>(Action<T> listener)
    {
        Type eventType = typeof(T);

        if (Events.ContainsKey(eventType))
        {
            Events[eventType] = (Action<T>)Events[eventType] + listener;
        }
        else
        {
            Events[eventType] = listener;
        }
    }

    /// <summary>
    /// Unsubscribe from a specific event type.
    /// </summary>
    public static void Unsubscribe<T>(Action<T> listener)
    {
        Type eventType = typeof(T);

        if (Events.ContainsKey(eventType))
        {
            Action<T> currentDel = (Action<T>)Events[eventType] - listener;
            
            if (currentDel == null)
            {
                Events.Remove(eventType);
            }
            else
            {
                Events[eventType] = currentDel;
            }
        }
    }

    /// <summary>
    /// Publish/Raise an event to all subscribers.
    /// </summary>
    public static void Publish<T>(T eventArgs)
    {
        Type eventType = typeof(T);

        if (Events.TryGetValue(eventType, out Delegate del))
        {
            (del as Action<T>)?.Invoke(eventArgs);
        }
    }

    internal static void Subscribe<T>()
    {
        throw new NotImplementedException();
    }
}