using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace C3AI.Events
{
    // Marker interface for event payload types
    public interface IEventData { }

    // Listener interface for a specific event payload type
    public interface IEventListener<T> where T : IEventData
    {
        GameObject GetGameObject();
        void OnEventOccurred(T eventData);
    }

    // Event source capable of subscription for a specific payload type
    public interface IEventSource<T> where T : IEventData
    {
        GameObject GetGameObject();
        bool SubscribeToEvents(IEventListener<T> listenerToSubscribe);
        bool UnsubscribeFromEvents(IEventListener<T> listenerToUnsubscribe);
    }

    // Central manager for one event payload type
    public class InterfaceEventManager<T> where T : IEventData
    {
        // Using HashSet de-dupes and speeds up membership ops
        private readonly HashSet<IEventListener<T>> _listeners = new();
        private readonly string _debugString;
        private readonly bool _catchListenerExceptions;

        // If events might be fired cross-thread, add a lock. Unity usually keeps things on main thread.
        private readonly object _gate = new();

        public InterfaceEventManager(string debugString = "EventManager", bool catchListenerExceptions = false)
        {
            _debugString = debugString;
            _catchListenerExceptions = catchListenerExceptions;
        }

        public bool AddListener(IEventListener<T> listener)
        {
            if (listener == null)
            {
                Debug.LogWarning($"[{_debugString}] Attempted to add null listener.");
                return false;
            }

            lock (_gate)
            {
                if (!_listeners.Add(listener))
                {
                    Debug.LogWarning($"[{_debugString}] Listener already subscribed: {listener}");
                    return false;
                }
            }
            return true;
        }

        public bool RemoveListener(IEventListener<T> listener)
        {
            if (listener == null)
            {
                Debug.LogWarning($"[{_debugString}] Attempted to remove null listener.");
                return false;
            }

            lock (_gate)
            {
                if (!_listeners.Remove(listener))
                {
                    Debug.LogWarning($"[{_debugString}] Attempted to remove non-existent listener: {listener}");
                    return false;
                }
            }
            return true;
        }

        // Fires an event to all listeners. Null or destroyed listeners are pruned first.
        public void RaiseEvent(T eventData)
        {
            IEventListener<T>[] snapshot;

            lock (_gate)
            {
                // Unity destroyed objects compare to null; clean them up.
                int before = _listeners.Count;
                _listeners.RemoveWhere(l => l == null || (l is UnityEngine.Object uo && uo == null));
                int removed = before - _listeners.Count;
                if (removed > 0)
                {
                    Debug.LogWarning($"[{_debugString}] Removed {removed} null/destroyed listener(s) before dispatch.");
                }

                // Take a snapshot to avoid reentrancy issues while iterating
                snapshot = _listeners.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                var listener = snapshot[i];
                if (listener == null || (listener is UnityEngine.Object uo && uo == null))
                    continue;

                if (_catchListenerExceptions)
                {
                    try
                    {
                        listener.OnEventOccurred(eventData);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[{_debugString}] Listener threw: {e}");
                    }
                }
                else
                {
                    listener.OnEventOccurred(eventData);
                }
            }
        }

        public IReadOnlyCollection<IEventListener<T>> GetListeners()
        {
            lock (_gate) { return _listeners.ToArray(); }
        }
    }

    // Example event payload
    public sealed class TestEventData : IEventData
    {
        public bool Value { get; }
        public TestEventData(bool value = true) { Value = value; }
    }

    // Example listener MonoBehaviour
    public sealed class TestListenerBehaviour : MonoBehaviour, IEventListener<TestEventData>
    {
        // Typically a serialized reference to a source in scene
        [SerializeField] private TestSource _source;

        public GameObject GetGameObject()
        {
            return this == null ? null : gameObject;
        }

        private void OnEnable()
        {
            if (_source != null)
                _source.SubscribeToEvents(this);
        }

        private void OnDisable()
        {
            if (_source != null)
                _source.UnsubscribeFromEvents(this);
        }

        public void OnEventOccurred(TestEventData eventData)
        {
            Debug.Log($"[TestListener] Received TestEventData with Value={eventData.Value}");
        }
    }

    // Example source MonoBehaviour
    public sealed class TestSource : MonoBehaviour, IEventSource<TestEventData>
    {
        // You can expose these toggles in inspector if you like
        [SerializeField] private string _debugString = "TestEvent";
        [SerializeField] private bool _catchListenerExceptions = false;

        public GameObject GetGameObject()
        {
            return this == null ? null : gameObject;
        }

        private InterfaceEventManager<TestEventData> _eventManager;

        private void Awake()
        {
            _eventManager = new InterfaceEventManager<TestEventData>(_debugString, _catchListenerExceptions);
        }

        public bool SubscribeToEvents(IEventListener<TestEventData> listenerToSubscribe)
            => _eventManager.AddListener(listenerToSubscribe);

        public bool UnsubscribeFromEvents(IEventListener<TestEventData> listenerToUnsubscribe)
            => _eventManager.RemoveListener(listenerToUnsubscribe);

        // Example trigger
        [ContextMenu("Dispatch Test Event")]
        public void DispatchTestEvent()
        {
            _eventManager.RaiseEvent(new TestEventData(true));
        }
        // You can call DispatchTestEvent() from code to fire.
    }
}
