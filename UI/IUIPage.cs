using C3AI.Events;
using UnityEngine;

namespace C3AI.UISystem
{
    public interface IUIPage: IEventSource<UIEventData>
    {
        internal void OnPageOpen(IUI ui);
                internal void OnPageClose(IUI ui);
    }

}