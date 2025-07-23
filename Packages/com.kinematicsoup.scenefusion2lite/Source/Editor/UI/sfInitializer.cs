using System.IO;
using UnityEngine;
using UnityEditor;
using KS.SceneFusion;
using KS.Unity.Editor;
using KS.SceneFusionCommon;
using UnityEngine.Networking;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Performs initialization logic when the editor loads.
     */
    [InitializeOnLoad]
    class sfInitializer
    {
        // Menu group name for Scene Fusion windows.
        private const string WINDOWS_MENU = "Window/" + Product.NAME + "/";

        // Menu group name for other Scene Fusion menu options.
        private const string TOOLS_MENU = "Tools/" + Product.NAME + "/";

        // Menu priority for Scene Fusion windows.
        private const int WINDOWS_PRIORITY = 4000;

        /**
         * Static constructor
         */
        static sfInitializer()
        {
            EditorApplication.update += Init;
        }

        /**
         * Performs initialization logic that must wait until after Unity derserialization finishes.
         */
        private static void Init()
        {
            EditorApplication.update -= Init;
            ksEditorUtils.SetDefineSymbol("SCENE_FUSION_2");
            EditorApplication.update += TrackUserId;
            SceneFusionService.Set(SceneFusion.Get().Service);
            sfGettingStartedWindow.OpenSessionWindow = OpenSessionWindow;

            if (sfConfig.Get().Version != sfConfig.Get().LastVersion)
            {
                if (sfConfig.Get().LastVersion == "0.0.0")
                {
                    sfAnalytics.Get().TrackEvent(sfAnalytics.Events.INSTALL);
                }
                else
                {
                    sfAnalytics.Get().TrackEvent(sfAnalytics.Events.UPGRADE);
                }
                SerializedObject config = new SerializedObject(sfConfig.Get());
                SerializedProperty lastVersion = config.FindProperty("LastVersion");
                lastVersion.stringValue = sfConfig.Get().Version;
                sfPropertyUtils.ApplyProperties(config);
            }


            ksWindow window = ksWindow.Find(ksWindow.SCENE_FUSION_MAIN);
            if (window != null && window.Menu == null)
            {
                window.Menu = ScriptableObject.CreateInstance<sfSessionsMenu>();
            }
        }

        /**
         * Opens the sessions menu.
         */
        [MenuItem(WINDOWS_MENU + "Session", priority = WINDOWS_PRIORITY)]
        private static void OpenSessionWindow()
        {
            ksWindow.Open(ksWindow.SCENE_FUSION_MAIN, delegate (ksWindow window)
            {
                window.titleContent = new GUIContent(" Session", KS.SceneFusion.sfTextures.Logo);
                window.minSize = new Vector2(380f, 100f);
                window.Menu = ScriptableObject.CreateInstance<sfSessionsMenu>();
            });
        }

        /**
         * Opens the notifications window.
         */
        [MenuItem(WINDOWS_MENU + "Notifications", priority = WINDOWS_PRIORITY + 1)]
        private static void OpenNotifications()
        {
            sfNotificationWindow.Open();
        }

        /**
         * Opens the getting started window.
         */
        [MenuItem(WINDOWS_MENU + "Getting Started", priority = WINDOWS_PRIORITY + 2)]
        private static void OpenGettingStarted()
        {
            sfGettingStartedWindow.Get().Open();
        }

        /**
         * Opens the Scene Fusion project settings.
         */
        [MenuItem(WINDOWS_MENU + "Settings", priority = WINDOWS_PRIORITY + 3)]
        private static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/" + Product.NAME);
        }

        /// <summary>
        /// Track the unity user who is using the plugin
        /// </summary>
        private static void TrackUserId()
        {
            string uid = ksEditorUtils.GetUnityUserId();
            string rid = ksEditorUtils.GetReleaseId();
            if (!string.IsNullOrEmpty(uid) && uid != "anonymous" && !string.IsNullOrEmpty(rid))
            {
                EditorApplication.update -= TrackUserId;
                string url = $"{sfConfig.Get().Urls.WebConsole}/unauth/api/v1/trackUserId?uid=unity-{uid}&rid={rid}";
                UnityWebRequest.Get(url).SendWebRequest();
            }
        }
    }
}