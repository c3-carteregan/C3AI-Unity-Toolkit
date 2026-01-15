using C3AI.Events;
using UnityEngine;

namespace C3AI.UISystem
{
    public enum UIEventType
    {
        ON_PAGE_OPEN,
        ON_PAGE_CLOSED,
          
    }
    public class UIEventData : IEventData
    {
        public IUI UI;
        public UIEventType EventType;
        public IUIPage UIPage;

    }
    public interface IUI : IEventSource<IEventData>
    {
    void OpenPage(IUIPage page, bool closeOpenPages);
        void ClosePage(IUIPage pageToClose);
        void CloseOpenPages();

    }

}