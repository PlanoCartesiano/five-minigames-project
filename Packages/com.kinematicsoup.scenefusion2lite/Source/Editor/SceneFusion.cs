using System;
using UnityEngine;
using UnityEditor;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Unity.Editor;
using KS.Reactor;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Scene Fusion entry point. Does initialization and runs the update loop.
     */
    public class SceneFusion : ksSingleton<SceneFusion>
    {
        /**
         * Update delegate
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        public delegate void UpdateDelegate(float deltaTime);

        /**
         * Service
         */
        public sfService Service
        {
            get { return m_service; }
        }
        private sfService m_service;

        /**
         * Did we reconnect to the current session after disconnecting temporarily to recompile or enter play mode?
         */
        public bool Reconnected
        {
            get { return m_isReconnect && m_running; }
        }

        /**
         * Invoked every update before processing server RPCs.
         */
        public event UpdateDelegate PreUpdate;

        /**
         * Invoked every update after processing server RPCs.
         */
        public event UpdateDelegate OnUpdate;

        private sfLogFile m_fileLogger;
        [SerializeField]
        private string m_sessionLogId;

        [NonSerialized]
        private long m_lastTime;
        [NonSerialized]
        private bool m_running = false;
        [NonSerialized]
        private bool m_isReconnect = false;
        [SerializeField]
        private bool m_isFirstLoad = true;
        [SerializeField]
        private sfSessionInfo m_reconnectInfo;
        [SerializeField]
        private string m_reconnectToken;
        [SerializeField]
        private sfOnlineMenuUI m_activeSessionUI;

        /**
         * Initialization
         */
        protected override void Initialize()
        {
            m_service = new sfService();
            m_service.OnConnect += OnConnect;
            m_service.OnDisconnect += OnDisconnect;
#if MOCK_WEB_SERVICE
            m_service.WebService = new sfMockWebService();
#else
            m_service.WebService = sfWebService.Get();
#endif
            sfGuidManager.Get().RegisterEventListeners();

            if (m_activeSessionUI == null)
            {
                m_activeSessionUI = new sfOnlineMenuUI();
            }
            sfOnlineMenu.DrawSettings = m_activeSessionUI.Draw;
            sfSessionsMenu.PreSessionCheck = PreSessionCheck;
            sfUI.Get().ViewportGetter = GetViewport;

            // Set icons for our scripts.
            if (sfTextures.Question == null || sfTextures.Logo == null)
            {
                // When reinstalling the package, texture may not be loaded yet so we delay...
                EditorApplication.delayCall += SetScriptIcons;
            }
            else
            {
                SetScriptIcons();
            }

            sfSceneTranslator sceneTranslator = new sfSceneTranslator();
            sfObjectEventDispatcher.Get().Register(sfType.Scene, sceneTranslator);
            sfObjectEventDispatcher.Get().Register(sfType.SceneLock, sceneTranslator);
            sfObjectEventDispatcher.Get().Register(sfType.SceneSubscriptions, sceneTranslator);
            sfObjectEventDispatcher.Get().Register(sfType.Hierarchy, sceneTranslator);

            sfLightingTranslator lightingTranslator = new sfLightingTranslator();
            sfObjectEventDispatcher.Get().Register(sfType.LightmapSettings, lightingTranslator);
            sfObjectEventDispatcher.Get().Register(sfType.RenderSettings, lightingTranslator);

            sfObjectEventDispatcher.Get().Register(sfType.OcclusionSettings, new sfOcclusionTranslator());
            sfObjectEventDispatcher.Get().Register(sfType.GameObject, new sfGameObjectTranslator());
            sfObjectEventDispatcher.Get().Register(sfType.Component, new sfComponentTranslator());
            sfObjectEventDispatcher.Get().Register(sfType.Terrain, new sfTerrainTranslator());
            sfObjectEventDispatcher.Get().Register(sfType.TerrainBrush, new sfTerrainBrushTranslator());
            sfObjectEventDispatcher.Get().Register(sfType.Prefab, new sfPrefabTranslator());

            // The asset translator should be registered after all other UObject translators so other translators get a
            // chance to handle sfObjectEventDispatcher.Create events first.
            sfObjectEventDispatcher.Get().Register(sfType.Asset, new sfAssetTranslator());

            sfObjectEventDispatcher.Get().Register(sfType.AssetPath, new sfAssetPathTranslator());

            sfAvatarTranslator avatarTranslator = new sfAvatarTranslator();
            sfObjectEventDispatcher.Get().Register(sfType.Avatar, avatarTranslator);
            sfUI.Get().OnFollowUserCamera = avatarTranslator.OnFollow;
            sfUI.Get().OnGotoUserCamera = avatarTranslator.OnGoTo;
            avatarTranslator.OnUnfollow = sfUI.Get().UnfollowUserCamera;

            sfObjectEventDispatcher.Get().InitializeTranslators();

            if (m_reconnectInfo != null && m_reconnectInfo.ProjectId != -1)
            {
                sfConfig.Get().Logging.OnVerbosityChange += SetLogVerbosity;
                SetLogVerbosity(sfConfig.Get().Logging.Verbosity);

                // It is not safe to join a session until all ksSingletons are finished initializing, so we wait till
                // the end of the frame.
                EditorApplication.delayCall += () =>
                {
                    m_isReconnect = true;
                    if (m_service.WebService != null)
                    {
                        m_service.WebService.SFToken = m_reconnectToken;
                    }
                    sfActivityIndicator.Get().AddTask();
                    m_service.JoinSession(m_reconnectInfo);
                    m_reconnectInfo = null;
                    m_reconnectToken = null;
                };
            }

            m_lastTime = DateTime.Now.Ticks;
            EditorApplication.update += Update;
            EditorApplication.quitting += OnQuit;

            // Show getting started window
            if ((sfConfig.Get().ShowGettingStartedScreen && m_isFirstLoad) ||
                sfConfig.Get().Version != sfConfig.Get().LastVersion)
            {
                sfGettingStartedWindow.Get().Open();
            }
            m_isFirstLoad = false;
        }

        /**
         * Called when the editor is closing. Disconnects from the session.
         */
        private void OnQuit()
        {
            if (m_service != null && m_service.IsConnected)
            {
                m_service.LeaveSession();
                Stop();
            }
        }

        /**
         * Unity on disable. Disconnects from the session.
         */
        private void OnDisable()
        {
            if (m_service != null && m_service.IsConnected)
            {
                if (EditorApplication.isCompiling)
                {
                    ksLog.Debug(this, "Disconnecting temporarily to recompile.");
                }
                else if (!EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    ksLog.Debug(this, "Disconnecting temporarily to enter play mode.");
                }
                else
                {
                    ksLog.Debug(this, "Disconnecting temporarily.");
                }
                m_reconnectInfo = m_service.SessionInfo;
                if (m_service.WebService != null)
                {
                    m_reconnectToken = m_service.WebService.SFToken;
                }
                m_service.LeaveSession(true);
                Stop();
            }
        }

        /**
         * Sets icons on our scripts.
         */
        private void SetScriptIcons()
        {
            ksIconUtility.Get().SetIcon<sfMissingPrefab>(sfTextures.Question);
            ksIconUtility.Get().SetIcon<sfMissingComponent>(sfTextures.Question);
            // Set the icon even if it already has an icon, as the logo icon will change if the editor theme changes.
            ksIconUtility.Get().SetDisabledIcon<sfGuidList>(sfTextures.Logo, true);
            ksIconUtility.Get().SetDisabledIcon<sfIgnore>(sfTextures.Logo, true);
        }

        /**
         * If the user is starting a session, prompts the user to save the untitled scene if there is one. Displays a
         * dialog asking the user if they want to change serialization modes if the serialization mode is not force
         * text. Starts file logging for the session.
         * 
         * @param   sfSessionInfo sessionInfo for the session to join, or null if starting a new session.
         * @return  bool true to start/join the session. False if the user had an untitled scene they did not save.
         */
        private bool PreSessionCheck(sfSessionInfo sessionInfo)
        {
            if (sessionInfo == null)
            {
                sfSceneTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfSceneTranslator>(
                    sfType.Scene);
                if (translator != null && !translator.PromptSaveUntitledScene())
                {
                    return false;
                }
            }
            if (EditorSettings.serializationMode != SerializationMode.ForceText && PromptChangeSerializationMode())
            {
                EditorSettings.serializationMode = SerializationMode.ForceText;
            }

            // Room Id is 0 when doing a manual LAN connection.
            m_sessionLogId = sessionInfo == null ? "new." + DateTime.Now.Ticks : 
                sessionInfo.RoomInfo.Id == 0 ? "manual." + DateTime.Now.Ticks : sessionInfo.RoomInfo.Id.ToString();
            sfConfig.Get().Logging.OnVerbosityChange += SetLogVerbosity;
            SetLogVerbosity(sfConfig.Get().Logging.Verbosity);

            sfActivityIndicator.Get().AddTask();

            return true;
        }

        private bool PromptChangeSerializationMode()
        {
            while (true)
            {
                int choice = EditorUtility.DisplayDialogComplex("Change Serialization Mode",
                    "It is recommended to use serialization mode 'ForceText' with Scene Fusion so that references to " +
                    "missing subassets or prefabs aren't broken when you get the asset from source control or elsewhere. The " +
                    "current serialization mode is '" + EditorSettings.serializationMode + "'. Do you want to change it?",
                    // Unity actually displays these out of order as "Yes", "No", "More Info".
                    "Yes", "More Info", "No");
                switch (choice)
                {
                    case 0: return true;
                    case 2: return false;
                    case 1:
                    {
                        Application.OpenURL(
                            "https://docs.kinematicsoup.com/SceneFusion/unity/TroubleshootingPages/missing_assets.html");
                        break;
                    }
                }
            }
        }

        /**
         * Gets the the viewport rect from a scene view.
         * 
         * @param   SceneView sceneView to viewport from.
         * @return  Rect viewport.
         */
        private Rect GetViewport(SceneView sceneView)
        {
#if UNITY_2022_2_OR_NEWER
            return sceneView.cameraViewport;
#else
            Rect rect = sceneView.camera.pixelRect;
            rect.width = sceneView.position.width;
            // Remove the height of the scene view toolbar (26.2) from the height of the scene view.
            rect.height = sceneView.position.height - 26.2f;
            return rect;
#endif
        }

        /**
         * Sets log verbosity for the file logger.
         * 
         * @param   sfConfig.LogVerbosity verbosity to set.
         */
        private void SetLogVerbosity(sfConfig.LogVerbosity verbosity)
        {
            if (verbosity == sfConfig.LogVerbosity.NONE)
            {
                if (m_fileLogger != null)
                {
                    ksLog.UnregisterHandler(m_fileLogger.Write);
                    Application.logMessageReceived -= m_fileLogger.LogUnityException;
                    m_fileLogger.Close();
                }
                return;
            }
            if (m_fileLogger == null)
            {
                m_fileLogger = new sfLogFile();
                m_fileLogger.StartSessionLog(m_sessionLogId);
            }
            ksLog.Level level;
            switch (verbosity)
            {
                case sfConfig.LogVerbosity.ERRORS: 
                    level = ksLog.Level.ERROR | ksLog.Level.FATAL; break;
                case sfConfig.LogVerbosity.WARNINGS:
                    level = ksLog.Level.WARNING | ksLog.Level.ERROR | ksLog.Level.FATAL; break;
                case sfConfig.LogVerbosity.INFO:
                    level = ksLog.Level.INFO | ksLog.Level.WARNING | ksLog.Level.ERROR | ksLog.Level.FATAL; break;
                case sfConfig.LogVerbosity.DEBUG:
                default:
                    level = ksLog.Level.ALL; break;
            }
            ksLog.RegisterHandler(m_fileLogger.Write, level);
            Application.logMessageReceived += m_fileLogger.LogUnityException;
        }

        /**
         * Called after connecting to a session.
         * 
         * @param   sfSession session
         * @param   string errorMessage
         */
        public void OnConnect(sfSession session, string errorMessage)
        {
            sfActivityIndicator.Get().RemoveTask();
            if (session == null)
            {
                ksLog.Error(this, errorMessage);

                // Stop the file logger.
                SetLogVerbosity(sfConfig.LogVerbosity.NONE);
                sfConfig.Get().Logging.OnVerbosityChange -= SetLogVerbosity;
                m_fileLogger = null;
                return;
            }
            if (m_running)
            {
                return;
            }
            m_running = true;
            // When starting a new session we don't know the session id until we connect, so we temporarly use id 0 for
            // the session log file. Now that we know the id, we rename the log file with the new session id.
            if (m_sessionLogId != null && (m_sessionLogId.StartsWith("new") || m_sessionLogId.StartsWith("manual")))
            {
                m_sessionLogId = session.Info.RoomInfo.Id.ToString();
                if (m_fileLogger != null)
                {
                    m_fileLogger.RenameSessionLog(ref m_sessionLogId);
                }
            }

            ksLog.Info(this, "Connected to Scene Fusion session.");

            sfLoader.Get().Initialize();
            sfIconDrawer.Get().Start();
            sfHierarchyWatcher.Get().Start();
            PreUpdate += sfHierarchyWatcher.Get().PreUpdate;
            OnUpdate += sfHierarchyWatcher.Get().Update;
            sfSelectionWatcher.Get().Start();
            sfUndoManager.Get().Start();
            sfLockManager.Get().Start();
            sfMissingScriptSerializer.Get().Start();
            sfPropertyManager.Get().Start();
            sfPrefabLocker.Get().Start();
            sfSessionFooterUI.Get().ShowUpgradeLink = session.GetObjectLimit(sfType.GameObject) != uint.MaxValue;
            if (!EditorApplication.isPlaying)
            {
                sfObjectEventDispatcher.Get().Start(m_service.Session);
            }
        }

        /**
         * Called after disconnecting from a session.
         * 
         * @param   sfSession session
         * @param   string errorMessage
         */
        public void OnDisconnect(sfSession session, string errorMessage)
        {
            if (errorMessage != null)
            {
                ksLog.Error(this, errorMessage);
            }
            ksLog.Info(this, "Disconnected from Scene Fusion session.");
            Stop();
        }

        /**
         * Stops running Scene Fusion.
         */
        private void Stop()
        {
            if (!m_running)
            {
                return;
            }
            m_running = false;
            m_isReconnect = false;
            sfUnityEventDispatcher.Get().Disable();
            sfGuidManager.Get().SaveGuids();
            sfGuidManager.Get().Clear();
            sfObjectEventDispatcher.Get().Stop(m_service.Session);
            sfIconDrawer.Get().Stop();
            sfHierarchyWatcher.Get().Stop();
            PreUpdate += sfHierarchyWatcher.Get().PreUpdate;
            OnUpdate += sfHierarchyWatcher.Get().Update;
            sfSelectionWatcher.Get().Stop();
            sfUndoManager.Get().Stop();
            sfLockManager.Get().Stop();
            sfMissingScriptSerializer.Get().Stop();
            sfPropertyManager.Get().Stop();
            sfPrefabLocker.Get().Stop();
            sfLoader.Get().CleanUp();
            sfObjectMap.Get().Clear();
            sfNotificationManager.Get().Clear();

            // Stop the file logger.
            SetLogVerbosity(sfConfig.LogVerbosity.NONE);
            sfConfig.Get().Logging.OnVerbosityChange -= SetLogVerbosity;
            m_fileLogger = null;
        }

        /**
         * Called every frame.
         */
        private void Update()
        {
            // Time.deltaTime is not accurate in the editor so we track it ourselves.
            long ticks = DateTime.Now.Ticks;
            float dt = (ticks - m_lastTime) / (float)TimeSpan.TicksPerSecond;
            m_lastTime = ticks;

            // Start the object event dispatcher when we leave play mode.
            if (!sfObjectEventDispatcher.Get().IsActive && m_running && m_service.Session != null && !EditorApplication.isPlaying)
            {
                sfObjectEventDispatcher.Get().Start(m_service.Session);
                // Create all the objects
                foreach (sfObject obj in m_service.Session.GetRootObjects())
                {
                    sfObjectEventDispatcher.Get().OnCreate(obj, -1);
                }
            }

            if (m_running && m_service.Session != null && !EditorApplication.isPlaying)
            {
                // Disable Unity events while SF is changing the scene
                sfUnityEventDispatcher.Get().Disable();
            }
            if (PreUpdate != null)
            {
                PreUpdate(dt);
            }
            m_service.Update(dt);
            if (OnUpdate != null)
            {
                OnUpdate(dt);
            }
            if (m_running && m_service.Session != null && !EditorApplication.isPlaying)
            {
                // Reenable Unity events when SF is done changing the scene
                sfUnityEventDispatcher.Get().Enable();
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                sfCameraManager.Get().LastSceneCamera = view.camera;
            }

            if (Application.isPlaying && Camera.allCamerasCount > 0)
            {
                sfCameraManager.Get().LastGameCamera = Camera.allCameras[0];
            }
        }
    }
}
