using C3AI.Events;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace C3AI.UISystem
{
    public class UI : MonoBehaviour, IUI
    {
        private List<IUIPage> _openPages = new List<IUIPage>();
        private InterfaceEventManager<UIEventData> _eventManager = new InterfaceEventManager<UIEventData>("UIEventManager", true);
        [SerializeField] private UIPage _startPage;
        private IUIPage[] _uiPages;

        private void Start()
        {
            _uiPages = GetComponentsInChildren<IUIPage>(true);
            _openPages = _uiPages.ToList();
            Debug.Log($"UI: Found {_uiPages.Length} UI Pages.");
            foreach (IUIPage page in _uiPages)
            {
                page.GetGameObject().transform.localPosition = Vector3.zero;
                if ((Object)page != _startPage)
                {
                    ClosePage(page);
                }
            }
        }


        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }

        public void OpenPage(IUIPage page, bool closeOpenPages)
        {
            if(_openPages.Count > 0 && closeOpenPages)
            {
            CloseOpenPages();
            }
            if(page == null)
            {
                Debug.LogWarning("UI: OpenPage called with null page.");        
                return;
            }
            OpenNewPage(page);
        }

        private void OpenNewPage(IUIPage page)
        {
            _openPages.Add(page);
            page.GetGameObject().SetActive(true);
            page.OnPageOpen(this);
            NotifyEventListeners(UIEventType.ON_PAGE_OPEN, page);
        }
        public  void ClosePage(IUIPage page)
        {
            if (!_openPages.Contains(page)){
                Debug.LogWarning("UI: ClosePage called on a page that is not open.");
                return;
            }
            _openPages.Remove(page);
            page.GetGameObject().SetActive(false);
            page.OnPageClose(this);
            NotifyEventListeners(UIEventType.ON_PAGE_CLOSED, page);
        }

        private void NotifyEventListeners(UIEventType eventType, IUIPage page)
        {
            UIEventData eventData = new UIEventData
            {
                UI = this,
                EventType = eventType,
                UIPage = page
            };
           _eventManager.RaiseEvent(eventData); 
        }
        public bool SubscribeToEvents(IEventListener<IEventData> listenerToSubscribe)
        {
            _eventManager.AddListener((IEventListener<UIEventData>)listenerToSubscribe);
            return true;
        }

        public bool UnsubscribeFromEvents(IEventListener<IEventData> listenerToUnsubscribe)
        {
            _eventManager.RemoveListener((IEventListener<UIEventData>)listenerToUnsubscribe);
            return true;
        }



        public void CloseOpenPages()
        {
            foreach (IUIPage openPage in _openPages.ToArray())
            {
                ClosePage(openPage);
            }
        }
    }
}
