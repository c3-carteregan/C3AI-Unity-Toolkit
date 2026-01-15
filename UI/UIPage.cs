using C3AI.Events;
using UnityEngine;

namespace C3AI.UISystem
{ 
public class UIPage : MonoBehaviour, IUIPage
{
        private InterfaceEventManager<UIEventData> _eventManager = new InterfaceEventManager<UIEventData>("UIPageEventManager", true);
        public GameObject GetGameObject()
        {
           return this == null ? null : this.gameObject;
        }

        public bool SubscribeToEvents(IEventListener<UIEventData> listenerToSubscribe)
        {
            _eventManager.AddListener(listenerToSubscribe);
            return true;
        }

        public bool UnsubscribeFromEvents(IEventListener<UIEventData> listenerToUnsubscribe)
        {
          _eventManager.RemoveListener(listenerToUnsubscribe);
            return true;
        }
        private void NotifyEventListeners(UIEventType eventType, IUI ui)    
        {
            UIEventData eventData = new UIEventData
            {
                UI = ui,
                EventType = eventType,
                UIPage = this
            };
            _eventManager.RaiseEvent(eventData);
        }
     
        void IUIPage.OnPageClose(IUI ui)
        {
            NotifyEventListeners(UIEventType.ON_PAGE_CLOSED, ui);
        }

        void IUIPage.OnPageOpen(IUI ui)
        {
            NotifyEventListeners(UIEventType.ON_PAGE_OPEN, ui);
        }
    }
}
