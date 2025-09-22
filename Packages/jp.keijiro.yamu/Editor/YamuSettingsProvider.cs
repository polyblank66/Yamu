using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yamu
{
    /// <summary>
    /// Project Settings provider for Yamu MCP server configuration
    /// </summary>
    public class YamuSettingsProvider : SettingsProvider
    {
        private SerializedObject _settings;

        public YamuSettingsProvider(string path, SettingsScope scope)
            : base(path, scope) { }

        /// <summary>
        /// Check if settings are available
        /// </summary>
        public static bool IsSettingsAvailable()
        {
            return YamuSettings.Instance != null;
        }

        /// <summary>
        /// Create the settings provider for Unity's Project Settings window
        /// </summary>
        [SettingsProvider]
        public static SettingsProvider CreateYamuSettingsProvider()
        {
            if (IsSettingsAvailable())
            {
                var provider = new YamuSettingsProvider("Project/Yamu MCP Server", SettingsScope.Project);

                // Keywords for search functionality in Project Settings
                provider.keywords = new[] {
                    "Yamu", "MCP", "Response", "Limit", "Character", "Truncation", "Message", "Server", "Port", "Debug", "Logs"
                };

                return provider;
            }

            // Settings not available, don't create provider
            return null;
        }

        /// <summary>
        /// Render the settings UI
        /// </summary>
        public override void OnGUI(string searchContext)
        {
            // Initialize serialized object if needed
            if (_settings == null)
            {
                var settings = YamuSettings.Instance;
                _settings = new SerializedObject(settings);
            }

            _settings.Update();

            // Header
            EditorGUILayout.LabelField("Yamu MCP Server Configuration", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // Help box with information
            EditorGUILayout.HelpBox(
                "Configure response character limits for the Yamu MCP server. " +
                "The MCP server will truncate responses that exceed the specified limit to prevent overwhelming AI agents.",
                MessageType.Info);

            EditorGUILayout.Space();

            // Response Configuration section
            EditorGUILayout.LabelField("Response Limits", EditorStyles.boldLabel);

            var characterLimitProp = _settings.FindProperty("responseCharacterLimit");
            EditorGUILayout.PropertyField(characterLimitProp, new GUIContent(
                "Response Character Limit",
                "Maximum characters in complete MCP response including JSON structure. Default: 25000"));

            var enableTruncationProp = _settings.FindProperty("enableTruncation");
            EditorGUILayout.PropertyField(enableTruncationProp, new GUIContent(
                "Enable Truncation",
                "When enabled, responses exceeding the limit will be truncated. When disabled, no limits are applied."));

            EditorGUILayout.Space();

            // Truncation Settings section
            EditorGUILayout.LabelField("Truncation Settings", EditorStyles.boldLabel);

            var truncationMessageProp = _settings.FindProperty("truncationMessage");
            EditorGUILayout.PropertyField(truncationMessageProp, new GUIContent(
                "Truncation Message",
                "Message appended to truncated responses to indicate content was cut off."));

            EditorGUILayout.Space();

            // Debug Configuration section
            EditorGUILayout.LabelField("Debug Configuration", EditorStyles.boldLabel);

            var enableDebugLogsProp = _settings.FindProperty("enableDebugLogs");
            EditorGUILayout.PropertyField(enableDebugLogsProp, new GUIContent(
                "Enable Debug Logs",
                "Enable debug logging for YamuServer HTTP handlers"));

            EditorGUILayout.Space();

            // Server Configuration section
            EditorGUILayout.LabelField("Server Configuration", EditorStyles.boldLabel);

            var serverPortProp = _settings.FindProperty("serverPort");
            EditorGUILayout.PropertyField(serverPortProp, new GUIContent(
                "Server Port",
                "TCP port for the Yamu MCP server (default: 17932)"));

            EditorGUILayout.Space();

            // Information about current settings
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Current Settings:", EditorStyles.boldLabel, GUILayout.Width(120));

                var settings = YamuSettings.Instance;
                var overhead = EstimateResponseOverhead(settings);
                var availableForContent = Mathf.Max(0, settings.responseCharacterLimit - overhead);

                EditorGUILayout.LabelField($"~{availableForContent} chars available for content", GUILayout.ExpandWidth(true));
            }

            // Buttons section
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Defaults", GUILayout.Width(150)))
                {
                    if (EditorUtility.DisplayDialog("Reset Settings",
                        "Reset all Yamu settings to default values?", "Reset", "Cancel"))
                    {
                        YamuSettings.Instance.ResetToDefaults();
                        _settings.Update(); // Refresh the serialized object
                    }
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Save Settings", GUILayout.Width(100)))
                {
                    YamuSettings.Instance.Save();
                    EditorUtility.DisplayDialog("Settings Saved",
                        "Yamu settings have been saved successfully.", "OK");
                }
            }

            // Apply any changes made in the inspector
            _settings.ApplyModifiedProperties();
        }

        /// <summary>
        /// Estimate the overhead of MCP response structure
        /// </summary>
        private int EstimateResponseOverhead(YamuSettings settings)
        {
            // Estimate JSON structure overhead for typical MCP response
            var sampleResponse = "{\"jsonrpc\":\"2.0\",\"id\":999999,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"\"}]}}";
            var jsonOverhead = sampleResponse.Length;

            // Add truncation message length
            var truncationOverhead = settings.enableTruncation ? settings.truncationMessage.Length : 0;

            // Add some buffer for safety
            return jsonOverhead + truncationOverhead + 50;
        }

        /// <summary>
        /// Called when the provider is activated
        /// </summary>
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            // Ensure settings instance exists when provider is activated
            var settings = YamuSettings.Instance;
            _settings = new SerializedObject(settings);
        }

        /// <summary>
        /// Called when the provider is deactivated
        /// </summary>
        public override void OnDeactivate()
        {
            // Save any pending changes when leaving the settings page
            if (_settings != null && _settings.hasModifiedProperties)
            {
                _settings.ApplyModifiedProperties();
                YamuSettings.Instance.Save();
            }
        }
    }
}
