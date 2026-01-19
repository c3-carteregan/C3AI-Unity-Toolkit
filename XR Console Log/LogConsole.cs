using UnityEngine;

namespace C3AI.Logging
{
    public class LogConsole : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private Transform _logItemTemplate;

        [Header("Behavior")]
        [SerializeField] private bool _ignoreDuplicateMessages = true;

        [Header("Unity Log Capture")]
        [SerializeField] private bool _captureUnityLogs = true;

        [Tooltip("Capture Debug.Log")]
        [SerializeField] private bool _logUnityLogs = false;

        [Tooltip("Capture Debug.LogWarning")]
        [SerializeField] private bool _logUnityWarnings = true;

        [Tooltip("Capture Debug.LogError / Exceptions / Asserts")]
        [SerializeField] private bool _logUnityErrors = true;

        private string _lastLoggedMessage;

        private void Awake()
        {
            if (_logItemTemplate != null)
                _logItemTemplate.gameObject.SetActive(false);

            if (_captureUnityLogs)
                Application.logMessageReceived += OnUnityLog;
        }

        private void OnDestroy()
        {
            if (_captureUnityLogs)
                Application.logMessageReceived -= OnUnityLog;
        }

        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            // Filter by type
            switch (type)
            {
                case LogType.Log:
                    if (!_logUnityLogs) return;
                    break;

                case LogType.Warning:
                    if (!_logUnityWarnings) return;
                    break;

                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    if (!_logUnityErrors) return;
                    break;
            }

            Color color = Color.white;

            switch (type)
            {
                case LogType.Warning:
                    color = Color.yellow;
                    break;

                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    color = Color.red;
                    break;
            }

            LogMessage(condition, color);

            // Optional: show stack trace for errors/exceptions
            if ((type == LogType.Error || type == LogType.Exception) &&
                !string.IsNullOrEmpty(stackTrace))
            {
                LogMessage(stackTrace, new Color(1f, 0.5f, 0.5f));
            }
        }

        public void LogMessage(string message)
        {
            LogMessage(message, Color.white);
        }

        public void LogMessage(string message, Color color)
        {
            if (_logItemTemplate == null)
            {
                Debug.LogWarning("LogConsole: logItemTemplate not assigned.");
                return;
            }

            if (string.IsNullOrEmpty(message))
                return;

            if (_ignoreDuplicateMessages && message == _lastLoggedMessage)
                return;

            _lastLoggedMessage = message;

            Transform instance =
                Instantiate(_logItemTemplate, _logItemTemplate.parent);

            instance.gameObject.SetActive(true);

            var text = instance.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (text == null)
            {
                Destroy(instance.gameObject);
                return;
            }

            text.text = message;
            text.color = color;

            // Newest on top
            instance.SetSiblingIndex(0);
        }
    }

}