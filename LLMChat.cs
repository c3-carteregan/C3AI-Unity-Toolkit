using UnityEngine;

namespace C3AI.LLM
{
    public enum  LLMChatEventType
    {
        ON_MESSAGE_SENT,
        ON_MESSAGE_SEND_ERROR,
        ON_MESSAGE_RECEIVED,
        ON_MESSAGE_RECEIVE_ERROR    
    }
    public interface ILLMChat
    {
        void SendChatMessage(string text);
    }
    public abstract class LLMChat : MonoBehaviour, ILLMChat
    {
        public abstract void SendChatMessage(string text);
    }

}